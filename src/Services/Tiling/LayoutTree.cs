using static WinDock.Interop.Native;

namespace WinDock.Services.Tiling;

internal enum NodeKind { Container, Window }

/// <summary>Como um container reparte o espaço entre os filhos.</summary>
internal enum SplitLayout
{
    /// <summary>Filhos lado a lado — o corte é uma linha vertical.</summary>
    Horizontal,
    /// <summary>Filhos empilhados — o corte é uma linha horizontal.</summary>
    Vertical
}

/// <summary>
/// Um nó da árvore de layout: ou uma janela (folha), ou um container que reparte o espaço entre
/// os filhos. A API imita a do DOM de propósito (<see cref="Children"/>, <see cref="Parent"/>,
/// <see cref="Index"/>) — é o que deixa o código de layout legível em vez de virar aritmética de
/// índices.
/// </summary>
internal sealed class Node
{
    public NodeKind Kind { get; init; }

    /// <summary>Só em <see cref="NodeKind.Window"/>.</summary>
    public nint Hwnd { get; set; }

    /// <summary>Só em <see cref="NodeKind.Container"/>.</summary>
    public SplitLayout Layout { get; set; }

    /// <summary>Quanto deste nó cabe no pai, de 0 a 1. <c>0</c> quer dizer "não escolhi nada,
    /// divide igual com os irmãos" — é o estado de toda janela recém-inserida, e o estado para
    /// onde os irmãos voltam sempre que a estrutura muda.</summary>
    public double Percent { get; set; }

    /// <summary>O retângulo que este nó recebeu no último cálculo.</summary>
    public RECT Rect { get; set; }

    /// <summary>O menor tamanho que esta janela aceita, em pixels — zero enquanto não se souber.
    /// Não é um palpite: só é preenchido depois de a janela recusar na prática um tamanho pedido,
    /// devolvendo um maior. O Windows Terminal, por exemplo, não desce de 466 px de largura.</summary>
    public int MinWidth { get; set; }
    public int MinHeight { get; set; }

    /// <summary>Quanto deste container sobrou para repartir entre os filhos no último cálculo —
    /// a largura ou altura já sem os gaps que ficam entre eles. É contra este número que o
    /// redimensionamento converte pixels em fração; usar o retângulo cheio faria "aumenta 100 px"
    /// render um pouco menos que isso, porque parte daquele espaço é gap e não é repartida.</summary>
    public int SplitSize { get; set; }

    public List<Node> Children { get; } = new();
    public Node? Parent { get; set; }

    public int Index => Parent?.Children.IndexOf(this) ?? -1;

    public static Node NewWindow(nint hwnd) => new() { Kind = NodeKind.Window, Hwnd = hwnd };
    public static Node NewContainer(SplitLayout layout) => new() { Kind = NodeKind.Container, Layout = layout };

    public void Append(Node child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void InsertAt(int index, Node child)
    {
        child.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), child);
    }

    public void RemoveChild(Node child)
    {
        if (Children.Remove(child)) child.Parent = null;
    }

    /// <summary>Todas as janelas abaixo deste nó, em ordem de leitura da árvore.</summary>
    public IEnumerable<Node> Windows()
    {
        if (Kind == NodeKind.Window) { yield return this; yield break; }
        foreach (var child in Children)
            foreach (var w in child.Windows()) yield return w;
    }
}

/// <summary>
/// A árvore de layout do mosaico — uma raiz por monitor. É a estrutura que faltava: antes o
/// arranjo era recalculado do zero a cada evento, sempre em metades iguais, e por isso nenhuma
/// proporção escolhida pela pessoa sobrevivia a abrir ou fechar uma janela.
///
/// **Só geometria.** Nenhuma chamada ao Windows entra aqui — a árvore não sabe o que é um
/// <c>SetWindowPos</c> nem o que é foco. Quem traduz isto em janelas de verdade é o
/// <see cref="TilingService"/>. O motivo é prático: o único jeito de conferir um layout sem
/// depender de teclado físico (ver a seção de mosaico em <c>docs/APRENDIZADOS.md</c>, sobre
/// automação e foco) é ter a parte que decide os retângulos separada da que fala com o sistema.
///
/// **O percentual é do lugar, não da janela.** Cada nó guarda quanto ocupa do pai; zero quer
/// dizer "divide igual". Trocar duas janelas de lugar troca os identificadores e deixa os
/// percentuais onde estão — quem vai para um lugar maior fica maior.
/// </summary>
internal sealed class LayoutTree
{
    private readonly Dictionary<nint, Node> _roots = new();

    // ── consultas ─────────────────────────────────────────────

    public void Clear() => _roots.Clear();

    public bool Contains(nint hwnd) => Find(hwnd) != null;

    public IEnumerable<nint> AllWindows() =>
        _roots.Values.SelectMany(r => r.Windows()).Select(n => n.Hwnd).ToList();

    public IEnumerable<nint> Monitors => _roots.Keys.ToList();

    /// <summary>Em que monitor a árvore acha que esta janela está — não onde o Windows diz que
    /// ela está agora. A diferença entre os dois é justamente o sinal de que ela mudou de tela e
    /// precisa ser realocada.</summary>
    public nint MonitorOf(nint hwnd)
    {
        foreach (var (monitor, root) in _roots)
            if (root.Windows().Any(n => n.Hwnd == hwnd)) return monitor;
        return 0;
    }

    private Node? Find(nint hwnd)
    {
        foreach (var root in _roots.Values)
        {
            var hit = root.Windows().FirstOrDefault(n => n.Hwnd == hwnd);
            if (hit != null) return hit;
        }
        return null;
    }

    // ── inserir e remover ─────────────────────────────────────

    /// <summary>
    /// Põe <paramref name="hwnd"/> no monitor pedido, ao lado de <paramref name="beside"/> — que
    /// é normalmente a janela em foco. Entrar ao lado de quem está em foco (e não no fim de uma
    /// lista) é o que faz a janela nova aparecer onde a pessoa estava olhando.
    ///
    /// Sem vizinha de referência — a primeira janela do monitor, ou uma referência que está em
    /// outra tela — entra no fim da raiz, que é o comportamento antigo e continua certo aqui.
    /// </summary>
    public void Insert(nint hwnd, nint monitor, nint beside, SplitLayout rootLayout)
    {
        if (Contains(hwnd)) return;

        var root = Root(monitor, rootLayout);
        var node = Node.NewWindow(hwnd);

        var reference = beside == 0 ? null : Find(beside);
        if (reference?.Parent == null || MonitorOf(beside) != monitor)
        {
            root.Append(node);
            ResetSiblingPercent(root);
            return;
        }

        // Entra como irmã da referência, no mesmo nível — todas as janelas do monitor repartem o
        // espaço por igual, lado a lado. A alternativa (criar um nível novo, com a janela nova
        // dividindo só o espaço da que estava em foco) foi implementada e descartada em uso: numa
        // tela larga, três janelas iguais são melhores que uma grande e duas pela metade. O preço
        // conhecido e aceito é que muitas janelas ficam estreitas, em vez de virar quadrantes —
        // numa ultrawide isso demora a incomodar, e o Ctrl+Alt+Shift+seta reajusta o que ficar
        // apertado.
        //
        // Entrar *ao lado da referência* (e não no fim da fila) continua importando: é o que faz a
        // janela nova aparecer perto de onde a pessoa estava olhando.
        var parent = reference.Parent;
        parent.InsertAt(reference.Index + 1, node);
        ResetSiblingPercent(parent);
    }

    /// <summary>
    /// Tira a janela da árvore e, se o container que a segurava ficar com um filho só, dissolve
    /// esse container — o filho sobe para o lugar dele. Sem isso a árvore acumula níveis que não
    /// correspondem a divisão nenhuma na tela, e o cálculo passa a repartir espaço entre
    /// containers de um filho só.
    /// </summary>
    public void Remove(nint hwnd)
    {
        var node = Find(hwnd);
        if (node?.Parent == null) return;

        var parent = node.Parent;
        parent.RemoveChild(node);
        ResetSiblingPercent(parent);
        Collapse(parent);

        foreach (var (monitor, root) in _roots.ToList())
            if (root.Children.Count == 0) _roots.Remove(monitor);
    }

    /// <summary>
    /// Registra o menor tamanho que esta janela aceita — descoberto na prática, quando ela devolve
    /// um tamanho maior que o pedido. Só cresce: uma janela que recusou 423 e ficou com 466 não
    /// volta a aceitar 423 depois, e baixar o mínimo por causa de uma medição solta faria o
    /// mosaico tentar espremê-la de novo, sem fim.
    /// </summary>
    public void SetMinimum(nint hwnd, int width, int height)
    {
        var node = Find(hwnd);
        if (node == null) return;

        if (width > node.MinWidth) node.MinWidth = width;
        if (height > node.MinHeight) node.MinHeight = height;
    }

    /// <summary>Troca duas janelas de lugar. Os percentuais ficam onde estão de propósito: quem
    /// vai para o lugar maior fica maior — é o lugar que tem tamanho, não a janela.</summary>
    public void Swap(nint a, nint b)
    {
        var first = Find(a);
        var second = Find(b);
        if (first == null || second == null || first == second) return;

        (first.Hwnd, second.Hwnd) = (second.Hwnd, first.Hwnd);
    }

    /// <summary>Devolve os irmãos ao "divide igual". Chamado depois de toda mudança de estrutura:
    /// com um irmão a mais ou a menos, os percentuais antigos já não somam 1, e mantê-los deixaria
    /// sobra ou falta de espaço no pai.</summary>
    private static void ResetSiblingPercent(Node parent)
    {
        foreach (var child in parent.Children) child.Percent = 0.0;
    }

    private static void Collapse(Node container)
    {
        while (container.Kind == NodeKind.Container &&
               container.Children.Count == 1 &&
               container.Parent != null)
        {
            var parent = container.Parent;
            var only = container.Children[0];
            var at = container.Index;

            container.RemoveChild(only);
            parent.RemoveChild(container);
            parent.InsertAt(at, only);

            container = parent;
        }
    }

    private Node Root(nint monitor, SplitLayout layout)
    {
        if (_roots.TryGetValue(monitor, out var existing)) return existing;

        var root = Node.NewContainer(layout);
        _roots[monitor] = root;
        return root;
    }

    // ── redimensionar ─────────────────────────────────────────

    /// <summary>
    /// Dá <paramref name="deltaPx"/> pixels a mais para <paramref name="hwnd"/> naquele lado,
    /// tirando-os do vizinho de lá — o espaço não vem do nada, sempre sai de alguém.
    ///
    /// O par é procurado subindo a árvore: se os irmãos diretos não dividem espaço no eixo pedido
    /// (estão empilhados, quando se pede esquerda ou direita), sobe um nível e tenta os
    /// containers. É o que faz a divisória de fora responder igual à de dentro.
    ///
    /// Devolve <c>false</c> quando não há vizinho naquele lado — a janela já está na ponta do
    /// grid, e quem chamou não precisa reaplicar layout nenhum.
    /// </summary>
    public bool Resize(nint hwnd, SplitLayout axis, bool towardsEnd, int deltaPx)
    {
        var node = Find(hwnd);
        if (node == null) return false;

        var current = node;
        while (current.Parent != null)
        {
            var parent = current.Parent;
            if (parent.Layout == axis)
            {
                var neighbour = current.Index + (towardsEnd ? 1 : -1);
                if (neighbour >= 0 && neighbour < parent.Children.Count)
                {
                    return Transfer(parent, current, parent.Children[neighbour], deltaPx, parent.SplitSize, axis);
                }
            }
            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Passa uma fatia de <paramref name="from"/> para <paramref name="to"/>. Nenhum dos dois pode
    /// ficar abaixo do próprio mínimo — nem do mínimo de verdade da janela, quando ele já é
    /// conhecido, nem de um piso de 5% do pai para as que ainda não revelaram o seu.
    ///
    /// Parar aqui é o que impede o caso que dava mais errado: continuar encolhendo uma janela que
    /// já está no limite dela faz o Windows devolver um tamanho maior que o pedido a cada
    /// tentativa, e o mosaico interpretava isso como "esta janela não obedece" e a expulsava.
    /// </summary>
    private static bool Transfer(Node parent, Node from, Node to, int deltaPx, int totalSize, SplitLayout axis)
    {
        if (totalSize <= 0) return false;

        // percentuais implícitos ("divide igual") viram explícitos antes de mexer em um só deles:
        // sem isso, dar 10% a um irmão deixaria os outros ainda dizendo "divide igual" e a soma
        // passaria de 1
        Materialize(parent);

        var change = (double)deltaPx / totalSize;
        var fromPercent = from.Percent + change;
        var toPercent = to.Percent - change;

        if (fromPercent < Floor(from, axis, totalSize)) return false;
        if (toPercent < Floor(to, axis, totalSize)) return false;

        from.Percent = fromPercent;
        to.Percent = toPercent;
        return true;
    }

    /// <summary>O menor percentual que este nó pode ocupar do pai: o mínimo real dele quando já se
    /// sabe qual é, senão 5% — o bastante para uma janela ainda desconhecida não virar uma tira
    /// sem alvo para arrastar de volta.</summary>
    private static double Floor(Node node, SplitLayout axis, int totalSize)
    {
        var minimum = MinimumOf(node, axis);
        return minimum > 0 ? (double)minimum / totalSize : 0.05;
    }

    private static void Materialize(Node parent)
    {
        if (parent.Children.Count == 0) return;

        var equal = 1.0 / parent.Children.Count;
        foreach (var child in parent.Children)
            if (child.Percent <= 0) child.Percent = equal;
    }

    // ── calcular os retângulos ────────────────────────────────

    /// <summary>
    /// Os retângulos de todas as janelas de um monitor. <paramref name="skip"/> são as janelas que
    /// continuam na árvore mas não ocupam espaço agora — minimizadas, sobretudo: elas ficam
    /// guardadas na posição delas justamente para voltarem ao mesmo lugar ao serem restauradas, em
    /// vez de reentrarem no fim.
    /// </summary>
    public Dictionary<nint, RECT> ComputeRects(nint monitor, RECT area, int gap, ISet<nint> skip)
    {
        var result = new Dictionary<nint, RECT>();
        if (_roots.TryGetValue(monitor, out var root)) Compute(root, area, gap, skip, result);
        return result;
    }

    private static void Compute(Node node, RECT area, int gap, ISet<nint> skip, Dictionary<nint, RECT> result)
    {
        node.Rect = area;

        if (node.Kind == NodeKind.Window)
        {
            if (!skip.Contains(node.Hwnd)) result[node.Hwnd] = area;
            return;
        }

        var visible = node.Children.Where(c => Occupies(c, skip)).ToList();
        if (visible.Count == 0) return;
        if (visible.Count == 1) { Compute(visible[0], area, gap, skip, result); return; }

        var horizontal = node.Layout == SplitLayout.Horizontal;
        var total = (horizontal ? area.Width : area.Height) - gap * (visible.Count - 1);
        if (total <= 0) return;

        node.SplitSize = total;

        var sizes = Sizes(visible, total, node.Layout);
        var offset = horizontal ? area.Left : area.Top;

        for (var i = 0; i < visible.Count; i++)
        {
            var slice = horizontal
                ? new RECT { Left = offset, Top = area.Top, Right = offset + sizes[i], Bottom = area.Bottom }
                : new RECT { Left = area.Left, Top = offset, Right = area.Right, Bottom = offset + sizes[i] };

            Compute(visible[i], slice, gap, skip, result);
            offset += sizes[i] + gap;
        }
    }

    /// <summary>Um nó ocupa espaço se tiver ao menos uma janela que não está sendo pulada — um
    /// container cujas janelas estão todas minimizadas não deve reservar uma fatia vazia.</summary>
    private static bool Occupies(Node node, ISet<nint> skip) =>
        node.Windows().Any(w => !skip.Contains(w.Hwnd));

    /// <summary>
    /// Reparte <paramref name="total"/> pelos percentuais de cada filho, respeitando o tamanho
    /// mínimo de cada um. A última fatia leva o resto exato da divisão: arredondar cada uma para
    /// baixo, sozinho, deixa uma fresta de alguns pixels na borda final do monitor.
    ///
    /// Quem cairia abaixo do próprio mínimo é fixado nele, e o que falta sai de quem ainda tem
    /// folga — em proporção, para a diferença não recair toda sobre um vizinho. Sem isso, pedir
    /// uma janela menor do que ela aceita não encolhia nada: ela devolvia o tamanho dela e passava
    /// por cima da vizinha.
    /// </summary>
    private static int[] Sizes(List<Node> children, int total, SplitLayout axis)
    {
        var count = children.Count;
        var sizes = new int[count];
        var minimums = new int[count];
        var fixado = new bool[count];

        for (var i = 0; i < count; i++) minimums[i] = MinimumOf(children[i], axis);

        // não cabe nem o mínimo de todo mundo: cada um leva o seu e a sobreposição é inevitável —
        // é o melhor possível, e melhor do que espremer alguém a zero
        if (minimums.Sum() >= total) return minimums;

        var livre = total;
        var repartir = Enumerable.Range(0, count).ToList();

        // fixar quem não cabe é o que muda a conta dos outros, então repete até ninguém mais
        // estourar o próprio mínimo
        while (true)
        {
            var pesoTotal = repartir.Sum(i => Weight(children[i], count));
            var estourou = false;

            foreach (var i in repartir.ToList())
            {
                var fatia = (int)(Weight(children[i], count) / pesoTotal * livre);
                if (fatia >= minimums[i]) continue;

                sizes[i] = minimums[i];
                fixado[i] = true;
                livre -= minimums[i];
                repartir.Remove(i);
                estourou = true;
            }

            if (!estourou) break;
        }

        // o que sobrou vai para quem tem folga, e o último da fila leva o resto exato
        var restante = livre;
        for (var k = 0; k < repartir.Count; k++)
        {
            var i = repartir[k];
            if (k == repartir.Count - 1) { sizes[i] = restante; break; }

            var pesoTotal = repartir.Sum(j => Weight(children[j], count));
            sizes[i] = (int)(Weight(children[i], count) / pesoTotal * livre);
            restante -= sizes[i];
        }

        // ninguém tem folga (todos fixados no mínimo): o resto sobra para o último
        if (repartir.Count == 0 && count > 0) sizes[^1] += restante;

        return sizes;
    }

    private static double Weight(Node child, int count) =>
        child.Percent > 0 ? child.Percent : 1.0 / count;

    /// <summary>
    /// O menor tamanho que este nó aceita naquele eixo. Numa janela é o mínimo dela; num container
    /// depende do sentido do corte: cortando no mesmo eixo, os filhos ficam em fila e os mínimos
    /// somam; cortando no outro, eles se sobrepõem naquele eixo e vale o maior.
    /// </summary>
    private static int MinimumOf(Node node, SplitLayout axis)
    {
        if (node.Kind == NodeKind.Window)
            return axis == SplitLayout.Horizontal ? node.MinWidth : node.MinHeight;

        if (node.Children.Count == 0) return 0;

        return node.Layout == axis
            ? node.Children.Sum(c => MinimumOf(c, axis))
            : node.Children.Max(c => MinimumOf(c, axis));
    }
}
