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
        PanelBackground = d.PanelBackground; PanelOpacity = d.PanelOpacity;
        PanelCenterClock = d.PanelCenterClock;
        PanelTray = d.PanelTray; PanelAppVolume = d.PanelAppVolume; PanelMedia = d.PanelMedia;
        Panel = d.Panel; PanelSize = d.PanelSize;
        TilingEnabled = d.TilingEnabled; TilingGap = d.TilingGap;
        TilingBorderColor = d.TilingBorderColor; TilingBorderThickness = d.TilingBorderThickness;
        TilingBorderRadius = d.TilingBorderRadius; TilingResizeStep = d.TilingResizeStep;
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
