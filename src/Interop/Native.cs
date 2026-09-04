using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Interop;

/// <summary>Assinaturas Win32 usadas pela dock. Nada aqui guarda estado.</summary>
internal static class Native
{
    // ── janelas ──────────────────────────────────────────────
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hWnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint SetActiveWindow(nint hWnd);

    /// <summary>
    /// Opacidade de uma janela em camada. Com alfa zero ela continua existindo, recebendo
    /// automacao e respondendo a comandos — so nao aparece. E o que permite mexer numa
    /// janela do shell sem a pessoa ver.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(nint hWnd, uint key, byte alpha, uint flags);

    public const long WS_EX_LAYERED = 0x00080000;

    /// <summary>A janela nao responde ao mouse: o cursor a atravessa como se nao existisse.</summary>
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint LWA_COLORKEY = 0x00000001;

    [DllImport("gdi32.dll")]
    public static extern int SelectClipRgn(nint hdc, nint hrgn);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int cmd);

    /// <summary>
    /// O que a barra de tarefas usa no Alt+Tab. Vale como ultimo recurso porque nao passa
    /// pela regra de "so o processo em primeiro plano troca o foco" — e o que ainda mexe
    /// numa janela que o SetForegroundWindow se recusou a trazer.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(nint hWnd, bool altTab);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ── DWM: detecta janelas UWP "cloaked" (existem mas nao estao na tela) ──
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hWnd, int attr, out int value, int size);

    public const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// A mesma função do Windows, mas pedindo um RECT em vez de um int — é o que devolve a
    /// moldura visual de verdade da janela (<see cref="DWMWA_EXTENDED_FRAME_BOUNDS"/>), sem a
    /// margem invisível que o Windows 11 deixa em volta de janelas comuns. O mosaico usa isto
    /// para saber quanto sobra pra fora do retângulo pedido e compensar.
    ///
    /// Nome próprio, e não uma sobrecarga de <see cref="DwmGetWindowAttribute(nint, int, out int, int)"/>:
    /// com <c>out var</c> nas chamadas existentes (que pedem um <c>int</c>) o compilador não
    /// tem como saber qual das duas escolher, e a ambiguidade quebrava a build inteira.
    /// </summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowRect(nint hWnd, int attr, out RECT value, int size);

    /// <summary>A moldura visual da janela — sem a margem invisível de redimensionar.</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hWnd, int attr, ref int value, int size);

    /// <summary>Pinta a barra de titulo da janela no tema escuro.</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Cantos arredondados do Windows 11 numa janela sem moldura propria.</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>A cor do contorno nativo que o Windows 11 já desenha em volta de qualquer
    /// janela comum — pedir pro DWM recolorir essa borda em vez de desenhar uma janela nossa
    /// por cima elimina de vez qualquer disputa de z-order (não tem overlay nenhum pra
    /// perder contra outra janela). Só existe a partir do Windows 11 22H2.</summary>
    public const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Cor da barra de título. Da mesma família do <see cref="DWMWA_BORDER_COLOR"/>, e
    /// como ele é dos poucos que o DWM aceita aplicar numa janela de OUTRO processo — a maior
    /// parte dessa família devolve E_ACCESSDENIED de fora, e é o que inviabiliza escolher a
    /// espessura da borda (ver a seção de mosaico em docs/APRENDIZADOS.md). Windows 11 22H2+.</summary>
    public const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>Cor do texto da barra de título — anda junto com a de cima: sem ajustar o texto,
    /// um fundo escuro deixa o título preto ilegível (e vice-versa).</summary>
    public const int DWMWA_TEXT_COLOR = 36;

    /// <summary>Valor especial: devolve a borda pro padrão do sistema (não desenha nada
    /// nosso).</summary>
    public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

    /// <summary>Valor especial: nenhuma borda visível.</summary>
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    public const int DWMWCP_ROUND = 2;

    // ── miniaturas ao vivo (o preview da barra de tarefas) ──

    /// <summary>
    /// Liga uma janela de origem a uma de destino: o DWM passa a desenhar o conteudo vivo da
    /// origem dentro do destino, sem custo de captura nem de repintura por nossa conta.
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DWM_THUMBNAIL_PROPERTIES props);

    /// <summary>Tamanho real da origem — serve para respeitar a proporcao da janela.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    public const int DWM_TNP_RECTDESTINATION      = 0x00000001;
    public const int DWM_TNP_OPACITY              = 0x00000004;
    public const int DWM_TNP_VISIBLE              = 0x00000008;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    // ── eventos de janela (para nao precisar ficar varrendo em loop) ──
    public delegate void WinEventProc(nint hook, uint ev, nint hWnd, int idObject, int idChild,
                                      uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback,
                                              uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hook);

    public const uint EVENT_SYSTEM_FOREGROUND   = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART= 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND  = 0x000B;
    public const uint EVENT_SYSTEM_MINIMIZESTART= 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND  = 0x0017;
    public const uint EVENT_OBJECT_CREATE       = 0x8000;
    public const uint EVENT_OBJECT_DESTROY      = 0x8001;
    public const uint EVENT_OBJECT_SHOW         = 0x8002;
    public const uint EVENT_OBJECT_HIDE         = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE   = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT     = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS   = 0x0002;

    // ── AppBar: reserva a faixa da tela para a dock ──
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Move uma janela sem ativa-la — usado para trazer o menu de um icone da
    /// bandeja para junto do cartao.</summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Move varias janelas de uma vez so. Reserva espaco para <paramref name="count"/> janelas,
    /// acumula cada uma com <see cref="DeferWindowPos"/>, e o <see cref="EndDeferWindowPos"/>
    /// aplica todas juntas.
    ///
    /// A diferenca em relacao a chamar <see cref="SetWindowPos"/> em sequencia e visual: uma a
    /// uma, cada janela se redesenha no lugar novo enquanto as outras ainda estao no antigo, e o
    /// rearranjo do mosaico aparece como um tremor. O <c>count</c> e so uma dica de tamanho — o
    /// Windows cresce a lista sozinho se passar disso.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern nint BeginDeferWindowPos(int count);

    /// <summary>Acumula uma janela na lista. Devolve um identificador NOVO (a lista pode ter sido
    /// realocada) — quem chama tem de sempre continuar com o valor devolvido, nunca com o que
    /// passou. Zero quer dizer que a lista se perdeu e nao ha mais o que aplicar.</summary>
    [DllImport("user32.dll")]
    public static extern nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint after,
                                             int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool EndDeferWindowPos(nint hWinPosInfo);

    /// <summary>after: manda a janela para o topo da pilha de Z sem virar topmost permanente.</summary>
    public const nint HWND_TOP = 0;

    public const uint ABM_NEW         = 0x00;
    public const uint ABM_REMOVE      = 0x01;
    public const uint ABM_QUERYPOS    = 0x02;
    public const uint ABM_SETPOS      = 0x03;
    public const uint ABN_POSCHANGED  = 0x01;
    public const uint ABN_FULLSCREENAPP = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    // ── AppUserModelID: o que separa dois perfis do mesmo .exe ──
    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(nint hWnd, ref Guid iid, out IPropertyStore store);

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public nint p;
        public nint p2;
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    public const ushort VT_LPWSTR = 31;

    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    /// <summary>System.AppUserModel.ID</summary>
    public static PROPERTYKEY PKEY_AppUserModel_ID => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    /// <summary>
    /// AppUserModelID da janela — vazio quando ela nao define um. E o que a barra de
    /// tarefas usa para agrupar, e o que separa dois perfis do mesmo executavel.
    /// </summary>
    public static string AppUserModelId(nint hWnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hWnd, ref iid, out store) != 0 || store is null)
                return string.Empty;

            var key = PKEY_AppUserModel_ID;
            store.GetValue(ref key, out var v);
            try { return v.vt == VT_LPWSTR ? Marshal.PtrToStringUni(v.p) ?? string.Empty : string.Empty; }
            finally { PropVariantClear(ref v); }
        }
        catch { return string.Empty; }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    // ── monitor ────────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    /// <summary>
    /// O Windows usa isto sozinho para "colar" o cursor no menu que acaba de abrir pela
    /// tecla Menu (a mesma coisa que Shift+F10) — é assim que ele mantém mouse e teclado no
    /// mesmo lugar. Aqui serve para desfazer esse salto depois que o menu já está no lugar
    /// certo: devolver o cursor a quem clicou, não mexer nele para abrir nada.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>
    /// Prende o cursor dentro de um retângulo; <c>null</c> solta.
    ///
    /// Aqui serve para uma coisa só: impedir o salto do cursor quando o menu de um ícone da
    /// bandeja abre pela tecla Menu. Quem move o cursor nessa hora é o Explorer, com o
    /// próprio <c>SetCursorPos</c> — e <c>SetCursorPos</c> respeita este limite. Preso numa
    /// caixa de 1×1 em cima de onde a pessoa clicou, ele simplesmente não sai do lugar.
    ///
    /// <b>Solte sempre</b>, em <c>finally</c>: um clip que vaza deixa o mouse da pessoa
    /// travado num pixel, que é muito pior que o salto que ele conserta.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool ClipCursor(ref RECT rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    public static extern bool ReleaseCursorClip(nint zero);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);

    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>Avisa o shell da mudanca — que, no caso da area de trabalho, a desfaz.</summary>
    public const uint SPIF_SENDCHANGE = 0x0002;

    // ── icones ──────────────────────────────────────────────
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info,
                                            uint size, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(nint hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern int LoadIconWithScaleDown(nint hinst, string name, int cx, int cy, out nint icon);

    // ── processo ────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(nint process, uint flags,
                                                        StringBuilder name, ref int size);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ── constantes de janela ────────────────────────────────
    public const int GWL_STYLE     = -16;
    public const int GWL_EXSTYLE   = -20;
    public const int GWL_HWNDPARENT = -8;

    public const long WS_VISIBLE   = 0x10000000;
    public const long WS_CHILD     = 0x40000000;
    public const long WS_THICKFRAME  = 0x00040000;
    public const long WS_MAXIMIZEBOX = 0x00010000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW  = 0x00040000;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint GW_OWNER = 4;

    /// <summary>A janela de topo da arvore de uma janela — a mesma, se ela ja for de topo.</summary>
    public const uint GA_ROOT = 2;

    public const int SW_RESTORE  = 9;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOW     = 5;

    /// <summary>Mostra sem ativar: a janela aparece e o foco fica onde estava.</summary>
    public const int SW_SHOWNA   = 8;

    public const uint WM_CLOSE = 0x0010;
    public const int SW_HIDE = 0;

    // ── barra de tarefas do Windows ─────────────────────────
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowEx(nint parent, nint after, string? className, string? windowName);

    public const uint ABM_SETSTATE = 0x0A;
    public const uint ABM_GETSTATE = 0x04;

    public const int ABS_AUTOHIDE    = 0x01;
    public const int ABS_ALWAYSONTOP = 0x02;

    // ── atalho global (Alt+Espaco abre o launcher) ──────────
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint MOD_WIN     = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_SPACE = 0x20;
    public const uint VK_LEFT  = 0x25;
    public const uint VK_UP    = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN  = 0x28;
    public const uint VK_C     = 0x43;
    public const uint VK_W     = 0x57;
    public const uint VK_Z     = 0x5A;
    public const uint WM_HOTKEY = 0x0312;

    // ── teclas sinteticas (abrir os paineis do proprio Windows) ──
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_LWIN = 0x5B;

    /// <summary>
    /// A tecla Menu (a do menu de contexto, ao lado do Ctrl direito).
    ///
    /// E o que abre o menu de um icone da bandeja sem tocar no mouse: a automacao so sabe
    /// acionar (<c>InvokePattern</c>), que e o clique esquerdo. Clicar de verdade com o botao
    /// direito foi tentado e descartado — sequestrava o cursor da pessoa e nem assim o menu
    /// vinha.
    /// </summary>
    public const byte VK_APPS = 0x5D;

    /// <summary>Win+A: configuracoes rapidas (wi-fi, bluetooth, volume, brilho).</summary>
    public const byte VK_A = 0x41;

    /// <summary>Win+N: notificacoes e calendario.</summary>
    public const byte VK_N = 0x4E;

    public const byte VK_ESCAPE = 0x1B;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int key);

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;

    // ── bateria ─────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 1 = na tomada
        public byte BatteryFlag;         // 128 = nao ha bateria
        public byte BatteryLifePercent;  // 255 = desconhecido
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ── energia ─────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);

    // ── icone de qualquer item do shell, inclusive apps da Store ──
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, nint bindCtx, ref Guid riid,
                                                         out IShellItemImageFactory item);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out nint bitmap);
    }

    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
        public SIZE(int w, int h) { cx = w; cy = h; }
    }

    /// <summary>Aceita um icone menor em vez de esticar um grande borrado.</summary>
    public const int SIIGBF_BIGGERSIZEOK = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(nint handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint handle);

    // ── regiao de janela (o contorno do mosaico recorta so a moldura) ──

    [DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
                                                  int cornerWidth, int cornerHeight);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(nint dest, nint src1, nint src2, int mode);

    public const int RGN_DIFF = 4;

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(nint hWnd, nint region, bool redraw);

    // ── capturar uma janela ────────────────────────────────

    /// <summary>
    /// Desenha a janela inteira num contexto nosso — inclusive coberta ou com a sessao
    /// bloqueada. O <see cref="PW_RENDERFULLCONTENT"/> e o que faz funcionar nas janelas
    /// compostas pelo DWM, que e o caso de tudo no shell do Windows 11.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    /// <summary>
    /// Executa o lote de desenho que o GDI ainda tem guardado.
    ///
    /// Sem isto, ler os pixels de um DIB logo depois de desenhar nele devolve a memoria como
    /// ela estava — de uma cor so. E o passo que falta entre o PrintWindow e o Marshal.Copy.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint handle);

    /// <summary>
    /// Um bitmap de que se conhece o endereco dos pixels. E o que permite ler o resultado
    /// do PrintWindow direto da memoria, sem passar pelo CreateBitmapSourceFromHBitmap —
    /// que descarta o canal alfa.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO info, uint usage,
                                               out nint bits, nint section, uint offset);

    public const uint DIB_RGB_COLORS = 0;

    // ── pintura direta do contorno (WM_PAINT via GDI, sem depender da composição do WPF) ──

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint hdc;
        [MarshalAs(UnmanagedType.Bool)] public bool fErase;
        public RECT rcPaint;
        [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
        [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    public static extern int FillRect(nint hdc, ref RECT rect, nint hbr);

    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_NCDESTROY = 0x0082;

    [DllImport("user32.dll")]
    public static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    // ── janela crua (sem WPF/HwndSource nenhum) — o contorno do mosaico ──
    //
    // Um HwndSource do WPF, mesmo sem conteúdo XAML nenhum, ainda tem um compositor por trás
    // (DirectComposition) que pode redesenhar a própria superfície por cima do que um WM_PAINT
    // em GDI acabou de pintar — daí o contorno sumir de vez em quando mesmo com o WM_PAINT
    // pintando direito. Uma janela criada por RegisterClassEx/CreateWindowEx não tem
    // compositor nenhum do WPF: só existe o que o WM_PAINT desenhar.

    public delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    public const uint WS_POPUP = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    public const uint BI_RGB = 0;
}
