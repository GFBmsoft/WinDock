using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinDock.Services;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

// ── o cenário: uma conta de mentira e o arquivo comum numa pasta temporária ──
//
// Nada daqui toca no %LOCALAPPDATA%\claude-stats de verdade nem na rede: a pasta do estado comum
// é trocada antes de qualquer leitura, e a API é a função `Api`, que conta os pedidos.

var raiz = Path.Combine(Path.GetTempPath(), "windock-cota-" + Guid.NewGuid().ToString("N")[..8]);
AiUsageShared.Pasta = Path.Combine(raiz, "claude-stats");

var conta = Path.Combine(raiz, ".claude");
Directory.CreateDirectory(conta);
var vence = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
File.WriteAllText(Path.Combine(conta, ".credentials.json"),
    """{"claudeAiOauth":{"accessToken":"token-de-teste","expiresAt":VENCE,"subscriptionType":"max"}}""".Replace("VENCE", vence.ToString(CultureInfo.InvariantCulture)));

var perfil = new AiProfile(".claude", conta, null, null, null, null);
var arquivo = AiUsageShared.Arquivo(".claude");

// a resposta da API no formato de verdade, com campos que a dock não lê e os outros leem
string Resposta(int cincoHoras) => """
    {"five_hour":{"utilization":CINCO,"resets_at":"2026-11-16T16:00:00.064292+00:00"},
     "seven_day":{"utilization":23,"resets_at":"2026-11-16T19:00:00.064312+00:00"},
     "seven_day_opus":null,
     "limits":[{"kind":"session","percent":CINCO,"resets_at":"2026-11-16T16:00:00.064292+00:00"},
               {"kind":"weekly_scoped","percent":2,"resets_at":"2026-11-16T19:00:00+00:00",
                "scope":{"model":{"display_name":"Fable"}}}],
     "spend":{"used":{"amount_minor":0,"currency":"USD"}}}
    """.Replace("CINCO", cincoHoras.ToString(CultureInfo.InvariantCulture));

// o arquivo como o GTerm grava: horas locais com o fuso, sem BOM
void GravarComoGTerm(DateTime em, int cincoHoras, DateTime? proxima = null, string? erro = null)
{
    Directory.CreateDirectory(AiUsageShared.Pasta);
    var estado = new JsonObject
    {
        ["at"] = em.ToString("o", CultureInfo.InvariantCulture),
        ["data"] = JsonNode.Parse(Resposta(cincoHoras)),
        ["nextTry"] = (proxima ?? em).ToString("o", CultureInfo.InvariantCulture),
    };
    if (erro is not null) estado["erro"] = erro;
    File.WriteAllText(arquivo, estado.ToJsonString());
}

JsonObject LerArquivo() => (JsonObject)JsonNode.Parse(File.ReadAllText(arquivo))!;
DateTime Hora(JsonNode? no) => DateTimeOffset.Parse((string)no!, CultureInfo.InvariantCulture).LocalDateTime;
bool Perto(DateTime a, DateTime b, double segundos = 5) => Math.Abs((a - b).TotalSeconds) <= segundos;
double CincoHoras(AiAccountUsage l) => l.Gauges.FirstOrDefault(g => g.Nome == "5 horas")?.Percent ?? -1;

var api = new Api();
AiUsageService Servico() => new((pedido, _) => api.Responder(pedido));

void Recomecar()
{
    api.Pedidos = 0;
    api.Proxima = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Resposta(33)) };
    api.Durante = null;
    if (File.Exists(arquivo)) File.Delete(arquivo);
}

try
{
    // ── o GTerm leu há pouco: a dock usa o número dele ──────────
    //
    // É o caso do defeito: com o GTerm aberto, a dock perguntava por fora e levava 429. Agora o
    // número de 30 segundos atrás vale, e ninguém vai à rede.

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddSeconds(-30), 41);
    var (lido, novo) = await Servico().LerAsync(perfil, consultar: true, default);

    Check("número do GTerm de 30 s atrás: a dock não pergunta", api.Pedidos == 0, $"{api.Pedidos} pedido(s)");
    Check("e mostra o número dele", lido.State == AiUsageState.Ok && CincoHoras(lido) == 41,
          $"{lido.State}, 5 horas = {CincoHoras(lido)}");
    Check("sem aviso de número velho", lido.Erro is null, lido.Erro ?? "");
    Check("e conta como número novo", novo);

    // ── o arquivo envelheceu: a dock pergunta e grava para os outros ──

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddMinutes(-3), 10);
    var servico = Servico();
    (lido, _) = await servico.LerAsync(perfil, consultar: true, default);
    var gravado = LerArquivo();

    Check("arquivo de 3 min atrás: a dock pergunta", api.Pedidos == 1, $"{api.Pedidos} pedido(s)");
    Check("e mostra o número de agora", CincoHoras(lido) == 33, $"5 horas = {CincoHoras(lido)}");
    Check("o arquivo fica com a hora de agora", Perto(Hora(gravado["at"]), DateTime.Now), (string?)gravado["at"] ?? "");
    Check("sem espera depois de uma leitura boa (nextTry = at)", Hora(gravado["nextTry"]) == Hora(gravado["at"]),
          $"at {gravado["at"]}, nextTry {gravado["nextTry"]}");
    Check("a resposta vai inteira, com o que só os outros leem", gravado["data"]?["spend"] is JsonObject);
    Check("sem erro no arquivo", gravado["erro"] is null);
    Check("a hora vai com o fuso, como o ctop e o GTerm leem",
          Regex.IsMatch((string)gravado["at"]!, @"[+-]\d\d:\d\d$"), (string?)gravado["at"] ?? "");
    Check("o token não vai para o arquivo", !File.ReadAllText(arquivo).Contains("token-de-teste"));

    (lido, _) = await servico.LerAsync(perfil, consultar: true, default);
    Check("logo depois, a mesma dock não pergunta de novo", api.Pedidos == 1, $"{api.Pedidos} pedido(s)");

    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    Check("nem outro programa, lendo o arquivo que ela gravou", api.Pedidos == 1, $"{api.Pedidos} pedido(s)");

    // ── 429: a espera vai para o arquivo, e o número bom fica ───

    Recomecar();
    var velho = DateTime.Now.AddMinutes(-10);
    GravarComoGTerm(velho, 10);
    api.Proxima = () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        return r;
    };
    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    gravado = LerArquivo();

    Check("429: a dock perguntou uma vez", api.Pedidos == 1, $"{api.Pedidos} pedido(s)");
    Check("e segue mostrando o número de antes", CincoHoras(lido) == 10, $"5 horas = {CincoHoras(lido)}");
    Check("com o aviso de que ele é velho", lido.Erro is not null && lido.Desde is not null);
    Check("o arquivo guarda a espera que a API pediu (Retry-After)",
          Perto(Hora(gravado["nextTry"]), DateTime.Now.AddSeconds(120)), (string?)gravado["nextTry"] ?? "");
    Check("e o número bom, com a hora dele", Perto(Hora(gravado["at"]), velho, 1) &&
          (double?)gravado["data"]?["five_hour"]?["utilization"] == 10, (string?)gravado["at"] ?? "");
    Check("com o porquê nas palavras do GTerm", (string?)gravado["erro"] == "a conta limitou as consultas",
          (string?)gravado["erro"] ?? "(nada)");

    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    Check("durante a espera, outro programa não pergunta", api.Pedidos == 1, $"{api.Pedidos} pedido(s)");

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddMinutes(-10), 10);
    api.Proxima = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    await Servico().LerAsync(perfil, consultar: true, default);
    Check("429 sem Retry-After: cinco minutos, como o ctop e o GTerm",
          Perto(Hora(LerArquivo()["nextTry"]), DateTime.Now.AddMinutes(5)), (string?)LerArquivo()["nextTry"] ?? "");

    Recomecar();
    api.Proxima = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    Check("429 sem número nenhum: o cartão diz quando tenta de novo",
          lido.Erro?.Contains("tentando de novo às") == true, lido.Erro ?? "");

    // ── 429 enquanto o GTerm lia: o número dele não é apagado ───

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddMinutes(-3), 10);
    api.Proxima = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    api.Durante = () => GravarComoGTerm(DateTime.Now, 55);
    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    gravado = LerArquivo();

    Check("429 com o GTerm lendo no meio: vale o número dele", CincoHoras(lido) == 55 && lido.Erro is null,
          $"5 horas = {CincoHoras(lido)}, erro = {lido.Erro ?? "(nada)"}");
    Check("e o arquivo dele não leva o erro por cima",
          (double?)gravado["data"]?["five_hour"]?["utilization"] == 55 && gravado["erro"] is null,
          gravado.ToJsonString());

    // ── a volta sem rede, de minuto em minuto ──────────────────

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddSeconds(-20), 41);
    servico = Servico();
    await servico.LerAsync(perfil, consultar: true, default);
    (lido, novo) = await servico.LerAsync(perfil, consultar: false, default);
    Check("sem rede e nada mudou: não é número novo", !novo && CincoHoras(lido) == 41);

    GravarComoGTerm(DateTime.Now, 60);
    (lido, novo) = await servico.LerAsync(perfil, consultar: false, default);
    Check("o GTerm leu de novo: a volta sem rede traz o número dele", novo && CincoHoras(lido) == 60,
          $"novo = {novo}, 5 horas = {CincoHoras(lido)}");
    Check("e não pergunta nada", api.Pedidos == 0, $"{api.Pedidos} pedido(s)");

    Recomecar();
    GravarComoGTerm(DateTime.Now.AddMinutes(-7), 12);
    (lido, novo) = await Servico().LerAsync(perfil, consultar: false, default);
    Check("sem rede com o arquivo velho: mostra o que há e não pergunta",
          api.Pedidos == 0 && CincoHoras(lido) == 12 && lido.Erro is null, $"{api.Pedidos} pedido(s)");

    // ── o arquivo do ctop rodando no PowerShell 5 (com BOM) ──────

    Recomecar();
    Directory.CreateDirectory(AiUsageShared.Pasta);
    var ctop = $$"""{"at":"{{DateTime.Now.AddSeconds(-20):o}}","data":{{Resposta(27)}},"nextTry":"{{DateTime.Now.AddSeconds(-20):o}}"}""";
    File.WriteAllText(arquivo, ctop, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    (lido, _) = await Servico().LerAsync(perfil, consultar: true, default);
    Check("arquivo do ctop com BOM: a dock lê", CincoHoras(lido) == 27 && api.Pedidos == 0,
          $"5 horas = {CincoHoras(lido)}, {api.Pedidos} pedido(s)");

    // ── outros erros e outras contas ───────────────────────────

    Recomecar();
    api.Proxima = () => throw new HttpRequestException("rede fora");
    await Servico().LerAsync(perfil, consultar: true, default);
    gravado = LerArquivo();
    Check("rede fora: um minuto de espera no arquivo", Perto(Hora(gravado["nextTry"]), DateTime.Now.AddMinutes(1)),
          (string?)gravado["nextTry"] ?? "");
    Check("com o porquê do GTerm", (string?)gravado["erro"] == "sem resposta da conta", (string?)gravado["erro"] ?? "");

    Recomecar();
    var semLogin = Path.Combine(raiz, ".claude-vazia");
    Directory.CreateDirectory(semLogin);
    (lido, _) = await Servico().LerAsync(new AiProfile(".claude-vazia", semLogin, null, null, null, null),
                                         consultar: true, default);
    Check("sem credencial: não pergunta", lido.State == AiUsageState.NoCredentials && api.Pedidos == 0);
    Check("e não cria arquivo", !File.Exists(AiUsageShared.Arquivo(".claude-vazia")));

    Check("a conta padrão usa o arquivo do ctop", Path.GetFileName(AiUsageShared.Arquivo(".claude")) == "limits-cache.json");
    Check("as outras, um ao lado com o nome da pasta (regra do GTerm)",
          Path.GetFileName(AiUsageShared.Arquivo(".claude-bm")) == "limits-cache-.claude-bm.json");
}
finally
{
    try { Directory.Delete(raiz, recursive: true); } catch (IOException) { }
}

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} falha(s)");
return falhas == 0 ? 0 : 1;

/// <summary>A API de mentira: conta os pedidos e devolve o que o teste mandar.</summary>
internal sealed class Api
{
    public int Pedidos;
    public Func<HttpResponseMessage> Proxima = () => new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>O que acontece enquanto a pergunta está no ar — outro programa gravando o arquivo.</summary>
    public Action? Durante;

    public Task<HttpResponseMessage> Responder(HttpRequestMessage pedido)
    {
        Pedidos++;
        Durante?.Invoke();
        return Task.FromResult(Proxima());
    }
}
