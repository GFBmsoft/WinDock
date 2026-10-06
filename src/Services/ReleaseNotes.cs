using System.IO;
using System.Reflection;

namespace WinDock.Services;

/// <summary>Uma versão do histórico: o número, a data e o que mudou nela.</summary>
public sealed record ReleaseNote(string Version, string Date, IReadOnlyList<string> Items)
{
    /// <summary>É a versão que está rodando — a página a destaca.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// O histórico de versões, para a página "Notas da versão" das Configurações.
///
/// Vem do <c>docs/NOTAS.md</c>, embutido no executável: a dock é um arquivo só, roda de qualquer
/// pasta, e as notas têm de estar à mão sem rede — é justamente depois de uma atualização, com a
/// dock recém-trocada, que alguém quer saber o que mudou. As Releases do GitHub teriam o mesmo
/// texto, mas custariam uma consulta à API (60 por hora, por IP) para ler o que já veio junto.
///
/// O formato é o mínimo: <c>## versão — data</c> abre uma versão e <c>- texto</c> é uma mudança
/// dela. O título é ignorado, e o que estiver entre <c>&lt;!--</c> e <c>--&gt;</c> também.
/// </summary>
public static class ReleaseNotes
{
    private const string Resource = "WinDock.NOTAS.md";

    private static IReadOnlyList<ReleaseNote>? _all;

    /// <summary>Todas as versões, da mais nova para a mais velha — a ordem do arquivo.</summary>
    public static IReadOnlyList<ReleaseNote> All => _all ??= Load();

    private static IReadOnlyList<ReleaseNote> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
            if (stream is null)
            {
                Log.Write("as notas da versão não vieram embutidas no executável");
                return [];
            }

            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd(), CurrentTag());
        }
        catch (Exception ex)
        {
            Log.Write("não foi possível ler as notas da versão", ex);
            return [];
        }
    }

    /// <summary>
    /// A versão em uso sem o sufixo de build local: "1.0.0.43-dev.2" é a 1.0.0.43 com commits por
    /// cima — a marca fica nela, e a entrada da versão que ainda não tem tag aparece acima.
    /// </summary>
    private static string CurrentTag()
    {
        var versao = SelfUpdateService.CurrentVersion;
        var corte = versao.IndexOfAny(['-', '+']);
        return corte < 0 ? versao : versao[..corte];
    }

    /// <summary>Separado da leitura para poder ser conferido com um texto qualquer.</summary>
    public static IReadOnlyList<ReleaseNote> Parse(string texto, string atual)
    {
        var notas = new List<ReleaseNote>();
        string? versao = null;
        var data = string.Empty;
        var itens = new List<string>();

        void Fechar()
        {
            if (versao is not null)
                notas.Add(new ReleaseNote(versao, data, itens.ToList()) { IsCurrent = versao == atual });
            itens.Clear();
        }

        var comentario = false;

        foreach (var bruta in texto.Split('\n'))
        {
            var linha = bruta.Trim();

            // o comentário do topo do arquivo explica o formato com exemplos — que têm
            // exatamente a cara de uma versão, e apareciam na página como a primeira delas
            if (linha.StartsWith("<!--", StringComparison.Ordinal)) comentario = true;
            if (comentario)
            {
                if (linha.EndsWith("-->", StringComparison.Ordinal)) comentario = false;
                continue;
            }

            if (linha.StartsWith("## ", StringComparison.Ordinal))
            {
                Fechar();

                var partes = linha[3..].Split('—', 2);
                versao = partes[0].Trim();
                data = partes.Length > 1 ? partes[1].Trim() : string.Empty;
            }
            else if (versao is not null && linha.StartsWith("- ", StringComparison.Ordinal))
            {
                itens.Add(linha[2..].Trim());
            }
        }

        Fechar();
        return notas;
    }
}
