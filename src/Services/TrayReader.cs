using System.Windows.Threading;

namespace WinDock.Services;

/// <summary>
/// A thread onde a bandeja é lida: uma só, STA, com bomba de mensagens.
///
/// As três características são obrigatórias, e cada uma custou uma tentativa:
///
/// - **Fora da thread da interface**, porque ler a bandeja leva uns 600 ms e a barra não
///   pode congelar nesse tempo.
/// - **STA**, porque a automação de interface consultada de uma thread MTA devolve
///   <c>BoundingRectangle</c> vazio para os botões do painel. Sem retângulo não há onde
///   recortar o ícone, e os ícones apareciam invisíveis — sem erro nenhum.
/// - **Com bomba de mensagens** (<see cref="Dispatcher.Run"/>), porque uma thread STA sem
///   bomba não processa as respostas do COM.
///
/// Duas regras de convivência, e as duas vieram de a barra travar:
///
/// 1. **Ninguém espera esta thread nascer.** A primeira versão criava a thread na primeira
///    leitura e bloqueava quem pediu até o dispatcher existir — e quem pedia era a thread da
///    interface. Agora ela sobe junto com a barra, e quem chega antes disso é dispensado.
/// 2. **Uma leitura de cada vez.** Cada leitura leva uns 600 ms; clicar de novo no meio
///    empilhava mais uma, e mais outra, e a barra ficava respondendo a cliques de minutos
///    atrás. Pedido que chega com uma leitura em curso é descartado, não enfileirado.
/// </summary>
internal static class TrayReader
{
    private static Dispatcher? _dispatcher;
    private static int _reading;

    /// <summary>Sobe a thread. Chamado uma vez, quando a barra é criada.</summary>
    public static void Start()
    {
        if (_dispatcher is not null) return;

        var thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WinDock bandeja"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Manda ler, se não houver leitura em curso.
    /// </summary>
    /// <returns>
    /// Falso quando o pedido foi descartado — a thread ainda não subiu, ou já há uma leitura
    /// acontecendo. Quem chamou precisa saber: o trabalho não vai acontecer, e esperar por um
    /// retorno que não vem deixaria a interface travada num "carregando" eterno.
    /// </returns>
    public static bool Run(Action work)
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null) return false;

        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return false;

        dispatcher.InvokeAsync(() =>
        {
            try { work(); }
            catch (Exception ex) { Log.Write("falha ao ler a bandeja", ex); }
            finally { Volatile.Write(ref _reading, 0); }
        });

        return true;
    }

    /// <summary>
    /// Manda fazer algo na mesma thread, **esperando a vez**.
    ///
    /// É o caminho do clique num ícone. Uma leitura pode ser dispensada sem prejuízo — a
    /// lista se corrige na próxima —, mas um clique da pessoa não pode sumir, então aqui não
    /// há o descarte do <see cref="Run"/>.
    ///
    /// Esperar a vez também é o que impede o pior: acionar um ícone abre o painel do Windows
    /// de novo, e fazer isso **enquanto** a releitura do cartão ainda está com ele aberto
    /// deixava as duas mexendo no shell ao mesmo tempo — o painel dele piscava na tela, às
    /// vezes duas vezes, e o clique demorava o dobro. O clique acontece logo depois de abrir
    /// o cartão, que é exatamente quando a releitura está em curso.
    /// </summary>
    public static bool Queue(Action work)
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null) return false;

        dispatcher.InvokeAsync(() =>
        {
            try { work(); }
            catch (Exception ex) { Log.Write("falha ao acionar um ícone da bandeja", ex); }
        });

        return true;
    }
}
