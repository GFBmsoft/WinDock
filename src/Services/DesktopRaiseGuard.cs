using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Põe a dock e a barra de volta por cima quando o Windows mostra a área de trabalho
/// (Win+D, "Mostrar área de trabalho").
///
/// <para><b>O que o Windows faz.</b> Não minimiza nem esconde nada nosso: as duas janelas
/// seguem visíveis e topmost. Ele sobe a área de trabalho (<c>Progman</c>) para logo abaixo
/// da barra do Windows — que é a nossa, apagada e atravessável, mas ainda na pilha — e tudo o
/// que estava abaixo dela fica coberto. Nesta máquina a barra do Windows fica no topo, em cima
/// da barra de cima: era ela que sumia no Win+D, e a dock, acima da barra do Windows na
/// pilha, ficava. Com a barra do Windows embaixo seria a dock. Medido em 26/10/2026 com o
/// <c>Shell.Application.ToggleDesktop</c>, o mesmo comando do Win+D: um
/// <c>SetWindowPos(HWND_TOPMOST)</c> na barra coberta a trouxe de volta na hora, por cima da
/// área de trabalho, e desfazer o comando não a tirou da tela.</para>
///
/// <para><b>Quando olhar.</b> A ordem em que o explorer sobe a área de trabalho e troca o
/// primeiro plano não ficou medida, e por isso a conferência não confia num instante só:
/// quando o primeiro plano passa à área de trabalho ou à barra do Windows ela olha na hora e
/// mais algumas vezes no segundo seguinte. Um relógio de um segundo cobre o caminho que não
/// troque o primeiro plano. Olhar é subir a pilha a partir da nossa janela, que é topmost e
/// tem pouca coisa por cima.</para>
/// </summary>
public sealed class DesktopRaiseGuard : IDisposable
{
    private readonly Func<IEnumerable<nint>> _windows;
    private readonly WinEventProc _proc;
    private readonly nint _hook;
    private readonly DispatcherTimer _soon = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _slow = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private int _soonLeft;

    /// <param name="windows">As janelas a defender, perguntadas a cada conferência: a barra de
    /// cima nasce e morre com a opção.</param>
    public DesktopRaiseGuard(Func<IEnumerable<nint>> windows)
    {
        _windows = windows;
        _proc = OnForeground;   // campo: o delegado não pode ser coletado enquanto o gancho existir
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _proc, 0, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _soon.Tick += (_, _) =>
        {
            Raise();
            if (--_soonLeft <= 0) _soon.Stop();
        };
        _slow.Tick += (_, _) => Raise();
        _slow.Start();
    }

    private void OnForeground(nint hook, uint ev, nint hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return;
        if (WindowService.ClassOf(GetAncestor(hWnd, GA_ROOT)) is not ("Progman" or "WorkerW" or
                                                                      "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
            return;

        Raise();
        _soonLeft = 10;
        _soon.Start();
    }

    private void Raise()
    {
        foreach (var janela in _windows())
        {
            if (janela == 0 || !IsWindowVisible(janela) || !UnderDesktop(janela)) continue;

            SetWindowPos(janela, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Log.Trace($"área de trabalho por cima de {janela:X}; posta de volta no topo");
        }
    }

    /// <summary>A área de trabalho está acima desta janela na pilha?</summary>
    private static bool UnderDesktop(nint janela)
    {
        var acima = GetWindow(janela, GW_HWNDPREV);
        for (var i = 0; acima != 0 && i < 500; i++, acima = GetWindow(acima, GW_HWNDPREV))
            if (IsWindowVisible(acima) && WindowService.ClassOf(acima) is "Progman" or "WorkerW")
                return true;

        return false;
    }

    public void Dispose()
    {
        _soon.Stop();
        _slow.Stop();
        if (_hook != 0) UnhookWinEvent(_hook);
    }
}
