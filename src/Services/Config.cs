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

    /// <summary>
    /// A hora do dia em que a tarefa acontece, ou vazio para a tarefa que é do dia e não de uma
    /// hora marcada. É o que separa "comprar pão" de "dentista às 14h": só a segunda tem quando
    /// avisar.
    /// </summary>
    private TimeSpan? _at;
    public TimeSpan? At
    {
        get => _at;
        set
        {
            if (_at == value) return;
            _at = value;
            Raise(nameof(At));
            Raise(nameof(TimeText));
            Raise(nameof(HasTime));
        }
    }

    /// <summary>"14:30", ou vazio quando a tarefa não tem hora.</summary>
    [JsonIgnore]
    public string TimeText => _at is { } h ? h.ToString(@"hh\:mm") : string.Empty;

    [JsonIgnore]
    public bool HasTime => _at is not null;

    /// <summary>
    /// Avisar quando chegar a hora — ou o começo do dia, na tarefa que não tem hora.
    ///
    /// É por tarefa, e não uma chave geral: a lista de um dia tem de tudo, o que precisa
    /// interromper e o que só precisa estar escrito.
    /// </summary>
    private bool _notify;
    public bool Notify
    {
        get => _notify;
        set
        {
            if (_notify == value) return;
            _notify = value;
            Raise(nameof(Notify));
        }
    }

    /// <summary>
    /// Quando o aviso desta tarefa já saiu. Existe para ele sair uma vez só: sem isto, o relógio
    /// que varre a lista avisaria de novo a cada passagem, para sempre.
    ///
    /// Mudar o texto, a hora ou remarcar o "avisar" limpa este campo — é a pessoa dizendo que a
    /// tarefa mudou, e o aviso da versão anterior não vale mais.
    /// </summary>
    public DateTime? NotifiedAt { get; set; }

    /// <summary>
    /// A tarefa está com o texto aberto para edição no cartão.
    ///
    /// Mora no objeto, e não no code-behind, porque quem desenha a linha é um <c>DataTemplate</c> e
    /// ele só enxerga o item. Não vai para o arquivo: é estado de tela, e a tela fecha.
    /// </summary>
    private bool _editing;
    [JsonIgnore]
    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            Raise(nameof(IsEditing));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
                            TimeSpan? hora = null;
                            var avisar = false;
                            DateTime? avisadoEm = null;

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
                                else if (string.Equals(campo, "At", StringComparison.OrdinalIgnoreCase) &&
                                         reader.TokenType == JsonTokenType.String &&
                                         TimeSpan.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                                           out var marcada))
                                    hora = marcada;
                                else if (string.Equals(campo, "Notify", StringComparison.OrdinalIgnoreCase))
                                    avisar = reader.TokenType == JsonTokenType.True;
                                else if (string.Equals(campo, "NotifiedAt", StringComparison.OrdinalIgnoreCase) &&
                                         reader.TokenType == JsonTokenType.String &&
                                         DateTime.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                                           System.Globalization.DateTimeStyles.RoundtripKind, out var avisado))
                                    avisadoEm = avisado;
                                else
                                    reader.Skip();
                            }

                            Add(itens, texto, feito, feitoEm, hora, avisar, avisadoEm);
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
    private static void Add(List<CalendarNote> itens, string? texto, bool feito, DateTime? feitoEm = null,
                            TimeSpan? hora = null, bool avisar = false, DateTime? avisadoEm = null)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        itens.Add(new CalendarNote
        {
            Text = texto.Trim(),
            Done = feito,
            DoneAt = feito ? feitoEm ?? DateTime.Now : null,
            At = hora,
            Notify = avisar,
            NotifiedAt = avisadoEm
        });
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

                // hora e aviso só vão ao arquivo quando existem: a tarefa comum continua com as
                // mesmas três linhas de antes, e o config não engorda por causa de um recurso que
                // a maioria das tarefas não usa
                if (item.At is { } hora)
                    writer.WriteString("At", hora.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
                if (item.Notify) writer.WriteBoolean("Notify", true);
                if (item.NotifiedAt is { } avisadoEm)
                    writer.WriteString("NotifiedAt", avisadoEm.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
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

/// <summary>
/// Quem tem um desenho escolhível nas Configurações — uma ferramenta, uma origem de notificação.
/// O <c>GlyphPicker</c> escreve em <see cref="Glyph"/> e <see cref="Color"/> (vazios = automático)
/// e mostra o que vale de fato, em <see cref="LookGlyph"/> e <see cref="LookFill"/>.
/// </summary>
public interface IGlyphChoice : INotifyPropertyChanged
{
    string Glyph { get; set; }
    string Color { get; set; }
    string LookGlyph { get; }
    System.Windows.Media.Brush LookFill { get; }
}

/// <summary>
/// De onde vem uma notificação, e o desenho dela no cartão do sino. A chave é o site, para o que
/// chega pelo navegador ("web.whatsapp.com" — o Chrome entrega tudo como "Google Chrome"), ou o
/// AppUserModelID, para um app.
///
/// <para>O nome é só o que a pessoa lê nas Configurações. Desenho e cor vazios: o automático, que
/// para os sites conhecidos (<c>NotificationService.Known</c>) é o deles e para o resto é o ícone
/// do app.</para>
/// </summary>
public sealed class NotificationSource : IGlyphChoice
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    private bool _show = true;
    /// <summary>
    /// Entra no sino (número e cartão). Desmarcada, a origem some só da dock: a notificação
    /// continua na Central do Windows, que é dela.
    /// </summary>
    public bool Show { get => _show; set { if (_show != value) { _show = value; Raise(nameof(Show)); } } }

    private bool _popup = true;
    /// <summary>Ganha o balão breve ao chegar (com <c>DockConfig.NotificationPopup</c> ligado).</summary>
    public bool Popup { get => _popup; set { if (_popup != value) { _popup = value; Raise(nameof(Popup)); } } }

    private string _glyph = string.Empty;
    public string Glyph
    {
        get => _glyph;
        set { if (Set(ref _glyph, value)) { Raise(nameof(LookGlyph)); Raise(nameof(LookFill)); } }
    }

    private string _color = string.Empty;
    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) Raise(nameof(LookFill)); }
    }

    // sem desenho, o cartão usa o ícone do app; aqui no seletor isso aparece como o sino
    [JsonIgnore] public string LookGlyph => NotificationService.Look(this).Glyph is { Length: > 0 } g ? g : "";
    [JsonIgnore] public System.Windows.Media.Brush LookFill => NotificationService.Look(this).Fill;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        value ??= string.Empty;
        if (field == value) return false;
        field = value;
        Raise(name!);
        return true;
    }
}

/// <summary>
/// Um comando do botão de ferramentas da barra: o nome que aparece no cartão e a linha de
/// comando que ele roda, como se digitada no Executar (Win+R) — "devmgmt.msc",
/// "control inetcpl.cpl", "mstsc /v:servidor".
/// </summary>
public sealed class ToolCommand : IGlyphChoice
{
    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _command = string.Empty;
    public string Command
    {
        get => _command;
        set
        {
            if (!Set(ref _command, value)) return;
            Raise(nameof(Problem));
            Raise(nameof(LookGlyph));
            Raise(nameof(LookFill));
        }
    }

    private string _glyph = string.Empty;
    /// <summary>
    /// O desenho escolhido à mão, pelo código do glifo na Segoe ("EA99"). Vazio: a dock escolhe
    /// pelo que o comando abre (veja <see cref="ToolsService.Look"/>).
    /// </summary>
    public string Glyph
    {
        get => _glyph;
        set { if (Set(ref _glyph, value)) { Raise(nameof(LookGlyph)); Raise(nameof(LookFill)); } }
    }

    private string _color = string.Empty;
    /// <summary>A cor escolhida à mão, em hex. Vazio: a da tabela, ou cinza.</summary>
    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) Raise(nameof(LookFill)); }
    }

    /// <summary>Por que o comando não vai abrir, quando dá para saber antes — vazio se está tudo certo.</summary>
    [JsonIgnore] public string Problem => ToolsService.Problem(Command) ?? string.Empty;

    /// <summary>O desenho que vale de fato: o escolhido ou o automático.</summary>
    [JsonIgnore] public string LookGlyph => ToolsService.Look(this).Glyph;
    [JsonIgnore] public System.Windows.Media.Brush LookFill => ToolsService.Look(this).Fill;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        value ??= string.Empty;
        if (field == value) return false;
        field = value;
        Raise(name!);
        return true;
    }
}

/// <summary>
/// Uma pasta do botão de locais da barra: o nome que aparece no cartão e o caminho que o
/// Explorador abre. O caminho aceita variável de ambiente ("%USERPROFILE%\Projetos"), pasta de
/// rede ("\\servidor\pasta") e os endereços do shell ("shell:Downloads").
/// </summary>
public sealed class PlaceEntry : IGlyphChoice
{
    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;
            Raise(nameof(Problem));
            Raise(nameof(LookGlyph));
            Raise(nameof(LookFill));
        }
    }

    private string _glyph = string.Empty;
    /// <summary>O desenho escolhido à mão ("E8B7"). Vazio: o da pasta conhecida, ou a pasta amarela.</summary>
    public string Glyph
    {
        get => _glyph;
        set { if (Set(ref _glyph, value)) { Raise(nameof(LookGlyph)); Raise(nameof(LookFill)); } }
    }

    private string _color = string.Empty;
    public string Color
    {
        get => _color;
        set { if (Set(ref _color, value)) Raise(nameof(LookFill)); }
    }

    /// <summary>Por que a pasta não vai abrir — vazio se ela existe.</summary>
    [JsonIgnore] public string Problem => PlacesService.Problem(Path) ?? string.Empty;

    [JsonIgnore] public string LookGlyph => PlacesService.Look(this).Glyph;
    [JsonIgnore] public System.Windows.Media.Brush LookFill => PlacesService.Look(this).Fill;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        value ??= string.Empty;
        if (field == value) return false;
        field = value;
        Raise(name!);
        return true;
    }
}

/// <summary>Onde e como a busca de aplicativos aparece.</summary>
public enum SearchStyle
{
    /// <summary>A caixa no meio da tela, com a lista para baixo — a de sempre.</summary>
    Center,

    /// <summary>
    /// Compacta, logo abaixo da lupa da barra — pela lupa e pelo Alt+Espaço. O Alt+Espaço já
    /// ficou no meio da tela neste modo, "onde o olho está"; o usuário pediu que ele seguisse a
    /// escolha (02/10/2026): quem pôs a busca no canto quer ela lá. Sem a lupa na barra, os dois
    /// abrem no meio.
    ///
    /// <para>Houve um terceiro, em faixa sobre a barra como o dmenu do Linux (02/10/2026). Mesmo
    /// com ícone e poucos resultados, o usuário preferiu ficar só com estes dois.</para>
    /// </summary>
    Button
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

    private bool _panelRemovable = true;
    /// <summary>
    /// O botão de remover pen-drive e HD externo com segurança.
    ///
    /// Ligada por padrão, e sem custo para quem nunca espeta nada: o item só existe na barra
    /// enquanto houver dispositivo externo montado — veja <c>PanelModel.HasRemovable</c>.
    /// A opção é para quem prefere não ter o botão nem quando há.
    /// </summary>
    public bool PanelRemovable { get => _panelRemovable; set => Set(ref _panelRemovable, value); }

    private bool _panelAiUsage;
    /// <summary>
    /// Quanto da cota do Claude já foi usada, num cartão da barra.
    ///
    /// Desligada por padrão: vale para quem usa o Claude Code, e só aparece quando há
    /// credencial dele nesta máquina. Os números vêm do mesmo endpoint que o
    /// <c>/usage</c> do Claude Code usa — veja <c>AiUsageService</c>, inclusive o que ele
    /// deliberadamente não faz com as credenciais.
    /// </summary>
    public bool PanelAiUsage { get => _panelAiUsage; set => Set(ref _panelAiUsage, value); }

    private bool _panelUpdates = true;
    /// <summary>
    /// O ícone de atualizações esperando — do Windows Update e do winget.
    ///
    /// Ligado por padrão, ao contrário da cota de IA: a cota vale para quem usa uma ferramenta
    /// específica, e atualização pendente é de toda máquina. O ícone só aparece quando há alguma,
    /// então ligado ele não ocupa espaço à toa.
    /// </summary>
    public bool PanelUpdates { get => _panelUpdates; set => Set(ref _panelUpdates, value); }

    private bool _panelSystem;
    /// <summary>
    /// CPU e memória na barra, em porcentagem, e o cartão com processador, memória e rede.
    ///
    /// Desligada por padrão: é um item que fica aceso o dia inteiro e mexe o tempo todo, e a
    /// barra já tem gente demais para ligá-lo na conta de quem nunca pediu. A medida é barata
    /// — duas chamadas do kernel por segundo, sem <c>PerformanceCounter</c> —, então ligá-la
    /// não pesa na máquina que ela mede; veja <c>SystemStatsService</c>.
    ///
    /// <para>A rede é medida junto e só aparece no cartão. Ela já teve item próprio na barra:
    /// dois números de velocidade mudando a cada segundo ocupavam mais espaço que qualquer
    /// outro item, para uma informação que só se olha quando se está procurando por ela.</para>
    /// </summary>
    public bool PanelSystem { get => _panelSystem; set => Set(ref _panelSystem, value); }

    private bool _panelCompact;
    /// <summary>
    /// Ícones da barra menores e mais juntos: o desenho cai de 15 para 12 px (a pilha da bateria
    /// encolhe na mesma proporção) e o respiro de cada botão cai pela metade. Só o espaço foi a
    /// primeira versão, e não bastou: ao lado do processador e da memória, de 11 px, wi-fi,
    /// bluetooth e sino continuavam parecendo do mesmo tamanho. Pedido em 01/10/2026, quando a barra passou a ter CPU e memória — os dois números
    /// cabem apertados, e o resto da fila parecia folgado ao lado deles.
    ///
    /// Desligada por padrão: o espaço largo é o alvo de clique de quem usa a barra com o mouse, e
    /// apertá-lo é escolha de quem prefere a barra curta.
    /// </summary>
    public bool PanelCompact { get => _panelCompact; set => Set(ref _panelCompact, value); }

    private bool _panelTools = true;
    /// <summary>
    /// O botão de ferramentas (a chave de boca) na barra, com os comandos de <see cref="Tools"/>.
    /// Ligado de fábrica: foi pedido junto com a lista, e a lista vem com exemplos que servem a
    /// quem mexe no Windows dos outros — limpeza de disco, opções da internet, gerenciador de
    /// dispositivos, área de trabalho remota.
    /// </summary>
    public bool PanelTools { get => _panelTools; set => Set(ref _panelTools, value); }

    /// <summary>
    /// Os comandos do botão de ferramentas, na ordem do cartão.
    ///
    /// Os exemplos de fábrica só entram para quem nunca teve a chave no config.json: uma lista
    /// esvaziada de propósito volta vazia, e não com os exemplos de novo.
    /// </summary>
    public List<ToolCommand> Tools { get; set; } = DefaultTools();

    public static List<ToolCommand> DefaultTools() =>
    [
        new() { Name = "Limpeza de disco", Command = "cleanmgr.exe" },
        new() { Name = "Opções da Internet", Command = "control.exe inetcpl.cpl" },
        new() { Name = "Gerenciador de dispositivos", Command = "devmgmt.msc" },
        new() { Name = "Área de trabalho remota", Command = "mstsc.exe" },
    ];

    /// <summary>A lista mudou por dentro — o mesmo caminho do <c>PanelOrder</c>.</summary>
    public void NotifyToolsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tools)));

    // ── o canto esquerdo da barra ────────────────────────────
    //
    // Busca e locais, pedidos juntos em 02/10/2026: o canto esquerdo era o único vazio da barra.

    private bool _panelSearch = true;
    /// <summary>A lupa da barra (no canto esquerdo, de fábrica): abre a mesma busca do Alt+Espaço.</summary>
    public bool PanelSearch { get => _panelSearch; set => Set(ref _panelSearch, value); }

    private SearchStyle _searchStyle = SearchStyle.Center;
    /// <summary>Onde e como a busca aparece — veja <see cref="Services.SearchStyle"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SearchStyle SearchStyle { get => _searchStyle; set => Set(ref _searchStyle, value); }

    private bool _panelPlaces = true;
    /// <summary>
    /// O botão "Locais" no canto esquerdo, com as pastas de <see cref="Places"/> — o menu de
    /// lugares do GNOME (referência EV27).
    /// </summary>
    public bool PanelPlaces { get => _panelPlaces; set => Set(ref _panelPlaces, value); }

    /// <summary>
    /// As pastas do botão de locais, na ordem do cartão. As de fábrica (as do usuário, como no
    /// GNOME) entram só para quem nunca teve a chave — a mesma regra das ferramentas.
    /// </summary>
    public List<PlaceEntry> Places { get; set; } = PlacesService.Defaults();

    public void NotifyPlacesChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Places)));

    /// <summary>
    /// As origens de notificação já vistas, com o desenho de cada uma no cartão do sino. A dock
    /// acrescenta sozinha cada origem nova que aparece na Central; WhatsApp e Gmail vêm de fábrica
    /// porque são as do pedido (02/10/2026) e assim dá para escolher antes da primeira mensagem.
    /// </summary>
    public List<NotificationSource> NotificationSources { get; set; } =
    [
        new() { Key = "web.whatsapp.com", Name = "WhatsApp" },
        new() { Key = "mail.google.com", Name = "Gmail" },
    ];

    private bool _notificationPopup = true;
    /// <summary>
    /// Um balão breve embaixo do sino quando chega uma notificação nova — o que a faixa do Windows
    /// faz, mas no lugar da barra e com o desenho da origem. Ligado de fábrica porque foi pedido
    /// (02/10/2026); quem deixa a faixa do Windows ligada vê as duas e pode desligar um.
    /// </summary>
    public bool NotificationPopup { get => _notificationPopup; set => Set(ref _notificationPopup, value); }

    private int _notificationPopupSeconds = 6;
    /// <summary>Quanto tempo o balão fica, em segundos; com o mouse em cima ele espera.</summary>
    public int NotificationPopupSeconds
    {
        get => _notificationPopupSeconds;
        set => Set(ref _notificationPopupSeconds, Clamp(value, 2, 60));
    }

    private bool _notificationPopupText = true;
    /// <summary>
    /// O balão mostra quem mandou e o texto. Desligado, só a origem e "nova mensagem" — para a
    /// tela que outra pessoa pode estar vendo.
    /// </summary>
    public bool NotificationPopupText { get => _notificationPopupText; set => Set(ref _notificationPopupText, value); }

    public void NotifyNotificationSourcesChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationSources)));

    private bool _panelSelfUpdate = true;
    /// <summary>
    /// Avisar na barra quando sai uma Release nova do WinDock — o ícone só existe enquanto houver
    /// uma. Ligado de fábrica: a consulta é uma por dia, e quem não quiser desliga.
    /// </summary>
    public bool PanelSelfUpdate { get => _panelSelfUpdate; set => Set(ref _panelSelfUpdate, value); }

    /// <summary>Quando o GitHub foi consultado pela última vez — a consulta é diária.</summary>
    public DateTime? SelfUpdateCheckedAt { get; set; }

    /// <summary>A tag da Release mais recente que o GitHub respondeu naquela consulta.</summary>
    public string SelfUpdateTag { get; set; } = string.Empty;

    /// <summary>
    /// Pacotes do winget que a pessoa mandou calar, pelo id (o nome, quando o pacote não tem id).
    ///
    /// Há pacote que o winget oferece e nunca atualiza — o Discord é o exemplo: ele se atualiza
    /// sozinho, por fora, e a versão que fica registrada não é a que o winget espera encontrar;
    /// a oferta reaparece na consulta seguinte, e na outra, e o ícone da barra acende todo dia
    /// por algo que nenhum comando resolve. Um ícone que avisa sempre deixa de avisar.
    ///
    /// Silenciar é decisão de quem usa, tomada pacote a pacote no cartão, e reversível ali mesmo.
    /// Guarda o id, e não a versão, de propósito: calar o Discord é calar o Discord, e não "esta
    /// versão do Discord" — se fosse por versão, o barulho voltaria a cada número novo, que é
    /// exatamente o que se está calando.
    /// </summary>
    public List<string> UpdatesSilenced { get; set; } = new();

    /// <summary>
    /// Quais contas do Claude Code o cartão acompanha, pelo nome da pasta de cada uma
    /// (".claude", ".claude-bm").
    ///
    /// **Vazia quer dizer todas**, e não nenhuma: é o que faz o cartão funcionar sem
    /// configuração nenhuma e o que faz uma conta nova aparecer sozinha quando ela passa a
    /// existir. Quem quiser só uma marca só ela.
    ///
    /// Guarda o nome da pasta, e não o e-mail, porque é a pasta que o Claude Code usa para
    /// separar as contas — o e-mail de dentro dela muda quando a pessoa troca de login ali,
    /// e a escolha continuaria valendo.
    /// </summary>
    public List<string> AiUsageAccounts { get; set; } = new();

    private bool _aiUsageExpanded;
    /// <summary>
    /// O cartão da cota mostra tudo (cada barra com a conta, o prazo e a hora em que zera) ou
    /// só o essencial (uma linha por janela de cota, com a barra ao lado do nome).
    ///
    /// Começa compacto porque o tamanho do cartão cresce com o número de contas: com duas, a
    /// versão detalhada passa de 400 px de altura. A seta no rodapé alterna, e o que a pessoa
    /// escolher fica — é preferência de leitura, não estado de sessão.
    /// </summary>
    public bool AiUsageExpanded { get => _aiUsageExpanded; set => Set(ref _aiUsageExpanded, value); }

    /// <summary>
    /// Os cartões da barra que a pessoa deixou detalhados, pela chave do item ("volume", "wifi",
    /// "bluetooth", "bateria"). Fora da lista, o cartão abre compacto — o mesmo jeito do da cota,
    /// numa lista só porque são quatro e podem vir mais.
    /// </summary>
    public List<string> CardsExpanded { get; set; } = new();

    /// <summary>
    /// Avisa que a lista acima mudou. A lista é um objeto só, então trocar o conteúdo dela
    /// não dispara o <c>PropertyChanged</c> sozinho — o mesmo caminho do <c>PanelOrder</c>.
    /// </summary>
    public void NotifyAiUsageAccountsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AiUsageAccounts)));

    private bool _trace;
    /// <summary>
    /// Registrar cada passo no log, e não só o que deu errado.
    ///
    /// Desligado, e não por economia de disco: cada linha custa quatro idas ao disco
    /// (<see cref="Log.Write"/>), síncronas na thread que chamou — que é a da interface na maior
    /// parte das vezes. Com o rastro ligado, uma janela que muda de título depressa (a caixa de
    /// progresso de uma descompactação, por exemplo) faz a dock escrever umas 150 linhas por
    /// segundo; se o disco estiver ocupado com a própria descompactação, é a barra que espera na
    /// fila do I/O. Medido nesta máquina, sob disco saturado: 104 ms de pico com rastro, 43 ms
    /// sem — o dobro da latência média.
    ///
    /// Vale ligar enquanto se investiga alguma coisa, e desligar depois. Um arquivo chamado
    /// <c>rastrear</c> na pasta de configuração liga do mesmo jeito, para o caso de a dock não
    /// chegar a abrir e não haver painel onde clicar.
    /// </summary>
    public bool Trace
    {
        get => _trace;
        set { Set(ref _trace, value); Log.Tracing = _trace; }
    }

    private bool _panelMedia = true;
    /// <summary>
    /// Mostrar na barra o que está tocando, com os controles de reprodução.
    ///
    /// Some sozinho quando não há mídia nenhuma; esta opção é para quem não quer o item nem
    /// quando há.
    /// </summary>
    public bool PanelMedia { get => _panelMedia; set => Set(ref _panelMedia, value); }

    private bool _panelMediaTicker = true;
    /// <summary>
    /// O nome da faixa rolando quando não cabe no espaço da barra.
    ///
    /// Ligado por padrão: sem isto, "Sinfonia nº 9 em Ré menor, Op. 125 — IV. Presto" vira
    /// "Sinfonia nº 9 em Ré me…" e a informação que interessa é justamente a que some. A
    /// rolagem só acontece com o texto grande demais <b>e</b> com alguma coisa tocando — em
    /// pausa ela para, para não haver movimento na tela por nada.
    /// </summary>
    public bool PanelMediaTicker { get => _panelMediaTicker; set => Set(ref _panelMediaTicker, value); }

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

    private bool _panelIslands;
    /// <summary>
    /// A barra em ilhas: a faixa some e ficam só pílulas — o relógio numa, os ícones do canto
    /// direito em uma ou mais (veja <see cref="PanelIslandBreaks"/>), com o fundo da área de
    /// trabalho aparecendo entre elas. Pedido de 01/10/2026, a partir de um mod do Windhawk
    /// que faz o mesmo com a barra do Windows.
    ///
    /// Desligada de fábrica: a faixa inteira é o desenho de quem nunca mexeu, e é a que tapa o
    /// que estiver atrás. A AppBar continua reservando a altura toda — as janelas maximizadas
    /// não sobem para o vão entre as ilhas.
    /// </summary>
    public bool PanelIslands { get => _panelIslands; set => Set(ref _panelIslands, value); }

    /// <summary>
    /// Os itens que abrem uma ilha nova, pela chave do <see cref="PanelOrder"/>: a ilha começa
    /// neles e vai até o próximo da lista. Vazia: o canto direito é uma ilha só.
    /// Guarda a chave, e não a posição, para sobreviver a uma reordenação.
    /// </summary>
    public List<string> PanelIslandBreaks { get; set; } = new();

    // As folgas das ilhas, em pixels — o mesmo par de opções que a pílula da dock tem. Pedido
    // de 01/10/2026: com 2 px em cima e embaixo, a ilha ficava descolada do topo da tela, e
    // quem quer a pílula encostada (ou mais solta) não tinha onde mexer.
    private int _islandMarginTop = 2;
    public int IslandMarginTop { get => _islandMarginTop; set => Set(ref _islandMarginTop, Clamp(value, 0, 20)); }

    private int _islandMarginBottom = 2;
    public int IslandMarginBottom { get => _islandMarginBottom; set => Set(ref _islandMarginBottom, Clamp(value, 0, 20)); }

    /// <summary>Da borda da tela até a primeira ilha, e da última até a outra borda.</summary>
    private int _islandMarginLeft = 4;
    public int IslandMarginLeft { get => _islandMarginLeft; set => Set(ref _islandMarginLeft, Clamp(value, 0, 200)); }

    private int _islandMarginRight = 4;
    public int IslandMarginRight { get => _islandMarginRight; set => Set(ref _islandMarginRight, Clamp(value, 0, 200)); }

    /// <summary>O vão entre uma ilha e a vizinha.</summary>
    private int _islandGap = 6;
    public int IslandGap { get => _islandGap; set => Set(ref _islandGap, Clamp(value, 0, 60)); }

    public void NotifyPanelIslandBreaksChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PanelIslandBreaks)));

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

    /// <summary>
    /// Com quantos ícones na bandeja já se sabe que **algum deles nunca ganha nome**, ou -1
    /// enquanto não se sabe.
    ///
    /// Não é preferência de ninguém: é uma medição que custa 400 ms e que, sem isto, era
    /// refeita a cada sessão. A espera pela lista do painel dá um prazo para os nomes
    /// chegarem, e há ícones que não vão ganhar nome nunca (o Discord desta máquina não
    /// publica dica de mouse) — esperar por eles é pagar o prazo inteiro na primeira leitura
    /// de todo dia. Sabendo de antemão com que contagem isso acontece, a espera é dispensada.
    ///
    /// A contagem faz parte da chave de propósito: se a bandeja mudar de composição, o que se
    /// aprendeu não vale mais e a espera acontece de novo. Veja <c>TrayService.AnonymousAt</c>.
    /// </summary>
    public int TrayAnonymousAt { get; set; } = -1;

    // ── mosaico (tiling window manager) ──────────────────────
    private bool _tilingEnabled;
    /// <summary>Liga o mosaico no monitor principal — atalhos e reorganizacao automatica.</summary>
    public bool TilingEnabled { get => _tilingEnabled; set => Set(ref _tilingEnabled, value); }

    private bool _tilingFloatNewWindows;
    /// <summary>
    /// Janela nova nasce flutuando (o que o Alt+C faria nela), em vez de entrar no grid: o
    /// tamanho guardado deste app em <see cref="FloatingSizes"/>, ou 60% da tela na primeira vez,
    /// centralizada. Dividir a tela passa a ser o gesto deliberado — o mesmo Alt+C a devolve ao
    /// mosaico.
    ///
    /// Vale só para as janelas que aparecerem daí em diante. As que já estavam abertas quando o
    /// mosaico ligou continuam no grid: elas já tinham lugar, e jogá-las todas para o centro
    /// empilharia a área de trabalho inteira a cada arranque da dock.
    /// </summary>
    public bool TilingFloatNewWindows { get => _tilingFloatNewWindows; set => Set(ref _tilingFloatNewWindows, value); }

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

                // Daqui em diante quem manda no rastro é a configuração, mesmo quando o JSON não
                // traz a chave: sem isto, uma config sem "Trace" deixaria valendo o arquivo
                // "rastrear", e o interruptor do painel apareceria desligado com o rastro ligado.
                Log.Tracing = config.Trace;
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
        PanelBrightness = d.PanelBrightness; PanelAiUsage = d.PanelAiUsage;
        PanelUpdates = d.PanelUpdates;
        PanelSystem = d.PanelSystem;
        PanelCompact = d.PanelCompact;
        PanelSelfUpdate = d.PanelSelfUpdate;
        PanelTools = d.PanelTools;
        PanelSearch = d.PanelSearch; SearchStyle = d.SearchStyle;
        PanelPlaces = d.PanelPlaces;
        PanelIslands = d.PanelIslands;
        IslandMarginTop = d.IslandMarginTop; IslandMarginBottom = d.IslandMarginBottom;
        IslandMarginLeft = d.IslandMarginLeft; IslandMarginRight = d.IslandMarginRight;
        IslandGap = d.IslandGap;
        PanelMediaTicker = d.PanelMediaTicker;
        PanelRemovable = d.PanelRemovable;
        Trace = d.Trace;
        Panel = d.Panel; PanelSize = d.PanelSize;
        TilingEnabled = d.TilingEnabled; TilingGap = d.TilingGap;
        TilingFloatNewWindows = d.TilingFloatNewWindows;
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
