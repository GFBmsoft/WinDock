using Microsoft.Win32;

namespace WinDock.Services;

/// <summary>
/// Liga e desliga o "iniciar com o Windows".
///
/// Nao e um servico: servicos rodam na sessao 0, sem acesso a area de trabalho, e uma dock
/// precisa desenhar na sessao interativa.
///
/// O caminho preferido e uma tarefa agendada com gatilho de logon. A chave Run, que era o
/// que se usava aqui antes, funciona — mas o proprio Windows segura o que sobe por ela: os
/// aplicativos de inicializacao esperam a fila do shell e so entram depois de uma dezena de
/// segundos. Numa dock isso e visivel demais, porque ela e moldura da tela: a pessoa faz
/// logon e fica olhando a area de trabalho sem barra. A tarefa agendada nao passa por essa
/// fila e sobe assim que a sessao abre.
///
/// A chave Run continua como plano B, para o caso de a politica da maquina nao deixar criar
/// tarefas. Nunca as duas ao mesmo tempo: seriam duas docks.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinDock";
    private const string TaskName = "WinDock";

    public static bool IsEnabled => TaskExists() || RunValueExists();

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            DeleteTask();
            SetRunValue(false);
            return;
        }

        // a tarefa primeiro; a chave Run so se ela nao puder ser criada
        if (CreateTask()) SetRunValue(false);
        else SetRunValue(true);
    }

    /// <summary>
    /// Passa para a tarefa agendada quem ja subia pela chave Run.
    ///
    /// Quem ligou o "iniciar com o Windows" antes desta versao continuaria esperando a fila
    /// dos aplicativos de inicializacao para sempre, sem nunca desconfiar do porque. A
    /// intencao ja esta dada — o que muda e so o mecanismo — entao a troca e feita sozinha,
    /// uma vez. Se a tarefa nao puder ser criada, a chave Run fica onde estava.
    /// </summary>
    public static void Migrate()
    {
        try
        {
            if (!RunValueExists() || TaskExists()) return;
            if (CreateTask()) SetRunValue(false);
        }
        catch { /* na duvida, deixa como estava: a dock ainda sobe */ }
    }

    // ── tarefa agendada ─────────────────────────────────────

    /// <summary>
    /// O agendador por COM tardio, como o resto do shell neste projeto: declarar as
    /// interfaces do Task Scheduler daria umas duzentas linhas para uma tarefa so.
    /// </summary>
    private static dynamic? Service()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return null;

        dynamic? service = Activator.CreateInstance(type);
        service?.Connect();
        return service;
    }

    private static bool TaskExists()
    {
        try
        {
            var service = Service();
            if (service is null) return false;

            dynamic folder = service.GetFolder("\\");
            // GetTask lanca quando nao existe; nao ha um TryGet
            dynamic task = folder.GetTask(TaskName);
            return task is not null;
        }
        catch { return false; }
    }

    private static bool CreateTask()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var service = Service();
            if (service is null) return false;

            dynamic folder = service.GetFolder("\\");
            dynamic def = service.NewTask(0);

            def.RegistrationInfo.Description = "Sobe a dock do WinDock junto com a sessao.";
            def.RegistrationInfo.Author = Environment.UserName;

            // TASK_LOGON_INTERACTIVE_TOKEN: roda com o token do usuario que fez logon.
            // TASK_RUNLEVEL_HIGHEST: o executavel pede elevacao no manifesto (app.manifest,
            // requireAdministrator — o contorno do mosaico precisa disso pra ficar por cima
            // de janela elevada, tipo Terminal ou Notepad++ "Executar como administrador";
            // sem isso o UIPI barra a nossa janela de ficar visualmente acima delas, mesmo
            // com o SetWindowPos "funcionando"). Uma tarefa assim sobe elevada sem pedir
            // consentimento do UAC a cada logon — é o unico jeito de autoelevar sem dialogo.
            // O preco conhecido: o Explorer (que roda sem elevacao) nao consegue soltar
            // arquivo nenhum por arrastar numa janela nossa agora — UIPI de novo, ao contrario.
            def.Principal.LogonType = 3;
            def.Principal.RunLevel = 1;

            // TASK_TRIGGER_LOGON, so para este usuario
            dynamic trigger = def.Triggers.Create(9);
            trigger.UserId = $@"{Environment.UserDomainName}\{Environment.UserName}";

            // uns segundos de folga: o gatilho dispara junto com o logon, e a dock precisa
            // do Explorer de pe para se registrar como AppBar e reservar a faixa da tela
            trigger.Delay = "PT4S";

            dynamic settings = def.Settings;
            settings.DisallowStartIfOnBatteries = false;   // notebook no logon esta na bateria
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";          // a dock fica aberta o dia inteiro
            settings.MultipleInstances = 2;                // IgnoreNew: nunca duas docks
            settings.StartWhenAvailable = true;
            settings.Priority = 5;

            dynamic action = def.Actions.Create(0);        // TASK_ACTION_EXEC
            action.Path = exe;
            action.WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? string.Empty;

            // TASK_CREATE_OR_UPDATE: se o executavel mudou de pasta, a tarefa acompanha
            folder.RegisterTaskDefinition(TaskName, def, 6, null, null, 3, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("nao deu para criar a tarefa de logon; usando a chave Run", ex);
            return false;
        }
    }

    private static void DeleteTask()
    {
        try
        {
            var service = Service();
            if (service is null) return;

            dynamic folder = service.GetFolder("\\");
            folder.DeleteTask(TaskName, 0);
        }
        catch { /* nao existia: e o estado que se queria */ }
    }

    // ── chave Run (plano B) ─────────────────────────────────

    private static bool RunValueExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Contains("WinDock", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void SetRunValue(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        catch { /* politica de grupo pode bloquear a chave; o app segue funcionando */ }
    }
}
