using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>Uma janela do preview: o que mostrar e o tamanho reservado para o desenho.</summary>
public sealed record ThumbnailItem(nint Handle, string Title, double Width, double Height);

/// <summary>
/// O preview das janelas de um app, como o da barra de tarefas.
///
/// Não desenhamos nada: o <c>DwmRegisterThumbnail</c> liga cada janela de origem a esta, e o
/// compositor passa a desenhar o conteúdo **ao vivo** dentro dos retângulos que pedimos. Sai
/// de graça — nada de capturar, redimensionar ou repintar por nossa conta — e é o mesmo
/// mecanismo que o Windows usa.
///
/// A contrapartida é que o desenho fica **por cima** desta janela, e não dentro do layout do
/// WPF: o XAML só reserva o espaço, e o retângulo de cada miniatura é calculado depois de a
/// janela ter tamanho, quando dá para perguntar onde cada espaço ficou.
/// </summary>
public partial class ThumbnailWindow : Window
{
    private readonly List<nint> _thumbnails = new();

    /// <summary>
    /// Lado maior de cada miniatura. O que o Windows usa é dessa ordem; maior que isto e o
    /// preview de um app com cinco janelas não caberia na tela.
    /// </summary>
    private const double MaxSide = 200;

    public ThumbnailWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Release();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hWnd = new WindowInteropHelper(this).Handle;

        // fora do Alt+Tab e sem roubar o foco, como a dock: o preview aparece por passar o
        // mouse, e quem estava trabalhando continua com o teclado
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        // cantos arredondados sem transparência do WPF: quem arredonda é o DWM. Transparência
        // faria desta uma janela em camada, e o DWM não desenha miniatura em janela em camada.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>
    /// Mostra o preview das janelas dadas, ancorado acima (ou abaixo) do botão da dock.
    /// </summary>
    /// <param name="anchor">Faixa do botão na tela, em pixels físicos.</param>
    /// <param name="above">A dock está embaixo: o preview sobe.</param>
    internal void ShowFor(IReadOnlyList<TaskWindow> windows, RECT anchor, bool above)
    {
        Release();

        var dpi = VisualTreeHelper.GetDpi(this);

        Items.ItemsSource = windows
            .Select(w =>
            {
                var (width, height) = Proportions(w.Handle);
                return new ThumbnailItem(w.Handle, Caption(w), width, height);
            })
            .ToList();

        // O tamanho só existe depois de a janela ser mostrada — é o `SizeToContent` que o
        // calcula —, e a posição depende do tamanho. Então ela nasce fora da tela e só
        // depois vai para o lugar: posicionar antes deixava o painel meio fora do monitor,
        // porque a conta usava altura zero.
        Left = -20000;
        Top = 0;
        Show();
        UpdateLayout();

        var width = ActualWidth * dpi.DpiScaleX;
        var height = ActualHeight * dpi.DpiScaleY;
        var centre = (anchor.Left + anchor.Right) / 2.0;

        Left = Math.Clamp(centre - width / 2, 0, Math.Max(0, ScreenWidth() - width)) / dpi.DpiScaleX;
        Top = (above ? anchor.Top - height - Gap : anchor.Bottom + Gap) / dpi.DpiScaleY;

        // os retângulos são pedidos depois de a janela existir e estar posicionada: antes
        // disso não há coordenadas de cliente para dar ao compositor
        Dispatcher.InvokeAsync(Attach, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Folga entre o botão da dock e o preview, em pixels físicos.</summary>
    private const double Gap = 8;

    private static double ScreenWidth() => SystemParameters.PrimaryScreenWidth *
                                           VisualTreeHelper.GetDpi(Application.Current.MainWindow).DpiScaleX;

    /// <summary>
    /// Largura e altura da miniatura, na proporção da janela real.
    ///
    /// Sem isto uma janela em pé apareceria esticada. A proporção vem do próprio DWM
    /// (<c>DwmQueryThumbnailSourceSize</c>), que sabe o tamanho da origem melhor que um
    /// <c>GetWindowRect</c> — ele desconta a moldura invisível.
    /// </summary>
    private static (double Width, double Height) Proportions(nint window)
    {
        var fallback = (MaxSide, MaxSide * 9 / 16);

        var host = Application.Current.MainWindow is null
                   ? 0 : new WindowInteropHelper(Application.Current.MainWindow).Handle;
        if (host == 0) return fallback;

        if (DwmRegisterThumbnail(host, window, out var probe) != 0) return fallback;

        try
        {
            if (DwmQueryThumbnailSourceSize(probe, out var size) != 0 || size.cx <= 0 || size.cy <= 0)
                return fallback;

            var scale = MaxSide / Math.Max(size.cx, size.cy);
            return (Math.Round(size.cx * scale), Math.Round(size.cy * scale));
        }
        finally { DwmUnregisterThumbnail(probe); }
    }

    /// <summary>
    /// Registra uma miniatura por espaço reservado e diz ao compositor onde desenhar.
    /// </summary>
    private void Attach()
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == 0 || Items.ItemsSource is not IEnumerable<ThumbnailItem> items) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var index = 0;

        foreach (var item in items)
        {
            var slot = Slot(index++);
            if (slot is null) continue;

            if (DwmRegisterThumbnail(hWnd, item.Handle, out var thumbnail) != 0) continue;
            _thumbnails.Add(thumbnail);

            // coordenadas do cliente desta janela, em pixels físicos
            var origin = slot.TranslatePoint(new Point(0, 0), this);

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE |
                          DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT
                {
                    Left   = (int)Math.Round(origin.X * dpi.DpiScaleX),
                    Top    = (int)Math.Round(origin.Y * dpi.DpiScaleY),
                    Right  = (int)Math.Round((origin.X + slot.ActualWidth) * dpi.DpiScaleX),
                    Bottom = (int)Math.Round((origin.Y + slot.ActualHeight) * dpi.DpiScaleY)
                },
                opacity = 255,
                fVisible = true,

                // só a área de cliente: a barra de título da origem não interessa no preview
                fSourceClientAreaOnly = true
            };

            DwmUpdateThumbnailProperties(thumbnail, ref props);
        }
    }

    /// <summary>O espaço reservado do item de índice dado, já dentro da árvore visual.</summary>
    private FrameworkElement? Slot(int index)
    {
        if (Items.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter cell) return null;
        cell.ApplyTemplate();
        return FindSlot(cell);
    }

    private static FrameworkElement? FindSlot(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "Slot" } found) return found;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindSlot(VisualTreeHelper.GetChild(root, i)) is { } slot) return slot;

        return null;
    }

    /// <summary>Solta as miniaturas. Sem isto o DWM continua desenhando janelas que já foram.</summary>
    private void Release()
    {
        foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }

    public void HideThumbnails()
    {
        Release();
        Hide();
    }

    private static string Caption(TaskWindow window) =>
        string.IsNullOrWhiteSpace(window.Title) ? "Sem título" : window.Title;

    /// <summary>Clicar numa miniatura traz aquela janela para a frente.</summary>
    private void OnThumbnailClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not nint handle) return;

        HideThumbnails();
        WindowService.Activate(handle);
    }
}
