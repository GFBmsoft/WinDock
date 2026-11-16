using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Toca um vídeo atrás dos ícones da área de trabalho, em todos os monitores, e pausa quando
/// ninguém está vendo.
///
/// <para><b>Onde a janela fica</b> (sondado em 16/11/2026, build 26300; a mesma árvore da 24H2): a
/// <c>Progman</c> não tem mais superfície própria (<c>WS_EX_NOREDIRECTIONBITMAP</c>) e os ícones
/// (<c>SHELLDLL_DefView</c>) são uma filha em camada dela. O truque antigo — pôr a janela atrás de
/// uma <c>WorkerW</c> solta no topo — não serve mais. O que funciona: uma janela-filha da
/// <c>Progman</c>, logo abaixo do <c>DefView</c> na ordem, com o conteúdo entregue pela composição
/// do Windows (<c>DesktopWindowTarget</c>). Filha comum desenhando por GDI ou pelo WPF não aparece
/// (não há superfície onde desenhar); o <c>HwndSource</c> do WPF ainda tira o
/// <c>WS_EX_LAYERED</c> em silêncio. Detalhes em docs/APRENDIZADOS.md.</para>
///
/// <para><b>Quem desenha:</b> o <see cref="MediaPlayer"/> do Windows entrega os quadros direto à
/// composição (<see cref="MediaPlayer.GetSurface"/>), e o dwm os põe na tela. Um player só para
/// todos os monitores: a mesma superfície em um pincel por monitor, cada um cortado do seu jeito
/// (<see cref="CompositionStretch.UniformToFill"/>). Medido no vídeo de 1080p: metade da placa que o
/// <c>MediaElement</c> do WPF gastava, com a mesma CPU.</para>
///
/// <para><b>A thread:</b> tudo aqui roda numa thread STA só deste serviço, com o próprio
/// <see cref="Dispatcher"/> — nunca na da dock. Uma janela-filha de outro processo tem a fila de
/// entrada ligada à da <c>Progman</c> do explorer: se esta thread parasse, a área de trabalho
/// pararia junto. Por isso nada aqui espera: a conversão de GIF e a troca do papel estático correm
/// fora, e as conversas com o explorer usam <c>SendMessageTimeout</c>.</para>
/// </summary>
internal sealed class AnimatedWallpaper : IDisposable
{
    private const string ClassName = "WinDockWallpaper";

    /// <summary>O que está acontecendo, em texto para a tela de Configurações. Chega na thread deste serviço.</summary>
    public event Action<string>? StatusChanged;

    private readonly Thread _thread;
    private readonly Dispatcher _host;

    // ── tudo abaixo só é tocado na thread do serviço ──
    private Compositor? _compositor;
    private nint _queueController;
    private MediaPlayer? _player;
    private MediaPlayerSurface? _surface;
    private readonly List<Screen> _screens = new();
    private nint _progman;
    private string _layout = string.Empty;
    private bool _needsBuild;
    private string? _name;
    private string? _pausedFor = "começando";
    private bool _pauseOnBattery = true;
    private DispatcherTimer? _watch;
    private static WndProcDelegate? _wndProc;   // vivo enquanto a classe estiver registrada

    private sealed record Screen(nint Hwnd, DesktopWindowTarget Target, SpriteVisual Visual, RECT Monitor);

    public AnimatedWallpaper()
    {
        using var pronta = new ManualResetEventSlim();
        Dispatcher? host = null;
        _thread = new Thread(() =>
        {
            host = Dispatcher.CurrentDispatcher;
            pronta.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "papel de parede animado" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        pronta.Wait();
        _host = host!;
    }

    /// <summary>Toca <paramref name="video"/> (já convertido, se era GIF), em laço e sem som.</summary>
    public void Play(string video, string name, bool pauseOnBattery) =>
        _host.BeginInvoke(() => Guard(() => PlayHere(video, name, pauseOnBattery)));

    public void SetPauseOnBattery(bool value) =>
        _host.BeginInvoke(() => { _pauseOnBattery = value; Guard(Evaluate); });

    /// <summary>Some com o vídeo e com as janelas; a thread fica, parada, para a próxima vez.</summary>
    public void Stop() => _host.BeginInvoke(() => Guard(StopHere));

    public void Dispose()
    {
        // espera um pouco para as janelas saírem da Progman antes de a dock fechar; elas morreriam
        // com o processo de qualquer jeito, mas sem deixar a área de trabalho piscando
        try { _host.Invoke(() => Guard(StopHere), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2)); }
        catch { /* thread presa: o fim do processo leva as janelas */ }
        _host.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    private void Guard(Action work)
    {
        try { work(); }
        catch (Exception ex) { Log.Write("papel de parede animado: falha", ex); Status("Falhou: " + ex.Message); }
    }

    private void Status(string text) => StatusChanged?.Invoke(text);

    // ── tocar e parar ─────────────────────────────────────────

    private void PlayHere(string video, string name, bool pauseOnBattery)
    {
        _pauseOnBattery = pauseOnBattery;
        _name = name;
        EnsureComposition();

        if (_player is null)
        {
            _player = new MediaPlayer { IsLoopingEnabled = true, IsMuted = true, AutoPlay = false };
            // sem isto o vídeo aparece como "tocando agora" no controle de mídia do Windows (e no
            // cartão de mídia da própria barra), com botão de pausar e tudo
            _player.CommandManager.IsEnabled = false;
            _player.MediaFailed += (_, e) => _host.BeginInvoke(() =>
            {
                Log.Write($"papel de parede animado: '{_name}' não abriu ({e.Error}: {e.ErrorMessage})");
                Status($"Não deu para tocar {_name}: o Windows não abriu o arquivo ({e.Error})");
            });
            _player.MediaOpened += (p, _) => Log.Trace(
                $"papel de parede animado: '{_name}' aberto, {p.PlaybackSession.NaturalVideoWidth}x{p.PlaybackSession.NaturalVideoHeight}, " +
                $"{p.PlaybackSession.NaturalDuration.TotalSeconds:0.0} s");
            _surface = _player.GetSurface(_compositor!);
        }

        _player.Source = MediaSource.CreateFromUri(new Uri(video));
        if (_screens.Count == 0) _needsBuild = true;
        _pausedFor = "começando";

        if (_watch is null)
        {
            _watch = new DispatcherTimer(DispatcherPriority.Background, _host) { Interval = TimeSpan.FromSeconds(1) };
            _watch.Tick += (_, _) => Guard(Evaluate);
        }
        _watch.Start();
        Evaluate();
    }

    private void StopHere()
    {
        _watch?.Stop();
        TearDownScreens();
        if (_player is not null)
        {
            _player.Pause();
            _player.Source = null;
            _surface?.Dispose();
            _player.Dispose();
            _player = null;
            _surface = null;
        }
        _name = null;
        _pausedFor = "começando";
    }

    // ── a cada segundo: refazer se precisar, tocar ou pausar ──

    private void Evaluate()
    {
        if (_player is null) return;

        var progman = FindWindow("Progman", null);
        if (_needsBuild || progman != _progman || _screens.Any(s => !IsWindow(s.Hwnd)) || Layout() != _layout)
        {
            if (!_needsBuild && _screens.Count > 0)
                Log.Write("papel de parede animado: a área de trabalho mudou (explorer reiniciado ou monitores trocados); remontando");
            if (!Build()) { _needsBuild = true; Pause("sem área de trabalho para desenhar"); return; }
            _needsBuild = false;
        }

        var motivo = PauseReason();
        if (motivo is null)
        {
            if (_pausedFor is null) return;
            _player.Play();
            _pausedFor = null;
            Log.Trace($"papel de parede animado: tocando '{_name}'");
            Status($"Tocando {_name}");
        }
        else Pause(motivo);
    }

    private void Pause(string motivo)
    {
        if (_pausedFor == motivo) return;
        _player?.Pause();
        _pausedFor = motivo;
        Log.Trace($"papel de parede animado: pausado ({motivo})");
        Status($"{_name} — pausado: {motivo}");
    }

    private string? PauseReason()
    {
        if (Locked()) return "sessão bloqueada";
        if (_pauseOnBattery && OnBattery()) return "na bateria";
        if (!DesktopVisible()) return "área de trabalho coberta";
        return null;
    }

    /// <summary>
    /// A sessão bloqueada (ou o UAC, ou outra sessão na frente) troca a área de entrada: a nossa,
    /// "Default", deixa de ser a que recebe teclado — e um processo comum nem consegue abrir a outra.
    /// </summary>
    private static bool Locked()
    {
        var d = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (d == 0) return true;
        try
        {
            var nome = new StringBuilder(64);
            return !GetUserObjectInformation(d, UOI_NAME, nome, nome.Capacity * 2, out _)
                   || !nome.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(d); }
    }

    private static bool OnBattery() =>
        GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && s.BatteryFlag != 128;

    /// <summary>
    /// Algum monitor mostra a área de trabalho? Conta só as janelas que estão <b>acima</b> da
    /// <c>Progman</c> na pilha: no Win+D é a área de trabalho que sobe para cima delas, e as
    /// janelas continuam lá, nem minimizadas — contar todas pausaria o vídeo justo quando ele
    /// aparece. Mede dentro da área útil de cada monitor, sem a faixa da dock e da barra, que
    /// sempre deixam ver um pouco do fundo.
    /// </summary>
    private bool DesktopVisible()
    {
        var janelas = new List<RECT>();
        var progman = _progman;
        EnumWindows((h, _) =>
        {
            if (h == progman) return false;   // daqui para baixo, nada cobre a área de trabalho
            if (IsIconic(h) || !WindowService.IsAltTabWindow(h)) return true;
            if (DwmGetWindowRect(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(h, out r);
            if (r.Width > 0 && r.Height > 0) janelas.Add(r);
            return true;
        }, 0);

        // a área útil é lida agora, e não guardada: a faixa da dock e a da barra mudam com as
        // opções, e remontar as janelas por isso faria a área de trabalho piscar
        return Monitors().Any(m => WallpaperRules.VisibleFraction(m.Work, janelas) >= WallpaperRules.MinVisible);
    }

    // ── as janelas, uma por monitor ───────────────────────────

    private void EnsureComposition()
    {
        if (_compositor is not null) return;

        // a composição pede uma fila de despacho do WinRT na thread; o Dispatcher do WPF é quem
        // bombeia as mensagens dela
        var opcoes = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        var hr = CreateDispatcherQueueController(opcoes, out _queueController);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        _compositor = new Compositor();

        if (_wndProc is null)
        {
            _wndProc = DefWindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
        }
    }

    /// <summary>
    /// Uma janela por monitor dentro da <c>Progman</c>, logo abaixo dos ícones. Falso quando não
    /// há onde pôr: explorer subindo, ou um Windows que ainda guarda os ícones fora da Progman.
    /// </summary>
    private bool Build()
    {
        TearDownScreens();

        _progman = FindWindow("Progman", null);
        if (_progman == 0) return false;

        // 0x052C: o pedido que o Wallpaper Engine e o Lively também fazem. Nesta árvore ele cria
        // uma WorkerW filha da Progman, abaixo dos ícones; sem ele a nossa janela ficaria sem par
        // na hora em que o Windows troca o papel de parede
        SendMessageTimeout(_progman, 0x052C, 0xD, 0x1, SMTO_NORMAL | SMTO_ABORTIFHUNG, 1000, out _);

        var icones = FindWindowEx(_progman, 0, "SHELLDLL_DefView", null);
        if (icones == 0)
        {
            Log.Write("papel de parede animado: os ícones não estão dentro da Progman (Windows anterior à 24H2?); nada a fazer");
            Status("Este Windows guarda os ícones da área de trabalho de outro jeito, e o papel de parede animado ainda não funciona nele");
            return false;
        }

        foreach (var (monitor, _) in Monitors())
        {
            var p = new POINT { X = monitor.Left, Y = monitor.Top };
            ScreenToClient(_progman, ref p);
            var hwnd = CreateWindowEx(WS_EX_NOACTIVATE, ClassName, null,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_DISABLED,
                p.X, p.Y, monitor.Width, monitor.Height, _progman, 0, GetModuleHandle(null), 0);
            if (hwnd == 0)
            {
                Log.Write($"papel de parede animado: CreateWindowEx falhou (erro {Marshal.GetLastWin32Error()})");
                continue;
            }

            var alvo = CreateTarget(hwnd);
            var visual = _compositor!.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            var pincel = _compositor.CreateSurfaceBrush(_surface!.CompositionSurface);
            pincel.Stretch = CompositionStretch.UniformToFill;
            visual.Brush = pincel;
            alvo.Root = visual;

            SetWindowPos(hwnd, icones, p.X, p.Y, monitor.Width, monitor.Height, SWP_NOACTIVATE);
            _screens.Add(new Screen(hwnd, alvo, visual, monitor));
        }

        _layout = Layout();
        Log.Trace($"papel de parede animado: {_screens.Count} janela(s) atrás dos ícones");
        return _screens.Count > 0;
    }

    private void TearDownScreens()
    {
        foreach (var s in _screens)
        {
            try { s.Target.Root = null; s.Visual.Dispose(); s.Target.Dispose(); } catch { }
            if (IsWindow(s.Hwnd)) DestroyWindow(s.Hwnd);
        }
        _screens.Clear();
    }

    /// <summary>O alvo de composição da janela, pela interface de interop do compositor.</summary>
    private DesktopWindowTarget CreateTarget(nint hwnd)
    {
        var comp = ((WinRT.IWinRTObject)_compositor!).NativeObject.ThisPtr;
        var iid = ICompositorDesktopInterop;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(comp, ref iid, out var interop));
        try
        {
            // a interface tem um método só além dos três do IUnknown: CreateDesktopWindowTarget
            var tabela = Marshal.ReadIntPtr(interop);
            var criar = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTargetFn>(Marshal.ReadIntPtr(tabela, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(criar(interop, hwnd, 1, out var alvo));
            // o FromAbi segura a sua própria referência; a que veio da criação é nossa e sai aqui
            try { return DesktopWindowTarget.FromAbi(alvo); }
            finally { Marshal.Release(alvo); }
        }
        finally { Marshal.Release(interop); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDesktopWindowTargetFn(nint self, nint hwnd, int isTopmost, out nint target);

    private static List<(RECT Monitor, RECT Work)> Monitors()
    {
        var lista = new List<(RECT, RECT)>();
        EnumDisplayMonitors(0, 0, (nint m, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref mi)) lista.Add((mi.rcMonitor, mi.rcWork));
            return true;
        }, 0);
        return lista;
    }

    /// <summary>Onde estão os monitores: mudou (um a mais, outra resolução), a janela de algum está no lugar errado.</summary>
    private static string Layout() =>
        string.Join(";", Monitors().Select(m => $"{m.Monitor.Left},{m.Monitor.Top},{m.Monitor.Right},{m.Monitor.Bottom}"));

    // ── Win32 que só este serviço usa ─────────────────────────

    private static readonly Guid ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_DISABLED = 0x08000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint obj, int index, StringBuilder info, int length, out int needed);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint desktop);
}
