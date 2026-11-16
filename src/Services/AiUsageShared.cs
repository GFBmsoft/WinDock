using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinDock.Services;

/// <summary>
/// A última leitura da cota de uma conta, no arquivo que a dock divide com o ctop e o GTerm.
///
/// <para><b>Por que dividir.</b> Os três perguntam ao mesmo endpoint, com o mesmo token, e o
/// endpoint corta quem pergunta demais (<c>429</c>). O ctop e o GTerm já combinavam a vez por
/// este arquivo; a dock perguntava por fora e, com o GTerm aberto consultando a cada dois
/// minutos, levou <c>429</c> em todas as tentativas de uma manhã inteira (16/11/2026, das 08:55
/// em diante) — o cartão parou enquanto o GTerm seguia mostrando o número. Lendo daqui, a dock
/// usa o número que o outro acabou de ler, sem ir à rede; e quando é ela quem lê, o número
/// serve aos outros dois.</para>
///
/// <para><b>O formato é o do ctop</b> (<c>Get-Limits</c> em
/// <c>%USERPROFILE%\Scripts\claude-stats\claude-stats.ps1</c>), seguido também pelo
/// <c>LimitesDoPlano</c> do GTerm: <c>{at, data, nextTry}</c>, mais <c>erro</c>, que só o GTerm
/// escreve e mostra. <c>data</c> é a resposta da API como veio — gravada inteira, porque os
/// outros leem campos que a dock ignora. <c>nextTry</c> é até quando ninguém pergunta, depois de
/// um erro; numa leitura boa, é a própria hora dela. Mudou lá, muda aqui.</para>
///
/// <para>Nada de credencial passa por aqui: o arquivo só tem os números, e mora na pasta do
/// ctop, não na do Claude Code.</para>
/// </summary>
/// <param name="Em">Quando <paramref name="Dados"/> foram lidos da API.</param>
/// <param name="Dados">A resposta da API; nula quando ninguém leu ainda.</param>
/// <param name="ProximaTentativa">Antes disto, ninguém pergunta à API.</param>
/// <param name="Erro">Por que a última tentativa não deu certo, nas palavras do GTerm.</param>
internal sealed record AiUsageShared(DateTime Em, JsonObject? Dados, DateTime ProximaTentativa, string? Erro)
{
    /// <summary>A pasta do ctop. Só os testes trocam.</summary>
    internal static string Pasta { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-stats");

    /// <summary>
    /// O arquivo de uma conta: o do ctop para a padrão, um ao lado com o nome da pasta para as
    /// outras — a regra do GTerm, porque a cota é de cada conta.
    /// </summary>
    internal static string Arquivo(string id) =>
        Path.Combine(Pasta, id is ".claude" or "" ? "limits-cache.json" : $"limits-cache-{id}.json");

    /// <summary>O que está no arquivo; nulo quando ele não existe ou não se entende.</summary>
    internal static AiUsageShared? Ler(string id)
    {
        var arquivo = Arquivo(id);
        try
        {
            if (!File.Exists(arquivo)) return null;

            // O outro pode estar trocando o arquivo neste instante: a leitura compartilhada evita
            // a exceção de arquivo em uso, e o StreamReader aceita o BOM que o PowerShell 5 põe.
            string texto;
            using (var fluxo = new FileStream(arquivo, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete))
            using (var leitor = new StreamReader(fluxo))
                texto = leitor.ReadToEnd();

            if (JsonNode.Parse(texto) is not JsonObject estado) return null;

            return new AiUsageShared(Data(estado["at"]) ?? DateTime.MinValue,
                                     estado["data"] as JsonObject,
                                     Data(estado["nextTry"]) ?? DateTime.MinValue,
                                     Texto(estado["erro"]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Grava o estado. Sem conseguir, a dock segue com o que tem na memória — e os outros, com o
    /// que estava no arquivo.
    /// </summary>
    internal static void Gravar(string id, AiUsageShared estado)
    {
        var arquivo = Arquivo(id);

        // um nome só nosso: o GTerm usa "<arquivo>.tmp", e dois gravando o mesmo provisório
        // misturariam os bytes
        var provisorio = $"{arquivo}.windock-{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Pasta);

            var json = new JsonObject
            {
                ["at"] = Hora(estado.Em),
                ["data"] = estado.Dados?.DeepClone(),
                ["nextTry"] = Hora(estado.ProximaTentativa)
            };
            if (!string.IsNullOrWhiteSpace(estado.Erro)) json["erro"] = estado.Erro;

            // em provisório e renomeando: quem lê no mesmo instante vê o antigo inteiro ou o
            // novo inteiro, nunca a metade
            File.WriteAllText(provisorio, json.ToJsonString());
            File.Move(provisorio, arquivo, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"cota de IA ({id}): não deu para gravar o estado comum ({ex.GetType().Name})");
            try { File.Delete(provisorio); } catch (Exception) { /* fica para a próxima */ }
        }
    }

    /// <summary>A hora com o fuso, como o ctop e o GTerm gravam ("o" de uma hora local).</summary>
    private static string Hora(DateTime quando) =>
        (quando == DateTime.MinValue ? quando : DateTime.SpecifyKind(quando, DateTimeKind.Local))
        .ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? Data(JsonNode? no) =>
        Texto(no) is { } texto &&
        DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var data)
            ? data.LocalDateTime
            : null;

    private static string? Texto(JsonNode? no) =>
        no is JsonValue valor && valor.TryGetValue<string>(out var texto) && !string.IsNullOrWhiteSpace(texto)
            ? texto
            : null;
}
