using System.IO;

namespace WinDock.Services;

/// <summary>
/// Um app que o launcher sabe abrir. <see cref="Target"/> e o AppUserModelID quando o app
/// veio da pasta de aplicativos do shell, e o caminho do .lnk quando veio de um atalho.
/// </summary>
public sealed record CatalogApp(string Name, string Target, bool IsAumid)
{
    /// <summary>Onde o icone e buscado — no AppsFolder o caminho do shell resolve tudo.</summary>
    public string IconSource => IsAumid ? $@"shell:AppsFolder\{Target}" : Target;

    /// <summary>
    /// O nome ja em minusculas e sem acento. Fica pronto aqui porque a busca compara todos
    /// os apps a cada tecla digitada — normalizar na hora seria refazer isso o tempo todo.
    /// </summary>
    public string Search { get; } = Text.Normalize(Name);
}

/// <summary>
/// A lista de aplicativos instalados, lida uma vez e guardada.
///
/// A fonte e a pasta de aplicativos do shell (<c>shell:AppsFolder</c>) — a mesma que o
/// menu iniciar usa. Ela ja traz num lugar so os programas Win32 e os apps da Store, cada
/// um com o AppUserModelID que abre o app; e o mesmo identificador que a dock usa para
/// agrupar janelas, entao um app aberto pelo launcher cai no botao certo.
/// </summary>
public static class AppCatalog
{
    private static List<CatalogApp>? _cache;

    public static IReadOnlyList<CatalogApp> All() => _cache ??= Scan();

    public static void Invalidate() => _cache = null;

    /// <summary>
    /// Monta o catalogo e resolve os icones antes de alguem pedir, logo que a dock sobe.
    /// Sem isso a primeira busca do dia pagaria a conta inteira — varrer a pasta de
    /// aplicativos e chamar o shell uma vez por icone — com a pessoa esperando.
    ///
    /// A thread e STA porque e disso que o COM do shell gosta; os BitmapSource saem
    /// congelados, entao a interface pode usa-los depois sem copiar nada.
    /// </summary>
    public static void Warm()
    {
        var thread = new Thread(() =>
        {
            try
            {
                // o login ja e o momento mais disputado da maquina: deixa a dock aparecer
                // e o resto do arranque passar antes de sair pedindo icone ao shell
                Thread.Sleep(TimeSpan.FromSeconds(3));

                foreach (var app in All()) IconService.Shell(app.IconSource);
            }
            catch { /* nada aqui e essencial: e so adiantar trabalho */ }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest   // a dock na tela vem antes disto
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static List<CatalogApp> Scan()
    {
        var found = new List<CatalogApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in FromAppsFolder())
            if (seen.Add(app.Target))
                found.Add(app);

        // rede de seguranca: se o COM do shell falhar, os .lnk do menu iniciar ainda dao
        // uma lista util (e sao eles que trazem os atalhos proprios, tipo BM e BMT)
        foreach (var lnk in ShortcutService.All())
            if (seen.Add(lnk.LnkPath) && !found.Any(a => a.Name.Equals(lnk.Name, StringComparison.OrdinalIgnoreCase)))
                found.Add(new CatalogApp(lnk.Name, lnk.LnkPath, IsAumid: false));

        return found.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Enumera <c>shell:AppsFolder</c> pelo automation do shell. E COM tardio de proposito:
    /// evita declarar meia duzia de interfaces so para percorrer uma pasta virtual.
    /// </summary>
    private static IEnumerable<CatalogApp> FromAppsFolder()
    {
        var list = new List<CatalogApp>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;

            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return list;

            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string target = item.Path;     // aqui o "caminho" e o AppUserModelID
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(target))
                    list.Add(new CatalogApp(name, target, IsAumid: true));
            }
        }
        catch { /* shell indisponivel: sobra a varredura dos .lnk */ }

        return list;
    }

    /// <summary>
    /// O nome que a pessoa reconhece para a chave com que a dock guarda um app: o AppUserModelID,
    /// ou o nome do executavel quando a janela nao tem um.
    ///
    /// Serve as listas das Configuracoes que se enchem sozinhas (programas de midia, tamanhos das
    /// janelas flutuantes): mostrar a chave crua era mostrar "Chrome.UserData.Profile1" e
    /// "Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI" (EV31).
    ///
    /// Na ordem: o perfil do Chrome pelo "Local State", no mesmo "Chrome - GF" da dica da dock (o
    /// AUMID dos perfis alem do padrao nao esta no catalogo, e o padrao viraria um "Google Chrome"
    /// sem dizer qual); o catalogo, que resolve o AUMID ("Spotify") e tambem o executavel dos
    /// programas sem AUMID, que a pasta de aplicativos lista pelo caminho; o atalho do menu iniciar
    /// que abre o executavel ("Code.exe" -> "Visual Studio Code"); e, sem nada disso, a chave
    /// limpa do que da.
    /// </summary>
    public static string FriendlyName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";

        if (ChromeProfiles.ByAumid(id) is { } perfil) return $"Chrome - {perfil.Name}";
        if (ByKey(id) is { } app) return app.Name;
        if (ShortcutFor(id) is { } atalho) return atalho.Name;

        // "Spotify.exe" -> "Spotify"; um AUMID sem correspondencia fica com a parte final: depois
        // do "!" num app da Store, e depois do ultimo ponto num AUMID com cara de dominio ao
        // contrario ("com.squirrel.SourceTree.SourceTree" -> "SourceTree"). Ponta curta e
        // extensao, e nao nome — o "VSCodeUserSetup-x64-1.137.0.tmp" fica inteiro.
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return id[..^4];
        if (id.Contains('!')) return id[(id.IndexOf('!') + 1)..];
        var ponta = id[(id.LastIndexOf('.') + 1)..];
        return ponta.Length > 4 && ponta.Length < id.Length ? ponta : id;
    }

    /// <summary>O icone para a mesma chave, achado pelo mesmo caminho do <see cref="FriendlyName"/>.</summary>
    public static System.Windows.Media.Imaging.BitmapSource? Icon(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        if (ChromeProfiles.ByAumid(id) is null && ByKey(id) is { } app) return IconService.Shell(app.IconSource);
        if (ShortcutFor(id) is { } atalho) return IconService.ForApp(id, atalho.LnkPath);
        return IconService.ForApp(id, string.Empty);
    }

    /// <summary>O app do catalogo com este AUMID, ou cujo caminho termina neste executavel.</summary>
    private static CatalogApp? ByKey(string id) =>
        All().FirstOrDefault(a => a.IsAumid && a.Target.Equals(id, StringComparison.OrdinalIgnoreCase)) ??
        (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? All().FirstOrDefault(a => a.IsAumid && a.Target.Contains('\\') &&
                                        Path.GetFileName(a.Target).Equals(id, StringComparison.OrdinalIgnoreCase))
            : null);

    /// <summary>
    /// O atalho do menu iniciar com este AUMID (o Discord e o Obsidian se anunciam assim e nao estao
    /// no catalogo por ele), ou o que abre este executavel sem argumento nenhum. Com argumento ele
    /// ja e outra coisa: o mmc.exe e o alvo de todos os consoles das Ferramentas Administrativas, e
    /// o chrome.exe, de um atalho por perfil.
    /// </summary>
    private static Shortcut? ShortcutFor(string id) =>
        id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ShortcutService.All()
                .Where(s => s.Arguments.Length == 0 &&
                            Path.GetFileName(s.Target).Equals(id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name.Length)
                .FirstOrDefault()
            : ShortcutService.All().FirstOrDefault(s => s.Aumid.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Abre um app da pasta de aplicativos. Quem resolve o AppUserModelID e o proprio
    /// explorer — e o unico jeito de lancar app da Store sem conhecer o executavel.
    /// </summary>
    public static void Launch(CatalogApp app)
    {
        if (app.IsAumid) WindowService.LaunchApp(app.Target);
        else WindowService.Launch(app.Target);
    }

    /// <summary>Abre o que foi digitado como se fosse o Win+R: comando, caminho ou URL.</summary>
    public static void Run(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return;

        // "notepad arquivo.txt": o primeiro pedaco e o programa, o resto sao argumentos
        var (file, args) = Split(command);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
        }
        catch { /* comando que nao existe: o launcher so fecha */ }
    }

    internal static (string File, string Args) Split(string command)
    {
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }

        // um caminho existente com espacos vale inteiro, sem virar programa + argumento
        if (File.Exists(command) || Directory.Exists(command)) return (command, string.Empty);

        var space = command.IndexOf(' ');
        return space < 0 ? (command, string.Empty) : (command[..space], command[(space + 1)..].Trim());
    }
}
