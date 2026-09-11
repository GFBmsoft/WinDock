using System.IO;
using System.Text.RegularExpressions;

namespace WinDock.Services;

/// <summary>
/// Conserta o caminho de um app instalado pelo Squirrel que se atualizou sozinho.
///
/// É o instalador do Discord, do Postman e do próprio SourceTree: o programa mora em
/// <c>%LOCALAPPDATA%\Discord\app-1.0.9256\Discord.exe</c>, e cada atualização cria uma pasta
/// <c>app-</c> nova ao lado e apaga a antiga. É o mesmo defeito do <see cref="PackagedApps"/> com
/// outra cara: o botão fixado guarda a pasta de quando foi fixado e, no dia em que o app se
/// atualiza, perde o ícone e deixa de abrir. Foi o que aconteceu com o Discord ao pular da
/// 1.0.9256 para a 1.0.9257.
///
/// O que marca uma instalação dessas é o <c>Update.exe</c> na pasta de cima, ao lado das
/// <c>app-*</c>. Sem ele o caminho não é tocado: uma pasta chamada "app-2" dentro de um programa
/// qualquer não é motivo para trocar o executável de ninguém.
/// </summary>
public static class SquirrelApps
{
    private static readonly Regex Versionada = new(
        @"^(?<raiz>.+)\\app-[^\\]+\\(?<interno>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Se <paramref name="path"/> aponta para uma pasta <c>app-</c> que já não existe, devolve o
    /// mesmo arquivo na versão instalada hoje. Responde <c>false</c> quando o caminho ainda
    /// existe, quando não é de uma instalação do Squirrel, ou quando nada equivalente foi
    /// encontrado — e aí <paramref name="atual"/> volta igual ao que entrou.
    /// </summary>
    public static bool TryRepair(string path, out string atual)
    {
        atual = path;
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return false;

        var m = Versionada.Match(path);
        if (!m.Success) return false;

        var raiz = m.Groups["raiz"].Value;        // ...\Local\Discord
        var interno = m.Groups["interno"].Value;  // Discord.exe
        if (!File.Exists(Path.Combine(raiz, "Update.exe"))) return false;

        try
        {
            foreach (var pasta in Directory.EnumerateDirectories(raiz, "app-*").OrderByDescending(VersionOf))
            {
                var candidato = Path.Combine(pasta, interno);
                if (!File.Exists(candidato)) continue;

                atual = candidato;
                return true;
            }
        }
        catch { /* a pasta pode estar sendo trocada no meio da atualização: fica para a próxima */ }

        return false;
    }

    /// <summary>A versão no nome da pasta, para preferir a mais nova quando duas convivem — o
    /// Squirrel só apaga a antiga depois que a nova sobe. Ordenar pelo texto poria a "1.0.9"
    /// na frente da "1.0.10".</summary>
    private static Version VersionOf(string pasta) =>
        Version.TryParse(Path.GetFileName(pasta)["app-".Length..], out var v) ? v : new Version(0, 0);
}
