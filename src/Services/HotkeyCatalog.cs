namespace WinDock.Services;

/// <summary>
/// As ações que têm atalho configurável. O nome vira a chave no <c>config.json</c> — renomear um
/// valor daqui faz quem já tinha escolhido voltar ao padrão.
/// </summary>
public enum HotkeyAction { Focus, Swap, Resize, Float, TileAll, ForgetSize, Hide, Close, Topmost, Media, Settings }

/// <summary>O que aconteceu ao registrar o atalho de uma ação.</summary>
public enum HotkeyState { Active, Off, TilingOff, TopBarOff, InUse, Duplicate }

/// <param name="Arrows">A ação é das setas, e o que se escolhe é só o modificador. Quais setas ela
/// usa é de quem registra — a de música deixa a ↓ livre para os programas.</param>
/// <param name="Tiling">Só existe com o mosaico ligado.</param>
/// <param name="Default">A combinação de antes de o atalho ser configurável.</param>
/// <param name="TopBar">Só existe com a barra superior ligada — é nela que mora o que a ação comanda.</param>
public sealed record HotkeyInfo(HotkeyAction Action, string Title, string Caption, bool Arrows, bool Tiling, string Default,
                                bool TopBar = false);

/// <summary>
/// O catálogo dos atalhos configuráveis: o que cada ação faz, o padrão e as combinações que o
/// painel oferece.
///
/// A escolha é entre uma lista fechada, e não qualquer tecla, porque um hotkey global rouba a
/// combinação do sistema inteiro. Ficaram de fora, medido nesta máquina com <c>RegisterHotKey</c>:
///
/// - **Win + qualquer coisa** — o Explorer registrou a família inteira antes (erro 1409), e o
///   atalho simplesmente não pegaria;
/// - **Alt + setas** — livre para registrar, mas é o voltar/avançar do navegador e do Explorer;
/// - **Shift + setas** sem outro modificador — quebra a seleção de texto em todo programa (o
///   projeto já passou por isso; veja o <c>docs/APRENDIZADOS.md</c>, seção do mosaico).
///
/// Alt+Shift entra na lista com um aviso, e não por descuido: Alt esquerdo + Shift é a troca de
/// layout do teclado do Windows quando há mais de um instalado. O Windows só troca quando os dois
/// são soltos sem outra tecla no meio, então Alt+Shift+seta não deveria disparar a troca — mas é
/// a pessoa, no teclado dela, quem confirma.
///
/// Nada aqui fala com o Windows: é texto entrando e flags saindo, para poder ser testado sozinho.
/// </summary>
public static class HotkeyCatalog
{
    // os mesmos valores de MOD_ALT, MOD_CONTROL e MOD_SHIFT do Win32 (Interop/Native.cs)
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;

    /// <summary>Na ordem em que aparecem no painel — e, havendo repetição, quem vem antes fica
    /// com a tecla.</summary>
    public static readonly IReadOnlyList<HotkeyInfo> All = new HotkeyInfo[]
    {
        new(HotkeyAction.Focus, "Mudar o foco",
            "Para a janela vizinha, ou para o outro monitor na ponta do grid",
            Arrows: true, Tiling: true, "Ctrl+Alt"),
        new(HotkeyAction.Swap, "Trocar de lugar",
            "A janela em foco troca com a vizinha, ou vai para o outro monitor",
            Arrows: true, Tiling: true, "Ctrl+Shift"),
        new(HotkeyAction.Resize, "Redimensionar",
            "A janela em foco cresce às custas da vizinha",
            Arrows: true, Tiling: true, "Ctrl+Alt+Shift"),
        new(HotkeyAction.Float, "Flutuar",
            "Tira do mosaico, centralizada; fora do lugar, recentraliza; centralizada, devolve",
            Arrows: false, Tiling: true, "Alt+C"),
        // o par do "Flutuar", e por isso na mesma tecla com um modificador a mais: com o "abrir
        // flutuando" ligado, nenhuma janela entra no grid sozinha, e devolver uma a uma seria um
        // atalho por janela — com a dela em foco antes
        new(HotkeyAction.TileAll, "Devolver todas ao mosaico",
            "As janelas que abriram flutuando entram no grid de uma vez; as que você mandou flutuar ficam onde estão",
            Arrows: false, Tiling: true, "Alt+Shift+C"),
        new(HotkeyAction.ForgetSize, "Esquecer tamanho",
            "A janela flutuante em foco volta aos 60% da tela",
            Arrows: false, Tiling: true, "Ctrl+Alt+C"),
        new(HotkeyAction.Hide, "Esconder",
            "Minimiza a janela em foco",
            Arrows: false, Tiling: true, "Alt+Z"),
        new(HotkeyAction.Close, "Fechar",
            "Fecha a janela em foco, como o X dela",
            Arrows: false, Tiling: true, "Alt+W"),
        // fora do grupo do mosaico de propósito: prender uma janela acima das outras não tem a ver
        // com o grid, e quem desliga o mosaico continua com o recurso
        new(HotkeyAction.Topmost, "Prender acima",
            "Deixa a janela em foco acima das outras; de novo, solta. Vale com o mosaico desligado",
            Arrows: false, Tiling: false, "Alt+T"),
        // Alt+Shift porque era o único modificador de setas ainda livre com os padrões do mosaico.
        // Os comandos são os mesmos dos botões da barra, e por isso seguem o filtro de programas
        // da barra de mídia: com só o Spotify marcado, um vídeo tocando no navegador não é afetado
        new(HotkeyAction.Media, "Música",
            "← anterior, → próxima, ↑ tocar ou pausar, no programa que a barra de mídia mostra",
            Arrows: true, Tiling: false, "Alt+Shift", TopBar: true),
        // Ctrl+Alt como o "Esquecer tamanho": é a família dos atalhos que mexem na própria dock, e
        // Alt+letra sozinho brigaria com os menus dos programas (Alt+S costuma ser um deles)
        new(HotkeyAction.Settings, "Configurações",
            "Abre este painel; se já estiver aberto, traz para a frente",
            Arrows: false, Tiling: false, "Ctrl+Alt+S"),
    };

    /// <summary>Modificadores oferecidos para as ações de setas.</summary>
    public static readonly IReadOnlyList<string> ArrowModifiers = ["Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Ctrl+Alt+Shift"];

    /// <summary>Modificadores oferecidos para as ações de uma tecla.</summary>
    public static readonly IReadOnlyList<string> KeyModifiers = ["Alt", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift"];

    /// <summary>As teclas das ações de uma tecla: só letras, que têm o código virtual igual ao
    /// próprio caractere maiúsculo.</summary>
    public static readonly IReadOnlyList<string> Letters =
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).ToList();

    public static HotkeyInfo Info(HotkeyAction action) => All.First(i => i.Action == action);

    /// <summary>
    /// A combinação em uso para a ação: a escolhida, quando é uma das que o painel oferece; vazio,
    /// quando foi desligada; o padrão, quando não há escolha ou quando o valor não é reconhecido
    /// (um <c>config.json</c> editado à mão, por exemplo).
    /// </summary>
    public static string Of(IReadOnlyDictionary<string, string> chosen, HotkeyAction action)
    {
        var info = Info(action);
        if (!chosen.TryGetValue(action.ToString(), out var text)) return info.Default;
        return Normalize(text, info.Arrows) ?? info.Default;
    }

    /// <summary>
    /// Põe a combinação na forma de sempre — modificadores na ordem Ctrl, Alt, Shift e a letra
    /// maiúscula no fim —, aceitando maiúsculas, espaços e ordem trocada. Devolve vazio para
    /// "desligado" e <c>null</c> para o que o painel não ofereceria.
    /// </summary>
    public static string? Normalize(string? text, bool arrows)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        uint mods = 0;
        string? letra = null;

        foreach (var parte in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (parte.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                default:
                    if (letra is not null || parte.Length != 1 || !char.IsAsciiLetter(parte[0])) return null;
                    letra = parte.ToUpperInvariant();
                    break;
            }
        }

        var modificador = FormatModifiers(mods);

        if (arrows) return letra is null && ArrowModifiers.Contains(modificador) ? modificador : null;
        return letra is not null && KeyModifiers.Contains(modificador) ? $"{modificador}+{letra}" : null;
    }

    /// <summary>
    /// As flags e a tecla para o <c>RegisterHotKey</c>. Nas ações de setas <paramref name="key"/>
    /// volta zero — a seta é de quem registra, uma por direção. Falso para desligado e para o que
    /// não é reconhecido.
    /// </summary>
    public static bool TryParse(string combo, bool arrows, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        var normal = Normalize(combo, arrows);
        if (string.IsNullOrEmpty(normal)) return false;

        foreach (var parte in normal.Split('+'))
        {
            switch (parte)
            {
                case "Ctrl": modifiers |= ModControl; break;
                case "Alt": modifiers |= ModAlt; break;
                case "Shift": modifiers |= ModShift; break;
                default: key = parte[0]; break;
            }
        }

        return true;
    }

    /// <summary>
    /// Ações cuja combinação repete a de outra que vem antes em <see cref="All"/>. O painel não
    /// deixa isso acontecer — escolher a combinação de outra ação troca as duas de lugar —, então
    /// só aparece com o <c>config.json</c> editado à mão. Desligado não repete nada.
    /// </summary>
    public static HashSet<HotkeyAction> Duplicates(IReadOnlyDictionary<string, string> chosen)
    {
        var vistas = new HashSet<string>();
        var repetidas = new HashSet<HotkeyAction>();

        foreach (var info in All)
        {
            var combo = Of(chosen, info.Action);
            if (combo.Length == 0) continue;
            if (!vistas.Add((info.Arrows ? "setas:" : "tecla:") + combo)) repetidas.Add(info.Action);
        }

        return repetidas;
    }

    /// <summary>Como a combinação aparece para a pessoa: "Ctrl + Alt + setas", "Alt + C".</summary>
    public static string Display(string combo, bool arrows) =>
        combo.Length == 0 ? "desligado" : combo.Replace("+", " + ") + (arrows ? " + setas" : string.Empty);

    // ── o que o Windows respondeu ────────────────────────────

    private static Dictionary<HotkeyAction, HotkeyState> _states = new();

    /// <summary>Avisa que <see cref="StateOf"/> mudou — quem registra os atalhos é a dock, e quem
    /// mostra o resultado é o painel.</summary>
    public static event Action? StatesChanged;

    public static HotkeyState StateOf(HotkeyAction action) =>
        _states.TryGetValue(action, out var state) ? state : HotkeyState.Off;

    public static void Publish(Dictionary<HotkeyAction, HotkeyState> states)
    {
        _states = states;
        StatesChanged?.Invoke();
    }

    private static string FormatModifiers(uint mods)
    {
        var partes = new List<string>(3);
        if ((mods & ModControl) != 0) partes.Add("Ctrl");
        if ((mods & ModAlt) != 0) partes.Add("Alt");
        if ((mods & ModShift) != 0) partes.Add("Shift");
        return string.Join("+", partes);
    }
}
