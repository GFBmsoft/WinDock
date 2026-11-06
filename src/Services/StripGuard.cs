using System.Runtime.InteropServices;
using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Não deixa janela solta embaixo da dock, nem embaixo da barra de cima: a que aparece, é
/// ativada, é restaurada ou é largada invadindo uma faixa reservada volta para a área útil
/// (<see cref="WindowFrame.KeepInsideWorkArea"/>).
///
/// <para>Relato de um usuário, 06/11/2026: "às vezes abro um app tipo o WhatsApp Web e ele fica
/// embaixo da dock quando vou digitar uma mensagem, às vezes até no Discord". A faixa reservada só
/// segura as janelas <b>maximizadas</b>: o Windows encolhe a área útil e as ajusta, mas a janela
/// solta fica no tamanho e no lugar em que estava, e esses apps reabrem com o tamanho que gravaram
/// — gravado, às vezes, com a parte de baixo da tela livre. Nos apps de conversa é ali que fica a
/// caixa de mensagem. O mosaico já fazia isso com a janela largada; na máquina de quem fez a dock
/// ele está ligado, e era por isso que aqui nunca aparecia.</para>
///
/// <para>Sem opção própria: com "Reservar a faixa" desligado a área útil é a tela inteira e nada se
/// move, e com o mosaico ligado a janela já está dentro.</para>
///
/// <para><b>O que não dá para mover também vai para o log.</b> Uma janela maximizada por baixo da
/// dock quer dizer que a faixa não está reservada — a opção desligada, ou o explorer que
/// reiniciou e esqueceu a AppBar —, e é isso que separa um caso do outro quando ele volta a
/// acontecer na máquina de alguém (pedido de 06/11/2026).</para>
/// </summary>
internal sealed class StripGuard : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DockConfig _config;
    private readonly Func<RECT> _dock;
    private readonly WinEventProc _proc;
    private readonly List<nint> _hooks = new();

    /// <summary>O último lugar anotado de cada janela que ficou por baixo sem poder ser movida: uma
    /// linha por lugar, e não uma a cada vez que ela volta ao primeiro plano.</summary>
    private readonly Dictionary<nint, RECT> _anotadas = new();

    /// <param name="dock">A faixa que a dock ocupa, em pixels físicos (a do <c>AppBar</c>).</param>
    public StripGuard(Dispatcher dispatcher, DockConfig config, Func<RECT> dock)
    {
        _dispatcher = dispatcher;
        _config = config;
        _dock = dock;
        _proc = OnEvent;   // campo: o delegado não pode ser coletado enquanto o gancho existir

        void Hook(uint ev) =>
            _hooks.Add(SetWinEventHook(ev, ev, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

        Hook(EVENT_OBJECT_SHOW);           // o app abriu, já no tamanho que gravou
        Hook(EVENT_SYSTEM_FOREGROUND);     // a que já estava aberta quando a dock subiu
        Hook(EVENT_SYSTEM_MINIMIZEEND);    // voltou do minimizado
        Hook(EVENT_SYSTEM_MOVESIZEEND);    // largada em cima da dock
    }

    private void OnEvent(nint hook, uint ev, nint hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hWnd == 0) return;

        // o primeiro plano pode vir de uma janela de dentro (o Terminal anuncia o foco pela ilha
        // XAML); os outros, só a janela inteira: mostrar um controle não diz nada
        var janela = ev == EVENT_SYSTEM_FOREGROUND ? GetAncestor(hWnd, GA_ROOT) : hWnd;
        if (janela == 0 || GetAncestor(janela, GA_ROOT) != janela) return;

        // o EVENT_OBJECT_SHOW vem de toda janela do sistema — dicas, menus —, e só a que tem botão
        // na barra (a mesma regra do mosaico e da dock) chega a ser agendada
        if (!WindowService.IsAltTabWindow(janela)) return;

        var motivo = ev switch
        {
            EVENT_OBJECT_SHOW => "abriu",
            EVENT_SYSTEM_FOREGROUND => "ativada",
            EVENT_SYSTEM_MINIMIZEEND => "restaurada",
            _ => "largada"
        };

        _dispatcher.InvokeAsync(() => Fit(janela, motivo, tentativas: 25), DispatcherPriority.Background);
    }

    /// <summary>
    /// Com o botão do mouse ainda apertado, espera ele ser solto: a janela ativada por clique,
    /// movida entre o apertar e o soltar, receberia o soltar em outro ponto dela — num item de
    /// lista, por exemplo, que nunca foi o que a pessoa clicou.
    /// </summary>
    private void Fit(nint janela, string motivo, int tentativas)
    {
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 && tentativas > 0)
        {
            var espera = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            espera.Tick += (_, _) => { espera.Stop(); Fit(janela, motivo, tentativas - 1); };
            espera.Start();
            return;
        }

        if (WindowFrame.KeepInsideWorkArea(janela, motivo, semEsperar: true))
        {
            _anotadas.Remove(janela);
            return;
        }

        NoteUnderDock(janela, motivo);
    }

    /// <summary>
    /// A janela continua por baixo da dock sem ter sido movida: maximizada numa área útil que não
    /// desconta a faixa, ou com a reserva desligada. Não há o que fazer daqui, mas o log diz qual
    /// dos dois era.
    /// </summary>
    private void NoteUnderDock(nint janela, string motivo)
    {
        var dock = _dock();
        if (dock.Right <= dock.Left || dock.Bottom <= dock.Top) return;
        if (!IsWindow(janela) || !IsWindowVisible(janela) || IsIconic(janela)) return;
        if (!WindowFrame.TryGetVisualRect(janela, out var r)) return;

        var tocam = r.Left < dock.Right && r.Right > dock.Left && r.Top < dock.Bottom && r.Bottom > dock.Top;
        if (!tocam)
        {
            _anotadas.Remove(janela);
            return;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(janela, MONITOR_DEFAULTTONEAREST), ref info)) return;
        var tela = info.rcMonitor;
        var work = info.rcWork;

        // tela cheia: a dock sai da frente sozinha (FullScreenWatcher)
        if (r.Left <= tela.Left && r.Top <= tela.Top && r.Right >= tela.Right && r.Bottom >= tela.Bottom) return;

        if (_anotadas.TryGetValue(janela, out var antes) &&
            antes.Left == r.Left && antes.Top == r.Top && antes.Right == r.Right && antes.Bottom == r.Bottom) return;
        if (_anotadas.Count > 200) _anotadas.Clear();
        _anotadas[janela] = r;

        var naoDescontada = work.Left < dock.Right && work.Right > dock.Left &&
                            work.Top < dock.Bottom && work.Bottom > dock.Top;
        var porque = !_config.ReserveSpace ? "\"Reservar a faixa\" está desligado"
                   : naoDescontada ? "a área útil não desconta a faixa da dock (o explorer esqueceu a AppBar?)"
                   : IsZoomed(janela) ? "maximizada além da área útil: o próprio app calculou o tamanho"
                   : dock.Height > dock.Width ? "a dock está na lateral, e pelos lados a janela não é movida"
                   : "a janela não se deixou mover";

        Log.Write($"faixa reservada: {WindowFrame.Describe(janela)} {motivo}, maximizada={IsZoomed(janela)}, " +
                  $"continua por baixo da dock: janela y {r.Top}..{r.Bottom}, dock y {dock.Top}..{dock.Bottom}, " +
                  $"área útil y {work.Top}..{work.Bottom} — {porque}");
    }

    public void Dispose()
    {
        foreach (var h in _hooks) if (h != 0) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
