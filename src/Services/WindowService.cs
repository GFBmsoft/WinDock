using System.IO;
using System.Text;
using static WinDock.Interop.Native;

namespace WinDock.Services;

public sealed record TaskWindow(nint Handle, string Title, string ExePath, string Aumid, bool IsMinimized)
{
    /// <summary>
    /// Como as janelas sao agrupadas em botoes. O AppUserModelID vem primeiro porque e
    /// o que a barra de tarefas usa: dois perfis do Chrome rodam no mesmo processo e no
    /// mesmo .exe, e so o AUMID os separa.
    /// </summary>
    public string AppKey => string.IsNullOrEmpty(Aumid) ? ExePath.ToLowerInvariant() : Aumid;
}

/// <summary>
/// Descobre as janelas que valem um botao na dock e faz as acoes sobre elas.
/// O criterio e o mesmo do Alt+Tab: visivel, sem dono, nao "tool window", nao cloaked.
/// </summary>
public static class WindowService
{
    public static List<TaskWindow> Enumerate()
    {
        var list = new List<TaskWindow>();
        var self = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsAltTabWindow(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == self) return true;                     // a propria dock nao entra

            var exe = ProcessPath(pid);
            if (string.IsNullOrEmpty(exe)) return true;

            list.Add(new TaskWindow(hWnd, TitleOf(hWnd), exe, AppUserModelId(hWnd), IsIconic(hWnd)));
            return true;
        }, 0);

        return list;
    }

    internal static bool IsAltTabWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (GetWindowTextLength(hWnd) == 0) return false;
        if (GetWindow(hWnd, GW_OWNER) != 0) return false;     // janelas-filhas/dialogos

        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;

        // UWP mantem janelas "cloaked" vivas mas invisiveis: elas nao devem virar botao
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return true;
    }

    private static string TitleOf(nint hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len == 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return string.Empty;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Traz a janela para frente; se ja estiver em foco, minimiza (igual a barra do Windows).
    ///
    /// A pergunta "ela ja esta em foco?" é feita de **duas** fontes, e basta uma dizer que sim:
    /// o que o Windows responde agora e o que a dock guardou. Nenhuma das duas serve sozinha —
    /// e foi por confiar só na guardada que certos ícones fixados paravam de responder. O
    /// valor guardado congela sempre que uma janela nossa passa pelo primeiro plano (o menu, a
    /// busca, o painel: veja <c>DockModel.Foreground</c>), e com ele desatualizado o clique
    /// caía num vão sem saída: não era "em foco" o bastante para minimizar, e o
    /// <see cref="ForceForeground"/> desistia calado porque a janela já estava à frente. Duas
    /// vezes seguidas com o Windows Terminal, sem uma linha no log.
    /// </summary>
    public static void ToggleActivate(nint hWnd, nint foreground)
    {
        if (!Alive(hWnd, "alternar")) return;

        var actual = GetForegroundWindow();
        var iconic = IsIconic(hWnd);
        var focused = !iconic && (actual == hWnd || foreground == hWnd);

        Log.Trace($"alternar janela {hWnd:X}: foco agora={actual:X}, guardado={foreground:X}, " +
                  $"minimizada={iconic} → {(focused ? "minimizar" : "trazer para frente")}");

        if (focused) { ShowWindow(hWnd, SW_MINIMIZE); return; }

        if (iconic) ShowWindow(hWnd, SW_RESTORE);
        ForceForeground(hWnd);
    }

    public static void Activate(nint hWnd)
    {
        if (!Alive(hWnd, "ativar")) return;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
        ForceForeground(hWnd);
    }

    /// <summary>
    /// A janela ainda existe? Um botão pode estar segurando o identificador de uma janela
    /// fechada entre a última atualização e este clique; antes isso era um retorno mudo, e
    /// o clique sem efeito não deixava rastro nenhum para distinguir do bug de foco.
    /// </summary>
    private static bool Alive(nint hWnd, string acao)
    {
        if (IsWindow(hWnd)) return true;

        Log.Write($"não deu para {acao}: a janela {hWnd:X} já não existe — o app foi fechado " +
                  "entre a última leitura e o clique");
        return false;
    }

    /// <summary>
    /// Traz uma janela nossa para frente com o teclado junto. O Show() do WPF nao basta:
    /// a janela aparece (ela e topmost) mas o foco continua no app anterior, e o que a
    /// pessoa digita vai parar la.
    /// </summary>
    public static void Focus(nint hWnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hWnd || hWnd == 0) return;

        // aqui o input e anexado a thread de QUEM esta em primeiro plano (nao a do alvo,
        // que somos nos): e dela que vem o direito de mudar o foco
        var owner = GetWindowThreadProcessId(foreground, out _);
        var current = GetCurrentThreadId();

        if (owner != current) AttachThreadInput(current, owner, true);
        SetForegroundWindow(hWnd);
        SetActiveWindow(hWnd);
        if (owner != current) AttachThreadInput(current, owner, false);
    }

    /// <summary>
    /// O Windows so deixa o processo em primeiro plano trocar o foco. Anexar o input da
    /// nossa thread a da janela alvo contorna isso — e o truque que os launchers usam.
    /// </summary>
    private static void ForceForeground(nint hWnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hWnd) { Log.Trace($"janela {hWnd:X} já estava em primeiro plano"); return; }

        var target = GetWindowThreadProcessId(hWnd, out _);
        var current = GetCurrentThreadId();

        if (target != current) AttachThreadInput(current, target, true);
        SetForegroundWindow(hWnd);
        if (target != current) AttachThreadInput(current, target, false);

        // O truque acima nao funciona contra uma janela de integridade mais alta: um app
        // aberto como administrador nao aceita nem o AttachThreadInput nem o foco vindo de
        // um processo comum, e o clique simplesmente nao fazia nada. O SwitchToThisWindow
        // ainda alcanca esses casos; quando nem ele resolve, fica o registro no log.
        if (GetForegroundWindow() == hWnd) return;

        SwitchToThisWindow(hWnd, true);

        if (GetForegroundWindow() != hWnd)
            Log.Write($"a janela nao veio para a frente (hwnd={hWnd:X}, '{TitleOf(hWnd)}'): " +
                      "costuma ser um app rodando como administrador");
    }

    public static void Close(nint hWnd) => PostMessage(hWnd, WM_CLOSE, 0, 0);

    /// <summary>
    /// Mata o processo dono da janela — o que o <c>taskkill /f</c> faz.
    ///
    /// É o último recurso, para quando o app travou e não responde nem ao pedido educado de
    /// fechar. O trabalho não salvo se perde, e por isso quem chama tem de perguntar antes.
    ///
    /// Mata-se o processo, e não a janela: um app travado é justamente o que não processa
    /// mensagem nenhuma, então mandar <c>WM_CLOSE</c> de novo não adiantaria. Várias janelas
    /// do mesmo programa costumam ser um processo só — daí o <see cref="Kill(nint)"/> devolver
    /// o identificador de quem morreu, para quem chama não tentar matá-lo duas vezes.
    /// </summary>
    public static uint Kill(nint hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return 0;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            process.Kill(entireProcessTree: true);
            Log.Write($"processo encerrado à força: {name} (pid {pid})");
            return pid;
        }
        catch (Exception ex)
        {
            // Acesso negado é o caso comum e não é defeito: um app rodando como
            // administrador não pode ser morto por um processo comum.
            Log.Write($"não foi possível encerrar o processo {pid} à força", ex);
            return 0;
        }
    }

    /// <summary>
    /// Abre o alvo. Se for um .lnk, deixa o shell executa-lo: assim os argumentos e o
    /// AppUserModelID do atalho sao aplicados, e a janela nova cai no botao certo.
    /// </summary>
    /// <summary>
    /// Abre um app pela pasta de aplicativos do shell. E o unico caminho para os apps da
    /// Store: quem resolve o AppUserModelID e o explorer, e o executavel deles nem sempre
    /// pode ser chamado direto.
    /// </summary>
    public static bool LaunchApp(string aumid)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $@"shell:AppsFolder\{aumid}",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            // o explorer aceita qualquer coisa depois de shell:AppsFolder e nao reclama:
            // so cai aqui se nem ele subir. Um AUMID inexistente falha em silencio, e por
            // isso quem chama tem que ter certeza (IconService.ShellExists) antes.
            Log.Write($"falha ao abrir pelo AppsFolder: {aumid}", ex);
            return false;
        }
    }

    public static bool Launch(string path, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            };
            if (!string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;

            System.Diagnostics.Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"falha ao abrir: {path} {arguments}".TrimEnd(), ex);
            return false;
        }
    }
}
