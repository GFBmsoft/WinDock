using System.Runtime.InteropServices;
using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>O que a WinDock faz com a barra de tarefas do Windows enquanto esta rodando.</summary>
public enum TaskbarMode
{
    /// <summary>Nao mexe: a barra fica como o usuario deixou.</summary>
    Keep,

    /// <summary>Some, e volta enquanto o mouse estiver encostado na borda dela.</summary>
    AutoHide,

    /// <summary>Some enquanto a dock roda. O menu iniciar vira o launcher (Alt+Espaco).</summary>
    Hidden
}

/// <summary>
/// Esconde a barra nativa e devolve os pixels dela para a area de trabalho.
///
/// Nao da para esconder so os botoes dos apps: no Windows 11 eles sao desenhados em XAML
/// dentro da propria barra (a antiga MSTaskListWClass ja vem oculta), entao nao existe
/// janela Win32 para mexer. O que da e sumir com a barra inteira.
///
/// Duas coisas que parecem obvias e nao funcionam nesta versao do Windows, testadas aqui:
/// <c>ABM_SETSTATE</c> com <c>ABS_AUTOHIDE</c> nao muda nada (o estado continua zero), e o
/// byte de auto-hide do <c>StuckRects3</c> nao vale mais nem depois de reiniciar o Explorer.
/// O que funciona e <c>SPI_SETWORKAREA</c> <b>sem</b> <c>SPIF_SENDCHANGE</c>: com o aviso o
/// shell recalcula na hora e desfaz o que acabamos de pedir; sem ele, a area de trabalho fica.
///
/// Por isso o auto-hide aqui e nosso: a barra e escondida e volta quando o mouse encosta na
/// borda. Tudo e desfeito no <see cref="Dispose"/> — uma barra que nao voltasse deixaria a
/// maquina sem shell visivel ate o proximo logon.
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private TaskbarMode _mode = TaskbarMode.Keep;
    private readonly DispatcherTimer _timer;
    private int _ticks;

    /// <summary>
    /// Faixa que a barra de cima do WinDock ocupa, quando ela existe. A barra do Windows
    /// tambem fica no topo: devolver os pixels dela sem saber desta levaria as duas
    /// juntas, e as janelas maximizadas passariam por baixo da nossa.
    /// </summary>
    public int TopReserve { get; set; }

    public TaskbarService()
    {
        // curto o bastante para a barra aparecer sem atraso perceptivel quando o mouse
        // encosta na borda, e barato: so le a posicao do cursor
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _timer.Tick += (_, _) => OnTick();
    }

    public void Apply(TaskbarMode mode)
    {
        _mode = mode;

        switch (mode)
        {
            case TaskbarMode.Keep:
                _timer.Stop();
                Restore();
                break;

            case TaskbarMode.AutoHide:
                SetTransparent(true);
                Reclaim();
                _timer.Start();
                break;

            case TaskbarMode.Hidden:
                SetTransparent(true);
                Reclaim();
                // o timer roda aqui tambem: o shell reexibe a barra sozinho em varias
                // ocasioes (ao abrir o painel de wi-fi ou o de notificacoes, por exemplo),
                // e ela volta por cima de tudo — inclusive roubando os cliques de quem
                // estiver embaixo
                _timer.Start();
                break;
        }
    }

    /// <summary>
    /// Reaplica a area de trabalho: o shell a recalcula sozinho sempre que uma appbar muda
    /// (a nossa, quando a espessura da dock muda), e no recalculo a barra escondida volta a
    /// cobrar o espaco dela.
    /// </summary>
    public void Refresh()
    {
        if (_mode == TaskbarMode.Keep) return;
        Reclaim();
    }

    /// <summary>
    /// Enquanto houver alguem segurando, o vigia nao mexe na barra do Windows.
    ///
    /// Existe por causa da leitura da bandeja: ela precisa da barra do shell de pe por meio
    /// segundo, e este relogio, que roda a cada 150 ms, a escondia no meio do caminho — o
    /// painel de icones ocultos nao abria e o clique na seta simplesmente nao fazia nada, de
    /// vez em quando.
    ///
    /// E um contador, e nao um booleano, para duas operacoes sobrepostas nao desligarem o
    /// vigia uma da outra. Estatico porque a barra do Windows e uma so, e quem precisa
    /// pausar o vigia (o servico da bandeja) nao tem — nem deveria ter — uma referencia a
    /// instancia que a dock guarda.
    /// </summary>
    private static int _suspensions;

    public static IDisposable Suspend() => new Suspension();

    private sealed class Suspension : IDisposable
    {
        public Suspension() => Interlocked.Increment(ref _suspensions);
        public void Dispose() => Interlocked.Decrement(ref _suspensions);
    }

    private void OnTick()
    {
        if (Volatile.Read(ref _suspensions) > 0) return;

        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !GetWindowRect(bar, out var rect)) return;

        if (_mode == TaskbarMode.Hidden)
        {
            // o shell reexibe a barra sozinho em várias ocasiões; aqui ela volta a ficar
            // apagada — e "apagada" agora é a camada, não o `SW_HIDE`
            if (!IsTransparent(bar) || !IsWindowVisible(bar)) SetTransparent(true);
        }
        else
        {
            if (!GetCursorPos(out var cursor)) return;

            // a barra aparece com o mouse dentro da faixa dela e some quando ele sai — e
            // aparecer aqui é ficar opaca de novo, não voltar do `SW_HIDE`: alternar a
            // visibilidade fazia o shell recalcular a área de trabalho a cada ida e volta do
            // cursor, com todas as janelas maximizadas junto
            var inside = cursor.X >= rect.Left && cursor.X <= rect.Right &&
                         cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;

            if (inside == IsTransparent(bar)) SetTransparent(!inside);
        }

        // de tempos em tempos confere se o shell devolveu o espaco da barra para si
        if (++_ticks < 7) return;
        _ticks = 0;
        Reclaim();
    }

    private static void SetVisible(bool visible) =>
        ForEachBar(h => ShowWindow(h, visible ? SW_SHOW : SW_HIDE));

    /// <summary>
    /// Apaga a barra do Windows **sem escondê-la**: <c>WS_EX_LAYERED</c> com alfa zero some
    /// com o desenho, e <c>WS_EX_TRANSPARENT</c> faz o mouse atravessá-la.
    ///
    /// A diferença em relação ao <c>SW_HIDE</c> não é estética, é o custo de tudo que
    /// depende dela. Escondida, a barra precisa **voltar** toda vez que a bandeja é lida —
    /// e é a volta que sai cara: o shell recalcula a área de trabalho (medido aqui, ela
    /// oscilava 24 → 32 → 24 px a cada abertura do cartão), o painel de ícones ocultos só
    /// abre com ela de pé, e a janela em que a pessoa está trabalhando pisca a barra de
    /// título. De pé o tempo todo, porém invisível, nada disso acontece: a leitura encontra
    /// a barra onde precisa e não mexe em nada.
    ///
    /// O espaço dela continua sendo devolvido à área de trabalho pelo <see cref="Reclaim"/>,
    /// exatamente como antes — quem cobra o espaço é a appbar registrada, e essa parte não
    /// muda com a camada.
    ///
    /// Se o WinDock morrer sem desfazer, a barra fica invisível; quem conserta é o
    /// <c>TrayService.ClearLeftovers</c>, que roda ao subir e tira a camada de qualquer
    /// janela do shell que a tenha.
    /// </summary>
    private static void SetTransparent(bool on) => ForEachBar(h =>
    {
        var style = (long)GetWindowLongPtr(h, GWL_EXSTYLE);

        if (on)
        {
            ShowWindow(h, SW_SHOW);   // pode estar escondida de uma versão anterior
            SetWindowLongPtr(h, GWL_EXSTYLE, (nint)(style | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA);
            return;
        }

        SetWindowLongPtr(h, GWL_EXSTYLE, (nint)(style & ~WS_EX_LAYERED & ~WS_EX_TRANSPARENT));
    });

    private static bool IsTransparent(nint bar) =>
        ((long)GetWindowLongPtr(bar, GWL_EXSTYLE) & WS_EX_LAYERED) != 0;

    /// <summary>A barra principal e as das telas secundarias.</summary>
    private static void ForEachBar(Action<nint> action)
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != 0) action(main);

        nint secondary = 0;
        while ((secondary = FindWindowEx(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0)
            action(secondary);
    }

    /// <summary>
    /// Devolve para a area de trabalho a faixa que a barra ocupa. So o lado dela e mexido:
    /// a faixa que a dock reserva continua reservada, mesmo quando as duas estao na mesma
    /// borda da tela.
    /// </summary>
    private void Reclaim()
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !GetWindowRect(bar, out var rect)) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(bar, MONITOR_DEFAULTTOPRIMARY), ref info)) return;

        var screen = info.rcMonitor;
        var work = info.rcWork;
        var horizontal = rect.Width >= rect.Height;

        // As comparacoes com o retangulo da barra sao o que torna isto repetivel: so
        // devolve o espaco se ele ainda estiver reservado. Sem essa guarda, cada passagem
        // descontava a mesma altura de novo e acabava comendo a faixa de quem veio depois
        // — a barra de cima do WinDock, por exemplo.
        if (horizontal && rect.Top <= screen.Top)
        {
            // devolve o espaco da barra do Windows, mas nunca abaixo da faixa da nossa
            // barra de cima: as duas ficam na mesma borda e o shell so conta a mais alta,
            // entao esse piso e a unica forma de a nossa continuar reservada
            var free = work.Top >= rect.Bottom ? work.Top - rect.Height : work.Top;
            work.Top = Math.Max(screen.Top + TopReserve, free);
        }
        else if (horizontal)
        {
            if (work.Bottom <= rect.Top) work.Bottom = Math.Min(screen.Bottom, work.Bottom + rect.Height);
        }
        else if (rect.Left <= screen.Left)
        {
            if (work.Left >= rect.Right) work.Left = Math.Max(screen.Left, work.Left - rect.Width);
        }
        else
        {
            if (work.Right <= rect.Left) work.Right = Math.Min(screen.Right, work.Right + rect.Width);
        }

        if (work.Left == info.rcWork.Left && work.Top == info.rcWork.Top &&
            work.Right == info.rcWork.Right && work.Bottom == info.rcWork.Bottom) return;

        // sem SPIF_SENDCHANGE de proposito: com o aviso o shell recalcula e desfaz
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref work, 0);
    }

    /// <summary>Barra de volta e area de trabalho recalculada pelo shell, como estava.</summary>
    private static void Restore()
    {
        SetTransparent(false);
        SetVisible(true);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !GetMonitorInfo(MonitorFromWindow(bar, MONITOR_DEFAULTTOPRIMARY), ref info)) return;

        // aqui o aviso e o que queremos: o shell refaz a conta e a barra cobra o espaco dela
        var work = info.rcWork;
        SystemParametersInfo(SPI_SETWORKAREA, 0, ref work, SPIF_SENDCHANGE);
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_mode != TaskbarMode.Keep) Restore();
        _mode = TaskbarMode.Keep;
    }
}
