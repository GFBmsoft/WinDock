using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.ComponentModel;
using static WinDock.Interop.Native;

namespace WinDock.Services;

public enum TilingDirection { Left, Right, Up, Down }

/// <summary>
/// Mosaico simples: divide o espaço entre as janelas abertas, sem sobreposição, com um
/// contorno marcando quem está em foco. Cada monitor tem o próprio grid, independente dos
/// outros — Ctrl+Shift+seta manda uma janela pro monitor vizinho quando ela já está na ponta
/// do grid atual.
///
/// **O modelo, de propósito simples.** Cada janela nova entra no fim de uma lista (a ordem
/// de quando apareceu); o espaço é dividido recursivamente ao meio, alternando entre corte
/// vertical e horizontal a cada nível — a primeira janela da lista fica com a primeira
/// metade, o resto recorre na segunda. Não existe uma árvore de verdade guardada em lugar
/// nenhum: a divisão é recalculada do zero a cada mudança, só a partir da lista e da ordem
/// dela — mais simples de acompanhar do que manter uma estrutura e ter que atualizá-la em
/// cada inserção e remoção.
///
/// **Foco direcional por geometria, não por ordem na lista.** Ctrl+Alt+seta acha, entre as
/// janelas do mosaico, a que tem o centro mais alinhado na direção pedida — é o que faz
/// "direita" ir para a janela realmente à direita na tela, mesmo que a ordem interna da
/// lista não bata com o arranjo visual.
///
/// **Flutuar (Alt+C) tira a janela do mosaico sem fechar nada.** Ela continua na tela, do
/// tamanho e lugar que estava, livre — e o espaço que ela ocupava no mosaico é redistribuído
/// entre as que sobraram. Esconder (Alt+Z) minimiza — a janela some da tela e do mosaico, e
/// volta para os dois ao ser restaurada pela dock ou pela barra do Windows.
/// </summary>
public sealed class TilingService : IDisposable
{
    private readonly DockConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly TilingBorderWindow _border;
    private readonly WinEventProc _proc;
    private readonly List<nint> _hooks = new();
    private DispatcherOperation? _pending;
    private bool _reclaimFocusAfterMinimize;

    /// <summary>Reafirma o contorno sozinho, várias vezes por segundo, em vez de confiar só em
    /// pegar exatamente o evento certo do Windows pra cada jeito de o z-order desandar (o
    /// Alt+Tab fechando, algum outro popup do sistema reordenando por cima, e o que mais
    /// aparecer que nem foi cogitado ainda). Um SetWindowPos redundante sem mudança nenhuma é
    /// baratíssimo; a alternativa era continuar caçando evento por evento pra sempre.</summary>
    private DispatcherTimer? _borderHeartbeat;

    /// <summary>Só existe durante um arraste de janela flutuante — liga no início e desliga no
    /// fim, pra acompanhar o contorno ao vivo sem ficar ouvindo isso o tempo todo.</summary>
    private nint _dragHook;
    private nint _draggedHwnd;

    /// <summary>A ordem em que as janelas entraram no mosaico — é toda a "árvore" que existe.</summary>
    private readonly List<nint> _tiles = new();

    /// <summary>Fora do mosaico por pedido da pessoa (Alt+C), mas ainda rastreada.</summary>
    private readonly HashSet<nint> _floating = new();

    /// <summary>Tem borda de redimensionar, mas ignora o tamanho que o mosaico pediu (comum em
    /// formulários Delphi com `Constraints` — o botão de maximizar não garante nada). Uma vez
    /// pega no flagra, fica de fora pelo resto da sessão em vez de brigar com ela a cada
    /// recálculo.</summary>
    private readonly HashSet<nint> _stubborn = new();

    /// <summary>Não bateu o tamanho pedido *uma vez* — pode ser a janela mesmo recusando, ou só
    /// o DWM ainda não ter processado o redimensionamento a tempo do `Apply()` seguinte medir.
    /// Só vira <see cref="_stubborn"/> se continuar não batendo na próxima conferência — falhar
    /// de primeira não é motivo pra banir uma janela cooperativa do mosaico pro resto da sessão.</summary>
    private readonly HashSet<nint> _suspect = new();

    /// <summary>O retângulo que cada janela do mosaico ocupa agora — usado pelo foco direcional
    /// e para posicionar o contorno sem recalcular o layout inteiro a cada troca de foco.</summary>
    private Dictionary<nint, RECT> _rects = new();

    public TilingService(DockConfig config, Dispatcher dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        _proc = OnWinEvent;
        _border = new TilingBorderWindow(config);

        _config.PropertyChanged += OnConfigChanged;
        if (_config.TilingEnabled) Start();
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DockConfig.TilingEnabled):
                if (_config.TilingEnabled) Start(); else Stop();
                break;
            case nameof(DockConfig.TilingGap):
            case nameof(DockConfig.TilingExcludedApps):
                if (_config.TilingEnabled) Refresh();
                break;
            case nameof(DockConfig.TilingBorderColor):
            case nameof(DockConfig.TilingBorderThickness):
            case nameof(DockConfig.TilingBorderRadius):
                Log.Trace($"OnConfigChanged: {e.PropertyName} -> chamando ApplyStyle()");
                _border.ApplyStyle();
                break;
        }
    }

    // ── ligar / desligar ─────────────────────────────────────

    private void Start()
    {
        void Hook(uint min, uint max) =>
            _hooks.Add(SetWinEventHook(min, max, 0, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND);

        _borderHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            (_, _) => UpdateBorder(), _dispatcher);
        _borderHeartbeat.Start();

        Refresh();
    }

    private void Stop()
    {
        StopDragTracking();
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();

        _borderHeartbeat?.Stop();
        _borderHeartbeat = null;

        _tiles.Clear();
        _floating.Clear();
        _rects.Clear();
        _border.Hide();
    }

    private void StartDragTracking(nint hwnd)
    {
        _draggedHwnd = hwnd;
        _dragHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _proc,
                                     0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private void StopDragTracking()
    {
        if (_dragHook != 0) { UnhookWinEvent(_dragHook); _dragHook = 0; }
        _draggedHwnd = 0;
    }

    private void OnWinEvent(nint hook, uint ev, nint hWnd, int idObject, int idChild, uint t, uint time)
    {
        if (idObject != 0) return;

        if (ev == EVENT_SYSTEM_FOREGROUND)
        {
            // só reposiciona o contorno — não precisa recalcular o mosaico inteiro para
            // uma simples troca de foco. O "batimento" (_borderHeartbeat) cobre qualquer
            // z-order que desande depois disso, então não precisa de recheck avulso aqui.
            _dispatcher.InvokeAsync(UpdateBorder, DispatcherPriority.Send);

            // uma janela nova pode ter passado batido pelo EVENT_OBJECT_CREATE: ele dispara
            // cedo demais na criação, antes do título/estilo dela estarem prontos, e o
            // Concerns()/IsResizable() daquele instante recusava — sem outro evento avulso
            // (abrir ou fechar outra coisa) para tentar de novo, ela ficava órfã pro resto
            // da vida: sem contorno e fora do alcance do Alt+C. Agora que está em foco de
            // verdade, ela já teve tempo de se estabelecer — se ainda é desconhecida do
            // mosaico e passa nos critérios, entra.
            if (hWnd != 0 && !_tiles.Contains(hWnd) && !_floating.Contains(hWnd) && !_stubborn.Contains(hWnd)
                && Concerns(hWnd) && IsResizable(hWnd)
                && _pending is not { Status: DispatcherOperationStatus.Pending })
            {
                _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
            }
            return;
        }

        // acompanha o contorno ao vivo, quadro a quadro, enquanto a pessoa arrasta uma janela
        // flutuando ou "teimosa" — sem isso, ele só pulava pro lugar certo depois de soltar
        // o mouse, com um atraso visível
        if (ev == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (hWnd == _draggedHwnd) _dispatcher.InvokeAsync(UpdateBorder, DispatcherPriority.Send);
            return;
        }

        if (ev == EVENT_SYSTEM_MOVESIZESTART)
        {
            if (hWnd == GetForegroundWindow() && (_floating.Contains(hWnd) || _stubborn.Contains(hWnd)))
                StartDragTracking(hWnd);
            return;
        }

        if (ev == EVENT_SYSTEM_MOVESIZEEND)
        {
            StopDragTracking();
            // uma tile arrastada só volta pro lugar quando o Refresh() (mais abaixo, com
            // debounce) rodar; o contorno de uma flutuante não depende dele pra nada
            _dispatcher.InvokeAsync(UpdateBorder, DispatcherPriority.Send);

            // arrastar uma flutuante, "teimosa" ou excluída (lista de exceções) não deve mexer
            // em quem está no grid — ela já está fora dele de propósito, então recalcular aqui
            // só reaplicaria (sem necessidade nenhuma) o retângulo de quem já estava tiled, com
            // um solavanco visível a cada arraste. Só quem ainda está no grid precisa do
            // Refresh abaixo pra voltar pro lugar (o EV04: arrastar uma tile pra fora e soltar,
            // sem isso ela ficava largada lá).
            if (_floating.Contains(hWnd) || _stubborn.Contains(hWnd)) return;
            var dragged = WindowService.Enumerate().FirstOrDefault(w => w.Handle == hWnd);
            if (dragged != null && IsExcluded(dragged)) return;
        }

        // o MINIMIZESTART dispara antes da animação acabar — o IsIconic() da janela ainda podia
        // ler falso nesse instante, e um Refresh() rodando aí a mantinha candidata: o Apply()
        // reaplicava o retângulo de tile bem no meio do minimizar e cancelava a animação (a
        // tela piscava e a janela nunca minimizava de verdade). Só o fim importa.
        if (ev == EVENT_SYSTEM_MINIMIZESTART) return;

        if (ev == EVENT_SYSTEM_MINIMIZEEND)
        {
            // não passa pelo Concerns() como os outros eventos: é a própria janela que acabou de
            // minimizar, e o estado dela (visível? cloaked?) pode ainda estar instável bem no
            // instante em que a animação termina — recusar aqui deixava a vizinha no mosaico com
            // o retângulo antigo, sem redistribuir o espaço que sobrou. O Refresh() de qualquer
            // forma recalcula do zero a partir de quem está de verdade elegível agora.
            //
            // o Windows às vezes larga o foco na área de trabalho em vez de passar pra próxima
            // janela do mosaico quando uma se minimiza — o Refresh() confere isso no final
            _reclaimFocusAfterMinimize = true;
            if (_pending is not { Status: DispatcherOperationStatus.Pending })
                _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
            return;
        }

        if (!Concerns(hWnd)) return;

        if (_pending is { Status: DispatcherOperationStatus.Pending }) return;
        _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    /// <summary>O mesmo critério que o <see cref="WindowService.Enumerate"/> já usa (visível,
    /// sem dono, não "tool window", não cloaked) — uma versão mais fraca daqui (sem checar
    /// visibilidade/cloaked) deixava passar coisas como o overlay do Alt+Tab do Windows, que
    /// ganhava contorno e disparava recálculo só de segurar Alt.</summary>
    private bool Concerns(nint hWnd) =>
        hWnd != 0 && GetAncestor(hWnd, GA_ROOT) == hWnd && WindowService.IsAltTabWindow(hWnd);

    /// <summary>Diálogos de tamanho fixo — a barra de progresso de copiar arquivo, uma caixa
    /// de mensagem — não têm borda de redimensionar nem botão de maximizar. É o mesmo sinal
    /// que o Windows usa pra saber se uma janela é "de verdade" ou só um aviso passageiro;
    /// forçar essas no mosaico as deixa espremidas num tamanho que não fazem sentido.</summary>
    private static bool IsResizable(nint hWnd)
    {
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        return (style & (WS_THICKFRAME | WS_MAXIMIZEBOX)) != 0;
    }

    /// <summary>Apps como a Calculadora do Windows têm um tamanho pensado pra ser aquele — não
    /// é um formulário "teimoso" que recusa redimensionar, é só um app que não faz sentido
    /// espremido num pedaço do grid. Em vez de tentar adivinhar isso automaticamente, a pessoa
    /// escolhe nas configurações; a janela some do mosaico automático mas continua alcançável
    /// pelo Alt+C, igual uma "teimosa" — pra flutuar manualmente quem quiser mexer nela do
    /// jeito do mosaico mesmo assim.
    ///
    /// Bate pelo nome do executável OU pelo AppUserModelID — a Calculadora (e boa parte dos
    /// apps "modernos" do Windows, Configurações incluso) não roda no próprio processo: a
    /// janela de verdade pertence ao <c>ApplicationFrameHost.exe</c>, que hospeda dezenas de
    /// apps diferentes. Só o AUMID (o mesmo que a dock já usa pra separar botão de app — ver
    /// <see cref="TaskWindow.AppKey"/>) distingue "a Calculadora" de "as Configurações" nesse
    /// caso; pelo nome do executável sozinho, excluir uma excluiria as duas.</summary>
    private bool IsExcluded(TaskWindow w)
    {
        if (_config.TilingExcludedApps.Count == 0) return false;
        var fileName = Path.GetFileName(w.ExePath);
        return _config.TilingExcludedApps.Any(p =>
            string.Equals(p, fileName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(w.Aumid) && string.Equals(p, w.Aumid, StringComparison.OrdinalIgnoreCase)));
    }

    // ── atalhos ───────────────────────────────────────────────

    /// <summary>Ctrl+Alt+seta: muda o foco para a janela vizinha naquela direção — sem vizinho
    /// nesse monitor (já está na ponta do grid) e havendo um monitor logo ali, muda o foco pra
    /// janela de lá em vez de não fazer nada. Mesma lógica de "ponta do grid → monitor vizinho"
    /// que o Ctrl+Shift+seta já usa pra mover a janela, só que aqui é o foco que atravessa.</summary>
    public void MoveFocus(TilingDirection direction)
    {
        if (!_config.TilingEnabled) return;

        var current = GetForegroundWindow();
        if (current == 0) return;

        if (_rects.TryGetValue(current, out var from))
        {
            var target = FindNeighbor(current, from, direction);
            Log.Trace($"MoveFocus {direction}: atual={current:X} está no grid, vizinho={target:X}");
            if (target != 0)
            {
                WindowService.Activate(target);
                // não espera o EVENT_SYSTEM_FOREGROUND voltar pelo hook (assíncrono, chega com
                // atraso perceptível): o contorno já sabe pra onde foi, então acompanha na hora
                UpdateBorder();
                return;
            }
        }
        else if (TryGetVisualRect(current, out var floatFrom))
        {
            // a atual está flutuando (ou "teimosa") — ela cobre o mosaico por cima, mas quem
            // está embaixo dela no mesmo monitor ainda é o destino natural do Ctrl+Alt+seta,
            // não o monitor vizinho
            var target = FocusSameMonitor(current, floatFrom, direction);
            Log.Trace($"MoveFocus {direction}: atual={current:X} está flutuando, vizinho no monitor={target:X}");
            if (target != 0)
            {
                WindowService.Activate(target);
                UpdateBorder();
                return;
            }
        }

        FocusAdjacentMonitor(current, direction);
    }

    /// <summary>Muda o foco para uma janela do monitor vizinho, na direção pedida — usada
    /// quando o Ctrl+Alt+seta chega na ponta do grid do monitor atual. Entre as janelas do
    /// mosaico no monitor de destino, escolhe a mais perto da borda por onde o foco "entraria"
    /// vindo desse lado (a mais à esquerda ao mover pra direita, e vice-versa), pra parecer uma
    /// continuação natural do grid em vez de pular pra qualquer canto.</summary>
    private void FocusAdjacentMonitor(nint current, TilingDirection direction)
    {
        var target = AdjacentMonitor(current, direction);
        Log.Trace($"FocusAdjacentMonitor {direction}: atual={current:X} monitor={MonitorFromWindow(current, MONITOR_DEFAULTTONEAREST):X} → monitor alvo={target:X}");
        if (target == 0) return;

        var candidates = new List<(nint Hwnd, RECT Rect)>();
        foreach (var (hwnd, rect) in _rects)
            if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target) candidates.Add((hwnd, rect));

        // uma janela flutuando ou "teimosa" não tem retângulo em _rects (não faz parte do
        // grid), mas ainda é um destino válido pro foco atravessar — sem isso, um monitor que
        // só tivesse uma janela flutuando ficava inalcançável pelo Ctrl+Alt+seta vindo de fora
        foreach (var hwnd in _floating.Concat(_stubborn))
        {
            if (candidates.Any(c => c.Hwnd == hwnd) || MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target)
                continue;
            if (TryGetVisualRect(hwnd, out var r)) candidates.Add((hwnd, r));
        }

        Log.Trace($"FocusAdjacentMonitor {direction}: candidatos no monitor alvo={candidates.Count}");
        if (candidates.Count == 0) return;

        var pick = direction == TilingDirection.Right
            ? candidates.OrderBy(c => c.Rect.Left).First().Hwnd
            : candidates.OrderByDescending(c => c.Rect.Right).First().Hwnd;

        Log.Trace($"FocusAdjacentMonitor {direction}: ativando {pick:X}");
        WindowService.Activate(pick);
        UpdateBorder();
    }

    /// <summary>Ctrl+Shift+seta: troca a janela em foco de lugar com a vizinha naquela direção
    /// — o foco continua na mesma janela, só o lugar dela no grid que muda. Sem vizinho pra
    /// esse lado (já está na ponta do grid, ou nem está no grid — já foi mandada pro segundo
    /// monitor antes) e havendo um monitor logo ali naquela direção, manda a janela pra lá em
    /// vez de não fazer nada — funciona pros dois lados: manda pro segundo monitor e traz de
    /// volta pro principal com a seta oposta.</summary>
    public void SwapFocused(TilingDirection direction)
    {
        if (!_config.TilingEnabled) return;

        var current = GetForegroundWindow();
        if (current == 0) return;

        if (_rects.TryGetValue(current, out var from))
        {
            var target = FindNeighbor(current, from, direction);
            if (target != 0)
            {
                var i = _tiles.IndexOf(current);
                var j = _tiles.IndexOf(target);
                (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);

                Refresh();
                return;
            }
        }

        MoveToAdjacentMonitor(current, direction);
    }

    /// <summary>Acha o monitor vizinho de quem contém <paramref name="hwnd"/>, na direção
    /// pedida — só faz sentido para Esquerda/Direita (monitores lado a lado é o arranjo comum;
    /// empilhados verticalmente não têm um sinal claro de "esquerda" ou "direita" pra escolher
    /// entre eles). Entre os que ficam do lado certo, escolhe o mais próximo — útil pra quem
    /// tem mais de dois monitores em fileira.</summary>
    private static nint AdjacentMonitor(nint hwnd, TilingDirection direction)
    {
        if (direction is not (TilingDirection.Left or TilingDirection.Right)) return 0;

        var current = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var currentInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(current, ref currentInfo)) return 0;

        nint bestMonitor = 0;
        var bestDistance = int.MaxValue;

        bool Callback(nint hMonitor, nint hdc, ref RECT rect, nint lParam)
        {
            if (hMonitor == current) return true;

            var matches = direction == TilingDirection.Right
                ? rect.Left >= currentInfo.rcMonitor.Right
                : rect.Right <= currentInfo.rcMonitor.Left;
            if (!matches) return true;

            var distance = Math.Abs(direction == TilingDirection.Right
                ? rect.Left - currentInfo.rcMonitor.Right
                : rect.Right - currentInfo.rcMonitor.Left);
            if (distance < bestDistance) { bestDistance = distance; bestMonitor = hMonitor; }
            return true;
        }

        EnumDisplayMonitors(0, 0, Callback, 0);
        return bestMonitor;
    }

    /// <summary>Manda a janela pro monitor vizinho, na mesma direção da seta. A janela sai
    /// do mosaico ao chegar lá: o recálculo seguinte já não a considera (não está mais no
    /// monitor principal) e o segundo monitor nunca foi gerenciado por aqui mesmo.</summary>
    private void MoveToAdjacentMonitor(nint hwnd, TilingDirection direction)
    {
        var bestMonitor = AdjacentMonitor(hwnd, direction);
        if (bestMonitor == 0) return;

        var targetInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(bestMonitor, ref targetInfo)) return;

        _tiles.Remove(hwnd);
        _floating.Remove(hwnd);
        _stubborn.Remove(hwnd);
        _suspect.Remove(hwnd);

        var work = targetInfo.rcWork;
        var w = work.Width * 6 / 10;
        var h = work.Height * 6 / 10;
        var target = new RECT
        {
            Left = work.Left + (work.Width - w) / 2,
            Top = work.Top + (work.Height - h) / 2,
            Right = work.Left + (work.Width - w) / 2 + w,
            Bottom = work.Top + (work.Height - h) / 2 + h
        };
        Apply(hwnd, target, HWND_TOP, extraFlags: 0);

        Refresh();
    }

    /// <summary>Acha, entre as janelas do mosaico *no mesmo monitor*, a que tem o centro mais
    /// alinhado na direção pedida — é o que faz "direita" ir para a janela realmente à direita
    /// na tela, mesmo que a ordem interna da lista não bata com o arranjo visual. Só considera
    /// o monitor atual: senão, na ponta do grid, acabaria "roubando" uma janela do monitor
    /// vizinho em vez de deixar o <see cref="MoveToAdjacentMonitor"/> cuidar da troca de tela.
    /// Sem bater a direção pra ninguém aqui, retorna 0 mesmo — é o que diz pro grid "acabou,
    /// olha o monitor vizinho".</summary>
    private nint FindNeighbor(nint current, RECT from, TilingDirection direction)
    {
        var monitor = MonitorFromWindow(current, MONITOR_DEFAULTTONEAREST);
        var candidates = _rects
            .Where(kv => kv.Key != current && MonitorFromWindow(kv.Key, MONITOR_DEFAULTTONEAREST) == monitor)
            .Select(kv => (kv.Key, kv.Value));

        return BestDirectionalMatch(from, direction, candidates);
    }

    /// <summary>Entre <paramref name="candidates"/>, a que tem o centro mais alinhado na
    /// direção pedida a partir do centro de <paramref name="from"/> — quem não bate a direção
    /// nem entra na disputa. Compartilhado pelo <see cref="FindNeighbor"/> (vizinho no grid) e
    /// pelo <see cref="FocusSameMonitor"/> (foco saindo de uma janela flutuante).</summary>
    private static nint BestDirectionalMatch(RECT from, TilingDirection direction, IEnumerable<(nint Hwnd, RECT Rect)> candidates)
    {
        var (cx, cy) = Center(from);
        nint best = 0;
        var bestScore = double.MaxValue;

        foreach (var (hwnd, rect) in candidates)
        {
            var (tx, ty) = Center(rect);
            var dx = tx - cx;
            var dy = ty - cy;

            var matches = direction switch
            {
                TilingDirection.Left  => dx < -1,
                TilingDirection.Right => dx > 1,
                TilingDirection.Up    => dy < -1,
                TilingDirection.Down  => dy > 1,
                _ => false
            };
            if (!matches) continue;

            // a distância na direção pedida pesa mais que o desvio para o lado — assim
            // "direita" prefere quem está reto à frente a quem está na diagonal, mas perto
            var primary = direction is TilingDirection.Left or TilingDirection.Right ? Math.Abs(dx) : Math.Abs(dy);
            var sideways = direction is TilingDirection.Left or TilingDirection.Right ? Math.Abs(dy) : Math.Abs(dx);
            var score = primary + sideways * 2;

            if (score < bestScore) { bestScore = score; best = hwnd; }
        }

        return best;
    }

    /// <summary>Ctrl+Alt+seta saindo de uma janela flutuante (ou "teimosa"): ela cobre o
    /// mosaico por cima, então antes de pular pro monitor vizinho, tenta achar outra janela —
    /// do grid ou flutuando/teimosa também — no mesmo monitor. Sem isso, uma flutuante por cima
    /// de duas outras janelas splitadas nunca deixava o Ctrl+Alt+seta chegar nelas: ia direto pro monitor
    /// vizinho, ignorando quem estava logo ali embaixo. Bate a direção pedida quando dá; sem
    /// ninguém alinhado (comum quando a flutuante cobre o mosaico inteiro, e "embaixo" não é
    /// claramente de nenhum lado), troca pra outra janela qualquer do monitor de qualquer jeito
    /// — a mais próxima — em vez de não fazer nada.</summary>
    private nint FocusSameMonitor(nint current, RECT from, TilingDirection direction)
    {
        var monitor = MonitorFromWindow(current, MONITOR_DEFAULTTONEAREST);
        var candidates = new List<(nint Hwnd, RECT Rect)>();

        foreach (var (hwnd, rect) in _rects)
            if (hwnd != current && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == monitor)
                candidates.Add((hwnd, rect));

        foreach (var hwnd in _floating.Concat(_stubborn))
        {
            if (hwnd == current || candidates.Any(c => c.Hwnd == hwnd) ||
                MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != monitor)
                continue;
            if (TryGetVisualRect(hwnd, out var r)) candidates.Add((hwnd, r));
        }

        if (candidates.Count == 0) return 0;

        var directional = BestDirectionalMatch(from, direction, candidates);
        if (directional != 0) return directional;

        var (cx, cy) = Center(from);
        return candidates
            .OrderBy(c => { var (tx, ty) = Center(c.Rect); return Math.Pow(tx - cx, 2) + Math.Pow(ty - cy, 2); })
            .First().Hwnd;
    }

    /// <summary>Alt+C: tira a janela em foco do mosaico (ou devolve, se já estava fora). Funciona
    /// em qualquer janela de verdade do monitor principal, não só nas que o mosaico já tinha
    /// pego sozinho — dá pra "resgatar" manualmente uma que ficou de fora (uma "teimosa", por
    /// exemplo) e trazer pro fluxo de flutuar, com contorno e tudo.</summary>
    public void ToggleFloat()
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return;

        if (_floating.Remove(hwnd)) { Refresh(); return; }

        if (!Concerns(hwnd)) return;

        _stubborn.Remove(hwnd);
        _floating.Add(hwnd);
        CenterFloating(hwnd);
        Refresh();
    }

    /// <summary>Alt+Z: minimiza a janela em foco. Some do mosaico até ser restaurada.</summary>
    public void HideFocused()
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return;

        ShowWindow(hwnd, SW_MINIMIZE);
        // o EVENT_SYSTEM_MINIMIZEEND já dispara um Refresh sozinho; nada mais a fazer aqui
    }

    /// <summary>Alt+W: fecha a janela em foco — o mesmo WM_CLOSE educado que a dock manda pelo
    /// botão de fechar, não um encerrar forçado. O EVENT_OBJECT_HIDE/DESTROY que vem em seguida
    /// já dispara um Refresh sozinho; nada mais a fazer aqui.</summary>
    public void CloseFocused()
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return;

        WindowService.Close(hwnd);
    }

    // ── layout ────────────────────────────────────────────────

    private void Refresh()
    {
        if (!_config.TilingEnabled) return;

        var candidates = WindowService.Enumerate()
            .Where(w => !w.IsMinimized)
            .Where(w => !_floating.Contains(w.Handle))
            .Where(w => !_stubborn.Contains(w.Handle))
            .Where(w => IsResizable(w.Handle))
            .Where(w => !IsExcluded(w))
            .Select(w => w.Handle)
            .ToHashSet();

        _tiles.RemoveAll(h => !candidates.Contains(h) || !IsWindow(h));
        _floating.RemoveWhere(h => !IsWindow(h));
        _stubborn.RemoveWhere(h => !IsWindow(h));
        foreach (var h in candidates) if (!_tiles.Contains(h)) _tiles.Add(h);

        if (_tiles.Count == 0) { _rects = new(); UpdateBorder(); ReclaimFocusIfLost(); return; }

        // cada monitor tem o próprio grid, independente dos outros — a ordem de entrada de
        // cada janela no seu monitor é a mesma ordem em que apareceu em _tiles
        var rects = new Dictionary<nint, RECT>();
        foreach (var group in _tiles.GroupBy(h => MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST)))
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(group.Key, ref info)) continue;

            var area = Inset(info.rcWork, _config.TilingGap);
            Split(area, group.ToList(), 0, rects);
        }

        foreach (var (hwnd, rect) in rects) Apply(hwnd, rect);

        // alguns formulários (Delphi com `Constraints`, sobretudo) têm botão de maximizar mas
        // ignoram o tamanho pedido — pegos no flagra, saem do mosaico em vez de ficar
        // encolhidos num canto do espaço que reservamos pra eles. Mas uma falha isolada pode só
        // ser o DWM ainda não ter processado o SetWindowPos a tempo desta medição — por isso
        // só bane de verdade quem falhar duas vezes seguidas.
        var stuck = new List<nint>();
        foreach (var (hwnd, rect) in rects)
        {
            if (FitsTarget(hwnd, rect)) { _suspect.Remove(hwnd); continue; }

            if (!_suspect.Add(hwnd)) stuck.Add(hwnd); // já era suspeita: essa é a segunda falha
        }

        if (stuck.Count > 0)
        {
            foreach (var h in stuck) { _stubborn.Add(h); _suspect.Remove(h); _tiles.Remove(h); }
            Refresh();
            return;
        }

        _rects = rects;
        UpdateBorder();
        ReclaimFocusIfLost();

        // primeira falha, ninguém banido ainda: confere de novo em pouco tempo em vez de
        // esperar o próximo evento de verdade acontecer — sem isso a janela suspeita ficava
        // com o tamanho meio aplicado, sem contorno, até alguém abrir ou fechar outra coisa
        if (_suspect.Count > 0) ScheduleRecheck();
    }

    private void ScheduleRecheck()
    {
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            timer!.Stop();
            Refresh();
        }, _dispatcher);
        timer.Start();
    }

    /// <summary>A janela realmente ficou do tamanho que o mosaico pediu? Compara pelo
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> — a mesma margem que o <see cref="Apply"/> já
    /// compensa — com uma folga pequena pra arredondamento de DPI não contar como recusa.</summary>
    private static bool FitsTarget(nint hwnd, RECT target)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visual, Marshal.SizeOf<RECT>()) != 0)
            return true;

        const int tolerance = 20;
        return Math.Abs(visual.Width - target.Width) <= tolerance &&
               Math.Abs(visual.Height - target.Height) <= tolerance;
    }

    /// <summary>
    /// Depois de minimizar, o Windows às vezes larga o foco na área de trabalho em vez de
    /// passar pra próxima janela do mosaico — em especial quando a pessoa vai minimizando
    /// várias em sequência rápida. Só age quando o gatilho foi mesmo um minimizar: nos
    /// outros casos (uma janela nova abrindo, por exemplo) a pessoa pode ter ido de propósito
    /// para outro app, inclusive num segundo monitor, e puxar o foco de volta seria o bug
    /// inverso.
    /// </summary>
    private void ReclaimFocusIfLost()
    {
        if (!_reclaimFocusAfterMinimize) return;
        _reclaimFocusAfterMinimize = false;

        var fg = GetForegroundWindow();
        if (_rects.ContainsKey(fg) || _floating.Contains(fg) || _stubborn.Contains(fg)) return;

        if (_tiles.Count > 0) WindowService.Activate(_tiles[0]);
        else if (_floating.Count > 0) WindowService.Activate(_floating.First());
    }

    /// <summary>
    /// Divide <paramref name="area"/> ao meio, primeira janela fica com a primeira metade,
    /// o resto recorre na segunda — alternando o eixo do corte a cada nível.
    /// </summary>
    private void Split(RECT area, List<nint> windows, int depth, Dictionary<nint, RECT> outRects)
    {
        if (windows.Count == 0) return;
        if (windows.Count == 1) { outRects[windows[0]] = area; return; }

        var gap = _config.TilingGap;
        var vertical = depth % 2 == 0;   // corte vertical: lado a lado (esquerda/direita)

        RECT a, b;
        if (vertical)
        {
            var mid = area.Left + (area.Width - gap) / 2;
            a = new RECT { Left = area.Left, Top = area.Top, Right = mid, Bottom = area.Bottom };
            b = new RECT { Left = mid + gap, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
        }
        else
        {
            var mid = area.Top + (area.Height - gap) / 2;
            a = new RECT { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = mid };
            b = new RECT { Left = area.Left, Top = mid + gap, Right = area.Right, Bottom = area.Bottom };
        }

        outRects[windows[0]] = a;
        Split(b, windows.Skip(1).ToList(), depth + 1, outRects);
    }

    private static RECT Inset(RECT r, int amount) => new()
    {
        Left = r.Left + amount, Top = r.Top + amount,
        Right = r.Right - amount, Bottom = r.Bottom - amount
    };

    private static (int X, int Y) Center(RECT r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    /// <summary>Move e redimensiona uma janela para caber exatamente em <paramref name="target"/>,
    /// compensando a margem invisível que o Windows 11 deixa em volta da moldura.</summary>
    private static void Apply(nint hwnd, RECT target, nint after = 0, uint extraFlags = SWP_NOZORDER)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var margin = FrameMargin(hwnd);

        SetWindowPos(hwnd, after,
            target.Left - margin.Left,
            target.Top - margin.Top,
            target.Width + margin.Left + margin.Right,
            target.Height + margin.Top + margin.Bottom,
            extraFlags | SWP_NOACTIVATE);
    }

    /// <summary>Alt+C: tamanho e posição de uma janela flutuando — 60% da tela, centralizada,
    /// e livre pra pessoa redimensionar do jeito que quiser depois (não é mais tocada pelo
    /// mosaico enquanto estiver em <see cref="_floating"/>).</summary>
    private static void CenterFloating(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        var w = work.Width * 6 / 10;
        var h = work.Height * 6 / 10;
        var target = new RECT
        {
            Left = work.Left + (work.Width - w) / 2,
            Top = work.Top + (work.Height - h) / 2,
            Right = work.Left + (work.Width - w) / 2 + w,
            Bottom = work.Top + (work.Height - h) / 2 + h
        };

        // sem SWP_NOZORDER: flutuar é pra ficar por cima do mosaico, não só fora dele
        Apply(hwnd, target, HWND_TOP, extraFlags: 0);
    }

    /// <summary>
    /// Quanto a moldura de verdade da janela (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>) sobra
    /// pra fora do retângulo que o Windows relata (<c>GetWindowRect</c>) — a margem
    /// invisível de redimensionar que o Windows 11 deixa na maioria das janelas comuns.
    /// Sem compensar isso, cada janela ficaria alguns pixels menor e deslocada do que o
    /// mosaico pediu, e um gap de 8 px virava um gap de 15 num lado e 1 no outro.
    /// </summary>
    private static RECT FrameMargin(nint hwnd)
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

    /// <summary>Contorna quem está em foco — uma janela do grid (pelo retângulo calculado) ou
    /// qualquer outra janela de verdade do monitor principal (flutuando, "teimosa", ou que nem
    /// chegou a entrar no mosaico por algum outro motivo): o contorno marca foco, não é
    /// exclusividade de quem está sendo gerenciada pelo grid.</summary>
    private void UpdateBorder()
    {
        if (_config.TilingBorderThickness <= 0) { _border.Hide(); return; }

        var focused = GetForegroundWindow();

        // a borda é a nativa do Windows 11 na própria janela (DWMWA_BORDER_COLOR, dentro de
        // TilingBorderWindow) — não uma janela nossa por cima, então não tem retângulo nenhum
        // pra calcular aqui: só decidir SE a janela em foco merece ser contornada.
        if (_rects.ContainsKey(focused) || Concerns(focused))
        {
            Log.Trace($"UpdateBorder: contornando {focused:X}");
            _border.ShowAround(focused);
            return;
        }

        Log.Trace($"UpdateBorder: focused={focused:X} escondendo contorno (concerns={Concerns(focused)})");
        _border.Hide();
    }

    /// <summary>O retângulo de verdade de uma janela, em pixels físicos —
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>, que já exclui a margem invisível de redimensionar
    /// (uns 7px de cada lado que não aparecem na tela). Nem toda janela responde a isso (estilo
    /// clássico, sem composição, dá erro) — por isso a saída de reserva com o
    /// <c>GetWindowRect</c> puro, senão essas nunca teriam retângulo nenhum pra contorno ou
    /// pra busca de vizinho.</summary>
    private static bool TryGetVisualRect(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0) return true;
        return GetWindowRect(hwnd, out rect);
    }

    public void Dispose()
    {
        _config.PropertyChanged -= OnConfigChanged;
        Stop();
        _border.Close();
    }
}
