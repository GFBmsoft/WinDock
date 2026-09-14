using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinDock.Interop;

namespace WinDock.Services;

/// <summary>
/// Um app fixado. <see cref="Id"/> e a identidade (AppUserModelID quando existe), e
/// <see cref="Path"/> o que abrir — normalmente o .lnk, que carrega os argumentos do
/// perfil. Sao campos distintos porque dois perfis do Chrome tem o mesmo executavel.
/// </summary>
/// <summary>
/// Uma anotação de um dia: o texto e se já foi resolvida.
///
/// Avisa quando muda porque a lista da caixa de edição desenha o texto riscado na hora em que
/// se marca o item — sem o aviso, só ao fechar e reabrir.
/// </summary>
public sealed class CalendarNote : INotifyPropertyChanged
{
    public string Text { get; set; } = string.Empty;

    private bool _done;
    public bool Done
    {
        get => _done;
        set
        {
            if (_done == value) return;
            _done = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Done)));
        }
    }

    /// <summary>
    /// Quando a tarefa foi marcada como feita — é daqui que conta o prazo para ela se apagar
    /// sozinha (<see cref="DockConfig.CalendarDoneRetentionDays"/>). Vazio enquanto não está feita.
    /// </summary>
    public DateTime? DoneAt { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Lê as anotações do calendário nos três formatos que o arquivo já teve: o texto único por dia
/// (de quando cada dia comportava uma anotação só), a lista de textos, e a lista de objetos com
/// o "concluído" de agora.
///
/// Sem isto, cada mudança de formato apagaria em silêncio o que a pessoa já tinha anotado — o
/// <c>System.Text.Json</c> falha ao ler um texto onde espera um vetor, e o <c>catch</c> que
/// protege o carregamento do config devolveria **todas** as preferências em branco. Gravar é
/// sempre no formato de agora, então o arquivo se converte sozinho na primeira anotação.
/// </summary>
public sealed class CalendarNotesConverter : JsonConverter<Dictionary<string, List<CalendarNote>>>
{
    public override Dictionary<string, List<CalendarNote>> Read(ref Utf8JsonReader reader, Type type,
                                                                JsonSerializerOptions options)
    {
        var notas = new Dictionary<string, List<CalendarNote>>();
        if (reader.TokenType != JsonTokenType.StartObject) return notas;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return notas;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var dia = reader.GetString() ?? string.Empty;
            reader.Read();

            var itens = new List<CalendarNote>();
            switch (reader.TokenType)
            {
                case JsonTokenType.String:                       // o mais antigo: um texto só
                    Add(itens, reader.GetString(), false);
                    break;

                case JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            Add(itens, reader.GetString(), false);
                        }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            string? texto = null;
                            var feito = false;
                            DateTime? feitoEm = null;

                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                                var campo = reader.GetString();
                                reader.Read();

                                if (string.Equals(campo, "Text", StringComparison.OrdinalIgnoreCase))
                                    texto = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                                else if (string.Equals(campo, "Done", StringComparison.OrdinalIgnoreCase))
                                    feito = reader.TokenType == JsonTokenType.True;
                                else if (string.Equals(campo, "DoneAt", StringComparison.OrdinalIgnoreCase) &&
                                         reader.TokenType == JsonTokenType.String &&
                                         DateTime.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                                           System.Globalization.DateTimeStyles.RoundtripKind, out var em))
                                    feitoEm = em;
                                else
                                    reader.Skip();
                            }

                            Add(itens, texto, feito, feitoEm);
                        }
                        else reader.Skip();
                    }
                    break;

                default:
                    reader.Skip();
                    break;
            }

            if (dia.Length > 0 && itens.Count > 0) notas[dia] = itens;
        }

        return notas;
    }

    /// <summary>
    /// Uma tarefa feita que chega sem <see cref="CalendarNote.DoneAt"/> — gravada antes de o campo
    /// existir — ganha a hora desta leitura. Assim o prazo de apagar conta a partir de agora, em vez
    /// de todas as tarefas já concluídas sumirem de uma vez na primeira limpeza.
    /// </summary>
    private static void Add(List<CalendarNote> itens, string? texto, bool feito, DateTime? feitoEm = null)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        itens.Add(new CalendarNote { Text = texto.Trim(), Done = feito, DoneAt = feito ? feitoEm ?? DateTime.Now : null });
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, List<CalendarNote>> value,
                               JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (dia, itens) in value)
        {
            writer.WritePropertyName(dia);
            writer.WriteStartArray();
            foreach (var item in itens)
            {
                writer.WriteStartObject();
                writer.WriteString("Text", item.Text);
                writer.WriteBoolean("Done", item.Done);
                if (item.Done && item.DoneAt is { } feitoEm)
                    writer.WriteString("DoneAt", feitoEm.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

/// <summary>Largura e altura que um app usa flutuando, em pixels da tela.</summary>
public sealed class FloatingSize
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class PinnedApp
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>Imagem sobreposta ao icone (foto do perfil), quando houver.</summary>
    public string Overlay { get; set; } = string.Empty;
}

/// <summary>
/// Preferencias da dock. Notifica mudancas para que o painel de configuracoes
/// aplique tudo ao vivo, sem reabrir o app.
/// </summary>
public sealed class DockConfig : INotifyPropertyChanged
{
    // ── posicao ─────────────────────────────────────────────
    private DockEdge _edge = DockEdge.Bottom;
    /// <summary>Borda da tela onde a dock encosta.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DockEdge Edge { get => _edge; set => Set(ref _edge, value); }

    private int _size = 38;
    /// <summary>Espessura da faixa, em pixels fisicos.</summary>
    public int Size { get => _size; set => Set(ref _size, Clamp(value, 24, 120)); }

    private bool _reserveSpace = true;
    /// <summary>Encolher a area de trabalho para nenhuma janela ficar por baixo.</summary>
    public bool ReserveSpace { get => _reserveSpace; set => Set(ref _reserveSpace, value); }

    private bool _centered = true;
    public bool Centered { get => _centered; set => Set(ref _centered, value); }

    // ── aparencia ───────────────────────────────────────────
    private string _background = "#202124";
    /// <summary>Cor da pilula, em hex (sem alfa: a opacidade e separada).</summary>
    public string Background { get => _background; set => Set(ref _background, value); }

    private string _indicatorColor = "#6CB6FF";
    /// <summary>Cor da bolinha que marca o app aberto, em hex.</summary>
    public string IndicatorColor { get => _indicatorColor; set => Set(ref _indicatorColor, value); }

    private int _opacity = 90;
    /// <summary>Opacidade da pilula, 0 (invisivel) a 100 (solida).</summary>
    public int Opacity { get => _opacity; set => Set(ref _opacity, Clamp(value, 0, 100)); }

    private int _cornerRadius = 19;
    /// <summary>Raio dos cantos da pilula. Metade da espessura deixa capsula perfeita.</summary>
    public int CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Clamp(value, 0, 60)); }

    private int _iconPadding = 5;
    /// <summary>Folga entre o icone e a borda do botao. Menor = icone maior.</summary>
    public int IconPadding { get => _iconPadding; set => Set(ref _iconPadding, Clamp(value, 0, 20)); }

    private int _pillPadding = 4;
    /// <summary>Folga entre a pilula e os botoes.</summary>
    public int PillPadding { get => _pillPadding; set => Set(ref _pillPadding, Clamp(value, 0, 20)); }

    private int _iconSize;
    /// <summary>
    /// Lado do icone, em pixels. Zero deixa a dock calcular pelo tamanho do botao — o que
    /// varia com a espessura; um valor fixo deixa todos os icones iguais.
    /// </summary>
    public int IconSize { get => _iconSize; set => Set(ref _iconSize, value == 0 ? 0 : Clamp(value, 12, 96)); }

    // ── margens da pilula, um lado de cada vez ──────────────
    private int _marginTop;
    public int MarginTop { get => _marginTop; set => Set(ref _marginTop, Clamp(value, 0, 40)); }

    private int _marginBottom;
    public int MarginBottom { get => _marginBottom; set => Set(ref _marginBottom, Clamp(value, 0, 40)); }

    private int _marginLeft;
    public int MarginLeft { get => _marginLeft; set => Set(ref _marginLeft, Clamp(value, 0, 400)); }

    private int _marginRight;
    public int MarginRight { get => _marginRight; set => Set(ref _marginRight, Clamp(value, 0, 400)); }

    /// <summary>
    /// Como a folga da borda era guardada antes das quatro margens. Fica aqui so para ler
    /// configuracoes antigas; <see cref="Load"/> converte o valor e zera este campo.
    /// </summary>
    public int EdgeMargin { get; set; }

    // ── comportamento ───────────────────────────────────────
    private bool _showRunning = true;
    /// <summary>Mostrar tambem os apps abertos que nao estao fixados.</summary>
    public bool ShowRunning { get => _showRunning; set => Set(ref _showRunning, value); }

    private TaskbarMode _taskbar = TaskbarMode.Keep;
    /// <summary>O que fazer com a barra de tarefas do Windows enquanto a dock roda.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskbarMode Taskbar { get => _taskbar; set => Set(ref _taskbar, value); }

    private bool _launcher = true;
    /// <summary>Alt+Espaco abre a busca de aplicativos.</summary>
    public bool Launcher { get => _launcher; set => Set(ref _launcher, value); }

    private bool _numberHotkeys = true;
    /// <summary>
    /// Alt+1 a Alt+9 acionam o 1o ao 9o botao da dock, na ordem em que aparecem.
    ///
    /// Nao e Win+numero porque nao da: o Explorer registra Win+1..9 (e Win+Shift, Win+Ctrl e
    /// Win+Alt com numero) para a barra de tarefas dele, e o RegisterHotKey devolve
    /// ERROR_HOTKEY_ALREADY_REGISTERED em todas essas combinacoes. O Win+numero que o Windows
    /// atende segue a ordem dos fixados da barra dele, que nao tem relacao com a da dock.
    ///
    /// Alt+numero e um atalho global: enquanto ligado, ele deixa de chegar ao programa em
    /// foco (alguns usam Alt+numero para menus ou abas). Por isso da para desligar.
    /// </summary>
    public bool NumberHotkeys { get => _numberHotkeys; set => Set(ref _numberHotkeys, value); }

    private int _launcherResults = 9;
    /// <summary>
    /// Quantos resultados a busca do Alt+Espaco mostra.
    ///
    /// A linha "Executar comando" nao entra nessa conta: ela e sempre acrescentada no fim,
    /// depois do corte, porque digitar um caminho ou uma URL tem de funcionar mesmo quando a
    /// lista de aplicativos ja encheu.
    /// </summary>
    public int LauncherResults
    {
        get => _launcherResults;
        set => Set(ref _launcherResults, Clamp(value, 3, 20));
    }

    private bool _panel;
    /// <summary>Barra no topo com relogio, data e os atalhos dos paineis do Windows.</summary>
    public bool Panel { get => _panel; set => Set(ref _panel, value); }

    private int _panelSize = 30;
    /// <summary>Altura da barra de cima, em pixels fisicos.</summary>
    public int PanelSize { get => _panelSize; set => Set(ref _panelSize, Clamp(value, 22, 60)); }

    // A barra de cima tem cor e opacidade proprias: ela e uma faixa inteira e a dock e
    // uma pilula solta, entao o que fica bom em uma nao fica na outra.
    private string _panelBackground = "#202124";
    public string PanelBackground { get => _panelBackground; set => Set(ref _panelBackground, value); }

    private int _panelOpacity = 95;
    public int PanelOpacity { get => _panelOpacity; set => Set(ref _panelOpacity, Clamp(value, 0, 100)); }

    private bool _panelCenterClock;
    /// <summary>Relogio no meio da barra, no lugar do canto.</summary>
    public bool PanelCenterClock { get => _panelCenterClock; set => Set(ref _panelCenterClock, value); }

    private bool _panelTray = true;
    /// <summary>
    /// Mostrar na barra os icones de bandeja dos programas que estao rodando — o do
    /// antivirus, o do AnyDesk, os que so existem ali. Com a barra do Windows escondida
    /// eles ficariam inalcancaveis.
    /// </summary>
    public bool PanelTray { get => _panelTray; set => Set(ref _panelTray, value); }

    // "aberto ou recolhido" nao mora aqui de proposito: a bandeja comeca sempre recolhida,
    // entao o estado e da sessao e vive no PanelModel. Guarda-lo faria a barra do Windows
    // piscar todo logon so para preencher uma area que talvez ninguem va olhar.

    private bool _panelAppVolume = true;
    /// <summary>Listar o volume de cada programa dentro do controle de volume.</summary>
    public bool PanelAppVolume { get => _panelAppVolume; set => Set(ref _panelAppVolume, value); }

    private bool _panelBrightness = true;
    /// <summary>
    /// Controle de brilho na barra.
    ///
    /// Some sozinho num monitor que nao fala DDC/CI (o painel de notebook costuma nao falar);
    /// esta opcao e para quem nao quer o item nem onde ele funciona.
    /// </summary>
    public bool PanelBrightness { get => _panelBrightness; set => Set(ref _panelBrightness, value); }

    private bool _panelMedia = true;
    /// <summary>
    /// Mostrar na barra o que está tocando, com os controles de reprodução.
    ///
    /// Some sozinho quando não há mídia nenhuma; esta opção é para quem não quer o item nem
    /// quando há.
    /// </summary>
    public bool PanelMedia { get => _panelMedia; set => Set(ref _panelMedia, value); }

    /// <summary>
    /// Quais programas podem ocupar a barra de mídia. <b>Lista vazia quer dizer todos.</b>
    ///
    /// Guardados pelo identificador que o Windows usa para a sessão de mídia — o Spotify se
    /// anuncia como <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>. Serve para quem quer
    /// a barra só para o player de música e não para todo vídeo aberto no navegador.
    /// </summary>
    public List<string> MediaApps { get; set; } = new();

    /// <summary>A lista é um objeto só: mexer no conteúdo não avisa ninguém sozinho.</summary>
    public void NotifyMediaAppsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaApps)));

    /// <summary>
    /// Todo programa que já apareceu tocando alguma coisa nesta máquina — é o que a tela de
    /// opções tem para listar.
    ///
    /// Fica no disco porque a lista em memória só conhecia o que tocou depois que a dock subiu:
    /// quem quisesse marcar o navegador precisava deixar um vídeo tocando **e** abrir as opções
    /// sem reiniciar a dock no meio, senão a lista aparecia vazia sem explicação.
    ///
    /// Identificadores que o Windows entrega, sem tradução: <c>Chrome</c> para o navegador,
    /// <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c> para o Spotify.
    /// </summary>
    public List<string> MediaAppsSeen { get; set; } = new();

    /// <summary>
    /// A ordem dos itens do canto direito da barra, por chave.
    ///
    /// Vazia quer dizer "a ordem que veio no XAML". Chave desconhecida é ignorada, e item
    /// que existe na barra mas não está aqui vai para o fim — assim uma configuração antiga
    /// continua valendo quando a barra ganha um item novo, em vez de escondê-lo.
    /// </summary>
    public List<string> PanelOrder { get; set; } = new();

    public void NotifyPanelOrderChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PanelOrder)));

    /// <summary>
    /// Aparelhos bluetooth que nao devem aparecer na lista da barra. Guardados pelo nome,
    /// que e como a pessoa os reconhece; a lista e curta e editada no proprio painel.
    /// </summary>
    public List<string> BluetoothHidden { get; set; } = new();

    /// <summary>
    /// Apelidos dados a ícones da bandeja que não publicam nome nenhum, guardados pela
    /// assinatura do desenho.
    ///
    /// Alguns programas simplesmente não fornecem dica de mouse — o <c>Master.exe</c> desta
    /// máquina é o caso —, e aí não há texto em lugar nenhum: a automação dá a todos os
    /// botões o mesmo identificador ("NotifyItemIcon"), o processo do botão é sempre o
    /// explorer, e o registro guarda a dica vazia. O que sobra para reconhecê-lo entre uma
    /// leitura e outra é o próprio desenho, e é ele que vira a chave aqui (veja
    /// <c>TrayService.Signature</c>).
    ///
    /// A consequência de usar o desenho: se o programa trocar o ícone, o apelido não
    /// acompanha — vira outro desenho, logo outra identidade, e a linha antiga fica órfã.
    /// </summary>
    public Dictionary<string, string> TrayNames { get; set; } = new();

    // ── mosaico (tiling window manager) ──────────────────────
    private bool _tilingEnabled;
    /// <summary>Liga o mosaico no monitor principal — atalhos e reorganizacao automatica.</summary>
    public bool TilingEnabled { get => _tilingEnabled; set => Set(ref _tilingEnabled, value); }

    private int _tilingGap = 8;
    /// <summary>Folga entre as janelas do mosaico e entre elas e a borda da tela, em pixels.</summary>
    public int TilingGap { get => _tilingGap; set => Set(ref _tilingGap, Clamp(value, 0, 60)); }

    private int _tilingResizeStep = 20;
    /// <summary>Quantos pixels cada Ctrl+Alt+Shift+seta tira da janela vizinha e dá pra janela em
    /// foco. O espaço nunca vem do nada: quem cresce, cresce às custas de quem está do lado.</summary>
    public int TilingResizeStep { get => _tilingResizeStep; set => Set(ref _tilingResizeStep, Clamp(value, 1, 200)); }

    private string _tilingBorderColor = "#4CC2FF";
    /// <summary>Cor do contorno ao redor da janela em foco, em hex.</summary>
    public string TilingBorderColor { get => _tilingBorderColor; set => Set(ref _tilingBorderColor, value); }

    private int _tilingBorderThickness = 2;
    /// <summary>Espessura do contorno, em pixels. Zero desliga o contorno sem desligar o mosaico.</summary>
    public int TilingBorderThickness { get => _tilingBorderThickness; set => Set(ref _tilingBorderThickness, Clamp(value, 0, 12)); }

    private int _tilingBorderRadius = 8;
    /// <summary>Raio dos cantos do contorno.</summary>
    public int TilingBorderRadius { get => _tilingBorderRadius; set => Set(ref _tilingBorderRadius, Clamp(value, 0, 30)); }

    /// <summary>
    /// Nome do executável (ex.: "mspaint.exe") ou AppUserModelID de apps que não devem entrar
    /// no mosaico sozinhos ao abrir — pensados pra um tamanho fixo, não pra ser espremidos num
    /// pedaço do grid. Continuam alcançáveis manualmente pelo Alt+C, igual uma janela "teimosa".
    ///
    /// A Calculadora entra pelo AUMID, não pelo nome do executável: a janela dela pertence ao
    /// <c>ApplicationFrameHost.exe</c>, que hospeda vários apps "modernos" do Windows — só o
    /// AUMID distingue um do outro (ver <see cref="TaskWindow.AppKey"/>).
    /// </summary>
    public List<string> TilingExcludedApps { get; set; } = new() { "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };

    /// <summary>A lista em si não passa pelo <see cref="Set{T}"/> (é o mesmo objeto sendo
    /// editado, não substituído) — quem adiciona ou remove um item chama isto pra avisar o
    /// mosaico na hora, em vez de esperar o próximo evento de janela acontecer por conta
    /// própria.</summary>
    public void NotifyTilingExcludedAppsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TilingExcludedApps)));

    /// <summary>
    /// A combinação escolhida para cada atalho, pelo nome da ação (<see cref="HotkeyAction"/>):
    /// "Alt+Shift" nas de setas, "Alt+C" nas de uma tecla, vazio para desligado. Só guarda o que
    /// a pessoa mudou — ação ausente usa o padrão do <see cref="HotkeyCatalog"/>, que são os
    /// atalhos de antes desta opção existir, e quem nunca abriu o painel não vê diferença.
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>O mesmo aviso da lista de exceções: o dicionário é editado, não substituído, e
    /// quem registra os atalhos precisa saber na hora.</summary>
    public void NotifyHotkeysChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hotkeys)));

    /// <summary>
    /// O tamanho que cada app usa quando está flutuando (Alt+C), em pixels, pela mesma chave da
    /// lista de exceções: nome do executável, ou AppUserModelID quando a janela tem um — o que
    /// dá tamanhos separados para cada perfil do Chrome, por exemplo.
    ///
    /// Só o tamanho, nunca a posição: a janela flutuante nasce e volta sempre ao centro do
    /// monitor onde está. Quem não tem tamanho guardado usa os 60% da área útil de sempre.
    /// </summary>
    public Dictionary<string, FloatingSize> FloatingSizes { get; set; } = new();

    /// <summary>
    /// Anotações do calendário: a data no formato <c>yyyy-MM-dd</c> e o que foi anotado nela.
    ///
    /// A chave é texto, e não <c>DateTime</c>, porque é o que sobrevive a um JSON legível e
    /// editável à mão — e porque data com hora dentro de dicionário vira armadilha (o mesmo
    /// dia guardado duas vezes por causa de um horário diferente).
    ///
    /// O valor é uma lista porque um dia tem mais de um compromisso. Quem já tinha anotações
    /// gravadas quando isto era um texto só não perde nada: o conversor lê os dois formatos
    /// (ver <see cref="CalendarNotesConverter"/>) e a primeira gravação passa tudo para lista.
    /// </summary>
    [JsonConverter(typeof(CalendarNotesConverter))]
    public Dictionary<string, List<CalendarNote>> CalendarNotes { get; set; } = new();

    private int _calendarDoneRetentionDays = 2;
    /// <summary>
    /// Depois de quantos dias uma tarefa concluída do calendário se apaga sozinha, contando de
    /// quando foi marcada como feita (<see cref="CalendarNote.DoneAt"/>). Zero: nunca.
    /// </summary>
    public int CalendarDoneRetentionDays
    {
        get => _calendarDoneRetentionDays;
        set => Set(ref _calendarDoneRetentionDays, Clamp(value, 0, 365));
    }

    /// <summary>Apps fixados, na ordem em que aparecem.</summary>
    public List<PinnedApp> Pinned { get; set; } = new();

    // ── persistencia ────────────────────────────────────────
    /// <summary>A pasta do usuario onde ficam o config e o log.</summary>
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DockConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(FilePath), Opts)
                             ?? new DockConfig();
                config.MigrateEdgeMargin();
                return config;
            }
        }
        catch { /* config corrompida: volta ao padrao em vez de nao abrir */ }
        return new DockConfig();
    }

    /// <summary>
    /// A folga da borda virou quatro margens. Quem ja usava a antiga nao perde o ajuste:
    /// o valor vai para o lado em que a dock esta encostada.
    /// </summary>
    private void MigrateEdgeMargin()
    {
        if (EdgeMargin <= 0) return;
        if (MarginTop + MarginBottom + MarginLeft + MarginRight > 0) { EdgeMargin = 0; return; }

        switch (Edge)
        {
            case DockEdge.Bottom: MarginBottom = EdgeMargin; break;
            case DockEdge.Top:    MarginTop = EdgeMargin; break;
            case DockEdge.Left:   MarginLeft = EdgeMargin; break;
            default:              MarginRight = EdgeMargin; break;
        }

        EdgeMargin = 0;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    /// <summary>Volta tudo ao padrao, menos os apps fixados.</summary>
    public void ResetAppearance()
    {
        var d = new DockConfig();
        Edge = d.Edge; Size = d.Size; ReserveSpace = d.ReserveSpace; Centered = d.Centered;
        Background = d.Background; Opacity = d.Opacity; CornerRadius = d.CornerRadius;
        IndicatorColor = d.IndicatorColor;
        IconPadding = d.IconPadding; PillPadding = d.PillPadding; IconSize = d.IconSize;
        MarginTop = d.MarginTop; MarginBottom = d.MarginBottom;
        MarginLeft = d.MarginLeft; MarginRight = d.MarginRight;
        ShowRunning = d.ShowRunning; Taskbar = d.Taskbar; Launcher = d.Launcher;
        NumberHotkeys = d.NumberHotkeys;
        PanelBackground = d.PanelBackground; PanelOpacity = d.PanelOpacity;
        PanelCenterClock = d.PanelCenterClock;
        CalendarDoneRetentionDays = d.CalendarDoneRetentionDays;
        PanelTray = d.PanelTray; PanelAppVolume = d.PanelAppVolume; PanelMedia = d.PanelMedia;
        PanelBrightness = d.PanelBrightness;
        Panel = d.Panel; PanelSize = d.PanelSize;
        TilingEnabled = d.TilingEnabled; TilingGap = d.TilingGap;
        TilingBorderColor = d.TilingBorderColor; TilingBorderThickness = d.TilingBorderThickness;
        TilingBorderRadius = d.TilingBorderRadius; TilingResizeStep = d.TilingResizeStep;
        Hotkeys.Clear(); NotifyHotkeysChanged();
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
