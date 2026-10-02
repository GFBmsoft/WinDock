using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// O balão breve de uma notificação nova. Quem decide quando abrir e onde empilhar é a barra
/// (<c>PanelWindow.ShowBalloon</c>); aqui mora o desenho, o relógio de sumir e o gesto.
/// </summary>
public partial class NotificationBalloon : Window
{
    private readonly DispatcherTimer _timer;

    public NotificationBalloon(NotificationItem item, bool showText, int seconds)
    {
        InitializeComponent();
        Item = item;

        Source.Text = item.App;
        if (item.HasGlyph)
        {
            Glyph.Glyph = item.Glyph;
            Glyph.Fill = item.GlyphFill ?? Brushes.White;
            AppIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppIcon.Source = item.Icon;
            Glyph.Visibility = Visibility.Collapsed;
            if (item.Icon is null) AppIcon.Visibility = Visibility.Collapsed;
        }

        // sem o texto: só de onde veio, para a tela que outra pessoa pode estar vendo
        if (showText)
        {
            Heading.Text = item.Title;
            Body.Text = item.Body;
            if (!item.HasBody) Body.Visibility = Visibility.Collapsed;
        }
        else
        {
            Heading.Text = "Nova notificação";
            Body.Visibility = Visibility.Collapsed;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) => FadeOut();

        // o mouse em cima segura o balão; saiu, o relógio recomeça inteiro
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { _timer.Stop(); _timer.Start(); };
        CloseBox.MouseEnter += (_, _) => CloseBox.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        CloseBox.MouseLeave += (_, _) => CloseBox.Background = Brushes.Transparent;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public NotificationItem Item { get; }

    /// <summary>O clique no balão: a barra leva a pessoa a quem mandou.</summary>
    public event Action<NotificationBalloon>? Opened;

    /// <summary>Não ativável e fora do Alt+Tab — a mesma escolha do balão de lembrete.</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        Opened?.Invoke(this);
        Close();
    }

    private void OnClose(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // o ✕ fica dentro do balão, e o clique dele não pode abrir a conversa
        Close();
    }

    private bool _closing;

    private void FadeOut()
    {
        _timer.Stop();
        if (_closing) return;
        _closing = true;

        var some = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        some.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, some);
    }
}
