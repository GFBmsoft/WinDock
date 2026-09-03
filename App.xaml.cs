using System.Windows;
using System.Windows.Threading;
using WinDock.Services;

namespace WinDock;

public partial class App : Application
{
    /// <summary>
    /// Registra o que explodiu antes de a janela sumir.
    ///
    /// Uma barra que não aparece não deixa rastro nenhum por si só: a janela chega a ser
    /// criada, a exceção sobe no meio da montagem e o que sobra na tela é o papel de parede.
    /// Um erro de XAML — um conversor que não resolve, um recurso que mudou de nome — cai
    /// exatamente assim, e sem isto a investigação começa do zero toda vez.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Trace("processo de pé; começando a montar a interface");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("erro não tratado: " + (args.ExceptionObject as Exception)?.ToString());

        base.OnStartup(e);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("erro na interface: " + e.Exception);

        // a dock continua de pé: derrubar a barra inteira por causa de um clique que falhou
        // é pior que o clique falhar
        e.Handled = true;
    }
}
