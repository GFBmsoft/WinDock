using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

public partial class App : Application
{
    /// <summary>
    /// O nome que só uma dock por sessão consegue segurar. <c>Local\</c> de propósito: outro usuário
    /// logado na mesma máquina tem a própria área de trabalho, e com ela a própria dock.
    /// </summary>
    private const string InstanceName = @"Local\WinDock.Instancia";

    /// <summary>Guardado em campo: um Mutex recolhido pelo coletor de lixo solta o nome sozinho, e uma
    /// segunda dock passaria.</summary>
    private static Mutex? _instance;

    /// <summary>
    /// O recado que uma segunda instância deixa para a primeira: "abra as configurações". Registrado
    /// pelo nome, então as duas chegam no mesmo número sem combinar nada.
    /// </summary>
    public static readonly uint OpenSettingsMessage = RegisterWindowMessage("WinDock.AbrirConfiguracoes");

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
        // Antes de qualquer janela: duas docks registrariam duas AppBars e dois conjuntos de
        // atalhos, e esconderiam a barra do Windows cada uma por conta própria — ao fechar uma, a
        // outra ficava sem a faixa reservada. Acontece fácil: a tarefa de logon já subiu a dock e a
        // pessoa clica no atalho de novo.
        if (!ClaimSingleInstance())
        {
            // Sumir calada não serve: esta instância já passou pelo aviso do UAC, e a pessoa
            // ficaria sem saber se o clique pegou. Abrir as configurações da que já roda mostra que
            // ela está ali. A permissão de vir para a frente é desta, que acabou de ser aberta por
            // um clique — sem repassá-la, o Windows só piscaria o painel na barra de tarefas.
            Log.Write("o WinDock já estava aberto: esta instância pediu à outra que abrisse as configurações e saiu");
            AllowSetForegroundWindow(ASFW_ANY);
            AskRunningInstanceToOpenSettings();
            Shutdown();
            return;
        }

        Log.Trace("processo de pé; começando a montar a interface");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("erro não tratado: " + (args.ExceptionObject as Exception)?.ToString());

        base.OnStartup(e);
    }

    /// <summary>
    /// Fica com o nome da instância, ou responde que outra dock já está com ele.
    ///
    /// Uma dock que morreu à força (Gerenciador de Tarefas, queda de energia) larga o nome
    /// "abandonado": o Windows avisa com a exceção, e a vez passa a ser desta — ninguém mais o
    /// segura.
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        _instance = new Mutex(initiallyOwned: false, InstanceName);
        try { return _instance.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>
    /// Deixa o recado na janela principal da dock que já está aberta.
    ///
    /// Não pode ser por <c>HWND_BROADCAST</c>, e isso foi medido: o broadcast só chega às janelas de
    /// topo **sem dono**, e a da dock tem um — o WPF dá um dono escondido a toda janela com
    /// <c>ShowInTaskbar</c> desligado. O recado ia para o sistema inteiro e não chegava justamente
    /// nela; mandado direto, chega. A janela é achada pelo título, e não pelo nome do processo: o
    /// executável baixado das Releases se chama <c>WinDock-1.0.0.10.exe</c>, não <c>WinDock.exe</c>.
    /// </summary>
    private static void AskRunningInstanceToOpenSettings()
    {
        var esta = (uint)Environment.ProcessId;
        var titulo = new System.Text.StringBuilder(16);

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == esta) return true;

            // sem mandar mensagem: esta varredura passa por todas as janelas do sistema, e uma de um
            // programa travado seguraria a saída desta instância
            titulo.Clear();
            InternalGetWindowText(hWnd, titulo, titulo.Capacity);
            if (titulo.ToString() == "WinDock") PostMessage(hWnd, OpenSettingsMessage, 0, 0);
            return true;
        }, 0);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("erro na interface: " + e.Exception);

        // a dock continua de pé: derrubar a barra inteira por causa de um clique que falhou
        // é pior que o clique falhar
        e.Handled = true;
    }
}
