using System.Runtime.InteropServices;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Apaga, a qualquer hora, os balões que o painel de ícones ocultos do Windows abre enquanto
/// está escondido.
///
/// <para><b>O vazamento.</b> Para não mandar Esc à janela de quem está trabalhando (veja o
/// <c>TrayService.EscIsSafe</c>), a leitura da bandeja quase sempre fecha o painel do Windows
/// <b>escondendo</b> a janela. Para o shell ele continua aberto — a seta passa a se chamar
/// "Mostrar Ícones Ocultos <i>Ocultar</i>" —, com o foco num ícone, e o XAML dele segue
/// mostrando a dica desse ícone quando lhe dá vontade: num balão à parte, que não herda o
/// escondido do painel. Com a barra do Windows no topo, ele pousava na barra da dock — era a
/// "Discord" debaixo da barra (EV01, 26/10/2026), "mensagens vazando, normalmente de apps na
/// bandeja". O <c>SweepShellPopups</c> só olhava 1,5 s depois de cada leitura, e poupava o que
/// já estava na tela quando começava.</para>
///
/// <para><b>Quem é lixo.</b> Um vigia passivo provou o dono: o balão (<c>Xaml_WindowedPopupClass</c>,
/// "PopupHost") tem como dono a janela do painel (<c>TopLevelWindowForOverflowXamlIsland</c>), e
/// ela estava escondida. Um balão cujo dono não está na tela não tem o que explicar a ninguém —
/// e é só esse que sai: o painel aberto de verdade (pelo teclado, Win+B) deixa os balões dele em
/// paz, e as notificações, o seletor de teclado e o resto do shell têm outro dono.</para>
///
/// <para><b>Como.</b> Um gancho de <c>EVENT_OBJECT_SHOW</c> só no processo do explorer, numa
/// thread própria com a sua fila de mensagens: a interface pode estar ocupada sem atrasar nada,
/// e o <c>ShowWindowAsync</c> esconde sem esperar o explorer responder. O explorer que reinicia
/// muda de processo, e uma conferência de cinco em cinco segundos refaz o gancho.</para>
/// </summary>
public sealed class ShellPopupGuard : IDisposable
{
    private const string PopupClass = "Xaml_WindowedPopupClass";
    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>De quanto em quanto tempo se confere se o explorer ainda é o mesmo processo.</summary>
    private const uint RehookEvery = 5000;

    private readonly Thread _thread;
    private uint _threadId;

    // num campo, e não numa variável: o gancho guarda só o ponteiro, e o GC levaria o delegate
    private WinEventProc? _proc;
    private nint _hook;
    private uint _explorer;

    public ShellPopupGuard()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ShellPopupGuard" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnShow;
        Rehook();

        var timer = SetTimer(0, 0, RehookEvery, 0);

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER) { Rehook(); continue; }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        KillTimer(0, timer);
        if (_hook != 0) UnhookWinEvent(_hook);
    }

    private void Rehook()
    {
        var barra = FindWindow("Shell_TrayWnd", null);
        if (barra == 0) return;

        GetWindowThreadProcessId(barra, out var pid);
        if (pid == _explorer && _hook != 0) return;

        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, 0, _proc!, pid, 0, WINEVENT_OUTOFCONTEXT);
        _explorer = pid;

        Log.Trace($"vigia dos balões do shell ligado no explorer {pid}");
    }

    private void OnShow(nint hook, uint ev, nint hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hWnd == 0) return;
        if (WindowService.ClassOf(hWnd) != PopupClass) return;

        var dono = GetWindow(hWnd, GW_OWNER);
        if (dono == 0 || WindowService.ClassOf(dono) != OverflowClass) return;

        // O painel na tela de verdade é o que a pessoa abriu: os balões são dela. Na tela quer
        // dizer visível **e** sem a camada de alfa zero que a leitura põe nele enquanto o usa
        // (veja o TrayService.Invisible) — no meio de uma leitura ele está "visível" e ninguém o vê.
        var naTela = IsWindowVisible(dono) &&
                     ((long)GetWindowLongPtr(dono, GWL_EXSTYLE) & WS_EX_LAYERED) == 0;
        if (naTela) return;

        ShowWindowAsync(hWnd, SW_HIDE);
        Log.Trace($"balão do painel de ícones ocultos escondido: {hWnd:X} (o painel {(IsWindowVisible(dono) ? "estava invisível" : "estava escondido")})");
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
    }

    // ── o que só este vigia usa ──────────────────────────────

    private const int OBJID_WINDOW = 0;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hWnd, nint id, uint elapse, nint callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hWnd, nint id);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int cmd);
}
