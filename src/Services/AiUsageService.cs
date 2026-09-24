using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDock.Services;

/// <summary>Um medidor do cartão: uma janela de cota, quanto dela foi usada e quando zera.</summary>
/// <param name="Nome">Como aparece no cartão ("5 horas", "7 dias", "7 dias · Opus").</param>
/// <param name="Percent">0 a 100.</param>
/// <param name="ResetaEm">Quando a janela zera, em hora local; nulo quando a API não diz.</param>
public sealed record AiGauge(string Nome, double Percent, DateTime? ResetaEm);

/// <summary>Em que pé está a leitura — é o que decide o que o cartão mostra.</summary>
public enum AiUsageState
{
    /// <summary>Ainda não se tentou ler nesta sessão.</summary>
    Unknown,

    /// <summary>Não há credencial do Claude Code nesta máquina — o cartão não tem o que mostrar.</summary>
    NoCredentials,

    /// <summary>A credencial existe mas venceu. Veja <see cref="AiUsageService"/> para o porquê de não renovarmos.</summary>
    Expired,

    /// <summary>Deu certo.</summary>
    Ok,

    /// <summary>Rede fora, servidor recusou, resposta que não se entende.</summary>
    Failed
}

/// <summary>
/// Quem está logado, para o cartão dizer de quem é a cota que ele mostra.
///
/// Importa mais do que parece: quem tem conta pessoal e conta da empresa troca de uma para
/// outra no Claude Code, e a cota que aparece é a da que estiver ativa. Sem o nome e o
/// e-mail, o cartão mostraria números de origem desconhecida.
/// </summary>
/// <param name="Nome">O nome da pessoa, como o Claude Code o guarda.</param>
/// <param name="Email">A conta.</param>
/// <param name="Organizacao">A organização, quando a conta pertence a uma.</param>
/// <param name="Papel">O papel dentro dela ("admin", "member"), quando houver.</param>
/// <param name="Plano">"max", "pro", "team" — do arquivo de credenciais.</param>
public sealed record AiAccount(string? Nome, string? Email, string? Organizacao,
                               string? Papel, string? Plano)
{
    /// <summary>Tem alguma coisa para mostrar?</summary>
    public bool Vazia => string.IsNullOrWhiteSpace(Nome) && string.IsNullOrWhiteSpace(Email) &&
                         string.IsNullOrWhiteSpace(Organizacao) && string.IsNullOrWhiteSpace(Plano);
}

/// <summary>O que o cartão precisa saber, junto.</summary>
public sealed record AiUsage(AiUsageState State, IReadOnlyList<AiGauge> Gauges,
                             AiAccount Conta, DateTime Quando, string? Erro = null);

/// <summary>
/// Quanto da cota do Claude já foi usada — o que o Claude Code mostra no <c>/usage</c>.
///
/// <para><b>De onde vêm os números.</b> Do mesmo lugar de onde o próprio Claude Code os tira:
/// <c>GET https://api.anthropic.com/api/oauth/usage</c>, autenticado com o token OAuth que o
/// Claude Code guarda em <c>%USERPROFILE%\.claude\.credentials.json</c>. Não há API pública
/// para isto — o endpoint não é documentado, e pode mudar ou sumir sem aviso. Quando isso
/// acontecer, o cartão passa a dizer que não conseguiu ler; nada mais no WinDock depende
/// dele.</para>
///
/// <para><b>Este serviço nunca escreve na pasta <c>.claude</c>, e é uma decisão, não um
/// esquecimento.</b> O token vence de tempos em tempos e existe um endpoint de renovação —
/// mas renovar rotaciona o <c>refreshToken</c>, e quem renova precisa gravar o novo no
/// arquivo. Duas coisas escrevendo no mesmo arquivo de credenciais (o Claude Code e nós) é
/// como se perde o login: basta uma gravação pela metade, ou nós renovarmos e não gravarmos,
/// para o refresh que o Claude Code tem na mão deixar de valer. O preço de não renovar é
/// pequeno: o cartão fica dizendo "sessão expirada" até você usar o Claude Code, que renova
/// sozinho — e quem tem este cartão usa o Claude Code todo dia.</para>
///
/// <para><b>O token não vai para o log, em nenhuma hipótese</b> — nem truncado, nem em
/// mensagem de erro. O que se registra é o código da resposta e o estado.</para>
/// </summary>
public sealed class AiUsageService
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// O cabeçalho que destrava o endpoint para um token de OAuth. Sem ele a resposta é 401,
    /// mesmo com o token certo.
    /// </summary>
    private const string OauthBeta = "oauth-2025-04-20";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // o cartão é atualizado de minutos em minutos; uma conexão parada não precisa durar
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>O caminho do arquivo de credenciais do Claude Code nesta máquina.</summary>
    private static string CredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".claude", ".credentials.json");

    /// <summary>
    /// O arquivo de estado do Claude Code, que guarda quem está logado.
    ///
    /// É outro arquivo, e vale reparar: o token mora no <c>.credentials.json</c>, dentro da
    /// pasta <c>.claude</c>; a identidade mora neste, ao lado dela. Aqui não há segredo
    /// nenhum — nome, e-mail, organização —, e é só isso que se lê dele.
    /// </summary>
    private static string StatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>
    /// Existe credencial do Claude Code aqui? É o que decide se o ícone aparece na barra —
    /// e é só um <c>File.Exists</c>, sem abrir nada.
    /// </summary>
    public static bool Installed => File.Exists(CredentialsPath);

    private static readonly AiAccount SemConta = new(null, null, null, null, null);

    /// <summary>A última leitura, para o cartão abrir com algo enquanto a nova não chega.</summary>
    public AiUsage Current { get; private set; } =
        new(AiUsageState.Unknown, Array.Empty<AiGauge>(), SemConta, DateTime.MinValue);

    /// <summary>
    /// Lê a cota. Roda fora da thread da interface — é rede, e rede trava.
    /// </summary>
    public async Task<AiUsage> ReadAsync(CancellationToken cancel = default)
    {
        var resultado = await LerAsync(cancel).ConfigureAwait(false);
        Current = resultado;
        return resultado;
    }

    private async Task<AiUsage> LerAsync(CancellationToken cancel)
    {
        var agora = DateTime.Now;
        var conta = LerConta();

        string token;
        try
        {
            if (!File.Exists(CredentialsPath))
                return new AiUsage(AiUsageState.NoCredentials, Array.Empty<AiGauge>(), conta, agora);

            // O arquivo é do Claude Code, que pode estar gravando nele neste instante: a
            // leitura compartilhada evita a exceção de arquivo em uso, e uma leitura que
            // pegue o arquivo pela metade cai no catch e vira "não deu para ler" — a próxima
            // atualização acerta.
            await using var fluxo = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete);
            var creds = await JsonSerializer.DeserializeAsync<CredentialsFile>(fluxo, Json, cancel)
                                            .ConfigureAwait(false);

            var oauth = creds?.ClaudeAiOauth;
            if (oauth is null || string.IsNullOrWhiteSpace(oauth.AccessToken))
                return new AiUsage(AiUsageState.NoCredentials, Array.Empty<AiGauge>(), conta, agora);

            // o plano é a única coisa que sai daqui além do token, e não é segredo
            conta = conta with { Plano = oauth.SubscriptionType };

            // `expiresAt` vem em milissegundos desde 1970. Uma folga de um minuto evita
            // gastar uma ida à rede para receber 401 de um token que vence agora.
            if (oauth.ExpiresAt > 0 &&
                DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt) <= DateTimeOffset.UtcNow.AddMinutes(1))
                return new AiUsage(AiUsageState.Expired, Array.Empty<AiGauge>(), conta, agora);

            token = oauth.AccessToken;
        }
        catch (Exception ex)
        {
            // sem o texto da exceção quando ela puder carregar conteúdo do arquivo: aqui só
            // interessa o tipo
            Log.Write($"cota de IA: não deu para ler as credenciais do Claude Code ({ex.GetType().Name})");
            return new AiUsage(AiUsageState.Failed, Array.Empty<AiGauge>(), conta, agora,
                               "não deu para ler as credenciais");
        }

        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            pedido.Headers.Add("anthropic-beta", OauthBeta);
            pedido.Headers.UserAgent.ParseAdd("WinDock/1.0");

            using var resposta = await Http.SendAsync(pedido, cancel).ConfigureAwait(false);

            if (!resposta.IsSuccessStatusCode)
            {
                // 401 aqui quer dizer token recusado — na prática, vencido do lado de lá
                // mesmo que a data no arquivo diga que não.
                var estado = resposta.StatusCode == System.Net.HttpStatusCode.Unauthorized
                             ? AiUsageState.Expired : AiUsageState.Failed;

                Log.Write($"cota de IA: a API respondeu {(int)resposta.StatusCode}");
                return new AiUsage(estado, Array.Empty<AiGauge>(), conta, agora,
                                   $"a API respondeu {(int)resposta.StatusCode}");
            }

            await using var corpo = await resposta.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            var uso = await JsonSerializer.DeserializeAsync<UsageResponse>(corpo, Json, cancel)
                                          .ConfigureAwait(false);

            var medidores = Montar(uso);
            if (medidores.Count == 0)
                return new AiUsage(AiUsageState.Failed, Array.Empty<AiGauge>(), conta, agora,
                                   "a resposta não trouxe nenhuma cota");

            Log.Trace($"cota de IA: {string.Join(" | ", medidores.Select(m => $"{m.Nome} {m.Percent:0}%"))}");
            return new AiUsage(AiUsageState.Ok, medidores, conta, agora);
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA: falha ao consultar a API ({ex.GetType().Name})");
            return new AiUsage(AiUsageState.Failed, Array.Empty<AiGauge>(), conta, agora,
                               "não deu para falar com a API");
        }
    }

    /// <summary>
    /// Quem está logado no Claude Code, do <c>~/.claude.json</c>.
    ///
    /// Guardado depois da primeira vez: a conta não muda enquanto a pessoa não troca de
    /// login, e o arquivo tem umas centenas de KB (ele carrega o histórico de projetos junto)
    /// — reler isso a cada dez minutos seria pagar o parse inteiro por um punhado de campos.
    /// Quem troca de conta vê o cartão acertar na próxima vez que a dock subir.
    ///
    /// Falhar aqui não é motivo para o cartão não aparecer: sem estes campos ele mostra só os
    /// medidores, que são o que ele existe para mostrar.
    /// </summary>
    private static AiAccount LerConta()
    {
        if (_conta is not null) return _conta;

        try
        {
            if (!File.Exists(StatePath)) return _conta = SemConta;

            using var fluxo = new FileStream(StatePath, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            var estado = JsonSerializer.Deserialize<StateFile>(fluxo, Json);
            var oauth = estado?.OauthAccount;
            if (oauth is null) return _conta = SemConta;

            // `fullName` é o nome completo e `displayName` costuma ser o primeiro nome; o
            // cartão é estreito, então o curto vem primeiro quando existe.
            var nome = Primeiro(oauth.DisplayName, oauth.FullName);

            return _conta = new AiAccount(nome, oauth.EmailAddress, oauth.OrganizationName,
                                          oauth.OrganizationRole, null);
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA: não deu para ler a conta do Claude Code ({ex.GetType().Name})");
            return _conta = SemConta;
        }
    }

    private static AiAccount? _conta;

    private static string? Primeiro(params string?[] valores) =>
        valores.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Vira a resposta em medidores, na ordem em que fazem sentido de ler: a janela curta
    /// primeiro, que é a que aperta no meio do trabalho.
    ///
    /// As cotas por modelo chegam em <c>limits</c>, e não em campo próprio — só a de cinco
    /// horas, a semanal e a semanal do Sonnet têm lugar fixo na resposta. As que vierem
    /// repetidas (o mesmo <c>seven_day</c> aparecendo também como <c>weekly_all</c>) são
    /// descartadas pelo nome.
    /// </summary>
    private static IReadOnlyList<AiGauge> Montar(UsageResponse? uso)
    {
        var medidores = new List<AiGauge>();
        if (uso is null) return medidores;

        void Add(string nome, Window? janela)
        {
            if (janela is null) return;
            if (medidores.Any(m => m.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase))) return;
            medidores.Add(new AiGauge(nome, Math.Clamp(janela.Utilization, 0, 100), Local(janela.ResetsAt)));
        }

        Add("5 horas", uso.FiveHour);
        Add("7 dias", uso.SevenDay);
        Add("7 dias · Sonnet", uso.SevenDaySonnet);

        foreach (var limite in uso.Limits ?? new List<LimitEntry>())
        {
            if (limite.Percent is not { } percent) continue;

            var modelo = limite.Scope?.Model?.DisplayName;
            var nome = limite.Kind switch
            {
                "session" => "5 horas",
                "weekly_all" => "7 dias",
                "weekly_scoped" when !string.IsNullOrWhiteSpace(modelo) => $"7 dias · {modelo}",
                _ when !string.IsNullOrWhiteSpace(modelo) => modelo!,
                _ => null
            };

            if (nome is null) continue;
            Add(nome, new Window { Utilization = percent, ResetsAt = limite.ResetsAt });
        }

        return medidores;
    }

    /// <summary>A data da API (ISO 8601, em UTC) na hora do relógio de quem está olhando.</summary>
    private static DateTime? Local(string? iso) =>
        DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                           System.Globalization.DateTimeStyles.AssumeUniversal,
                                out var quando)
            ? quando.ToLocalTime().DateTime
            : null;

    // ── o formato do que se lê ───────────────────────────────

    private sealed class CredentialsFile
    {
        [JsonPropertyName("claudeAiOauth")] public OauthCreds? ClaudeAiOauth { get; set; }
    }

    private sealed class StateFile
    {
        [JsonPropertyName("oauthAccount")] public OauthAccount? OauthAccount { get; set; }
    }

    /// <summary>
    /// Só os campos de identidade. O <c>~/.claude.json</c> tem dezenas de outros (histórico de
    /// projetos, preferências, avisos já vistos) e nenhum interessa aqui — o que não está
    /// declarado nem chega a virar objeto.
    /// </summary>
    private sealed class OauthAccount
    {
        [JsonPropertyName("emailAddress")] public string? EmailAddress { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("fullName")] public string? FullName { get; set; }
        [JsonPropertyName("organizationName")] public string? OrganizationName { get; set; }
        [JsonPropertyName("organizationRole")] public string? OrganizationRole { get; set; }
    }

    private sealed class OauthCreds
    {
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
        [JsonPropertyName("subscriptionType")] public string? SubscriptionType { get; set; }
    }

    private sealed class UsageResponse
    {
        [JsonPropertyName("five_hour")] public Window? FiveHour { get; set; }
        [JsonPropertyName("seven_day")] public Window? SevenDay { get; set; }
        [JsonPropertyName("seven_day_sonnet")] public Window? SevenDaySonnet { get; set; }
        [JsonPropertyName("limits")] public List<LimitEntry>? Limits { get; set; }
    }

    private sealed class Window
    {
        [JsonPropertyName("utilization")] public double Utilization { get; set; }
        [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
    }

    private sealed class LimitEntry
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("percent")] public double? Percent { get; set; }
        [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
        [JsonPropertyName("scope")] public LimitScope? Scope { get; set; }
    }

    private sealed class LimitScope
    {
        [JsonPropertyName("model")] public LimitModel? Model { get; set; }
    }

    private sealed class LimitModel
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    }
}
