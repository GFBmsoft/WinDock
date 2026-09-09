using System.IO;

namespace WinDock.Services;

/// <summary>
/// Conserta o caminho de um app da Store que se atualizou sozinho.
///
/// Um app empacotado (MSIX) mora numa pasta com a versão no nome —
/// <c>...\WindowsApps\SpotifyAB.SpotifyMusic_1.298.301.0_x64__zpdnekdrzrea0\Spotify.exe</c> — e
/// cada atualização cria uma pasta nova e apaga a antiga. Um botão fixado guarda o caminho de
/// quando foi fixado, então o dia em que o app se atualiza é o dia em que o botão perde o ícone
/// (o executável não existe mais para ter ícone extraído) e para de abrir. Foi o que aconteceu
/// com o Spotify ao pular para a 1.299.317.0.
///
/// O que não muda entre versões é o resto do nome da pasta: o nome do pacote, a arquitetura e o
/// hash do publicador. É por ele que a pasta atual é encontrada — a versão vira curinga.
/// </summary>
public static class PackagedApps
{
    private const string Marca = @"\WindowsApps\";

    /// <summary>
    /// Se <paramref name="path"/> aponta para dentro de um pacote que já não existe, devolve o
    /// mesmo arquivo na versão instalada hoje. Responde <c>false</c> quando o caminho ainda
    /// existe, quando não é de app empacotado, ou quando nada equivalente foi encontrado — e aí
    /// <paramref name="atual"/> volta igual ao que entrou.
    /// </summary>
    public static bool TryRepair(string path, out string atual)
    {
        atual = path;
        if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) return false;

        var corte = path.IndexOf(Marca, StringComparison.OrdinalIgnoreCase);
        if (corte < 0) return false;

        var raiz = path[..(corte + Marca.Length)];      // ...\WindowsApps\
        var resto = path[(corte + Marca.Length)..];     // <pacote>\Spotify.exe

        var barra = resto.IndexOf('\\');
        if (barra <= 0) return false;

        var pacote = resto[..barra];
        var interno = resto[(barra + 1)..];

        // Nome_Versao_Arquitetura__Hash — o "__" antes do hash deixa um campo vazio no meio,
        // então são cinco pedaços, não quatro
        var partes = pacote.Split('_');
        if (partes.Length < 5) return false;

        var padrao = $"{partes[0]}_*_{partes[^3]}__{partes[^1]}";

        try
        {
            foreach (var pasta in Directory.EnumerateDirectories(raiz, padrao)
                                           .OrderByDescending(VersionOf))
            {
                var candidato = Path.Combine(pasta, interno);
                if (!File.Exists(candidato)) continue;

                atual = candidato;
                return true;
            }
        }
        catch { /* WindowsApps é fechada por ACL: sem leitura, fica o caminho antigo */ }

        return false;
    }

    /// <summary>
    /// O nome de família do pacote a que este caminho pertence, ou <c>null</c> se ele não for de
    /// um app empacotado.
    ///
    /// A pasta se chama <c>Nome_Versão_Arquitetura__Hash</c> e a família é <c>Nome_Hash</c> — o
    /// mesmo par que sobrevive a toda atualização. Serve para achar o AppUserModelID do app
    /// (<c>Família!Id</c>), que é a **única** forma de abrir um app da Store: executar o .exe
    /// direto dentro do <c>WindowsApps</c> devolve "Acesso negado", por mais que o arquivo exista
    /// e o ícone dele possa ser lido.
    /// </summary>
    public static string? FamilyOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var corte = path.IndexOf(Marca, StringComparison.OrdinalIgnoreCase);
        if (corte < 0) return null;

        var resto = path[(corte + Marca.Length)..];
        var barra = resto.IndexOf('\\');
        var pacote = barra > 0 ? resto[..barra] : resto;

        var partes = pacote.Split('_');
        return partes.Length >= 5 ? $"{partes[0]}_{partes[^1]}" : null;
    }

    /// <summary>
    /// O AppUserModelID com que este caminho se abre — procurado na pasta de aplicativos do
    /// shell, que é quem sabe o identificador de cada app instalado.
    ///
    /// A busca é pelo prefixo <c>Família!</c>: o que vem depois do "!" é escolha do fabricante
    /// (o Spotify usa "!Spotify") e não dá para adivinhar.
    /// </summary>
    public static string? AumidOf(string path)
    {
        var familia = FamilyOf(path);
        if (familia is null) return null;

        var prefixo = familia + "!";

        return AppCatalog.All()
            .FirstOrDefault(a => a.IsAumid && a.Target.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase))
            ?.Target;
    }

    /// <summary>A versão no nome da pasta, para preferir a mais nova quando duas convivem — o
    /// Windows deixa a antiga para trás por um tempo depois de atualizar. Ordenar pelo texto
    /// poria a "1.9" na frente da "1.10".</summary>
    private static Version VersionOf(string pasta)
    {
        var partes = Path.GetFileName(pasta).Split('_');
        return partes.Length >= 2 && Version.TryParse(partes[1], out var v) ? v : new Version(0, 0);
    }
}
