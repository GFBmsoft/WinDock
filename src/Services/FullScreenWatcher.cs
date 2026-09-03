using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Avisa quando um app ocupa a tela inteira — uma sessao remota, um vídeo, um jogo — para
/// a dock e a barra saírem da frente.
///
/// Elas sao topmost (precisam ser, ou sumiriam atras das janelas comuns), e topmost fica
/// por cima ate de quem esta em tela cheia: era a dock aparecendo sobre a sessao RDP.
///
/// A conta e simples: a janela em primeiro plano cobre o monitor inteiro? A area de
/// trabalho e a barra de tarefas nao contam — elas sao "tela cheia" o tempo todo.
/// </summary>
public sealed class FullScreenWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _active;

    /// <summary>
    /// Faixa que a dock ocupa, em pixels. Serve para a segunda regra: uma sessao remota
    /// costuma cobrir a tela sem se declarar "tela cheia", e o que interessa e se ela
    /// passou por cima da dock.
    /// </summary>
    internal RECT DockBounds { get; set; }

    /// <summary>
    /// Janelas de acesso remoto. Sao tratadas a parte porque ignoram a area de trabalho
    /// reservada: a sessao ocupa a tela toda e a dock ficava boiando por cima dela.
    /// </summary>
    private static readonly string[] RemoteSessionClasses =
    {
        "TscShellContainerClass",   // Conexao de Area de Trabalho Remota (mstsc)
        "RAIL_WINDOW",              // RemoteApp
        "TscShellAxWindowClass"
    };

    /// <summary>Disparado quando entra ou sai do estado de tela cheia.</summary>
    public event Action<bool>? Changed;

    public FullScreenWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>
    /// Comeca a vigiar. Separado do construtor de proposito: quem escuta precisa se
    /// inscrever antes, senao a primeira leitura passa em branco e o estado inicial se
    /// perde.
    /// </summary>
    public void Start()
    {
        Check();
        _timer.Start();
    }

    private void Check()
    {
        var active = ShouldHide();
        if (active == _active) return;

        _active = active;
        Changed?.Invoke(active);
    }

    private bool ShouldHide()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;

        var name = new StringBuilder(120);
        GetClassName(window, name, name.Capacity);
        var className = name.ToString();

        // o desktop e a barra de tarefas cobrem a tela inteira por natureza
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        // a propria dock, a barra de cima e o launcher tambem nao contam
        GetWindowThreadProcessId(window, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;

        if (!GetWindowRect(window, out var rect)) return false;

        if (IsFullScreen(window, rect)) return true;

        // sessao remota que passou por cima da faixa da dock
        return RemoteSessionClasses.Contains(className) && Overlaps(rect, DockBounds);
    }

    private static bool Overlaps(RECT a, RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static bool IsFullScreen(nint window, RECT rect)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info)) return false;

        // uma folga de um pixel para cada lado: janelas em tela cheia as vezes passam do
        // monitor por causa da borda invisivel do DWM
        var screen = info.rcMonitor;
        return rect.Left <= screen.Left + 1 && rect.Top <= screen.Top + 1 &&
               rect.Right >= screen.Right - 1 && rect.Bottom >= screen.Bottom - 1;
    }

    public void Dispose() => _timer.Stop();
}
