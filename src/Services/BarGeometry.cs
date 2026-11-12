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
    /// A área de trabalho com a faixa da barra do Windows devolvida: no lado em que a barra está,
    /// a área vai até a faixa do WinDock que estiver nele — a dock, a barra de cima — ou até a
    /// borda da tela. Os outros três lados não são tocados.
    ///
    /// <para><b>A borda é gravada, e não somada.</b> A conta antiga partia da área de agora e
    /// somava a altura da barra ("devolver o que ela ocupa"), com uma guarda para não somar duas
    /// vezes: só enquanto a área ainda parasse antes da barra. A guarda falha justamente quando há
    /// mais coisa reservada naquela borda do que a espessura da barra — depois de uma soma a área
    /// ainda para antes da barra, e a passagem seguinte soma de novo, e a seguinte também, até
    /// cruzá-la: o resultado dependia de quantas vezes o relógio tinha passado. E só dava certo
    /// com a dock na mesma borda por causa do limite posto na 1.0.0.75; sem o lugar dela, a área
    /// andava uma barra por passagem. Gravando a borda, o resultado é o mesmo na primeira
    /// passagem e na centésima, e vale a partir de qualquer estado — inclusive o que sobra de uma
    /// dock que morreu sem desfazer (revisão de 12/11/2026).</para>
    ///
    /// <para>O limite é onde a dock está de fato, e não a espessura dela contada da borda: com a
    /// barra só recolhida, a dock fica empilhada acima da faixa em que a barra reaparece. E só a
    /// dock que está nesta tela e reserva faixa conta — quem chama entrega vazio nos outros
    /// casos.</para>
    ///
    /// <para><b>O que se perde:</b> a barra de outro programa na mesma borda da barra do Windows
    /// deixa de ter a faixa dela descontada. A conta antiga guardava o <i>tamanho</i> dessa faixa
    /// quando ela era mais fina que a barra (e a comia aos poucos quando era mais grossa), mas o
    /// lugar já estava errado de qualquer jeito: o shell empilha essa barra depois da barra do
    /// Windows escondida, longe da borda. Em outra borda nada muda.</para>
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

        // o lugar da dock vale dentro da tela: um retângulo de outra resolução não leva a área
        // para fora dela
        if (horizontal && bar.Top <= screen.Top)
        {
            var piso = screen.Top + Math.Max(0, topReserve);
            if (Na(DockEdge.Top)) piso = Math.Max(piso, dock.Bottom);
            work.Top = Math.Clamp(piso, screen.Top, screen.Bottom);
        }
        else if (horizontal)
        {
            work.Bottom = Na(DockEdge.Bottom) ? Math.Clamp(dock.Top, screen.Top, screen.Bottom) : screen.Bottom;
        }
        else if (bar.Left <= screen.Left)
        {
            work.Left = Na(DockEdge.Left) ? Math.Clamp(dock.Right, screen.Left, screen.Right) : screen.Left;
        }
        else
        {
            work.Right = Na(DockEdge.Right) ? Math.Clamp(dock.Left, screen.Left, screen.Right) : screen.Right;
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
