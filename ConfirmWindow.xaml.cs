using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// A caixa de pergunta do WinDock, no desenho do Windows 11 e no tema do Windows.
///
/// Substitui o <c>MessageBox</c>: a caixa do WPF ainda é a do Windows XP por dentro — moldura
/// cinza, fonte pequena, ícone amarelo — e num Windows 11 ela aparece como se fosse de outro
/// programa. Aqui a barra de título é a do sistema (inclusive escura, quando o tema é escuro),
/// a instrução principal vem em corpo grande e os botões ficam numa faixa própria no rodapé,
/// que é a silhueta das caixas do sistema.
///
/// O tema vem do Windows e não da dock (veja <see cref="WindowsTheme"/>): esta janela nasce no
/// meio da tela, longe da barra, e é a única parte do WinDock que a pessoa vê como uma caixa
/// do sistema.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string heading, string body, string accept, string cancel)
    {
        InitializeComponent();

        Heading.Text = heading;
        Body.Text = body;
        Body.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;

        AcceptButton.Content = accept;
        CancelBtn.Content = cancel;
        CancelBtn.Visibility = string.IsNullOrEmpty(cancel) ? Visibility.Collapsed : Visibility.Visible;

        ApplyTheme();
        SourceInitialized += OnSourceInitialized;
    }

    private bool _dark;

    /// <summary>
    /// Põe a paleta do tema atual nos recursos da janela.
    ///
    /// Os valores são os do próprio Windows 11 — o cinza #202020 do escuro e o #F3F3F3 do
    /// claro, com o rodapé um degrau adiante —, e a cor de destaque é a que a pessoa escolheu
    /// nas configurações dela.
    /// </summary>
    private void ApplyTheme()
    {
        _dark = WindowsTheme.IsDark;

        var accent = WindowsTheme.Accent(_dark);

        void Set(string key, Color color) => Resources[key] = new SolidColorBrush(color);

        Set("DialogSurface",       _dark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3));
        Set("DialogFooter",        _dark ? Rgb(0x1C, 0x1C, 0x1C) : Rgb(0xEE, 0xEE, 0xEE));
        Set("DialogDivider",       _dark ? Rgb(0x30, 0x30, 0x30) : Rgb(0xE0, 0xE0, 0xE0));
        Set("DialogText",          _dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1B, 0x1B, 0x1B));
        Set("DialogTextDim",       _dark ? Rgb(0xC5, 0xC5, 0xC5) : Rgb(0x5D, 0x5D, 0x5D));
        Set("DialogControl",       _dark ? Rgb(0x2D, 0x2D, 0x2D) : Rgb(0xFD, 0xFD, 0xFD));
        Set("DialogControlHover",  _dark ? Rgb(0x33, 0x33, 0x33) : Rgb(0xF6, 0xF6, 0xF6));
        Set("DialogControlBorder", _dark ? Rgb(0x3D, 0x3D, 0x3D) : Rgb(0xE0, 0xE0, 0xE0));
        Set("DialogAccent",        accent);
        Set("DialogOnAccent",      WindowsTheme.OnAccent(accent));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>
    /// A barra de título é do Windows, não do WPF: sem avisar o DWM ela viria branca por cima
    /// de uma caixa escura. É o mesmo aviso que o painel de configurações dá.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                              DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnAccept(object sender, RoutedEventArgs e) { DialogResult = true; }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    /// <summary>
    /// Pergunta, e devolve verdadeiro se a pessoa escolheu a ação principal.
    /// </summary>
    /// <param name="owner">
    /// A janela dona. Importa: a dock é topmost, e sem dono a caixa nasceria **por baixo**
    /// dela — a pessoa clicaria em "Forçar fechamento" e não veria pergunta nenhuma.
    /// </param>
    public static bool Ask(Window owner, string heading, string body,
                           string accept = "Sim", string cancel = "Cancelar")
    {
        var dialog = new ConfirmWindow(heading, body, accept, cancel)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        dialog.Loaded += (_, _) =>
        {
            // A dona é a dock, que é `WS_EX_NOACTIVATE`: nada que ela abre vem para a frente
            // com o teclado junto. Sem este empurrão a caixa aparece, mas o que a pessoa
            // digitar continua indo para o app anterior — e o Esc não fecharia nada.
            WindowService.Focus(new WindowInteropHelper(dialog).Handle);

            // O foco começa no "Cancelar": a caixa só aparece antes de algo que não se
            // desfaz, e um Enter dado por reflexo não pode ser o que mata o programa da
            // pessoa.
            dialog.CancelBtn.Focus();
        };

        return dialog.ShowDialog() == true;
    }

    /// <summary>Avisa, com um botão só — o equivalente ao <c>MessageBox</c> de OK.</summary>
    public static void Tell(Window? owner, string heading, string body)
    {
        var dialog = new ConfirmWindow(heading, body, "OK", string.Empty);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
    }
}
