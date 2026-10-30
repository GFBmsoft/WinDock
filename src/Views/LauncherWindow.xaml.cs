using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// O launcher: Alt+Espaco, digita, Enter. Existe uma instancia so, escondida entre um uso
/// e outro — reabrir e mais rapido que recriar, e a lista de apps ja fica quente.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly Launcher _launcher;
    private readonly DockConfig _config;

    /// <summary>
    /// Altura de uma linha da lista, em DIPs — o ícone de 26 px mais o respiro do estilo
    /// `Row` (padding de 7 em cima e embaixo) e a margem entre elas. É a conta que dava os
    /// 420 px que o XAML fixava para nove itens, agora feita para o limite escolhido.
    /// </summary>
    private const double RowHeight = 46;

    /// <summary>A mesma conta na linha compacta: ícone de 18 e respiro de 4 em cima e embaixo.</summary>
    private const double CompactRowHeight = 28;

    private double _rowHeight = RowHeight;

    public LauncherWindow(Launcher launcher, DockConfig config)
    {
        _launcher = launcher;
        _config = config;
        InitializeComponent();

        _debounce.Tick += (_, _) => Refresh();
        Deactivated += (_, _) => Hide();   // clicou fora: some, como o menu iniciar
    }

    /// <summary>
    /// Mostra com a busca limpa: centralizada na tela onde o mouse esta, ou — com
    /// <paramref name="below"/>, o ponto da tela logo abaixo da lupa da barra — compacta ali.
    /// </summary>
    public void Open(Point? below = null)
    {
        ApplyLook(compacta: below is not null);
        IconService.RefreshShell();

        Query.Text = string.Empty;
        Refresh();

        Show();
        if (below is { } ponto) PositionBelow(ponto);
        else PositionOnActiveScreen();

        // o Show() sozinho mostra a janela mas deixa o teclado no app anterior: sem esse
        // empurrao o que a pessoa digitar vai parar la, e nao no campo de busca
        Activate();
        WindowService.Focus(new WindowInteropHelper(this).Handle);

        Query.Focus();
        Keyboard.Focus(Query);
    }

    /// <summary>
    /// Compacta: mais estreita e com a letra do campo menor. Embaixo da lupa ela é um cartão da
    /// barra, e os 560 px do meio da tela pareciam uma janela caída no canto. Por isso também
    /// veste o desenho dos cartões dela (fundo, borda e cantos de 10) e a linha tem uma altura
    /// só, com o "de onde veio" ao lado do nome.
    /// </summary>
    private void ApplyLook(bool compacta)
    {
        Width = compacta ? 380 : 560;
        Query.FontSize = Hint.FontSize = compacta ? 14 : 20;
        Query.Padding = compacta ? new Thickness(8, 5, 8, 5) : new Thickness(10, 8, 10, 8);
        Hint.Padding = compacta ? new Thickness(10, 5, 10, 5) : new Thickness(12, 8, 12, 8);
        Hint.Text = compacta ? "Buscar…  ou  run <comando>"
                             : "Buscar aplicativo, pasta ou configuração…  ou  run <comando>";

        Card.Padding = new Thickness(compacta ? 6 : 8);
        Card.CornerRadius = new CornerRadius(compacta ? 10 : 14);
        Card.Background = compacta ? CompactBackground : NormalBackground;
        Card.BorderBrush = compacta ? CompactBorder : NormalBorder;
        QueryRow.Margin = new Thickness(0, 0, 0, compacta ? 4 : 6);
        Divider.Margin = compacta ? new Thickness(4, 0, 4, 4) : new Thickness(6, 0, 6, 6);

        Results.ItemContainerStyle = (Style)FindResource(compacta ? "RowCompact" : "Row");
        Results.ItemTemplate = (DataTemplate)FindResource(compacta ? "ItemCompact" : "ItemNormal");
        _rowHeight = compacta ? CompactRowHeight : RowHeight;
    }

    /// <summary>
    /// Abre na tela onde o mouse esta: centralizado na horizontal e um pouco acima do meio,
    /// que e onde o olho ja esta e deixa a lista crescer para baixo sem sair da tela.
    /// </summary>
    private void PositionOnActiveScreen()
    {
        if (!GetCursorPos(out var cursor)) return;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info)) return;

        // as coordenadas do Win32 sao em pixels e as do WPF em DIPs: sem dividir pela
        // escala a janela erra o lugar em monitor com DPI alto
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        var scaleX = transform?.M11 ?? 1;
        var scaleY = transform?.M22 ?? 1;

        var work = info.rcWork;
        Left = (work.Left + (work.Width - Width * scaleX) / 2) / scaleX;
        Top = (work.Top + work.Height * 0.22) / scaleY;
    }

    /// <summary>
    /// Encostada embaixo da lupa: a borda esquerda da caixa alinhada com a do botão e 6 px abaixo
    /// da barra, o mesmo respiro dos cartões dela.
    /// </summary>
    private void PositionBelow(Point ponto)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        var scaleX = transform?.M11 ?? 1;
        var scaleY = transform?.M22 ?? 1;

        Left = ponto.X / scaleX;
        Top = ponto.Y / scaleY + 6;
    }

    private void Refresh()
    {
        _debounce.Stop();

        var results = _launcher.Search(Query.Text, _config.LauncherResults);
        Results.ItemsSource = results;
        Results.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results.Count > 0) Results.SelectedIndex = 0;

        // A altura acompanha o limite escolhido, em vez do teto fixo que havia aqui: com um
        // limite maior, o teto antigo (420 px, uns nove itens) deixava o resto só alcançável
        // rolando — o oposto do que a pessoa pediu ao aumentar o número.
        //
        // O teto agora é a tela: metade da área útil, para a janela não virar uma coluna do
        // topo ao rodapé quando alguém pedir vinte resultados.
        Results.MaxHeight = Math.Min((results.Count + 1) * _rowHeight,
                                     SystemParameters.WorkArea.Height * 0.5);
    }

    // o fundo de sempre, e o dos cartões da barra (PanelWindow.xaml) para a compacta
    private static readonly System.Windows.Media.Brush NormalBackground = Frozen(0xF2, 0x1B, 0x1C, 0x1F);
    private static readonly System.Windows.Media.Brush NormalBorder = Frozen(0xFF, 0x34, 0x35, 0x3B);
    private static readonly System.Windows.Media.Brush CompactBackground = Frozen(0xF7, 0x24, 0x24, 0x24);
    private static readonly System.Windows.Media.Brush CompactBorder = Frozen(0x2E, 0xFF, 0xFF, 0xFF);

    private static System.Windows.Media.Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Uma pausa curta entre a tecla e a busca. Digitando "chrome" a lista era refeita seis
    /// vezes, cinco delas para um texto que ja nao esta mais no campo.
    /// </summary>
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(70) };

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;

            // as setas navegam a lista sem tirar o cursor do campo de texto
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (Results.Items.Count == 0) return;

        var next = Results.SelectedIndex + delta;
        if (next < 0) next = Results.Items.Count - 1;
        if (next >= Results.Items.Count) next = 0;

        Results.SelectedIndex = next;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e) => RunSelected();

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RunSelected();
        e.Handled = true;
    }

    private void RunSelected()
    {
        if (Results.SelectedItem is not LauncherEntry entry) return;

        Hide();          // some antes de abrir o app, senao ela rouba a janela nova
        entry.Run();
    }

    /// <summary>Fechar a janela do launcher e so esconder: quem a destroi e a dock ao sair.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private bool _reallyClosing;

    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }
}
