using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.ComponentModel;
using WinDock.Services.Tiling;
using static WinDock.Interop.Native;

namespace WinDock.Services;

public enum TilingDirection { Left, Right, Up, Down }

/// <summary>
/// Mosaico simples: divide o espaço entre as janelas abertas, sem sobreposição, com um
/// contorno marcando quem está em foco. Cada monitor tem o próprio grid, independente dos
/// outros — Ctrl+Shift+seta manda uma janela pro monitor vizinho quando ela já está na ponta
/// do grid atual.
///
/// **O modelo é uma árvore, e ela é guardada** (ver <see cref="LayoutTree"/>): uma raiz por
/// monitor e cada nó sabendo quanto ocupa do pai. As janelas de um monitor repartem o espaço por
/// igual, lado a lado — a janela nova entra ao lado da que está em foco (não no fim da fila), e
/// por isso aparece perto de onde a pessoa estava olhando, mas no mesmo nível das outras. O
/// <see cref="Refresh"/> reconcilia essa árvore com o que está aberto agora, em vez de refazê-la:
/// é o que faz uma proporção escolhida à mão sobreviver a abrir e fechar janela. Antes disso o
/// arranjo era recalculado do zero em metades iguais, e não havia onde guardar "esta eu quero
/// maior".
///
/// **Redimensionar sempre tira de alguém.** Ctrl+Alt+Shift+seta (e arrastar a divisória de uma
/// tile) passa uma fatia da vizinha daquele lado para a janela em foco; na ponta do grid não há
/// de quem tirar, e nada acontece.
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

    /// <summary>Onde cada janela está no arranjo, e quanto do espaço do vizinho ela tomou. Uma
    /// raiz por monitor; ver <see cref="LayoutTree"/>.</summary>
    private readonly LayoutTree _tree = new();

    /// <summary>A última janela do mosaico que teve foco — é ao lado dela que a próxima janela
    /// nova entra. Guardada porque no instante em que o mosaico recalcula, quem está em primeiro
    /// plano costuma ser justamente a janela recém-aberta, que ainda não faz parte de nada.</summary>
    private nint _lastTileFocus;

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

        _tree.Clear();
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
            // guarda a referência para a próxima janela nova entrar ao lado desta
            if (_tree.Contains(hWnd)) _lastTileFocus = hWnd;

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
            //
            // O IsExcluded aqui não é detalhe: uma janela da lista de exceções nunca entra na
            // árvore, então esta condição continuava verdadeira nela para sempre e um Refresh
            // completo (com o ApplyAll no fim) era agendado a **cada** vez que ela ganhava
            // foco. Todo clique no WinRAR reposicionava o grid inteiro — o app estava fora do
            // mosaico, como pedido, mas continuava mandando nele.
            if (hWnd != 0 && !_tree.Contains(hWnd) && !_floating.Contains(hWnd) && !_stubborn.Contains(hWnd)
                && Concerns(hWnd) && IsResizable(hWnd) && !IsExcluded(hWnd)
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
            if (_floating.Contains(hWnd))
            {
                // acabou de mexer numa flutuante: o tamanho em que ela ficou passa a ser o
                // tamanho deste app, e as próximas já nascem assim
                RememberFloatingSize(hWnd);
                return;
            }

            if (_stubborn.Contains(hWnd)) return;
            if (IsExcluded(hWnd)) return;

            AbsorbDrag(hWnd);
        }

        // ── Cuidado com os nomes destes dois eventos: eles enganam. ──────────────────────
        //
        // Medido nesta máquina (build 26200), com uma janela própria e um hook só de escuta:
        //
        //     MINIMIZESTART  IsIconic=True    <- a janela MINIMIZOU
        //     MINIMIZEEND    IsIconic=False   <- a janela RESTAUROU
        //
        // "Start" e "End" não são o começo e o fim de uma minimização: são o começo e o fim
        // do *estado* minimizado. MINIMIZESTART é o gesto de minimizar e MINIMIZEEND é o de
        // restaurar — e o `IsIconic` já responde certo no instante em que cada um chega.
        //
        // Este código lia os dois ao contrário: descartava o MINIMIZESTART e tratava o
        // MINIMIZEEND como "acabou de minimizar". Daí os dois sintomas: minimizar pelo botão
        // do Windows não refazia o mosaico (o evento era jogado fora), e restaurar roubava o
        // foco da janela recém-restaurada para outra qualquer.
        //
        // O comentário antigo dizia que o `IsIconic` "ainda podia ler falso" no
        // MINIMIZESTART, e que um Refresh ali cancelava a animação de minimizar. A medição
        // não confirma a leitura falsa. Se aquele travamento voltar a aparecer, o lugar de
        // consertar é o `Apply` — que restaura toda janela iconificada que receba retângulo —
        // e não descartar o evento.
        if (ev == EVENT_SYSTEM_MINIMIZESTART)
        {
            // não passa pelo Concerns(): é a própria janela que acabou de minimizar, e para o
            // IsAltTabWindow ela já não é candidata. Recusar aqui deixava a vizinha com o
            // retângulo antigo, sem receber o espaço que sobrou.
            if (!_tree.Contains(hWnd)) return;

            // O mosaico recalcula, mas NÃO mexe no foco. Havia aqui uma reivindicação: ao
            // minimizar, o foco era passado para outra janela do mosaico, porque o Windows às
            // vezes o larga na área de trabalho. Na prática ela errava mais do que acertava —
            // minimizando a última janela de uma tela, o foco pulava para a outra (o caso
            // relatado: um Discord aberto sem foco no notebook virava o primeiro plano).
            //
            // Agora quem decide é o Windows, e o foco só muda por ação de quem usa: clique,
            // Alt+Tab ou as teclas do próprio mosaico (Ctrl+Alt+setas). Previsível vale mais
            // aqui do que esperto.
            if (_pending is not { Status: DispatcherOperationStatus.Pending })
                _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
            return;
        }

        if (ev == EVENT_SYSTEM_MINIMIZEEND)
        {
            // restaurar: a janela volta a ocupar o lugar dela no mosaico. Sem reivindicar
            // foco nenhum — quem acabou de ser restaurada é justamente quem deve ficar em
            // primeiro plano, e mexer nisso aqui era o que jogava o foco para outra tela.
            if (_pending is not { Status: DispatcherOperationStatus.Pending })
                _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
            return;
        }

        // Uma janela fechando não passa pelo Concerns(): no instante em que o evento chega o hwnd
        // já está morto (ou escondido), e o IsAltTabWindow — que pede janela visível e com título
        // — recusa. Era por isso que fechar uma de três não devolvia o espaço às outras duas: o
        // recálculo nunca era agendado, e o arranjo só se acertava quando outra coisa qualquer
        // acontecia. A pergunta certa para esses dois eventos não é "essa janela nos interessa?"
        // (já não dá para saber) e sim "essa janela era nossa?", que a árvore ainda responde.
        if (ev is EVENT_OBJECT_DESTROY or EVENT_OBJECT_HIDE)
        {
            if (!_tree.Contains(hWnd) && !_floating.Contains(hWnd) && !_stubborn.Contains(hWnd)) return;
        }
        // Abrir, mostrar ou renomear uma janela da lista de exceções também não muda o mosaico:
        // ela não disputa espaço no grid nem hoje nem depois. Sem isto, cada caixa de progresso
        // ou diálogo que o WinRAR abre durante uma operação agendava um recálculo, e o grid
        // inteiro era reaplicado no meio do trabalho de quem estava do outro lado da tela.
        else if (!Concerns(hWnd) || IsExcluded(hWnd)) return;

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
    /// caso; pelo nome do executável sozinho, excluir uma excluiria as duas.
    ///
    /// E bate também pela **classe da janela**, com o prefixo <c>classe:</c>. Há casos em que nem
    /// o executável nem o AUMID resolvem: a caixa "0% concluído" do Explorer tem borda de
    /// redimensionar, título e nenhum dono, então é candidata legítima ao grid — e excluir
    /// <c>explorer.exe</c> para tirá-la levaria junto toda janela de pasta. A classe
    /// (<c>OperationStatusWindow</c> contra <c>CabinetWClass</c>) é o que separa as duas.</summary>
    private bool IsExcluded(TaskWindow w)
    {
        if (_config.TilingExcludedApps.Count == 0) return false;

        var fileName = Path.GetFileName(w.ExePath);

        // medida só se alguém realmente pedir por classe: é uma chamada ao Windows por janela,
        // e isto roda para cada janela aberta a cada recálculo do mosaico
        string? classe = null;

        foreach (var padrao in _config.TilingExcludedApps)
        {
            if (padrao.StartsWith(ClassPrefix, StringComparison.OrdinalIgnoreCase))
            {
                classe ??= WindowService.ClassOf(w.Handle);
                if (string.Equals(padrao[ClassPrefix.Length..].Trim(), classe, StringComparison.OrdinalIgnoreCase))
                    return true;

                continue;
            }

            if (string.Equals(padrao, fileName, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(w.Aumid) &&
                string.Equals(padrao, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Como se escreve uma exceção por classe de janela na lista. O prefixo existe
    /// porque nome de classe não tem forma reconhecível — <c>.exe</c> denuncia um executável e o
    /// <c>!</c> denuncia um AUMID, mas <c>OperationStatusWindow</c> poderia ser qualquer coisa.</summary>
    private const string ClassPrefix = "classe:";

    /// <summary>A mesma pergunta a partir do hwnd, para os eventos — que chegam com a janela, não
    /// com a lista. A lista vazia (o caso comum) responde antes de consultar coisa nenhuma ao
    /// Windows; uma janela que não se deixa consultar não é tratada como excluída, que é o
    /// comportamento de antes.</summary>
    private bool IsExcluded(nint hWnd)
    {
        if (_config.TilingExcludedApps.Count == 0) return false;
        var w = WindowService.Describe(hWnd);
        return w is not null && IsExcluded(w);
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
                // as duas trocam de lugar sem levar o tamanho junto: quem vai pro lugar maior
                // fica maior — é a posição no arranjo que tem tamanho, não a janela
                _tree.Swap(current, target);
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

        _tree.Remove(hwnd);
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

        if (_floating.Contains(hwnd))
        {
            // já flutuando: se a pessoa arrastou ou redimensionou desde o último Alt+C, a tecla
            // devolve a janela ao centro em vez de recolher pro mosaico — recentralizar não
            // tinha tecla nenhuma antes. Só o toque seguinte, com ela já no lugar, fecha o
            // ciclo e devolve pro grid.
            if (!IsCentered(hwnd)) { CenterFloating(hwnd); return; }

            _floating.Remove(hwnd);
            Refresh();
            return;
        }

        if (!Concerns(hwnd)) return;

        _stubborn.Remove(hwnd);
        _floating.Add(hwnd);
        CenterFloating(hwnd);
        Refresh();
    }

    /// <summary>
    /// Ctrl+Alt+C: esquece o tamanho flutuante guardado do app da janela em foco, que volta a
    /// abrir com os 60% da tela.
    ///
    /// Redimensionar de novo só troca um tamanho por outro — nunca desfaz —, e sem isto a única
    /// saída seria editar o <c>config.json</c> na mão. Estando a janela flutuando, ela é
    /// recolocada na hora: sem esse pulo a tecla não teria resposta nenhuma na tela, e ninguém
    /// saberia se funcionou.
    /// </summary>
    public void ForgetFloatingSize()
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return;

        var chave = FloatingKey(hwnd);
        if (chave is null || !_config.FloatingSizes.Remove(chave)) return;

        _config.Save();
        Log.Trace($"tamanho flutuante de '{chave}' esquecido");

        if (_floating.Contains(hwnd)) CenterFloating(hwnd);
    }

    /// <summary>
    /// Ctrl+Alt+Shift+seta: alarga a janela em foco naquele lado, encolhendo a vizinha de lá na
    /// mesma medida. O espaço nunca vem do nada.
    ///
    /// Na ponta do grid não há de quem tirar — a árvore devolve que não deu, e nada é reaplicado
    /// em vez de mexer nas outras janelas à toa.
    /// </summary>
    public void Resize(TilingDirection direction)
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || !_tree.Contains(hwnd)) return;

        var axis = direction is TilingDirection.Left or TilingDirection.Right
            ? SplitLayout.Horizontal
            : SplitLayout.Vertical;
        var towardsEnd = direction is TilingDirection.Right or TilingDirection.Down;

        if (!_tree.Resize(hwnd, axis, towardsEnd, _config.TilingResizeStep))
        {
            Log.Trace($"Resize {direction}: {hwnd:X} não tem vizinho desse lado — nada a fazer");
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Fim de um arraste numa janela do grid: se ela mudou de tamanho, guarda essa mudança como
    /// proporção em vez de descartá-la. Antes, arrastar a divisória de uma tile não levava a nada
    /// — o <see cref="Refresh"/> seguinte devolvia a janela ao tamanho antigo, e não havia onde
    /// registrar "eu quero esta maior".
    ///
    /// Só o tamanho conta, não a posição: arrastar a janela pela barra de título move as duas
    /// bordas na mesma medida e não muda largura nem altura — nesse caso ela volta pro lugar,
    /// como sempre voltou.
    /// </summary>
    private void AbsorbDrag(nint hwnd)
    {
        if (!_rects.TryGetValue(hwnd, out var before)) return;
        if (!TryGetVisualRect(hwnd, out var after)) return;

        // folga contra o arredondamento de DPI e contra a margem invisível da moldura: sem ela,
        // um arraste que não mexeu em tamanho nenhum ainda registraria uns pixels de diferença
        const int tolerance = 4;

        var widthChange = after.Width - before.Width;
        if (Math.Abs(widthChange) > tolerance)
        {
            // qual das duas bordas verticais se mexeu mais é o que diz de que lado o espaço saiu
            var movedRight = Math.Abs(after.Right - before.Right) >= Math.Abs(after.Left - before.Left);
            _tree.Resize(hwnd, SplitLayout.Horizontal, movedRight, widthChange);
        }

        var heightChange = after.Height - before.Height;
        if (Math.Abs(heightChange) > tolerance)
        {
            var movedDown = Math.Abs(after.Bottom - before.Bottom) >= Math.Abs(after.Top - before.Top);
            _tree.Resize(hwnd, SplitLayout.Vertical, movedDown, heightChange);
        }
    }

    /// <summary>Alt+Z: minimiza a janela em foco. Some do mosaico até ser restaurada.</summary>
    public void HideFocused()
    {
        if (!_config.TilingEnabled) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return;

        ShowWindow(hwnd, SW_MINIMIZE);
        // o EVENT_SYSTEM_MINIMIZESTART — que, apesar do nome, é o evento de *minimizar*;
        // veja o OnWinEvent — já dispara um Refresh sozinho; nada mais a fazer aqui
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

    /// <summary>
    /// Reconcilia a árvore com o que está aberto agora e reaplica os retângulos. Reconciliar, e
    /// não reconstruir: é o que preserva as proporções que a pessoa escolheu — refazer a estrutura
    /// do zero a cada evento devolvia tudo pra metades iguais assim que qualquer janela abrisse ou
    /// fechasse.
    /// </summary>
    /// <summary>Quantos <see cref="Refresh"/> encadeados estão em andamento. O recálculo se
    /// rechama quando aprende algo que muda o arranjo (uma janela que saiu do mosaico, um tamanho
    /// mínimo recém-descoberto); as duas coisas só crescem, então a cadeia sempre termina — mas
    /// uma janela que devolvesse tamanhos diferentes a cada medição travaria a interface, e um
    /// teto é mais barato que confiar no bom comportamento alheio.</summary>
    private int _refreshDepth;
    private const int MaxRefreshDepth = 4;

    private void Refresh()
    {
        if (!_config.TilingEnabled) return;
        if (_refreshDepth >= MaxRefreshDepth)
        {
            Log.Trace($"Refresh: parando a cadeia em {MaxRefreshDepth} — alguma janela não " +
                      "estabiliza no tamanho, e insistir só travaria a dock");
            return;
        }

        _refreshDepth++;
        try { RefreshCore(); }
        finally { _refreshDepth--; }
    }

    private void RefreshCore()
    {

        // minimizada continua sendo candidata: ela fica guardada na árvore, no lugar dela, só sem
        // ocupar espaço — é o que a faz voltar pra mesma posição ao ser restaurada, em vez de
        // reentrar no fim como uma janela nova qualquer
        var todas = WindowService.Enumerate();
        var managed = todas
            .Where(w => !_floating.Contains(w.Handle))
            .Where(w => !_stubborn.Contains(w.Handle))
            .Where(w => IsResizable(w.Handle))
            .Where(w => !IsExcluded(w))
            .ToList();

        // quem ficou de fora, e por qual dos filtros — a pergunta que mais aparece quando uma
        // janela "some" do mosaico sem motivo aparente
        foreach (var w in todas.Where(w => !w.IsMinimized && !managed.Contains(w)))
        {
            var razao = _floating.Contains(w.Handle) ? "flutuando (Alt+C)"
                      : _stubborn.Contains(w.Handle) ? "teimosa (recusou o tamanho duas vezes)"
                      : !IsResizable(w.Handle) ? "sem WS_THICKFRAME nem WS_MAXIMIZEBOX"
                      : "na lista de exceções";
            // a classe entra no rastro porque é o que se precisa saber para escrever uma exceção
            // por classe — e não há de onde tirá-la sem uma ferramenta externa
            Log.Trace($"Refresh: {w.Handle:X} '{w.Title}' [{WindowService.ClassOf(w.Handle)}] " +
                      $"fora do mosaico — {razao}");
        }

        var handles = managed.Select(w => w.Handle).ToHashSet();
        var minimized = managed.Where(w => w.IsMinimized).Select(w => w.Handle).ToHashSet();

        _floating.RemoveWhere(h => !IsWindow(h));
        _stubborn.RemoveWhere(h => !IsWindow(h));

        // saiu do mosaico — fechou, foi flutuar, virou "teimosa", entrou na lista de exceções
        foreach (var hwnd in _tree.AllWindows())
            if (!handles.Contains(hwnd) || !IsWindow(hwnd)) _tree.Remove(hwnd);

        // mudou de tela: sai da árvore de origem aqui pra entrar na de destino logo abaixo. Uma
        // janela minimizada fica de fora dessa conferência — o Windows guarda ela num canto fora
        // da tela enquanto está minimizada, e o MonitorFromWindow dali responde qualquer coisa
        foreach (var hwnd in _tree.AllWindows())
        {
            if (minimized.Contains(hwnd)) continue;
            if (_tree.MonitorOf(hwnd) != MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)) _tree.Remove(hwnd);
        }

        // Janela nova entra ao lado de quem estava em foco — é o que faz ela aparecer onde a
        // pessoa estava olhando, e o que reparte o espaço de uma janela só em vez de espremer
        // todas em colunas.
        //
        // A referência tem de ser o último foco *que está no mosaico*, não o foco de agora: no
        // instante em que este Refresh roda, quem está em primeiro plano costuma ser a própria
        // janela que acabou de abrir — ainda desconhecida da árvore. Perguntar o foco aqui
        // devolvia uma janela que a árvore não acha, e toda inserção caía no fim da raiz: três
        // janelas viravam três colunas iguais em vez de a terceira dividir o espaço da segunda.
        var current = GetForegroundWindow();
        var beside = _tree.Contains(current) ? current : _lastTileFocus;

        foreach (var w in managed)
        {
            if (_tree.Contains(w.Handle)) continue;

            var monitor = MonitorFromWindow(w.Handle, MONITOR_DEFAULTTONEAREST);
            Log.Trace($"Refresh: entrando {w.Handle:X} '{w.Title}' ao lado de {beside:X} " +
                      $"(na árvore={_tree.Contains(beside)}, foco agora={current:X}, último foco no mosaico={_lastTileFocus:X})");
            _tree.Insert(w.Handle, monitor, beside, RootLayout(monitor));

            // as próximas entram ao lado desta, não todas ao lado da mesma — é o que dá o
            // aninhamento sucessivo quando várias janelas entram de uma vez (no arranque, por
            // exemplo), em vez de uma fileira de irmãos
            beside = w.Handle;
        }

        // cada monitor tem o próprio grid, independente dos outros
        var rects = new Dictionary<nint, RECT>();
        foreach (var monitor in _tree.Monitors)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) continue;

            var area = Inset(info.rcWork, _config.TilingGap);
            foreach (var (hwnd, rect) in _tree.ComputeRects(monitor, area, _config.TilingGap, minimized))
                rects[hwnd] = rect;
        }

        Log.Trace($"Refresh: {managed.Count} candidatas ({minimized.Count} minimizadas), " +
                  $"árvore com {_tree.AllWindows().Count()} em {_tree.Monitors.Count()} monitor(es), " +
                  $"{rects.Count} retângulo(s): " +
                  string.Join(" | ", rects.Select(r => $"{r.Key:X} {r.Value.Left},{r.Value.Top} {r.Value.Width}x{r.Value.Height}")));

        if (rects.Count == 0) { _rects = new(); UpdateBorder(); return; }

        ApplyAll(rects);

        // alguns formulários (Delphi com `Constraints`, sobretudo) têm botão de maximizar mas
        // ignoram o tamanho pedido — pegos no flagra, saem do mosaico em vez de ficar
        // encolhidos num canto do espaço que reservamos pra eles. Mas uma falha isolada pode só
        // ser o DWM ainda não ter processado o SetWindowPos a tempo desta medição — por isso
        // só bane de verdade quem falhar duas vezes seguidas.
        var stuck = new List<nint>();
        var aprendeuMinimo = false;

        foreach (var (hwnd, rect) in rects)
        {
            if (FitsTarget(hwnd, rect)) { _suspect.Remove(hwnd); continue; }

            TryGetVisualRect(hwnd, out var real);

            // A janela devolveu um tamanho MAIOR do que o pedido em algum eixo: isso não é
            // desobediência, é o menor tamanho que ela aceita — o Windows Terminal não desce de
            // 466 px de largura, e pedir 423 devolvia 466 para sempre. Antes isso contava como
            // "não obedece" e a expulsava do mosaico na segunda tentativa, que é o pior desfecho
            // possível para o que era só um limite legítimo. Agora o limite é anotado e o cálculo
            // passa a respeitá-lo, tirando o espaço de quem tem folga.
            var largura = real.Width > rect.Width ? real.Width : 0;
            var altura = real.Height > rect.Height ? real.Height : 0;

            if (largura > 0 || altura > 0)
            {
                Log.Trace($"Refresh: {hwnd:X} não encolhe além de {real.Width}x{real.Height} " +
                          $"(pedimos {rect.Width}x{rect.Height}) — anotando o mínimo dela");
                _tree.SetMinimum(hwnd, largura, altura);
                _suspect.Remove(hwnd);
                aprendeuMinimo = true;
                continue;
            }

            Log.Trace($"Refresh: {hwnd:X} não bateu o tamanho pedido — queria {rect.Width}x{rect.Height}, " +
                      $"ficou {real.Width}x{real.Height}{(_suspect.Contains(hwnd) ? " (segunda vez: sai do mosaico)" : " (primeira vez: fica sob suspeita)")}");

            if (!_suspect.Add(hwnd)) stuck.Add(hwnd); // já era suspeita: essa é a segunda falha
        }

        if (stuck.Count > 0)
        {
            foreach (var h in stuck) { _stubborn.Add(h); _suspect.Remove(h); _tree.Remove(h); }
            Refresh();
            return;
        }

        // com um mínimo novo anotado, o arranjo que acabou de ser aplicado já não é o certo:
        // refaz agora, respeitando o limite descoberto
        if (aprendeuMinimo) { Refresh(); return; }

        _rects = rects;

        // a janela que acabou de entrar já está na árvore agora: se é ela que está em foco, é a
        // referência da próxima. O evento de foco dela passou antes de ela existir aqui, então
        // não seria pego pelo hook.
        var focused = GetForegroundWindow();
        if (_tree.Contains(focused)) _lastTileFocus = focused;

        UpdateBorder();
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

    /// <summary>Em que sentido o grid de um monitor corta o espaço quando ganha a segunda janela:
    /// numa tela mais larga que alta, lado a lado; numa tela em pé, uma sobre a outra. É o único
    /// palpite dado pelo mosaico — daí em diante quem decide o arranjo é a pessoa, movendo e
    /// redimensionando.</summary>
    private static SplitLayout RootLayout(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return SplitLayout.Horizontal;

        return info.rcMonitor.Width >= info.rcMonitor.Height
            ? SplitLayout.Horizontal
            : SplitLayout.Vertical;
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

    /// <summary>
    /// Move o grid inteiro de uma vez. Uma janela por vez, cada uma se redesenhava no lugar novo
    /// enquanto as vizinhas ainda estavam no antigo, e o rearranjo aparecia como um tremor —
    /// coisa que passou a acontecer muito mais depois que redimensionar virou uma sequência de
    /// pressionadas de tecla, não um evento isolado.
    ///
    /// Se o Windows recusar a lista em qualquer ponto (o identificador volta zero), cai no
    /// caminho antigo, uma a uma: é só perder o ganho visual, não o rearranjo.
    /// </summary>
    private static void ApplyAll(Dictionary<nint, RECT> rects)
    {
        // sair do minimizado tem de vir antes: uma janela minimizada ignora o retângulo pedido,
        // e a margem invisível dela nem existe ainda pra ser medida
        foreach (var hwnd in rects.Keys) if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var batch = BeginDeferWindowPos(rects.Count);
        if (batch != 0)
        {
            foreach (var (hwnd, target) in rects)
            {
                var margin = FrameMargin(hwnd);
                batch = DeferWindowPos(batch, hwnd, 0,
                    target.Left - margin.Left,
                    target.Top - margin.Top,
                    target.Width + margin.Left + margin.Right,
                    target.Height + margin.Top + margin.Bottom,
                    SWP_NOZORDER | SWP_NOACTIVATE);

                if (batch == 0) break;
            }
        }

        if (batch != 0) { EndDeferWindowPos(batch); return; }

        Log.Trace("ApplyAll: o Windows recusou a lista em bloco — reaplicando uma a uma");
        foreach (var (hwnd, rect) in rects) Apply(hwnd, rect);
    }

    /// <summary>Alt+C: tamanho e posição de uma janela flutuando — o tamanho que este app usa
    /// (ou 60% da tela, na primeira vez), centralizada, e livre pra pessoa redimensionar do
    /// jeito que quiser depois (não é mais tocada pelo mosaico enquanto estiver em
    /// <see cref="_floating"/>).</summary>
    private void CenterFloating(nint hwnd)
    {
        if (!TryCenteredRect(hwnd, out var target)) return;

        // sem SWP_NOZORDER: flutuar é pra ficar por cima do mosaico, não só fora dele
        Apply(hwnd, target, HWND_TOP, extraFlags: 0);
    }

    /// <summary>O retângulo que o <see cref="CenterFloating"/> daria pra essa janela: o tamanho
    /// guardado para o app (ver <see cref="DockConfig.FloatingSizes"/>) ou 60% da área útil,
    /// sempre no centro do monitor mais próximo.</summary>
    private bool TryCenteredRect(nint hwnd, out RECT rect)
    {
        rect = default;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var work = info.rcWork;
        var (w, h) = FloatingSizeOf(hwnd, work);
        rect = new RECT
        {
            Left = work.Left + (work.Width - w) / 2,
            Top = work.Top + (work.Height - h) / 2,
            Right = work.Left + (work.Width - w) / 2 + w,
            Bottom = work.Top + (work.Height - h) / 2 + h
        };
        return true;
    }

    /// <summary>
    /// O tamanho que este app usa flutuando: o guardado, se houver, senão os 60% de sempre.
    ///
    /// O guardado é sempre limitado à área útil do monitor de agora — o tamanho foi gravado numa
    /// tela e pode estar sendo aplicado noutra bem menor (aqui são duas, de tamanhos diferentes),
    /// e uma janela maior que a tela não teria como ser centralizada nem arrastada de volta.
    /// </summary>
    private (int Width, int Height) FloatingSizeOf(nint hwnd, RECT work)
    {
        var padrao = (work.Width * 6 / 10, work.Height * 6 / 10);

        var chave = FloatingKey(hwnd);
        if (chave is null || !_config.FloatingSizes.TryGetValue(chave, out var salvo)) return padrao;
        if (salvo.Width <= 0 || salvo.Height <= 0) return padrao;

        return (Math.Min(salvo.Width, work.Width), Math.Min(salvo.Height, work.Height));
    }

    /// <summary>
    /// Sob que nome o tamanho de uma janela é guardado — o mesmo critério da lista de exceções:
    /// o AppUserModelID quando a janela tem um (é ele que separa um perfil do Chrome do outro, e
    /// um app "moderno" do vizinho que divide o mesmo <c>ApplicationFrameHost.exe</c>), senão o
    /// nome do executável. Nada de caminho completo: ele muda quando o app se atualiza, e o
    /// tamanho guardado ficaria órfão — foi o que aconteceu com o botão do Spotify.
    /// </summary>
    private static string? FloatingKey(nint hwnd)
    {
        var w = WindowService.Describe(hwnd);
        if (w is null) return null;

        return !string.IsNullOrEmpty(w.Aumid) ? w.Aumid : Path.GetFileName(w.ExePath);
    }

    /// <summary>
    /// Guarda o tamanho em que a pessoa deixou uma janela flutuante, para todas as próximas
    /// desse app já nascerem assim.
    ///
    /// Chamado ao soltar o arraste, que é quando a escolha ficou pronta — no meio dele o
    /// tamanho muda a cada quadro. Uma janela maximizada não conta: o gesto ali foi "ocupar a
    /// tela", não "este é o meu tamanho", e gravar isso faria todo Alt+C do app virar tela cheia.
    /// </summary>
    private void RememberFloatingSize(nint hwnd)
    {
        if (IsZoomed(hwnd) || IsIconic(hwnd)) return;
        if (!TryGetVisualRect(hwnd, out var rect)) return;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var chave = FloatingKey(hwnd);
        if (chave is null) return;

        if (_config.FloatingSizes.TryGetValue(chave, out var atual) &&
            atual.Width == rect.Width && atual.Height == rect.Height) return;

        _config.FloatingSizes[chave] = new FloatingSize { Width = rect.Width, Height = rect.Height };
        _config.Save();

        Log.Trace($"tamanho flutuante de '{chave}' guardado: {rect.Width}x{rect.Height}");
    }

    /// <summary>Se a janela já está onde o Alt+C a colocaria.
    ///
    /// A folga de 8 px existe porque muita janela não aceita o tamanho pedido ao pixel — as que
    /// arredondam pra um passo de grade (terminais, editores) ficariam "fora do centro" pra
    /// sempre numa comparação exata, e o Alt+C nunca mais devolveria elas pro mosaico.
    ///
    /// Quando não dá pra medir (monitor ou janela que não respondem), responde que já está
    /// centralizada: o pior caso vira o comportamento antigo, recolher pro grid, em vez de
    /// prender a janela flutuando.</summary>
    private bool IsCentered(nint hwnd)
    {
        const int folga = 8;

        if (!TryCenteredRect(hwnd, out var target)) return true;
        if (!TryGetVisualRect(hwnd, out var atual)) return true;

        return Math.Abs(atual.Left - target.Left) <= folga
            && Math.Abs(atual.Top - target.Top) <= folga
            && Math.Abs(atual.Right - target.Right) <= folga
            && Math.Abs(atual.Bottom - target.Bottom) <= folga;
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
