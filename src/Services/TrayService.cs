using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Um ícone da bandeja, do jeito que o Windows o mostra agora.
///
/// A <see cref="Position"/> é o lugar dele no painel de ícones ocultos — é por ela que o
/// clique encontra o botão de volta, já que os botões de lá não têm identificador nenhum.
/// </summary>
public sealed record TrayIcon(int Position, string Name, BitmapSource? Image, string Signature = "",
                             string Alias = "")
{
    /// <summary>
    /// O que a pessoa lê: o apelido dela, se houver, senão o nome que veio do Windows.
    ///
    /// O apelido é um campo à parte, e <b>não</b> uma troca do <see cref="Name"/>, porque o
    /// nome é a identidade usada para reachar o botão na hora do clique — o
    /// <c>Find</c> confere se o botão naquela posição ainda é o mesmo comparando o nome
    /// montado pela mesma regra da leitura. Trocar o <c>Name</c> pelo apelido fazia essa
    /// conferência falhar sempre, e o clique era recusado em silêncio: o ícone batizado
    /// deixava de abrir o programa.
    /// </summary>
    public string Label => !string.IsNullOrWhiteSpace(Alias) ? Alias
                         : string.IsNullOrWhiteSpace(Name) ? "Ícone da bandeja"
                         : Name;
}

/// <summary>
/// Os ícones da bandeja do Windows 11.
///
/// A bandeja legada morreu — não existe mais <c>ToolbarWindow32</c> dentro de
/// <c>TrayNotifyWnd</c>, e ler os botões por <c>TB_GETBUTTON</c> + <c>ReadProcessMemory</c>
/// deixou de ser possível. O que resta, e funciona, é o painel de ícones ocultos: a
/// automação de interface enumera os botões dele e sabe acioná-los, e o
/// <see cref="PrintWindow"/> tira dele o desenho de cada ícone.
///
/// **É a única fonte que diz a verdade.** A primeira versão disto montava a lista pelo
/// registro (<c>NotifyIconSettings</c>) cruzado com os processos vivos, o que é barato e
/// não pisca — mas o registro é um histórico, não um retrato: mostrava seis ícones onde o
/// Windows mostrava três, incluindo programas que rodavam sem ter ícone nenhum na bandeja.
/// Um painel que não bate com o do lado não serve.
///
/// O preço é que ler exige abrir o painel do Windows, e abrir exige a barra dele visível por
/// uns 300 ms. Por isso a leitura não acontece sozinha: só ao subir e quando a pessoa abre a
/// seta — que é exatamente quando o Windows também mostraria esses ícones.
/// </summary>
public static class TrayService
{
    /// <summary>Classe da janela do painel de ícones ocultos no Windows 11.</summary>
    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>Como o Windows chama o botão de cada ícone dentro do painel.</summary>
    private const string NotifyItemId = "NotifyItemIcon";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    /// <summary>Folga para o DWM criar a superficie de uma janela recem-marcada em camada.</summary>
    private static readonly TimeSpan SurfaceSettle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Deixa uma janela do shell invisível sem escondê-la.
    ///
    /// É o que tira a piscada. Ler a bandeja obriga a barra do Windows a ficar de pé por
    /// meio segundo — escondida, o painel de ícones ocultos não abre —, e ela aparecia por
    /// cima da nossa junto com o painel dele. Com <c>WS_EX_LAYERED</c> e alfa zero a janela
    /// continua existindo, continua respondendo à automação e continua sendo fotografável
    /// pelo <c>PrintWindow</c>; só não é desenhada na tela.
    ///
    /// Junto vai <c>WS_EX_TRANSPARENT</c>, que a faz não responder ao mouse. Sem isso a
    /// barra do Windows, mesmo invisível, continua debaixo do cursor: a nossa seta fica no
    /// mesmo canto da seta dela, e o Windows mostrava a **dica dele** — "Mostrar ícones
    /// ocultos" — por cima do nosso cartão. A dica é outra janela, e não herda a camada.
    ///
    /// O estilo original volta no <see cref="Dispose"/>, sempre — deixar o painel do Windows
    /// invisível estragaria a bandeja de verdade, aquela que a pessoa abre pela barra dele.
    /// Uma janela que já era em camada fica intocada: mexer no alfa dela seria pior.
    /// </summary>
    private sealed class Invisible : IDisposable
    {
        private readonly nint _window;
        private readonly nint _style;
        private readonly bool _applied;

        public Invisible(nint window)
        {
            _window = window;
            if (window == 0) return;

            _style = GetWindowLongPtr(window, GWL_EXSTYLE);
            if (((long)_style & WS_EX_LAYERED) != 0) return;

            SetWindowLongPtr(window, GWL_EXSTYLE,
                             (nint)((long)_style | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            SetLayeredWindowAttributes(window, 0, 0, LWA_ALPHA);
            _applied = true;
        }

        public void Dispose()
        {
            if (_applied) SetWindowLongPtr(_window, GWL_EXSTYLE, _style);
        }
    }

    /// <summary>
    /// Guarda a área de trabalho enquanto a bandeja é lida e a devolve **uma vez**, no fim.
    ///
    /// Mostrar a barra do Windows faz o shell devolver a ela os pixels que a dock tinha
    /// tomado: a área útil pula de 24 para 32. Toda janela maximizada é redimensionada nesse
    /// instante — é a mudança de tamanho que se via ao abrir a bandeja.
    ///
    /// A tentação é reagir: um relógio repondo o valor a cada 25 ms. Foi o que havia aqui, e
    /// **piorava as duas coisas de uma vez**. Enquanto a barra do Windows está de pé o shell
    /// insiste em cobrar o espaço dela, então cada reposição comprava uma nova cobrança:
    /// medido, oito idas e voltas — oito redimensionamentos — numa única abertura, em vez de
    /// um. E cada chamada dessas atravessa o shell no exato momento em que a automação
    /// tenta ler o painel dele: os botões vinham com retângulo vazio, a foto saía preta, a
    /// leitura ia a dezesseis segundos e os ícones apareciam sem desenho.
    ///
    /// Reagir depois também não resolve, e por um motivo que custou uma volta inteira: **o
    /// que redimensiona janela não é o valor da área, é o anúncio.** Os pedidos daqui vão sem
    /// <c>SPIF_SENDCHANGE</c> e ninguém se mexe; o do shell vai com aviso, e todas se mexem.
    /// Corrigir depois é sempre um anúncio a mais.
    ///
    /// Então aqui se chega **antes**: a área é posta no valor que o shell vai querer — o dela
    /// com a barra dele ocupando o espaço — enquanto a barra ainda está escondida. Como esse
    /// pedido é silencioso, nada se mexe; e quando a barra aparece o shell recalcula, encontra
    /// o valor que já queria e não tem o que anunciar. No fim, com a barra escondida de novo,
    /// o valor da dock volta do mesmo jeito silencioso. Nenhuma janela é redimensionada em
    /// nenhum dos dois momentos.
    /// </summary>
    private sealed class PinnedWorkArea : IDisposable
    {
        private readonly RECT _area;
        private readonly bool _active;

        public PinnedWorkArea(nint taskbar, bool active)
        {
            _active = active;
            if (!active) return;

            var area = new RECT();
            if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref area, 0)) { _active = false; return; }
            _area = area;

            var wanted = WithBar(area, taskbar);
            if (!Same(wanted, area)) SystemParametersInfo(SPI_SETWORKAREA, 0, ref wanted, 0);
        }

        /// <summary>
        /// A área de trabalho como ela fica com a barra do Windows ocupando o espaço dela —
        /// a conta que o shell vai fazer sozinho assim que a barra aparecer.
        /// </summary>
        private static RECT WithBar(RECT work, nint taskbar)
        {
            if (taskbar == 0 || !GetWindowRect(taskbar, out var bar)) return work;

            if (bar.Width >= bar.Height)
            {
                if (bar.Top <= work.Top) work.Top = Math.Max(work.Top, bar.Bottom);
                else work.Bottom = Math.Min(work.Bottom, bar.Top);
            }
            else if (bar.Left <= work.Left) work.Left = Math.Max(work.Left, bar.Right);
            else work.Right = Math.Min(work.Right, bar.Left);

            return work;
        }

        private static bool Same(RECT a, RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        /// <summary>
        /// Devolve o valor da dock, também sem aviso. Roda depois de a barra do Windows já
        /// ter sido escondida — antes disso o shell cobraria o espaço de volta.
        /// </summary>
        public void Dispose()
        {
            if (!_active) return;

            var now = new RECT();
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref now, 0) && Same(now, _area)) return;

            var area = _area;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref area, 0);
        }
    }

    /// <summary>
    /// Tira a camada que uma leitura interrompida possa ter deixado nas janelas do shell.
    ///
    /// A leitura marca a barra do Windows e o painel de ícones ocultos com
    /// <c>WS_EX_LAYERED</c> e alfa zero, e desmarca no fim — mas se o processo morrer no meio
    /// dos ~600 ms, a marca fica. Uma barra do Windows invisível é um estrago silencioso: ela
    /// existe, recebe cliques, e não se vê. Como só nós a marcamos, e sempre em passagem,
    /// encontrá-la marcada ao subir quer dizer que a última vez não terminou.
    ///
    /// **Sobra é só a que está escondida.** Desde que o modo "esconder a barra do Windows"
    /// passou a apagá-la pela camada em vez do <c>SW_HIDE</c> (veja o
    /// <see cref="TaskbarService"/>), uma barra em camada e **visível** é a marcação em
    /// vigor, não um resto — e tirá-la aqui faria a barra do Windows piscar na tela toda vez
    /// que a dock subisse, já que esta limpeza roda depois de o modo ser aplicado.
    /// </summary>
    public static void ClearLeftovers()
    {
        foreach (var (window, ehBarra) in new[]
                 {
                     (FindWindow("Shell_TrayWnd", null), true),
                     (FindWindow(OverflowClass, null), false)
                 })
        {
            if (window == 0) continue;
            if (ehBarra && IsWindowVisible(window)) continue;

            var style = GetWindowLongPtr(window, GWL_EXSTYLE);
            if (((long)style & WS_EX_LAYERED) == 0) continue;

            SetWindowLongPtr(window, GWL_EXSTYLE,
                             (nint)((long)style & ~WS_EX_LAYERED & ~WS_EX_TRANSPARENT));
            Log.Write("uma janela do shell tinha ficado em camada de uma leitura interrompida; desfeito");
        }
    }

    /// <summary>
    /// Devolve o foco a quem o tinha antes.
    ///
    /// Abrir o painel de ícones ocultos ativa o shell, e a janela em que a pessoa estava
    /// trabalhando perde o foco — a barra de título dela apaga e acende. Como o painel é
    /// invisível, o que se vê é uma piscada sem motivo aparente.
    ///
    /// A devolução usa o mesmo empurrão do resto do projeto (<see cref="WindowService.Focus"/>):
    /// só o processo em primeiro plano pode trocar o foco, e neste instante ele é o shell.
    ///
    /// **Quando devolver importa mais do que devolver.** Medido nesta máquina: a janela da
    /// pessoa ficava 126 ms sem foco, porque a devolução só acontecia no fim de tudo — depois
    /// de ler a lista, fotografar os ícones e fechar o painel. É esse intervalo que se vê como
    /// a barra de título apagando e acendendo, e era ele que fazia a abertura do cartão
    /// parecer que "a tela redesenhou". Devolver assim que o painel está de pé encurta a
    /// piscada para o tempo de abrir, e a leitura por automação não precisa de foco nenhum
    /// para continuar.
    /// </summary>
    private sealed class KeepsFocus : IDisposable
    {
        private readonly nint _window = GetForegroundWindow();
        private bool _done;

        /// <summary>Devolve agora, se ainda não foi devolvido.</summary>
        public void Restore()
        {
            if (_done) return;
            _done = true;

            if (_window == 0 || !IsWindow(_window)) return;
            if (GetForegroundWindow() == _window) return;   // ninguém mexeu: nada a fazer

            Log.Trace("o painel do Windows tinha tomado o foco; devolvendo a quem o tinha");
            WindowService.Focus(_window);
        }

        /// <summary>Rede de segurança: se algo estourou antes da devolução, ela acontece aqui.</summary>
        public void Dispose() => Restore();
    }

    // ── ler ─────────────────────────────────────────────────

    /// <summary>
    /// Os ícones que o Windows tem na bandeja agora.
    ///
    /// Custa abrir e fechar o painel de ícones ocultos — uns 600 ms —, então acontece só
    /// quando a pessoa abre a seta. Houve uma tentativa de acelerar isto conferindo antes
    /// só os nomes (mostrando a janela do painel direto, sem a barra: 90 ms): a lista vinha
    /// certa, mas deixava o painel num estado em que a leitura seguinte não pintava, e os
    /// ícones apareciam sem imagem. Correção vale mais que os 500 ms.
    /// </summary>
    /// <summary>
    /// Por quanto tempo a última leitura vale sem reler.
    ///
    /// Ler tem um custo que não é só tempo: abrir o painel do Windows **traz ele para
    /// primeiro plano**, e a janela em que a pessoa está trabalhando perde o foco e pisca a
    /// barra de título. O foco é devolvido no fim, mas a piscada acontece.
    ///
    /// Abrir e fechar o cartão em seguida — que é o gesto de quem está conferindo alguma
    /// coisa — relia toda vez e piscava toda vez. Poucos segundos de validade cobrem esse
    /// vaivém sem deixar a lista velha: o cartão se fecha sozinho em três.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);

    private static DateTime _read = DateTime.MinValue;

    /// <summary>
    /// Faz a próxima abertura reler, mesmo dentro da validade.
    ///
    /// Quem chama é a dock, quando uma janela desaparece: um programa que fecha costuma
    /// levar o ícone dele junto, e o cartão mostrava o ícone de um app já fechado até os
    /// cinco segundos passarem. É o oposto de reler por precaução — só relê quando alguma
    /// coisa de fato aconteceu.
    /// </summary>
    public static void Invalidate() => _read = DateTime.MinValue;

    public static IReadOnlyList<TrayIcon> Read()
    {
        if (_cache.Count > 0 && DateTime.UtcNow - _read < Fresh)
        {
            Log.Trace($"leitura dispensada: a de {(DateTime.UtcNow - _read).TotalSeconds:0.0}s atrás ainda vale");
            return _cache;
        }

        Log.Trace("leitura começou (abre o painel do Windows — é aqui que o foco sai)");
        var clock = Stopwatch.StartNew();

        var icons = WithShell(Capture);
        _read = DateTime.UtcNow;

        // os nomes vão junto de propósito: só assim o log distingue "o painel listou o mesmo
        // ícone duas vezes" de "a lista mudou entre uma leitura e outra"
        Log.Trace($"leitura terminou em {clock.ElapsedMilliseconds} ms com {icons?.Count ?? 0} ícones: " +
                  $"{(icons is null ? "—" : string.Join(" | ", icons.Select(i => i.Label)))}");

        // Na primeira leitura de cada sessão a janela do painel ainda não existia, e nasceu
        // sem camada — sem camada o DWM não guarda a superfície de onde sai a foto, e os
        // ícones vêm sem desenho. Agora ela existe: uma segunda tentativa sai completa.
        //
        // Só nesse caso, e só uma vez: é o dobro do tempo, mas acontece uma vez por sessão.
        if (icons is null || icons.Any(i => i.Image is null))
        {
            var again = WithShell(Capture);
            if (again is { Count: > 0 } && again.All(i => i.Image is not null)) icons = again;
        }

        // uma leitura que falhou não deve apagar o cartão: fica o que se leu por último
        if (icons is { Count: > 0 }) _cache = icons;
        return _cache;
    }

    /// <summary>A última leitura que deu certo — o que o cartão mostra se a próxima falhar.</summary>
    private static IReadOnlyList<TrayIcon> _cache = Array.Empty<TrayIcon>();

    /// <summary>
    /// A última lista lida, sem custo nenhum.
    ///
    /// É o que as Configurações usam para mostrar os ícones e deixar batizá-los: uma leitura
    /// de verdade levanta meio shell e passa de meio segundo, e abrir uma janela de opções
    /// não é hora para isso. Vem vazia se a bandeja ainda não foi lida nesta sessão.
    /// </summary>
    public static IReadOnlyList<TrayIcon> Cached => _cache;
    /// <summary>
    /// Abre o painel de ícones ocultos, faz o que for pedido com ele e devolve tudo ao lugar.
    ///
    /// As três coisas que precisam acontecer juntas moram aqui, porque errar a ordem de
    /// qualquer uma delas custou uma sessão de depuração:
    ///
    /// 1. **o vigia da barra fica pausado** — ele roda a cada 150 ms e esconde a barra do
    ///    Windows; no meio desta operação, isso fazia o painel não abrir e o clique não
    ///    fazer nada, de vez em quando;
    /// 2. **as duas janelas do shell ficam invisíveis** antes de aparecerem, em vez de
    ///    piscarem na tela;
    /// 3. **tudo volta como estava** — inclusive se algo explodir no meio.
    /// </summary>
    /// <param name="handsOff">
    /// Deixa o painel e o foco em paz no fim, para quando o trabalho foi **acionar** um
    /// ícone e não apenas ler. O Esc que fecha o painel fecharia junto o menu que o ícone
    /// acabou de abrir, e devolver o foco à janela anterior o tiraria desse menu — os dois
    /// faziam o clique parecer que não tinha funcionado.
    /// </param>
    /// <param name="quietClose">
    /// Fecha o painel **sem** o Esc, escondendo a janela. É o que o menu de um ícone exige: o
    /// Esc vai para quem está em primeiro plano, e nesse instante quem está lá é o menu que o
    /// programa acabou de abrir — medido, ele nascia e morria 400 ms depois, sem deixar
    /// vestígio na tela. Veja o <see cref="HideOverflow"/>.
    /// </param>
    private static T? WithShell<T>(Func<nint, T> work, bool handsOff = false,
                                   bool quietClose = false) where T : class
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var wasVisible = IsWindowVisible(taskbar);

        using var calm = TaskbarService.Suspend();

        // As duas janelas do shell ficam em camada com alfa zero: some da tela o que a
        // pessoa não precisa ver.
        //
        // No painel isso não é só estética — é o que faz a foto sair. Uma janela em camada
        // ganha superfície de redirecionamento no DWM, e é dela que o `PrintWindow` tira o
        // desenho; sem a camada ele devolve preto, e o recorte descarta tudo. (Os retângulos
        // vazios que apareciam antes não vinham daqui: vinham de repor a área de trabalho
        // durante a leitura — veja o `PinnedWorkArea`.)
        var known = FindWindow(OverflowClass, null);
        using var hiddenFlyout = new Invisible(known);
        using var hiddenTaskbar = new Invisible(wasVisible ? 0 : taskbar);
        using var pinned = new PinnedWorkArea(taskbar, !wasVisible);

        // Abrir o painel do Windows traz **ele** para primeiro plano, e quem estava
        // trabalhando perde o foco: a janela pisca a barra de título e volta. O painel é
        // invisível, então a piscada não tem nem explicação na tela.
        //
        // Guardar quem estava na frente e devolver no fim resolve — e o `using` garante que
        // isso aconteça também quando algo dá errado no meio.
        using var focus = handsOff ? null : new KeepsFocus();

        try
        {
            // O `pinned` já pôs a área de trabalho no valor que o shell vai querer, então
            // este ShowWindow não muda nada e ninguém é redimensionado. O valor da dock
            // volta quando o `pinned` for solto — depois do `finally` que esconde a barra.
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            long tAbrir, tLista, tTrabalho;

            if (!wasVisible) ShowWindow(taskbar, SW_SHOW);
            if (!OpenOverflow(taskbar)) return null;
            tAbrir = relogio.ElapsedMilliseconds;

            var flyout = Overflow();
            if (flyout == 0) { Log.Write("o painel do Windows não ficou visível depois de abrir"); return null; }

            // Na primeira abertura de cada sessão do explorer a janela do painel ainda não
            // existia lá em cima, então nasceu sem camada — e sem camada a foto sai preta.
            // Aqui ela ganha a camada, e a pausa é para o DWM criar a superfície antes de
            // alguém tentar fotografar.
            using var late = new Invisible(known == 0 ? flyout : 0);
            if (known == 0) Thread.Sleep(SurfaceSettle);

            // A barra do Windows fica de pé até o fim, e não há como abreviar isso: tentado,
            // escondê-la aqui faz o painel parar de ser pintado — a foto sai de uma cor só, os
            // retângulos vêm vazios e a leitura entra em retentativa (medido: 24 s e quatro
            // ícones sem desenho). Quem a esconde é o `finally`; a área de trabalho fica como
            // o shell quiser durante esses milissegundos e volta ao lugar depois, no `pinned`.

            // Só agora se espera a lista ficar pronta, e o lugar disto na ordem vale por dois
            // problemas:
            //
            // - **a piscada**: esta espera acontecia dentro do `OpenOverflow`, ou seja, antes
            //   da camada. O painel do Windows ficava visível na tela o tempo todo dela — era
            //   isso que se via como a bandeja "aparecendo duas vezes", o cartão de cá
            //   fechando e o painel de lá abrindo no mesmo canto.
            // - **a lentidão**: ali ela também caía debaixo do martelo que repunha a área de
            //   trabalho, e ele atrapalha a automação exatamente como atrapalhava os
            //   retângulos dos ícones. Medido: a mesma espera custava 1,2 s debaixo do
            //   martelo e ~40 ms sem ele. O martelo já não existe (veja o `PinnedWorkArea`),
            //   mas a ordem continua valendo pela piscada.
            // A lista é esperada **com o painel ainda em foco**. Tentado devolver o foco antes
            // desta espera, para encurtar a piscada: o painel perde a ativação e para de montar
            // a lista — medido, a leitura ia a 4 s e voltava com zero ícones. O XAML do shell só
            // preenche os botões enquanto o painel está ativo.
            if (!WaitForItems()) Log.Write("o painel do Windows abriu, mas a lista dele não ficou pronta");
            tLista = relogio.ElapsedMilliseconds;

            // Depois da lista pronta, sim: a leitura que vem a seguir é automação e foto, e
            // nenhuma das duas precisa de foco. Veja o `KeepsFocus`.
            focus?.Restore();

            var result = work(flyout);
            tTrabalho = relogio.ElapsedMilliseconds;

            // onde o tempo foi: sem isto, otimizar a leitura é chutar qual das quatro fases
            // está cara — e as constantes de espera daqui já foram calibradas no escuro antes
            Log.Trace($"fases da leitura: abrir={tAbrir} ms, lista=+{tLista - tAbrir} ms, " +
                      $"foto/recorte=+{tTrabalho - tLista} ms");
            Log.Trace($"  espera pela lista: {_probeCount} sondagem(ns), {_probeCost} ms dentro " +
                      $"do Buttons() e ~{(tLista - tAbrir) - _probeCost} ms dormindo");


            // Acionar um ícone normalmente já fecha o painel do Windows — é o que acontece
            // quando se clica nele pela barra dele. Dar o Esc por cima disso fecharia também
            // o menu que o ícone acabou de abrir, então aqui se espera um instante e só se
            // insiste se o painel tiver ficado aberto: soltar o `using` com ele ainda de pé
            // devolveria a camada e ele apareceria na tela.
            if (handsOff) WaitForOverflowToClose();
            if (!handsOff || Overflow() != 0)
            {
                if (quietClose)
                {
                    HideOverflow();
                }
                else
                {
                    // O Esc pede ao shell para fechar o painel de verdade — é a troca de
                    // estado que o mantém consistente para a próxima abertura.
                    CloseOverflow();

                    // E o esconder vem logo atrás, sem esperar nada.
                    //
                    // <para><b>Por que não se espera mais.</b> O Esc fecha o painel de fora
                    // para dentro, com animação do shell. O que não podia acontecer era o
                    // `using` soltar a camada de alfa zero com a janela ainda na tela — ela
                    // piscava opaca no canto da bandeja, o que já pareceu "um segundo cartão
                    // abrindo sozinho". A defesa era esperar a animação (até 400 ms) e, se
                    // ainda estivesse de pé, esconder à força.</para>
                    //
                    // <para>Só que o `SW_HIDE` resolve o mesmo problema <b>instantaneamente</b>
                    // e sem depender de animação nenhuma: janela escondida não pisca, esteja
                    // a animação em que ponto estiver. A espera era a parte cara do fechamento
                    // — medido, ~510 ms dos 653 de uma leitura, e o log mostrava que quase
                    // sempre ela estourava o prazo e o esconder à força acontecia do mesmo
                    // jeito. Ou seja: pagava-se a espera para, no fim, fazer o que agora se
                    // faz de saída.</para>
                    HideOverflow();
                }
            }

            Log.Trace($"  fechar o painel: {relogio.ElapsedMilliseconds - tTrabalho} ms " +
                      $"({(quietClose ? "sem Esc" : "Esc + esconder")})");

            SweepShellPopups();
            return result;
        }
        catch (Exception ex)
        {
            Log.Write("falha ao usar o painel de ícones ocultos do Windows", ex);
            return null;
        }
        finally
        {
            if (!wasVisible) ShowWindow(taskbar, SW_HIDE);
        }
    }

    /// <summary>Fotografa o painel e recorta um ícone de cada botão.</summary>
    private static IReadOnlyList<TrayIcon> Capture(nint flyout)
    {
        var found = new List<TrayIcon>();
        _blank = 0;

        if (!GetWindowRect(flyout, out var bounds)) return found;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) { Log.Write("o painel sumiu antes de ser fotografado"); return found; }

        // A foto é tentada até sair com conteúdo: os botões aparecem na árvore de automação
        // **antes** de a janela pintar, e fotografar nesse instante devolve uma cor só — o
        // recorte descarta tudo e os ícones ficam invisíveis. Antes havia um tempo fixo de
        // 150 ms aqui, pago inteiro toda vez; esperar pela pintura custa o que ela precisar,
        // que em geral é bem menos.
        byte[]? shot = null;
        for (var waited = TimeSpan.Zero; waited < Timeout; waited += Poll)
        {
            shot = Screenshot(flyout, width, height);
            if (shot is not null && Painted(shot)) break;
            Thread.Sleep(Poll);
        }

        if (shot is null) Log.Write($"a foto do painel saiu vazia ({width}x{height})");
        else if (!Painted(shot))
            Log.Write($"o painel não chegou a pintar para ser fotografado ({width}x{height})");

        var position = 0;
        foreach (var button in Buttons(flyout))
        {
            var box = button.Cached.BoundingRectangle;
            var signature = string.Empty;
            var image = shot is null ? null : Crop(shot, width, height,
                                                   (int)box.Left - bounds.Left,
                                                   (int)box.Top - bounds.Top,
                                                   (int)box.Width, (int)box.Height,
                                                   out signature);

            var name = Clean(NameOf(button, position));
            if (name.StartsWith("Ícone da bandeja"))
                Log.Trace($"  ícone {position + 1} sem nome: assinatura do desenho = {signature}");

            found.Add(new TrayIcon(position, name, image, signature));
            position++;
        }

        if (found.Count == 0) Log.Write("o painel abriu, mas sem nenhum ícone dentro");

        // o que se aprendeu sobre os anônimos vale para esta composição de bandeja; veja o
        // `_anonymous`
        _anonymous = _blank > 0;
        _anonymousAt = _anonymous ? found.Count : -1;

        // um ícone sem imagem é sintoma, não detalhe: quer dizer que a foto do painel não
        // saiu, ou que a automação devolveu retângulos vazios (é o que acontece se a janela
        // estiver invisível na hora da leitura)
        var blank = found.Count(i => i.Image is null);
        if (blank > 0)
            Log.Write($"{blank} de {found.Count} ícones da bandeja vieram sem imagem " +
                      $"(painel {width}x{height}, primeiro retângulo {First(flyout)}, " +
                      $"foto {Sample(shot, width, height)})");

        return found;
    }

    /// <summary>
    /// O nome do botão, e um nome próprio para quem não tem.
    ///
    /// Alguns ícones chegam da automação sem nome nenhum — não é pressa nossa, é assim
    /// mesmo: esperar por eles até o estouro dos 2 s não muda nada. O problema é que todos
    /// caíam no mesmo rótulo genérico, e dois ícones diferentes viravam duas linhas iguais no
    /// cartão: **parecia que a bandeja tinha duplicado**. A posição, que já é o que identifica
    /// o botão na hora de acioná-lo, também serve para diferenciá-los aqui.
    ///
    /// A segunda tentativa é ao vivo, sem o cache: custa uma ida ao shell, e só acontece para
    /// os poucos anônimos — de vez em quando o nome chegou depois da busca.
    /// </summary>
    private static string NameOf(AutomationElement button, int position)
    {
        var name = button.Cached.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name;

        try { name = button.Current.Name; } catch { /* o botão sumiu no meio */ }
        if (!string.IsNullOrWhiteSpace(name)) return name;

        _blank++;
        Log.Trace($"o ícone na posição {position + 1} não tem nome na automação " +
                  "(o programa dono não publica dica de mouse — o Windows também não o nomeia)");

        DumpProperties(button, position);
        return $"Ícone da bandeja {position + 1}";
    }

    /// <summary>
    /// Despeja no rastro tudo o que a automação sabe sobre um ícone sem nome.
    ///
    /// Existe para uma pergunta específica: o <c>Name</c> vem vazio para alguns ícones, mas
    /// será que o nome real está em outra propriedade? Só roda para os anônimos, que são
    /// poucos, e só com o rastro ligado — enumerar propriedades ao vivo custa uma ida ao
    /// shell por item.
    /// </summary>
    private static void DumpProperties(AutomationElement button, int position)
    {
        try
        {
            var c = button.Current;
            Log.Trace($"  ícone {position + 1}: AutomationId='{c.AutomationId}' HelpText='{c.HelpText}' " +
                      $"ClassName='{c.ClassName}' ItemStatus='{c.ItemStatus}' ItemType='{c.ItemType}' " +
                      $"AccessKey='{c.AccessKey}' FrameworkId='{c.FrameworkId}' pid={c.ProcessId}");

            // e o que mais houver: varre todas as propriedades com valor, sem lista fixa
            foreach (var prop in button.GetSupportedProperties())
            {
                var value = button.GetCurrentPropertyValue(prop);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    Log.Trace($"    {prop.ProgrammaticName} = '{s}'");
            }
        }
        catch (Exception ex) { Log.Trace($"  ícone {position + 1}: não deu para ler as propriedades — {ex.Message}"); }
    }

    /// <summary>
    /// Um resumo da foto para o log: quantas cores distintas ela tem e o que há no canto e
    /// no meio. É o que separa "a foto não saiu" de "o recorte caiu no lugar errado".
    /// </summary>
    private static string Sample(byte[]? shot, int width, int height)
    {
        if (shot is null) return "nula";

        var colors = new HashSet<int>();
        for (var i = 0; i < shot.Length; i += 4)
            colors.Add(shot[i] | (shot[i + 1] << 8) | (shot[i + 2] << 16));

        var middle = ((height / 2) * width + width / 2) * 4;
        return $"{colors.Count} cores, canto=({shot[2]},{shot[1]},{shot[0]}), " +
               $"meio=({shot[middle + 2]},{shot[middle + 1]},{shot[middle]})";
    }

    /// <summary>O retângulo do primeiro ícone, só para o log dizer o que a automação viu.</summary>
    private static string First(nint flyout)
    {
        try
        {
            var button = Buttons(flyout).FirstOrDefault();
            return button is null ? "nenhum" : button.Cached.BoundingRectangle.ToString();
        }
        catch { return "erro"; }
    }

    /// <summary>
    /// A foto tem mais de uma cor?
    ///
    /// É como se sabe que a janela pintou. Sai no primeiro pixel diferente, então numa foto
    /// boa custa quase nada; só a foto vazia percorre tudo.
    /// </summary>
    private static bool Painted(byte[] shot)
    {
        for (var i = 4; i + 2 < shot.Length; i += 4)
            if (shot[i] != shot[0] || shot[i + 1] != shot[1] || shot[i + 2] != shot[2]) return true;

        return false;
    }

    /// <summary>
    /// Os pixels da janela, em BGRA.
    ///
    /// O bitmap é um DIB para os pixels terem endereço conhecido: passar por
    /// <c>CreateBitmapSourceFromHBitmap</c> jogaria fora o canal alfa, e é dele que sai o
    /// recorte limpo do ícone.
    /// </summary>
    private static byte[]? Screenshot(nint window, int width, int height)
    {
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0, previous = 0;

        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,      // negativo: primeira linha em cima, como se lê
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            bitmap = CreateDIBSection(memory, ref info, DIB_RGB_COLORS, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) return null;

            previous = SelectObject(memory, bitmap);
            if (!PrintWindow(window, memory, PW_RENDERFULLCONTENT)) return null;

            // o GDI acumula desenho em lote: sem descarregar, os bits do DIB ainda são os
            // originais e a foto sai de uma cor só — que era o motivo de os ícones da bandeja
            // aparecerem sem desenho
            GdiFlush();

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        catch { return null; }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Quanto do botão é moldura em volta do ícone. O botão do painel é bem maior que o
    /// desenho; recortá-lo inteiro deixaria o ícone minúsculo na barra.
    /// </summary>
    private const double Inset = 0.25;

    /// <summary>
    /// Distância de cor até a qual um pixel conta como fundo. Generosa o bastante para o
    /// gradiente sutil do painel, apertada o bastante para não comer a parte escura de um
    /// ícone.
    /// </summary>
    private const int BackgroundTolerance = 40;

    /// <summary>
    /// Tira um ícone da foto do painel, sem o fundo.
    ///
    /// O fundo do painel é opaco, não transparente: recortado e colado na nossa barra ele
    /// viraria um quadradinho escuro visível atrás de cada ícone. A cor de fundo é lida do
    /// canto do recorte — que é sempre moldura — e tudo que se parece com ela vira
    /// transparente.
    /// </summary>
    private static BitmapSource? Crop(byte[] pixels, int width, int height,
                                      int x, int y, int boxWidth, int boxHeight,
                                      out string signature)
    {
        signature = string.Empty;

        // O recorte é quadrado e o botão não é: ancorá-lo no canto — que era o que se fazia
        // — deixa o ícone deslocado para um lado e comido do outro, o que na barra aparece
        // como um desenho torto. Centralizado nos dois eixos, o ícone sai inteiro e no lugar.
        var size = Math.Min(boxWidth, boxHeight) - (int)Math.Round(Math.Min(boxWidth, boxHeight) * Inset) * 2;
        var left = x + (boxWidth - size) / 2;
        var top = y + (boxHeight - size) / 2;

        if (size <= 0 || left < 0 || top < 0 || left + size > width || top + size > height) return null;

        var crop = new byte[size * size * 4];
        for (var row = 0; row < size; row++)
            Buffer.BlockCopy(pixels, ((top + row) * width + left) * 4, crop, row * size * 4, size * 4);

        // A cor de fundo vem do canto do **botão**, não do canto do recorte: o recorte já
        // começa recuado 25% e nesse ponto pode haver ícone. Amostrar ali fazia a cor do
        // próprio desenho virar "fundo", e o ícone saía inteiro transparente.
        var corner = (y * width + x) * 4;
        byte bb = pixels[corner], bg = pixels[corner + 1], br = pixels[corner + 2];

        var opaque = 0;
        for (var i = 0; i < crop.Length; i += 4)
        {
            var distance = Math.Abs(crop[i] - bb) + Math.Abs(crop[i + 1] - bg) + Math.Abs(crop[i + 2] - br);
            crop[i + 3] = distance <= BackgroundTolerance ? (byte)0 : (byte)255;
            if (crop[i + 3] != 0) opaque++;
        }

        // recorte de uma cor só quer dizer que o painel não tinha pintado quando foi
        // fotografado: tudo fica dentro da tolerância e o ícone sai inteiro transparente —
        // uma imagem que existe e não se vê é pior que imagem nenhuma, porque não avisa
        if (opaque == 0) return null;

        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, crop, size * 4);
        image.Freeze();
        signature = Signature(crop, size);
        return image;
    }

    /// <summary>
    /// Uma assinatura curta do desenho de um ícone, para servir de identidade quando não há
    /// nome nenhum.
    ///
    /// Alguns programas não publicam dica de mouse (o `Master.exe` desta máquina é o caso), e
    /// aí não sobra **nada** de texto para identificá-los: a automação dá a todos os botões o
    /// mesmo <c>AutomationId</c> ("NotifyItemIcon") e a mesma classe, e o registro guarda a
    /// dica vazia. O que resta é o próprio desenho.
    ///
    /// A assinatura reduz o recorte a uma grade 8×8 de médias e guarda 4 bits por canal. Os
    /// dois cortes são de propósito: a média dilui o antialiasing (que muda de uma foto para
    /// outra), e a quantização absorve o que sobrar. O que ela **não** tolera é o programa
    /// trocar o próprio ícone — nesse caso é outro desenho, e vira outra identidade.
    /// </summary>
    private static string Signature(byte[] crop, int size)
    {
        const int Cells = 8;
        var sb = new StringBuilder(Cells * Cells * 3);

        for (var cy = 0; cy < Cells; cy++)
        for (var cx = 0; cx < Cells; cx++)
        {
            long b = 0, g = 0, r = 0;
            var opaque = 0;
            var total = 0;

            var x0 = cx * size / Cells; var x1 = (cx + 1) * size / Cells;
            var y0 = cy * size / Cells; var y1 = (cy + 1) * size / Cells;

            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * size + x) * 4;
                total++;
                if (crop[i + 3] == 0) continue;

                b += crop[i]; g += crop[i + 1]; r += crop[i + 2];
                opaque++;
            }

            // Célula de borda é quase toda fundo, e quanto dela é fundo muda de uma foto
            // para a outra: a máscara de transparência sai da tolerância de cor, que depende
            // do pixel amostrado como fundo. Medido — era só nas bordas que a assinatura
            // oscilava entre leituras. Abaixo de um quinto de pixels desenhados a célula
            // conta como vazia, e a assinatura passa a depender só do miolo, que é estável.
            if (total == 0 || opaque * 5 < total) { sb.Append("000"); continue; }

            sb.Append((b / opaque >> 4).ToString("x1"))
              .Append((g / opaque >> 4).ToString("x1"))
              .Append((r / opaque >> 4).ToString("x1"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// O nome que a automação dá ao botão repete a dica de mouse ("AnyDesk - pronto AnyDesk
    /// - pronto / 1 sessões") porque junta o rótulo e o tooltip. Aqui fica só uma vez.
    /// </summary>
    private static string Clean(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var parts = name.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Deduplicate)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase);

        return string.Join(" — ", parts).Trim();
    }

    /// <summary>
    /// Tira a metade repetida de um rótulo colado nele mesmo: o botão do AnyDesk chega como
    /// "AnyDesk - pronto AnyDesk - pronto".
    ///
    /// A limpeza é por pedaço, e não no texto já juntado: com a segunda linha no fim
    /// ("… — 1 sessões") as duas metades deixam de ser iguais e a repetição passava batida.
    /// </summary>
    private static string Deduplicate(string text)
    {
        var half = text.Length / 2;
        return text.Length > 3 && text.Length % 2 == 1 && text[half] == ' ' &&
               text[..half].Equals(text[(half + 1)..], StringComparison.CurrentCultureIgnoreCase)
               ? text[..half]
               : text;
    }

    // ── acionar ─────────────────────────────────────────────

    /// <summary>
    /// Aciona um ícone — o mesmo que clicar nele na bandeja do Windows.
    ///
    /// Quem responde é o programa dono do ícone, por um protocolo que a bandeja nova não
    /// expõe mais; o jeito é acionar o botão de verdade dentro do painel. A
    /// <see cref="TrayIcon.Position"/> diz qual, e o nome confere se a lista não mudou desde
    /// a leitura — se mudou, é melhor não acionar nada do que acionar o ícone errado.
    /// </summary>
    public static bool Invoke(TrayIcon icon)
    {
        // Quem estava em primeiro plano antes do clique — para devolver depois, se o clique
        // não trouxer ninguém de verdade para a frente. Veja o porquê logo abaixo.
        var before = GetForegroundWindow();

        var ok = WithShell<object>(flyout =>
        {
            if (Find(flyout, icon) is not { } button) return null!;

            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            return icon;

            // `quietClose` pela mesma razão que o menu de contexto já usava: quando o painel
            // do Windows não se fecha sozinho depois do clique, o caminho antigo mandava um
            // **Esc** — e o Esc não tem endereço, vai para quem estiver em primeiro plano.
            // Se o programa dono do ícone já tiver aberto a janela dele até lá, é ela que
            // recebe a tecla; num app cuja tela fecha no Esc (é o caso de muita coisa em
            // Delphi), o efeito é "abre e some". O `HideOverflow` tira o painel da tela sem
            // tecla nenhuma, é instantâneo e não depende de animação.
        }, handsOff: true, quietClose: true) is not null;

        // Abrir o painel do Windows para achar o botão do ícone traz o **explorer** para
        // primeiro plano — e diferente de uma leitura comum, aqui ninguém devolve o foco no
        // fim (é o `handsOff`: existe para não atrapalhar um ícone que abra um popup próprio,
        // tipo o volume ou o wi-fi do Windows). Quando o ícone clicado **não** tem popup nem
        // janela própria para assumir — o Pageant é o caso —, o primeiro plano ficava preso
        // no explorer (medido: 661 ms, e sem devolver sozinho nunca). A janela que estava na
        // frente antes ficava desativada até a pessoa clicar nela de novo à mão — é isso que
        // se via como "a tela redesenha" ao clicar num ícone da bandeja.
        //
        // A devolução só acontece se o primeiro plano continuar em algo do explorer: se o
        // ícone abriu a própria janela (o caso do Discord) ou um popup, esses são o motivo do
        // clique e não se deve tirá-los da frente.
        var after = GetForegroundWindow();
        if (after != before && Owner(after) == "explorer")
        {
            Log.Trace($"depois do clique em '{icon.Label}' o primeiro plano ficou preso no " +
                      "explorer; devolvendo a quem estava antes");
            WindowService.Focus(before);
        }

        // O clique pode ter fechado o programa (alguns saem com um único clique no ícone,
        // sem passar pelo menu), e sem isto o cartão continuaria mostrando o ícone de um
        // programa que já não existe até o cache de cinco segundos vencer sozinho — o
        // "demora a sumir" de quem acabou de fechar algo. A próxima leitura decide sozinha
        // se ainda há alguma coisa para mostrar; aqui só se marca que vale a pena conferir.
        Invalidate();
        return ok;
    }

    /// <summary>
    /// Abre o menu do ícone — o mesmo que clicar nele com o botão direito na bandeja do
    /// Windows. É onde moram o "Sair", o "Abrir configurações" e o resto do que cada programa
    /// oferece; o Discord é o exemplo de sempre.
    ///
    /// A automação não alcança isto sozinha: <c>InvokePattern</c> é o clique **esquerdo**, e não
    /// existe padrão de automação para o direito.
    ///
    /// **Três caminhos tentados; um funciona, e nenhum é livre de mexer no cursor.**
    ///
    /// 1. Mirar o retângulo do botão com <c>mouse_event</c> — sequestrava o cursor da pessoa,
    ///    que saltava para o canto da tela e voltava, à vista, no meio do que ela estivesse
    ///    fazendo, e ainda assim o menu não vinha.
    /// 2. <c>WM_RBUTTONDOWN</c>/<c>WM_RBUTTONUP</c> postados direto na janela do painel, nas
    ///    coordenadas do ícone — não move o cursor, mas também não abre o menu. O painel de
    ///    ícones ocultos é XAML moderno, e não reage a mensagem de mouse postada; confirmado
    ///    no log, "clique direito mandado" seguido de "nenhum menu apareceu" toda vez.
    /// 3. **Dar foco ao botão e apertar a tecla Menu** (o mesmo que Shift+F10) — o único que
    ///    funciona de verdade. O preço: o **Windows** move o cursor sozinho até o menu quando
    ///    ele abre por teclado, para casar mouse e teclado no que acabou de aparecer. Não dá
    ///    para evitar — é o sistema reagindo à tecla, não algo que a gente manda. Devolver o
    ///    cursor depois **também foi tentado e tirado**: o menu já pulava até o ícone sozinho,
    ///    e a devolução criava um vaivém — ida e volta, os dois visíveis — pior que o salto
    ///    único que fica agora. Ida sem volta é o mesmo que Shift+F10 faz em qualquer canto do
    ///    Windows; ninguém acha isso quebrado.
    ///
    /// O menu em si é desenhado pelo programa dono do ícone, junto da bandeja do Windows: é de
    /// lá que ele acha que foi chamado.
    /// </summary>
    /// <param name="anchor">
    /// Onde o ícone está no **nosso** cartão, em pixels de tela. O menu é desenhado pelo
    /// programa dono, e ele o põe junto da bandeja do Windows — do outro lado da tela, para
    /// quem clicou aqui. Com a âncora, o menu é trazido para debaixo do ícone assim que
    /// aparece; sem ela (âncora vazia), fica onde nasceu.
    /// </param>
    internal static bool ShowMenu(TrayIcon icon, RECT anchor = default)
    {
        var ok = ShowMenuCore(icon, anchor);

        // é o menu que tem o "Sair"/"Quit": a próxima vez que o cartão abrir, confere de
        // novo em vez de confiar no que já estava em cache. Veja o comentário do `Invoke`.
        Invalidate();
        return ok;
    }

    private static bool ShowMenuCore(TrayIcon icon, RECT anchor) =>
        WithShell<object>(flyout =>
        {
            if (Find(flyout, icon) is not { } button) return null!;

            // o retrato de antes: é a diferença que revela a janela do menu, que não tem
            // título nem classe previsível — o Discord desenha a dele em Chrome_WidgetWin_1 e
            // o Pageant usa o menu clássico do Windows (#32768)
            var before = VisibleWindows();

            try { button.SetFocus(); }
            catch (Exception ex)
            {
                // sem foco a tecla Menu iria para outro lugar, e abrir o menu do ícone errado
                // é pior que não abrir nenhum
                Log.Write($"não deu para pôr o foco no ícone '{icon.Label}'; o menu não foi aberto", ex);
                return null!;
            }

            Thread.Sleep(FocusSettle);

            using (PrendeCursor(icon))
            {
                keybd_event(VK_APPS, 0, 0, 0);
                keybd_event(VK_APPS, 0, KEYEVENTF_KEYUP, 0);

                Log.Trace($"tecla Menu enviada ao ícone '{icon.Label}'");

                MoveMenuToAnchor(before, anchor, icon);
            }

            return icon;
        }, handsOff: true, quietClose: true) is not null;

    /// <summary>
    /// Segura o cursor onde a pessoa o deixou enquanto o menu do ícone abre.
    ///
    /// <para><b>O problema.</b> Abrir o menu pela tecla Menu (`VK_APPS`) é o único caminho que
    /// funciona — os outros cinco estão documentados no APRENDIZADOS —, e ele vem com um
    /// efeito colateral: o cursor salta do nosso cartão até a bandeja do Windows, do outro
    /// lado da tela. Quem move é o **Explorer**, com `SetCursorPos`, como muleta para
    /// programas que perguntam `GetCursorPos()` na hora de desenhar o próprio menu.</para>
    ///
    /// <para><b>O que se tenta aqui.</b> `SetCursorPos` respeita `ClipCursor`. Preso numa
    /// caixa de 1×1 em cima de onde a pessoa clicou, o cursor não tem para onde ir — o salto
    /// não acontece em vez de acontecer e ser desfeito, que era a tentativa antiga (removida
    /// por criar um vaivém pior que o salto). De brinde, quem lê `GetCursorPos()` desenha o
    /// menu já debaixo do nosso ícone.</para>
    ///
    /// <para><b>O risco, e por que o rastro mede.</b> O Windows solta o clip por conta
    /// própria quando outra janela vira primeiro plano — e o menu, ao abrir, é exatamente
    /// isso. Se o Explorer mover o cursor **depois** dessa troca, o clip não alcança e o
    /// salto volta. O rastro registra a posição antes e depois para dizer qual dos dois
    /// aconteceu, sem depender de quem estava olhando.</para>
    /// </summary>
    private static IDisposable PrendeCursor(TrayIcon icon)
    {
        if (!GetCursorPos(out var antes)) return new Solta(null, icon, default);

        var caixa = new RECT { Left = antes.X, Top = antes.Y, Right = antes.X + 1, Bottom = antes.Y + 1 };
        if (!ClipCursor(ref caixa))
        {
            Log.Trace($"não deu para prender o cursor em ({antes.X},{antes.Y}); o menu de '{icon.Label}' vai abrir sem isso");
            return new Solta(null, icon, antes);
        }

        return new Solta(antes, icon, antes);
    }

    /// <summary>Solta o cursor e conta o que aconteceu com ele. Sempre roda.</summary>
    private sealed class Solta(POINT? preso, TrayIcon icon, POINT antes) : IDisposable
    {
        public void Dispose()
        {
            if (preso is null) return;

            ReleaseCursorClip(0);

            if (!GetCursorPos(out var depois)) return;

            Log.Trace(depois.X == antes.X && depois.Y == antes.Y
                ? $"cursor ficou parado em ({antes.X},{antes.Y}) durante o menu de '{icon.Label}'"
                : $"cursor escapou do limite no menu de '{icon.Label}': ({antes.X},{antes.Y}) -> ({depois.X},{depois.Y})");
        }
    }

    /// <summary>
    /// Entre pedir o foco e apertar a tecla: o XAML do painel move o foco por conta própria, e
    /// a tecla que chega antes disso vai para o botão errado.
    /// </summary>
    private static readonly TimeSpan FocusSettle = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Traz o menu recém-aberto para debaixo do ícone no nosso cartão.
    ///
    /// Quem desenha o menu é o programa dono do ícone, e ele o põe onde acha que foi chamado:
    /// junto da bandeja do Windows. Para quem clicou na nossa barra, o menu aparecia do outro
    /// lado da tela — o gesto e a resposta em lugares diferentes, que é o que fazia o recurso
    /// parecer quebrado mesmo funcionando.
    ///
    /// A janela do menu não tem título nem classe previsível (o Discord desenha a dele numa
    /// janela do Chromium, o Pageant usa o menu clássico do Windows), então ela é reconhecida
    /// por **ser nova**: o que apareceu entre o retrato de antes e agora, fora do nosso
    /// processo e do explorer.
    /// </summary>
    /// <returns>Verdadeiro se alguma janela nova apareceu (o menu abriu de verdade).</returns>
    private static bool MoveMenuToAnchor(HashSet<nint> before, RECT anchor, TrayIcon icon)
    {
        for (var waited = TimeSpan.Zero; waited < MenuWait; waited += Poll)
        {
            Thread.Sleep(Poll);

            foreach (var window in VisibleWindows())
            {
                if (before.Contains(window)) continue;
                if (!GetWindowRect(window, out var box)) continue;

                GetWindowThreadProcessId(window, out var pid);
                if (pid == (uint)Environment.ProcessId) continue;

                // as janelas do shell que a própria leitura faz nascer não são o menu
                var cls = new StringBuilder(64);
                GetClassName(window, cls, cls.Capacity);
                if (cls.ToString() is OverflowClass or ShellPopupClass) continue;

                if (anchor.Right > anchor.Left) Place(window, box, anchor);   // sem âncora: deixa onde nasceu
                Log.Trace($"menu de '{icon.Label}' ({cls}) trazido para junto do ícone");
                return true;
            }
        }

        Log.Trace($"nenhum menu apareceu para '{icon.Label}' em {MenuWait.TotalMilliseconds:0} ms");
        return false;
    }

    /// <summary>
    /// Põe a janela do menu logo abaixo da âncora, sem deixá-la sair da tela.
    ///
    /// A borda direita é o que se alinha, e não a esquerda: o cartão da bandeja ancora pela
    /// direita, e um menu mais largo que o ícone cresceria para fora da tela se fosse pelo
    /// outro lado.
    /// </summary>
    private static void Place(nint window, RECT box, RECT anchor)
    {
        var width = box.Right - box.Left;
        var height = box.Bottom - box.Top;

        // Nada de calcular monitor: a área de trabalho vem direto do monitor **principal**
        // do Windows — o mesmo que `SPI_GETWORKAREA` já devolve em outros pontos do
        // projeto. Tentado antes escolher o monitor pela âncora (`MonitorFromPoint`), e
        // ainda saía errado numa máquina com dois vídeos de DPI diferentes — a conta do
        // WPF para a âncora (`PointToScreen` + `GetDpi`) não bate necessariamente com a
        // do Win32 puro que o `MonitorFromPoint`/`GetMonitorInfo` devolvem quando os dois
        // monitores escalam de jeitos diferentes. Ir direto ao principal evita todo esse
        // cálculo — e é onde o painel do WinDock mora nesta máquina de qualquer forma.
        var screen = new RECT();
        if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref screen, 0)) return;

        // A borda que estica é a esquerda da âncora, não a direita — como qualquer menu
        // normal do Windows. O ícone tem uns 40 px; o menu, mais de 200. Esticar a partir
        // da borda direita do ícone (como estava) jogava o menu 180 px para a **esquerda**,
        // por cima dos outros ícones do cartão e além — com o cartão já fechado a essa
        // altura, isso parecia "o menu apareceu longe, sem relação com nada" (era o EV09).
        // O Windows de verdade estica para a direita (EV10), e é isso que faz o menu
        // continuar perto do ícone que a pessoa clicou.
        var x = Math.Max(anchor.Left, screen.Left);
        if (x + width > screen.Right) x = screen.Right - width;   // sem couber à direita, aí sim vira à esquerda

        var y = anchor.Bottom + MenuGap;

        // não cabe embaixo (dock na borda de baixo, por exemplo): vai para cima da âncora
        if (y + height > screen.Bottom) y = anchor.Top - MenuGap - height;

        x = Math.Max(screen.Left, x);
        y = Math.Max(screen.Top, y);

        Log.Trace($"posicionando o menu: âncora=({anchor.Left},{anchor.Top})-({anchor.Right},{anchor.Bottom}) " +
                  $"caixa={width}x{height} área=({screen.Left},{screen.Top})-({screen.Right},{screen.Bottom}) " +
                  $"alvo=({x},{y})");

        SetWindowPos(window, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // confere se pegou de verdade: alguns popups (o do Discord é o caso) redesenham a
        // própria posição por conta própria um instante depois do nosso SetWindowPos, e o
        // reposicionamento parece funcionar (o comando roda sem erro) mas não se sustenta
        // na tela. Sem essa segunda olhada, o log diria "ok" para um menu que a pessoa via
        // no lugar errado.
        Thread.Sleep(120);
        if (GetWindowRect(window, out var depois))
            Log.Trace($"posição do menu 120 ms depois: ({depois.Left},{depois.Top})-" +
                      $"({depois.Right},{depois.Bottom}) {(depois.Left == x && depois.Top == y ? "ficou" : "*** SE MOVEU SOZINHO ***")}");
    }

    private static HashSet<nint> VisibleWindows()
    {
        var found = new HashSet<nint>();
        EnumWindows((window, _) => { if (IsWindowVisible(window)) found.Add(window); return true; }, 0);
        return found;
    }

    /// <summary>Quanto se espera o menu do programa aparecer.</summary>
    private static readonly TimeSpan MenuWait = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Folga entre o ícone e o menu. O Windows de verdade (EV10) cola o menu bem junto do
    /// ícone — praticamente sem respiro nenhum; 6 px ainda deixava uma faixa visível de
    /// fundo entre os dois. 2 px é o mínimo que ainda impede o menu de nascer colado a
    /// ponto de o primeiro clique do usuário poder acertar o ícone por engano.
    /// </summary>
    private const int MenuGap = 2;

    /// <summary>
    /// O botão deste ícone dentro do painel, ou nulo se a bandeja mudou desde a leitura.
    ///
    /// O nome tem de ser montado pela mesma regra da leitura (<see cref="NameOf"/>): os ícones
    /// anônimos ganham lá um nome tirado da posição, e conferir aqui o nome cru fazia a
    /// comparação falhar sempre para eles — o clique não acionava nada. E a conferência
    /// existe porque a posição sozinha não basta: se um programa fechou nesse meio-tempo, a
    /// posição guardada passou a ser de outro ícone, e acionar o errado é pior que não fazer
    /// nada.
    /// </summary>
    private static AutomationElement? Find(nint flyout, TrayIcon icon)
    {
        var buttons = Buttons(flyout);

        if (icon.Position < buttons.Count &&
            Clean(NameOf(buttons[icon.Position], icon.Position)).Equals(icon.Name, StringComparison.Ordinal))
            return buttons[icon.Position];

        Log.Write($"a bandeja mudou desde a última leitura; '{icon.Label}' não foi acionado");
        return null;
    }

    /// <summary>Nome do processo dono de uma janela — "explorer" para as do shell.</summary>
    private static string Owner(nint window)
    {
        if (window == 0) return string.Empty;
        GetWindowThreadProcessId(window, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    // ── o painel do Windows ─────────────────────────────────

    /// <summary>
    /// O painel aberto, ou zero.
    ///
    /// A visibilidade é parte da pergunta, e não um detalhe: a janela do painel **continua
    /// existindo depois de fechada**, só invisível. Procurar só pela classe achava esse
    /// fantasma, o código concluía "já está aberto" e nunca chegava a abrir nada.
    /// </summary>
    private static nint Overflow()
    {
        var flyout = FindWindow(OverflowClass, null);
        return flyout != 0 && IsWindowVisible(flyout) ? flyout : 0;
    }

    private static bool OpenOverflow(nint taskbar)
    {
        if (Overflow() != 0) return true;

        var chevron = WaitForChevron(taskbar);
        if (chevron is null) return false;

        // Duas tentativas, e a segunda não é teimosia: a seta **alterna**, e o shell pode estar
        // achando que o painel dele já está aberto — é o que fica depois de um menu de ícone,
        // que obriga a esconder o painel em vez de fechá-lo pelo Esc (veja o `HideOverflow`).
        // Nesse caso o primeiro acionamento só desfaz o engano, e é o segundo que abre.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ((InvokePattern)chevron.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

            for (var waited = TimeSpan.Zero; waited < Attempt; waited += Poll)
            {
                // Aqui se espera só a **janela**. A lista dentro dela demora mais para o XAML
                // montar, e quem espera por ela é o `WithShell`, depois de a janela já estar
                // escondida — senão o painel do Windows fica mais de um segundo à mostra.
                if (Overflow() != 0)
                {
                    if (attempt > 1) Log.Trace("o painel abriu na segunda tentativa da seta");
                    return true;
                }
                Thread.Sleep(Poll);
            }
        }

        Log.Write("o painel de ícones ocultos do Windows não abriu");
        return false;
    }

    /// <summary>Quanto se espera o painel aparecer a cada acionamento da seta.</summary>
    private static readonly TimeSpan Attempt = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Espera a lista do painel ficar **pronta**, e não apenas existir.
    ///
    /// Existir não basta, e a diferença apareceu quando a leitura ficou rápida demais: os
    /// botões entram na árvore de automação antes de o XAML preencher o nome deles, e a
    /// leitura trazia dois "Ícone da bandeja" sem nome onde havia dois programas diferentes
    /// — no cartão isso parece um ícone repetido. Pelo mesmo motivo a contagem oscilava
    /// entre três, quatro e cinco de uma abertura para outra: cada leitura pegava a lista num
    /// estágio diferente de montagem.
    ///
    /// Pronta quer dizer **a contagem repetiu** de uma sondagem para a outra: um botão que
    /// ainda vai nascer não avisa que vem, e só a repetição mostra que a lista parou de
    /// crescer. Era essa a razão de a contagem oscilar entre três, quatro e cinco de uma
    /// abertura para outra.
    ///
    /// O nome é o segundo tempo, e tem prazo separado. O shell preenche o nome dos botões
    /// depois de criá-los, e sem esperar por isso o cartão mostrava "Ícone da bandeja 2" onde
    /// devia dizer "Discord". Mas esperar por **todos** foi tentado e não serve: há ícones
    /// que nunca ganham nome — a sondagem mostrou por quê, o botão pertence ao explorer e não
    /// carrega nada do programa dono —, e a espera ia até o estouro dos 2 s toda vez (medido:
    /// leitura de 3.975 ms). Daí um prazo curto: quem chegar a tempo tem nome, quem não
    /// chegar cai no rótulo por posição do <see cref="NameOf"/> e não segura os outros.
    /// </summary>
    private static bool WaitForItems()
    {
        var previous = -1;
        var settled = TimeSpan.MinValue;

        // Onde vão os ~840 ms desta fase: dormindo entre sondagens, ou dentro delas? Cada
        // volta enumera a árvore de automação do painel, e isso não é barato — se o custo
        // estiver aqui, encurtar prazos não adianta e o caminho é sondar menos ou mais
        // barato. Sem separar as duas coisas, calibrar o Poll seria chute.
        var sondagens = 0;
        long emButtons = 0;
        var cronometro = new System.Diagnostics.Stopwatch();

        for (var waited = TimeSpan.Zero; waited < Timeout; waited += Poll)
        {
            var flyout = Overflow();
            if (flyout == 0) return false;

            cronometro.Restart();
            var buttons = Buttons(flyout);
            emButtons += cronometro.ElapsedMilliseconds;
            sondagens++;
            _probeCount = sondagens;
            _probeCost = emButtons;

            if (buttons.Count > 0 && buttons.Count == previous)
            {
                if (settled == TimeSpan.MinValue) settled = waited;
                if (buttons.All(b => !string.IsNullOrWhiteSpace(b.Cached.Name))) return true;
                if ((_anonymous && buttons.Count == _anonymousAt) ||
                    waited - settled >= Naming) return true;
            }
            else settled = TimeSpan.MinValue;

            previous = buttons.Count;
            Thread.Sleep(Poll);
        }

        return Overflow() != 0;
    }

    /// <summary>Quantas sondagens a última espera fez, e quanto delas foi enumerar a árvore.</summary>
    private static int _probeCount;
    private static long _probeCost;

    /// <summary>Quanto se espera pelos nomes depois de a lista parar de crescer.</summary>
    private static readonly TimeSpan Naming = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Há um ícone que já se sabe que não vai ganhar nome nenhum.
    ///
    /// Existe para o prazo de nomeação não virar imposto: alguns programas — o Discord é o
    /// caso daqui — simplesmente não publicam dica de mouse, e o registro do Windows confirma
    /// (<c>NotifyIconSettings</c>, <c>InitialTooltip</c> vazio). Esperar por eles custaria os
    /// 400 ms em **toda** leitura, para sempre. Depois da primeira, já se sabe: espera-se uma
    /// vez, aprende-se, e as seguintes voltam aos ~200 ms. Se a bandeja mudar, a contagem
    /// muda com ela e a espera acontece de novo.
    /// </summary>
    private static bool _anonymous;

    /// <summary>Com quantos ícones o aprendizado acima foi feito — se mudar, ele não vale mais.</summary>
    private static int _anonymousAt = -1;

    /// <summary>Quantos ícones ficaram sem nome na leitura em curso.</summary>
    private static int _blank;

    /// <summary>
    /// Os botões de ícone dentro do painel.
    ///
    /// A condição vai para a automação em vez de virem todos os elementos para cá e serem
    /// filtrados em C#: cada elemento que atravessa a fronteira custa, e a árvore da barra
    /// tem dezenas deles.
    /// </summary>
    private static IReadOnlyList<AutomationElement> Buttons(nint flyout)
    {
        try
        {
            // O nome e o retângulo vêm junto com a busca, num pedido só. Sem isso cada
            // propriedade de cada botão é uma ida e volta ao processo do shell — e a busca
            // inteira custava mais de um segundo, o grosso do tempo de abrir a bandeja.
            var pedido = new CacheRequest();
            pedido.Add(AutomationElement.NameProperty);
            pedido.Add(AutomationElement.BoundingRectangleProperty);
            using (pedido.Activate())
                return AutomationElement.FromHandle(flyout)
                    .FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemId))
                    .Cast<AutomationElement>()
                    .ToList();
        }
        catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>
    /// O botão "Mostrar ícones ocultos", assim que ele existir.
    ///
    /// A barra acabou de ser mostrada e a árvore de automação dela leva um instante para
    /// ficar de pé. Antes havia um tempo fixo de 250 ms aqui, escolhido no chute e pago
    /// inteiro toda vez; esperar pelo que se precisa custa o que a máquina precisar — em
    /// geral bem menos — e não quebra quando a máquina está mais lenta que o chute.
    /// </summary>
    /// <summary>
    /// A seta do Windows, guardada entre leituras.
    ///
    /// Procurá-la custava cerca de um segundo por leitura — a barra acabava de ser mostrada
    /// e a árvore de automação dela leva esse tempo para ficar de pé. Mas a seta é a mesma
    /// enquanto o explorer for o mesmo: o elemento continua válido, e acioná-lo direto pula
    /// a busca inteira. Se ele tiver morrido (explorer reiniciado), a chamada estoura e a
    /// busca acontece como antes.
    /// </summary>
    private static AutomationElement? _chevron;

    private static AutomationElement? WaitForChevron(nint taskbar)
    {
        if (_chevron is not null)
        {
            try
            {
                _ = _chevron.Current.Name;
                return _chevron;
            }
            catch { _chevron = null; }
        }

        for (var waited = TimeSpan.Zero; waited < Timeout; waited += Poll)
        {
            // a árvore da barra só aparece pela janela filha; na Shell_TrayWnd ela vem vazia
            var bridge = FindWindowEx(taskbar, 0, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            if (bridge != 0)
            {
                // a condição vai para a automação: a árvore da barra tem dezenas de
                // elementos, e trazer todos para filtrar em C# custava mais de cem
                // milissegundos por leitura
                var chevron = AutomationElement.FromHandle(bridge)
                    .FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(e => e.Current.Name.Contains("Ícones Ocultos",
                                                                 StringComparison.CurrentCultureIgnoreCase));
                if (chevron is not null) { _chevron = chevron; return chevron; }
            }

            Thread.Sleep(Poll);
        }

        Log.Write("o botão \"mostrar ícones ocultos\" não foi encontrado na barra do Windows");
        return null;
    }


    /// <summary>A classe das janelas que hospedam os balões XAML do shell.</summary>
    private const string ShellPopupClass = "Xaml_WindowedPopupClass";

    /// <summary>
    /// Apaga o balão que o Windows deixa para trás depois da leitura.
    ///
    /// Acionar a seta do Windows faz o shell preparar a **dica dele** — "Mostrar ícones
    /// ocultos" — e ela aparece uns duzentos milissegundos *depois* de tudo já ter voltado
    /// ao lugar, numa janela própria que não herda a camada com que escondemos a barra. O
    /// resultado é uma mensagem do Windows pousando em cima do nosso cartão, sem nada na
    /// tela que explique de onde veio. Foi o que se via como "mensagem sobreposta".
    ///
    /// Como ela chega atrasada, não dá para escondê-la junto com o resto: é preciso ficar de
    /// olho por um instante depois. A varredura é curta e restrita à faixa de cima da tela,
    /// onde só cabe o balão da bandeja — e roda fora da thread da interface, que a essa
    /// altura já está desenhando o cartão.
    /// </summary>
    private static void SweepShellPopups()
    {
        Task.Run(() =>
        {
            // Quem já estava na tela quando a leitura terminou **não** é lixo nosso: essa mesma
            // classe hospeda as notificações do Windows, e a faixa de cima é onde elas pousam
            // quando a barra do shell está escondida. Medido em 09/09/2026: um aviso do Pageant
            // ficou 16 s em (2257,100) — bem dentro do alcance desta varredura — sem a dock ter
            // encostado na bandeja. Se uma leitura acontecesse ali no meio, nós o apagaríamos da
            // tela antes de a pessoa ler, e ela nunca saberia que houve aviso.
            //
            // O balão que interessa apagar é o que o shell prepara **por causa** do nosso
            // acionamento, e ele chega uns 200 ms depois de tudo já ter voltado ao lugar — ou
            // seja, sempre depois deste retrato.
            var veteranas = ShellPopupsOnScreen();

            for (var i = 0; i < 150; i++)
            {
                foreach (var popup in ShellPopupsOnScreen())
                    if (!veteranas.Contains(popup)) ShowWindow(popup, SW_HIDE);

                Thread.Sleep(10);
            }
        });
    }

    /// <summary>Os balões do shell visíveis agora na faixa de cima da tela.</summary>
    private static HashSet<nint> ShellPopupsOnScreen()
    {
        var achados = new HashSet<nint>();

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != ShellPopupClass) return true;

            if (!GetWindowRect(window, out var box) || box.Top > PopupStrip) return true;

            achados.Add(window);
            return true;
        }, 0);

        return achados;
    }

    /// <summary>Até onde a varredura olha: a faixa onde o balão da bandeja pousa.</summary>
    private const int PopupStrip = 200;

    /// <summary>Dá ao painel do Windows um instante para se fechar por conta própria.</summary>
    private static void WaitForOverflowToClose()
    {
        for (var waited = TimeSpan.Zero; waited < SelfClose && Overflow() != 0; waited += Poll)
            Thread.Sleep(Poll);
    }

    /// <summary>Quanto se espera o painel se fechar sozinho depois de um ícone ser acionado.</summary>
    private static readonly TimeSpan SelfClose = TimeSpan.FromMilliseconds(400);

    /// <summary>O Esc é o que fecha o painel: ele não é uma janela que se possa mandar fechar.
    ///
    /// Passa pelo <see cref="SyntheticKeys"/> para a barra saber que este Esc é nosso — ela
    /// escuta o teclado para fechar os cartões dela, e sem o carimbo fechava o cartão da bandeja
    /// no meio da própria leitura que o abastece.</summary>
    private static void CloseOverflow() => SyntheticKeys.SendEscape();

    /// <summary>
    /// Tira o painel da tela sem mandar tecla nenhuma.
    ///
    /// É o único jeito de fechá-lo quando o programa dono do ícone acabou de abrir o menu
    /// dele: o Esc não tem endereço — vai para quem está em primeiro plano, e nesse instante
    /// é o menu. Medido aqui: o menu do Discord nascia 278 ms depois da tecla e morria no Esc
    /// que vinha logo atrás, o que fazia o recurso inteiro parecer não funcionar.
    ///
    /// Esconder a janela deixa o shell achando que o painel dele continua aberto; quem paga
    /// isso é o <see cref="OpenOverflow"/>, que por causa daqui aciona a seta uma segunda vez
    /// quando a primeira só serviu para desfazer esse engano.
    /// </summary>
    private static void HideOverflow()
    {
        var flyout = Overflow();
        if (flyout == 0) return;

        ShowWindow(flyout, SW_HIDE);

        // Vale a pena o rastro dizer QUEM está em primeiro plano aqui: é exatamente a janela
        // que teria recebido o Esc no caminho antigo. Quando alguém relatar "cliquei no ícone,
        // a tela abriu e sumiu", esta linha é a que responde se era esse o caso.
        var fg = GetForegroundWindow();
        var titulo = new StringBuilder(120);
        GetWindowText(fg, titulo, titulo.Capacity);
        Log.Trace($"painel do Windows escondido sem Esc (o Esc iria para '{titulo}' {fg:X})");
    }
}
