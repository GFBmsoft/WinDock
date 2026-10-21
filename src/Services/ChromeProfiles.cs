using System.IO;
using System.Text.Json;

namespace WinDock.Services;

/// <summary>
/// Um perfil do Chrome: a pasta, o nome que o usuario deu, a foto e o icone do perfil.
///
/// O icone (<c>Google Profile.ico</c>) e o que o proprio Chrome gera para a barra de tarefas:
/// o simbolo do Chrome com o avatar do perfil no canto. Existe ate para o perfil sem foto do
/// Google — e o unico desenho que separa todos os perfis.
/// </summary>
public sealed record ChromeProfile(string Directory, string Name, string Aumid, string PicturePath,
                                   string IconPath)
{
    public string Arguments => $"--profile-directory=\"{Directory}\"";
}

/// <summary>
/// Regra especifica do Chrome, e proposital.
///
/// Cada perfil do Chrome expoe um AppUserModelID proprio ("Chrome" para o Default,
/// "Chrome.UserData.Profile2" para os outros), o que ja basta para virarem botoes
/// separados. Mas quando o perfil nao tem atalho no Windows, sobra o chrome.exe puro:
/// o botao abriria o perfil errado e mostraria o nome generico. Aqui o "Local State"
/// do proprio Chrome preenche essa lacuna — nome, argumento e foto do perfil.
/// </summary>
public static class ChromeProfiles
{
    private static List<ChromeProfile>? _cache;

    public static IReadOnlyList<ChromeProfile> All() => _cache ??= Scan();

    public static void Invalidate() => _cache = null;

    public static ChromeProfile? ByAumid(string aumid) =>
        string.IsNullOrEmpty(aumid)
            ? null
            : All().FirstOrDefault(p => string.Equals(p.Aumid, aumid, StringComparison.OrdinalIgnoreCase));

    private static List<ChromeProfile> Scan()
    {
        var list = new List<ChromeProfile>();
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\User Data");

        var localState = Path.Combine(userData, "Local State");
        if (!File.Exists(localState)) return list;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (!doc.RootElement.TryGetProperty("profile", out var profile) ||
                !profile.TryGetProperty("info_cache", out var cache)) return list;

            foreach (var entry in cache.EnumerateObject())
            {
                var dir = entry.Name;                       // "Default", "Profile 2", ...
                var name = entry.Value.TryGetProperty("name", out var n) ? n.GetString() ?? dir : dir;

                var picture = Path.Combine(userData, dir, "Google Profile Picture.png");
                var icon = Path.Combine(userData, dir, "Google Profile.ico");
                list.Add(new ChromeProfile(dir, name, AumidFor(dir),
                                           File.Exists(picture) ? picture : string.Empty,
                                           File.Exists(icon) ? icon : string.Empty));
            }
        }
        catch { /* Local State ilegivel: sem perfis, o resto da dock segue igual */ }

        return list;
    }

    /// <summary>
    /// O chrome.exe instalado, para abrir um perfil cujo atalho sumiu — o Chrome apaga e
    /// recria os atalhos de perfil quando o perfil muda de nome ou de foto.
    /// </summary>
    public static string? ExePath()
    {
        foreach (var pasta in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (string.IsNullOrEmpty(pasta)) continue;
            var exe = Path.Combine(pasta, @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>
    /// Como o Chrome monta o AppUserModelID: o perfil padrao fica so "Chrome" e os
    /// demais viram "Chrome.UserData." + a pasta sem espacos ("Profile 2" -> Profile2).
    /// </summary>
    private static string AumidFor(string directory) =>
        directory.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? "Chrome"
            : "Chrome.UserData." + directory.Replace(" ", string.Empty);
}
