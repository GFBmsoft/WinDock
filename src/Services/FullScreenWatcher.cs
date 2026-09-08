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

        // Tela cheia no outro monitor não é problema nosso. A dock e a barra ocupam faixa em
        // **um** monitor; sumir com elas por causa do que acontece no outro é desaparecer sem
        // motivo — e era exatamente o que acontecia: maximizar o app Configurações do Windows
        // na tela secundária escondia as duas barras da tela principal.
        //
        // Lá o engano é inevitável por geometria: como nada é reservado naquele monitor (a dock
        // está no outro e a barra do Windows fica escondida), a área de trabalho é a tela
        // inteira, e uma janela **maximizada** fica com o mesmo retângulo de uma em tela cheia
        // de verdade. Medido no caso relatado: a janela passou a max=True e virou
        // `-1928,-8 a 8,1088` num monitor de `-1920,0 a 0,1080` — cobre com folga, graças à
        // margem invisível do DWM. Nenhum teste de tamanho separa as duas ali; o que separa é
        // perguntar de que monitor se está falando.
        if (!NoMonitorDaDock(window)) return false;

        if (IsFullScreen(window, rect)) return true;

        // sessao remota que passou por cima da faixa da dock
        return RemoteSessionClasses.Contains(className) && Overlaps(rect, DockBounds);
    }

    /// <summary>
    /// Se a janela está no mesmo monitor em que a dock reservou faixa.
    ///
    /// Antes de a dock subir por completo o <see cref="DockBounds"/> ainda está zerado — nesse
    /// intervalo a resposta é "sim", que mantém o comportamento de sempre em vez de cegar o
    /// vigia logo no arranque, que é quando uma sessão remota já pode estar de pé.
    /// </summary>
    private bool NoMonitorDaDock(nint window)
    {
        var dock = DockBounds;
        if (dock.Right <= dock.Left || dock.Bottom <= dock.Top) return true;

        var centro = new POINT
        {
            X = dock.Left + (dock.Right - dock.Left) / 2,
            Y = dock.Top + (dock.Bottom - dock.Top) / 2
        };

        return MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) ==
               MonitorFromPoint(centro, MONITOR_DEFAULTTONEAREST);
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
