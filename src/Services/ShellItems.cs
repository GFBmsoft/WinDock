using System.Diagnostics;

namespace WinDock.Services;

/// <summary>
/// Um lugar do Windows que não é aplicativo: a Lixeira, o Este Computador, uma pasta, um item do
/// Painel de Controle. <see cref="Address"/> é o endereço do shell (<c>::{GUID}</c>) ou o
/// caminho da pasta — os dois servem para o ícone e para abrir.
/// </summary>
public sealed record ShellItem(string Name, string Address, string Kind)
{
    /// <summary>O nome já normalizado, pelo mesmo motivo do <see cref="CatalogApp.Search"/>.</summary>
    public string Search { get; } = Text.Normalize(Name);
}

/// <summary>
/// O que o menu iniciar acha e a pasta de aplicativos não tem.
///
/// A busca do launcher só conhecia aplicativos, e "lixeira" não achava nada: a Lixeira é uma
/// pasta do shell, como o Este Computador e a Rede, e os itens do Painel de Controle
/// (Gerenciador de Dispositivos, Opções de Energia, Programas e Recursos) moram numa pasta
/// virtual própria. Os nomes vêm do shell, então saem no idioma do Windows.
/// </summary>
public static class ShellItems
{
    /// <summary>
    /// As pastas do shell que entram na busca. As do usuário também, porque o nome que o
    /// Explorador mostra ("Documentos", "Imagens") não é o da pasta no disco.
    /// </summary>
    private static readonly string[] Folders =
    [
        "shell:RecycleBinFolder",
        "shell:MyComputerFolder",
        "shell:NetworkPlacesFolder",
        "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}",   // Início (o antigo Acesso rápido)
        "shell:ConnectionsFolder",
        "shell:Profile",
        "shell:Desktop",
        "shell:Personal",
        "shell:Downloads",
        "shell:My Pictures",
        "shell:My Music",
        "shell:My Video",
    ];

    /// <summary>Todos os itens do Painel de Controle, na visão de ícones.</summary>
    private const string ControlPanel = @"shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}\0";

    public const string PlaceKind = "Local";
    public const string ControlPanelKind = "Painel de Controle";

    private static List<ShellItem>? _cache;

    /// <summary>Lido uma vez: a varredura custa ~260 ms, e a lista só muda com o Windows.</summary>
    public static IReadOnlyList<ShellItem> All() => _cache ??= Scan();

    /// <summary>
    /// Pelo automation do shell, o mesmo COM tardio da pasta de aplicativos
    /// (<see cref="AppCatalog"/>). Um item que falhe não derruba os outros.
    /// </summary>
    private static List<ShellItem> Scan()
    {
        var found = new List<ShellItem>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return found;

            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return found;

            foreach (var endereco in Folders)
            {
                try
                {
                    dynamic? pasta = shell.NameSpace(endereco);
                    if (pasta is null) continue;

                    string nome = pasta.Self.Name;
                    string caminho = pasta.Self.Path;
                    if (!string.IsNullOrWhiteSpace(nome) && !string.IsNullOrWhiteSpace(caminho))
                        found.Add(new ShellItem(nome, caminho, PlaceKind));
                }
                catch { /* pasta que este Windows não tem: segue para a próxima */ }
            }

            dynamic? painel = shell.NameSpace(ControlPanel);
            if (painel is not null)
            {
                foreach (dynamic item in painel.Items())
                {
                    string nome = item.Name;
                    string caminho = item.Path;

                    // há um item sem nome nesta máquina ({98F2AB62-…}); sem nome não há o que buscar
                    if (!string.IsNullOrWhiteSpace(nome) && !string.IsNullOrWhiteSpace(caminho))
                        found.Add(new ShellItem(nome, caminho, ControlPanelKind));
                }
            }
        }
        catch (Exception ex) { Log.Write("não deu para ler os lugares do Windows para a busca", ex); }

        return found;
    }

    /// <summary>
    /// Abre pelo explorer.exe, como os Locais da barra. O endereço do shell vai com o prefixo
    /// <c>shell:</c> — "shell:" + "::{GUID}" é a forma que o Win+R também aceita.
    /// </summary>
    public static void Open(string address)
    {
        var alvo = address.StartsWith("::", StringComparison.Ordinal) ? "shell:" + address : address.TrimEnd('\\');

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = alvo.Contains(' ') ? $"\"{alvo}\"" : alvo,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Write($"não deu para abrir '{address}' pela busca", ex); }
    }
}
