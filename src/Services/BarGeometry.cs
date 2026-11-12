using WinDock.Interop;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// As duas contas de retângulo que decidem onde a dock fica e onde a área de trabalho termina
/// quando a barra do Windows está escondida — sem tocar em janela nenhuma.
///
/// Estão separadas do <see cref="TaskbarService"/> e do <see cref="AppBar"/> por um motivo: nesta
/// máquina a barra do Windows fica no topo e a dock embaixo, e o defeito que as criou (EV07,
/// 06/11/2026) veio da máquina de outra pessoa, com a barra embaixo. Mudar a barra de borda aqui
/// pede reiniciar o explorer; conferir a conta, não. O <c>tests/BarCheck</c> compila este arquivo
/// e passa por todas as bordas — por isso nada aqui chama o Windows.
/// </summary>
internal static class BarGeometry
{
    /// <summary>
    /// A área de trabalho com a faixa da barra do Windows devolvida.
    /// </summary>
    /// <param name="screen">O monitor da barra do Windows.</param>
    /// <param name="work">A área de trabalho dele agora.</param>
    /// <param name="bar">A barra do Windows.</param>
    /// <param name="topReserve">A espessura da barra de cima do WinDock, ou zero sem ela.</param>
    /// <param name="dock">Onde a dock está, ou vazio quando ela não reserva faixa nesta tela.</param>
    public static RECT Reclaimed(RECT screen, RECT work, RECT bar, int topReserve, DockEdge dockEdge, RECT dock)
    {
        var temDock = dock.Right > dock.Left && dock.Bottom > dock.Top;
        bool Na(DockEdge borda) => temDock && dockEdge == borda;
        var horizontal = bar.Width >= bar.Height;

        // As comparacoes com o retangulo da barra sao o que torna isto repetivel: so
        // devolve o espaco se ele ainda estiver reservado. Sem essa guarda, cada passagem
        // descontava a mesma altura de novo e acabava comendo a faixa de quem veio depois
        // — a barra de cima do WinDock, por exemplo.
        //
        // Em toda borda o espaço devolvido para na faixa do WinDock que estiver nela: as duas
        // ficam na mesma borda e o shell só conta a mais grossa, então esse limite é a única forma
        // de a nossa continuar reservada. No topo era só a barra de cima; embaixo não havia limite
        // nenhum, e a área útil ia até o fim da tela por cima da dock (EV07). O limite é onde a
        // dock está de fato, e não a espessura dela contada da borda: com a barra só recolhida, a
        // dock fica empilhada acima da faixa em que a barra reaparece.
        if (horizontal && bar.Top <= screen.Top)
        {
            var free = work.Top >= bar.Bottom ? work.Top - bar.Height : work.Top;
            var piso = screen.Top + topReserve;
            if (Na(DockEdge.Top)) piso = Math.Max(piso, dock.Bottom);
            work.Top = Math.Max(piso, free);
        }
        else if (horizontal)
        {
            if (work.Bottom <= bar.Top)
                work.Bottom = Math.Min(Na(DockEdge.Bottom) ? dock.Top : screen.Bottom, work.Bottom + bar.Height);
        }
        else if (bar.Left <= screen.Left)
        {
            if (work.Left >= bar.Right)
                work.Left = Math.Max(Na(DockEdge.Left) ? dock.Right : screen.Left, work.Left - bar.Width);
        }
        else
        {
            if (work.Right <= bar.Left)
                work.Right = Math.Min(Na(DockEdge.Right) ? dock.Left : screen.Right, work.Right + bar.Width);
        }

        return work;
    }

    /// <summary>
    /// Onde a dock fica com a barra do Windows escondida: no lugar que o shell respondeu
    /// (<paramref name="queried"/>, o <c>ABM_QUERYPOS</c>), desfeito <b>só</b> o empurrão que veio
    /// da barra do Windows.
    ///
    /// A barra nativa escondida continua registrada, e o shell arruma a dock em volta dela: na
    /// mesma borda, a dock fica empilhada acima de uma faixa vazia (EV07); na lateral, termina
    /// antes da faixa. A 1.0.0.75 resolveu isso mandando a dock ignorar <b>todas</b> as barras —
    /// e aí ela ignorava também a barra de cima do próprio WinDock (dock no topo: as duas nos
    /// mesmos pixels; dock na lateral: de alto a baixo, cruzando o canto da barra de cima) e
    /// qualquer barra de outro programa. Aqui a pergunta ao shell volta a ser feita, e só o lado
    /// que parou <b>exatamente</b> na barra do Windows volta para onde a dock queria estar. Um
    /// lado que parou em outro lugar parou em outra barra, e fica.
    ///
    /// O <paramref name="topReserve"/> (a barra de cima do WinDock) vale como piso de qualquer
    /// jeito: ela ignora as outras barras, sobe para o topo de verdade e fica por dentro da faixa
    /// da barra do Windows quando as duas estão em cima — desfeito o empurrão da barra do Windows,
    /// o shell não tem mais como dizer onde a nossa termina.
    /// </summary>
    /// <param name="queried">O que o <c>ABM_QUERYPOS</c> devolveu, já com a espessura da dock.</param>
    /// <param name="desired">Onde a dock ficaria sozinha na tela, encostada na borda.</param>
    /// <param name="taskbar">A barra do Windows no monitor da dock, ou vazio se não houver.</param>
    public static RECT WithoutTaskbar(RECT queried, RECT desired, RECT screen, RECT taskbar,
                                      DockEdge edge, int thickness, int topReserve)
    {
        // o shell e o GetWindowRect contam o mesmo retângulo; a folga é para arredondamento de escala
        const int folga = 2;
        var r = queried;

        if (taskbar.Width > 0 && taskbar.Height > 0)
        {
            var horizontal = taskbar.Width >= taskbar.Height;

            if (horizontal && taskbar.Top <= screen.Top)
            {
                if (Math.Abs(r.Top - taskbar.Bottom) <= folga) r.Top = desired.Top;
            }
            else if (horizontal)
            {
                if (Math.Abs(r.Bottom - taskbar.Top) <= folga) r.Bottom = desired.Bottom;
            }
            else if (taskbar.Left <= screen.Left)
            {
                if (Math.Abs(r.Left - taskbar.Right) <= folga) r.Left = desired.Left;
            }
            else
            {
                if (Math.Abs(r.Right - taskbar.Left) <= folga) r.Right = desired.Right;
            }
        }

        // a espessura é contada a partir do lado que encosta na borda; na lateral e no topo a
        // barra de cima é o piso
        var piso = screen.Top + Math.Max(0, topReserve);

        switch (edge)
        {
            case DockEdge.Top:
                if (r.Top < piso) r.Top = piso;
                r.Bottom = r.Top + thickness;
                break;
            case DockEdge.Bottom:
                r.Top = r.Bottom - thickness;
                break;
            case DockEdge.Left:
                r.Right = r.Left + thickness;
                if (r.Top < piso) r.Top = piso;
                break;
            case DockEdge.Right:
                r.Left = r.Right - thickness;
                if (r.Top < piso) r.Top = piso;
                break;
        }

        return r;
    }
}
