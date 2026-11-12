using System.Runtime.InteropServices;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// O retângulo que se vê da janela de outro programa, e como pô-la num lugar. Era do mosaico; saiu
/// dele quando o <see cref="StripGuard"/> passou a precisar do mesmo com o mosaico desligado.
/// </summary>
internal static class WindowFrame
{
    /// <summary>O retângulo de verdade de uma janela, em pixels físicos —
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, que já exclui a margem invisível de redimensionar
    /// (uns 7px de cada lado que não aparecem na tela). Nem toda janela responde a isso (estilo
    /// clássico, sem composição, dá erro) — por isso a saída de reserva com o
    /// <c>GetWindowRect</c> puro, senão essas nunca teriam retângulo nenhum pra contorno ou
    /// pra busca de vizinho.</summary>
    public static bool TryGetVisualRect(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0) return true;
        return GetWindowRect(hwnd, out rect);
    }

    /// <summary>
    /// Quanto a moldura de verdade da janela (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>) sobra
    /// pra fora do retângulo que o Windows relata (<c>GetWindowRect</c>) — a margem
    /// invisível de redimensionar que o Windows 11 deixa na maioria das janelas comuns.
    /// Sem compensar isso, cada janela ficaria alguns pixels menor e deslocada do que o
    /// mosaico pediu, e um gap de 8 px virava um gap de 15 num lado e 1 no outro.
    /// </summary>
    public static RECT Margin(nint hwnd)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visual, Marshal.SizeOf<RECT>()) != 0)
            return default;
        if (!GetWindowRect(hwnd, out var actual)) return default;

        return new RECT
        {
            Left = visual.Left - actual.Left,
            Top = visual.Top - actual.Top,
            Right = actual.Right - visual.Right,
            Bottom = actual.Bottom - visual.Bottom
        };
    }

    /// <summary>
    /// Move e redimensiona uma janela para caber exatamente em <paramref name="target"/>,
    /// compensando a margem invisível que o Windows 11 deixa em volta da moldura.
    ///
    /// Devolve se a janela <b>já está</b> no lugar quando isto volta. Com a janela atendendo, o
    /// movimento é feito esperando por ela, como sempre foi. Com ela ocupada
    /// (<see cref="WindowService.Responds"/>) o pedido vai para a fila dela
    /// (<c>SWP_ASYNCWINDOWPOS</c>) e isto devolve falso: ela chega ao lugar quando voltar a
    /// atender, e quem precisa conferir o tamanho confere depois. Esperar aqui era a dock inteira
    /// parada enquanto um programa recém-aberto carregava — o caso mais comum é justamente a
    /// janela nova, que o mosaico centraliza ou encaixa no instante em que ela aparece
    /// (12/11/2026). Quem já pede <c>SWP_ASYNCWINDOWPOS</c> nem pergunta.
    /// </summary>
    public static bool Place(nint hwnd, RECT target, nint after = 0, uint extraFlags = SWP_NOZORDER)
    {
        var espera = (extraFlags & SWP_ASYNCWINDOWPOS) == 0 && WindowService.Responds(hwnd);

        if (IsIconic(hwnd))
        {
            if (espera) ShowWindow(hwnd, SW_RESTORE);
            else ShowWindowAsync(hwnd, SW_RESTORE);
        }

        var margin = Margin(hwnd);

        SetWindowPos(hwnd, after,
            target.Left - margin.Left,
            target.Top - margin.Top,
            target.Width + margin.Left + margin.Right,
            target.Height + margin.Top + margin.Bottom,
            extraFlags | SWP_NOACTIVATE | (espera ? 0 : SWP_ASYNCWINDOWPOS));

        return espera;
    }

    /// <summary>
    /// Janela solta em cima da dock (ou embaixo da barra superior) volta para dentro da área útil.
    /// Devolve se moveu.
    ///
    /// O grid e a janela maximizada já respeitam as faixas reservadas: os dois partem do
    /// <c>rcWork</c>, que o registro de AppBar encolhe. A janela solta não — o Windows deixa
    /// arrastá-la para qualquer lugar, e o app reabre com o tamanho que gravou, e a dock, que fica
    /// sempre por cima, cobria a parte de baixo dela. Ela sobe (ou desce) o que for preciso, com o
    /// mesmo tamanho; só encolhe se for mais alta que a área útil inteira.
    ///
    /// Só as faixas de cima e de baixo contam: pelos lados não há barra, e deixar meia janela para
    /// fora da tela pela lateral é gesto comum. Maximizada e tela cheia ficam como estão — ocupar o
    /// monitor é a intenção. Com "Reservar a faixa" desligado a área útil é a tela inteira, e nada se
    /// move; no notebook, sem dock, idem.
    ///
    /// Vai sempre para o log, e não só com o rastro ligado (pedido de 06/11/2026): o caso que
    /// motivou isto aconteceu na máquina de outra pessoa, e "às vezes". O
    /// <paramref name="motivo"/> diz o que fez a janela ser olhada ("abriu", "largada", …).
    ///
    /// <paramref name="semEsperar"/> pede o movimento sem esperar a janela responder
    /// (<c>SWP_ASYNCWINDOWPOS</c>). É o do <see cref="StripGuard"/>, que olha toda janela que vem
    /// para o primeiro plano: esperar um programa ocupado seria a dock parada junto com ele. Sem
    /// ele, o <see cref="Place"/> ainda deixa de esperar a janela que não atende.
    /// </summary>
    public static bool KeepInsideWorkArea(nint hwnd, string motivo, bool semEsperar = false)
    {
        if (!IsWindow(hwnd) || IsZoomed(hwnd) || IsIconic(hwnd)) return false;
        if (!TryGetVisualRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var work = info.rcWork;
        var tela = info.rcMonitor;

        if (r.Left <= tela.Left && r.Top <= tela.Top && r.Right >= tela.Right && r.Bottom >= tela.Bottom) return false;

        var altura = Math.Min(r.Height, work.Height);
        var topo = Math.Clamp(r.Top, work.Top, work.Bottom - altura);
        if (topo == r.Top && altura == r.Height) return false;

        Log.Write($"faixa reservada: {Describe(hwnd)} {motivo} em y {r.Top}..{r.Bottom}, fora da área útil " +
                  $"{work.Top}..{work.Bottom} — movida para y {topo}..{topo + altura}" +
                  (altura < r.Height ? $" (encolhida de {r.Height} para {altura})" : ""));

        Place(hwnd, new RECT { Left = r.Left, Top = topo, Right = r.Right, Bottom = topo + altura },
              extraFlags: SWP_NOZORDER | (semEsperar ? SWP_ASYNCWINDOWPOS : 0));
        return true;
    }

    /// <summary>
    /// A janela no log: o título curto, o programa e a classe. O título é o que identifica o app
    /// instalado pelo navegador — o WhatsApp Web roda no chrome.exe —, e é lido sem mandar
    /// mensagem à janela: um programa travado não segura a dock só para ser descrito.
    /// </summary>
    public static string Describe(nint hwnd)
    {
        var texto = new System.Text.StringBuilder(80);
        InternalGetWindowText(hwnd, texto, texto.Capacity);
        var titulo = texto.Length > 60 ? texto.ToString(0, 60) + "…" : texto.ToString();

        GetWindowThreadProcessId(hwnd, out var pid);
        string programa;
        try { programa = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { programa = $"pid {pid}"; }

        return $"'{titulo}' ({programa}, {WindowService.ClassOf(hwnd)})";
    }
}
