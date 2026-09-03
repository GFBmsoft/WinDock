using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows;
using System.Windows.Interop;
using static WinDock.Interop.Native;

namespace WinDock.Interop;

public enum DockEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>
/// Registra a janela como AppBar: o Windows encolhe a area de trabalho, entao nenhuma
/// janela maximizada fica por baixo da dock. Sem isso a dock so flutuaria por cima.
/// </summary>
public sealed class AppBar : IDisposable
{
    private readonly Window _window;
    private nint _hWnd;
    private uint _callbackMessage;
    private bool _registered;
    private bool _reserving = true;

    /// <summary>Altura (ou largura) reservada, em pixels fisicos.</summary>
    public int Thickness { get; private set; } = 38;
    public DockEdge Edge { get; private set; } = DockEdge.Bottom;

    /// <summary>Faixa que a janela ocupa, em pixels fisicos.</summary>
    internal RECT Bounds { get; private set; }

    /// <summary>Disparado quando o shell muda o layout e a dock precisa se reposicionar.</summary>
    public event Action? PositionChanged;

    public AppBar(Window window) => _window = window;

    /// <summary>
    /// Ignora as outras AppBars ao se posicionar. A barra de tarefas do Windows continua
    /// registrada mesmo escondida por nos, e o shell empurraria esta barra para baixo dela
    /// — 32 pixels de terra de ninguem entre o topo da tela e a barra, que ainda por cima
    /// engoliriam os cliques.
    /// </summary>
    public bool IgnoreOtherBars { get; set; }

    public void Register(DockEdge edge, int thickness, bool reserveSpace)
    {
        _hWnd = new WindowInteropHelper(_window).Handle;
        if (_hWnd == 0) throw new InvalidOperationException("a janela ainda nao tem handle");

        Edge = edge;
        Thickness = thickness;
        _reserving = reserveSpace;
        _callbackMessage = RegisterWindowMessage("WinDockAppBarMessage");

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
            uCallbackMessage = _callbackMessage
        };
        SHAppBarMessage(ABM_NEW, ref data);
        _registered = true;

        HwndSource.FromHwnd(_hWnd)?.AddHook(WndProc);
        UpdatePosition();
    }

    public void Resize(DockEdge edge, int thickness, bool reserveSpace)
    {
        Edge = edge;
        Thickness = thickness;
        _reserving = reserveSpace;
        if (_registered) UpdatePosition();
    }

    /// <summary>
    /// Pergunta ao shell onde a barra cabe (ABM_QUERYPOS: outras AppBars ja registradas
    /// podem empurrar a nossa) e so entao fixa a posicao.
    /// </summary>
    public void UpdatePosition()
    {
        if (!_registered) return;

        var screen = ScreenBounds();
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
            uEdge = (uint)Edge,
            rc = screen
        };

        Snap(ref data.rc);
        var desired = data.rc;   // onde queremos ficar, encostado na borda do monitor

        if (_reserving)
        {
            // ABM_QUERYPOS: as AppBars ja registradas podem empurrar a nossa. Quem ignora
            // (a barra de cima, quando a do Windows esta escondida) pula a pergunta.
            if (!IgnoreOtherBars) SHAppBarMessage(ABM_QUERYPOS, ref data);
            Snap(ref data.rc);

            SHAppBarMessage(ABM_SETPOS, ref data);
            Snap(ref data.rc);   // o shell tambem mexe no retangulo aqui

            // o SETPOS avisa o shell da faixa que ocupamos, mas quem ignora as outras
            // barras fica onde pediu: senao a resposta do shell traria de volta o empurrao
            if (IgnoreOtherBars) data.rc = desired;
        }

        // sem isto, um retangulo torto devolvido pelo shell viraria largura negativa e
        // derrubaria a janela na hora de aplicar
        if (data.rc.Width <= 0 || data.rc.Height <= 0) data.rc = screen;

        Bounds = data.rc;

        var dpi = VisualTreeHelper.GetDpi(_window);
        _window.Left   = data.rc.Left   / dpi.DpiScaleX;
        _window.Top    = data.rc.Top    / dpi.DpiScaleY;
        _window.Width  = data.rc.Width  / dpi.DpiScaleX;
        _window.Height = data.rc.Height / dpi.DpiScaleY;
    }

    /// <summary>Encosta o retangulo na borda escolhida, com a espessura pedida.</summary>
    private void Snap(ref RECT rc)
    {
        switch (Edge)
        {
            case DockEdge.Top:    rc.Bottom = rc.Top + Thickness; break;
            case DockEdge.Bottom: rc.Top = rc.Bottom - Thickness; break;
            case DockEdge.Left:   rc.Right = rc.Left + Thickness; break;
            case DockEdge.Right:  rc.Left = rc.Right - Thickness; break;
        }
    }

    /// <summary>Limites fisicos do monitor onde a dock esta (nao a area de trabalho).</summary>
    private RECT ScreenBounds()
    {
        var mon = MonitorFromWindow(_hWnd, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != 0 && GetMonitorInfo(mon, ref info)) return info.rcMonitor;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)_callbackMessage && (uint)wParam == ABN_POSCHANGED)
        {
            UpdatePosition();
            PositionChanged?.Invoke();
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        if (!_registered) return;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hWnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }
}
