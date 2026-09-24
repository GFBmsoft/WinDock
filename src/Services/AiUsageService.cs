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

/// <summary>Em que pé está a leitura de uma conta — é o que decide o que o cartão mostra dela.</summary>
public enum AiUsageState
{
    /// <summary>Ainda não se tentou ler nesta sessão.</summary>
    Unknown,

    /// <summary>A pasta não tem credencial utilizável.</summary>
    NoCredentials,

    /// <summary>A credencial existe mas venceu. Veja <see cref="AiUsageService"/> para o porquê de não renovarmos.</summary>
    Expired,

    /// <summary>Deu certo.</summary>
    Ok,

    /// <summary>Rede fora, servidor recusou, resposta que não se entende.</summary>
    Failed
}

/// <summary>
/// Uma conta do Claude Code nesta máquina: a pasta de configuração dela e quem está logado.
///
/// <para><b>Por que há mais de uma.</b> O Claude Code guarda uma conta por vez, mas a variável
/// <c>CLAUDE_CONFIG_DIR</c> permite apontá-lo para outra pasta — é assim que se mantém a conta
/// pessoal e a da empresa lado a lado, cada uma com o seu login. Nesta máquina são
/// <c>~/.claude</c> e <c>~/.claude-bm</c>.</para>
/// </summary>
/// <param name="Id">O nome da pasta (".claude", ".claude-bm"). É o que a configuração guarda.</param>
/// <param name="Pasta">O caminho completo dela.</param>
public sealed record AiProfile(string Id, string Pasta, string? Nome, string? Email,
                               string? Organizacao, string? Papel)
{
    /// <summary>O nome que a pessoa reconhece: o dela, senão a conta, senão a pasta.</summary>
    public string Titulo => !string.IsNullOrWhiteSpace(Nome) ? Nome!
                          : !string.IsNullOrWhiteSpace(Email) ? Email!
                          : Id;
}

/// <summary>O que se leu de uma conta.</summary>
/// <param name="Erro">Por que não deu certo — só quando não há <paramref name="Gauges"/>.</param>
/// <param name="Desde">
/// Quando estes números foram lidos, se eles vieram de uma leitura anterior porque a de agora
/// falhou. Nulo quer dizer recém-lidos.
/// </param>
public sealed record AiAccountUsage(AiProfile Perfil, AiUsageState State,
                                    IReadOnlyList<AiGauge> Gauges, string? Plano,
                                    string? Erro = null, DateTime? Desde = null);

/// <summary>O que o cartão precisa saber, junto.</summary>
public sealed record AiUsage(IReadOnlyList<AiAccountUsage> Contas, DateTime Quando);

/// <summary>
/// Quanto da cota do Claude já foi usada — o que o Claude Code mostra no <c>/usage</c>.
///
/// <para><b>De onde vêm os números.</b> Do mesmo lugar de onde o próprio Claude Code os tira:
/// <c>GET https://api.anthropic.com/api/oauth/usage</c>, autenticado com o token OAuth que o
/// Claude Code guarda em <c>&lt;pasta&gt;\.credentials.json</c>. Não há API pública para isto —
/// o endpoint não é documentado, e pode mudar ou sumir sem aviso. Quando isso acontecer, o
/// cartão passa a dizer que não conseguiu ler; nada mais no WinDock depende dele.</para>
///
/// <para><b>Este serviço nunca escreve nessas pastas, e é uma decisão, não um
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

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A pasta padrão, a que o Claude Code usa sem <c>CLAUDE_CONFIG_DIR</c>.</summary>
    private const string DefaultId = ".claude";

    // ── as contas que existem nesta máquina ──────────────────

    /// <summary>
    /// As contas do Claude Code encontradas aqui, na ordem: a padrão primeiro, depois as
    /// outras em ordem alfabética.
    ///
    /// <para><b>Como se acha cada uma.</b> Toda pasta <c>~/.claude*</c> que tenha um
    /// <c>.credentials.json</c> dentro é uma conta, mais a que <c>CLAUDE_CONFIG_DIR</c>
    /// apontar. A identidade está num <c>.claude.json</c> cujo lugar **muda com o caso**, e
    /// isso foi medido nesta máquina: para uma pasta alternativa ele fica **dentro** dela
    /// (<c>~/.claude-bm/.claude.json</c>); para a padrão, fica no home
    /// (<c>~/.claude.json</c>), ao lado da pasta. Os dois lugares são tentados, nessa ordem.</para>
    /// </summary>
    public static IReadOnlyList<AiProfile> Profiles()
    {
        var pastas = new List<string>();

        try
        {
            var padrao = Path.Combine(Home, DefaultId);
            if (TemCredencial(padrao)) pastas.Add(padrao);

            foreach (var dir in Directory.EnumerateDirectories(Home, ".claude*").OrderBy(d => d))
                if (!pastas.Contains(dir) && TemCredencial(dir)) pastas.Add(dir);

            // a variável pode apontar para fora do home, e aí a varredura acima não a acha
            var apontada = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(apontada))
            {
                var cheio = Path.GetFullPath(apontada);
                if (!pastas.Contains(cheio) && TemCredencial(cheio)) pastas.Add(cheio);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA: não deu para procurar as contas do Claude Code ({ex.GetType().Name})");
        }

        return pastas.Select(Identidade).ToList();
    }

    private static bool TemCredencial(string pasta) =>
        File.Exists(Path.Combine(pasta, ".credentials.json"));

    /// <summary>
    /// Quem está logado numa pasta, do <c>.claude.json</c> dela.
    ///
    /// Guardado entre leituras e conferido pela data do arquivo: ele tem centenas de KB (traz
    /// o histórico de projetos junto) e reler isso a cada dez minutos seria pagar o parse
    /// inteiro por um punhado de campos — mas quem troca de conta precisa ver o cartão mudar
    /// sem reiniciar a dock, e a data do arquivo responde isso de graça.
    ///
    /// Falhar aqui não impede o cartão: sem estes campos ele mostra a pasta e os medidores,
    /// que são o que ele existe para mostrar.
    /// </summary>
    private static AiProfile Identidade(string pasta)
    {
        var id = Path.GetFileName(pasta.TrimEnd(Path.DirectorySeparatorChar));

        // o de dentro primeiro: é onde ele fica quando a pasta veio do CLAUDE_CONFIG_DIR
        var caminho = new[] { Path.Combine(pasta, ".claude.json"), Path.Combine(Home, $"{id}.json") }
            .FirstOrDefault(File.Exists);

        if (caminho is null) return new AiProfile(id, pasta, null, null, null, null);

        try
        {
            var carimbo = File.GetLastWriteTimeUtc(caminho);
            if (_identidades.TryGetValue(caminho, out var guardada) && guardada.Quando == carimbo)
                return guardada.Perfil with { Id = id, Pasta = pasta };

            using var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            var oauth = JsonSerializer.Deserialize<StateFile>(fluxo, Json)?.OauthAccount;

            // `fullName` é o nome completo e `displayName` costuma ser o primeiro nome; o
            // cartão é estreito, então o curto vem primeiro quando existe.
            var perfil = new AiProfile(id, pasta,
                                       Primeiro(oauth?.DisplayName, oauth?.FullName),
                                       oauth?.EmailAddress, oauth?.OrganizationName,
                                       oauth?.OrganizationRole);

            _identidades[caminho] = (carimbo, perfil);
            return perfil;
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA: não deu para ler a conta em {id} ({ex.GetType().Name})");
            return new AiProfile(id, pasta, null, null, null, null);
        }
    }

    private static readonly Dictionary<string, (DateTime Quando, AiProfile Perfil)> _identidades = new();

    private static string? Primeiro(params string?[] valores) =>
        valores.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Há alguma conta do Claude Code aqui? É o que decide se o ícone aparece na barra.
    /// </summary>
    public static bool Installed => Profiles().Count > 0;

    // ── ler ──────────────────────────────────────────────────

    /// <summary>A última leitura, para o cartão abrir com algo enquanto a nova não chega.</summary>
    public AiUsage Current { get; private set; } = new(Array.Empty<AiAccountUsage>(), DateTime.MinValue);

    /// <summary>
    /// Lê a cota das contas pedidas. Roda fora da thread da interface — é rede, e rede trava.
    /// </summary>
    /// <param name="escolhidas">
    /// Os <see cref="AiProfile.Id"/> que a pessoa quer ver. Vazio quer dizer **todas as que
    /// existirem**: é o que faz o cartão funcionar sem configuração nenhuma, e o que faz uma
    /// conta nova aparecer sozinha quando ela é criada.
    /// </param>
    public async Task<AiUsage> ReadAsync(IReadOnlyCollection<string> escolhidas,
                                         CancellationToken cancel = default)
    {
        var perfis = Profiles();
        if (escolhidas.Count > 0)
            perfis = perfis.Where(p => escolhidas.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        // em paralelo: são idas à rede independentes, e enfileirá-las faria o cartão de duas
        // contas demorar o dobro do de uma
        var leituras = await Task.WhenAll(perfis.Select(p => ComCacheAsync(p, cancel))).ConfigureAwait(false);

        var resultado = new AiUsage(leituras, DateTime.Now);
        Current = resultado;
        return resultado;
    }

    /// <summary>
    /// O que se sabe de uma conta entre leituras: os últimos números bons e até quando não
    /// vale insistir.
    /// </summary>
    private sealed class Estado
    {
        public AiAccountUsage? Ultima;
        public DateTime LidaEm;
        public DateTime EsperarAte;
    }

    private static readonly Dictionary<string, Estado> Estados = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Quanto uma leitura vale antes de valer a pena perguntar de novo.
    ///
    /// **Isto existe porque a API cortou.** Cada abertura do cartão pedia uma leitura por
    /// conta, e abrir e fechar o cartão algumas vezes seguidas — que é o que se faz ao testar
    /// qualquer coisa nele — virou quatro pedidos em 34 segundos e um <c>429 Too Many
    /// Requests</c> (log de 24/09, 16:03). O cartão já abre com o que tem; perguntar de novo
    /// só faz sentido quando o número teve tempo de mudar, e cota não muda em segundos.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Quanto se espera depois de um <c>429</c>, quando o servidor não diz quanto esperar.
    ///
    /// Generoso de propósito: quem cortou fomos nós, e insistir cedo é pedir para ser cortado
    /// de novo — e por mais tempo. O cartão continua mostrando os últimos números bons nesse
    /// intervalo, então a espera não deixa ninguém sem informação.
    /// </summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A leitura de uma conta, respeitando a validade da anterior e o castigo do 429.
    ///
    /// <para><b>E uma leitura que falha nunca apaga os números que já havia.</b> É a mesma
    /// regra da bandeja, aprendida lá: o cartão mostrar um "a API respondeu 429" no lugar das
    /// barras é pior que mostrar as barras de dois minutos atrás com um aviso. Sem isto, o
    /// primeiro corte da API deixava o cartão inteiro vazio — que foi exatamente o que se viu
    /// quando ele apareceu.</para>
    /// </summary>
    private async Task<AiAccountUsage> ComCacheAsync(AiProfile perfil, CancellationToken cancel)
    {
        Estado estado;
        lock (Estados)
        {
            if (!Estados.TryGetValue(perfil.Id, out estado!)) Estados[perfil.Id] = estado = new Estado();
        }

        var agora = DateTime.Now;

        if (estado.Ultima is { } guardada)
        {
            if (agora - estado.LidaEm < Fresh) return Envelhecida(guardada, estado.LidaEm, null);

            if (agora < estado.EsperarAte)
                return Envelhecida(guardada, estado.LidaEm, "a API pediu para esperar");
        }
        else if (agora < estado.EsperarAte)
        {
            // nem números velhos existem: o castigo continua valendo, mas é preciso dizer algo
            return new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), null,
                                      "a API pediu para esperar um pouco");
        }

        var leitura = await LerContaAsync(perfil, cancel).ConfigureAwait(false);

        if (leitura.State == AiUsageState.Ok)
        {
            estado.Ultima = leitura;
            estado.LidaEm = agora;
            estado.EsperarAte = default;
            return leitura;
        }

        // falhou: guarda o castigo quando foi a API que pediu, e devolve o que já se tinha
        if (leitura.State == AiUsageState.Failed && _ultimoRetry.TryGetValue(perfil.Id, out var espera))
        {
            estado.EsperarAte = agora + espera;
            _ultimoRetry.Remove(perfil.Id);
            Log.Write($"cota de IA ({perfil.Id}): esperando {espera.TotalMinutes:0} min antes de perguntar de novo");

            // sem números velhos para mostrar, o cartão fica só com esta frase — então ela
            // precisa dizer o que aconteceu e quando melhora, e não um número de protocolo
            if (estado.Ultima is null)
                leitura = leitura with { Erro = $"muitas consultas seguidas — tentando de novo às {estado.EsperarAte:HH:mm}" };
        }

        return estado.Ultima is { } antiga
               ? Envelhecida(antiga, estado.LidaEm, leitura.Erro)
               : leitura;
    }

    /// <summary>Os números de antes, marcados com a hora em que foram lidos.</summary>
    private static AiAccountUsage Envelhecida(AiAccountUsage guardada, DateTime quando, string? erro) =>
        guardada with { Erro = erro, Desde = quando };

    /// <summary>Quanto a API pediu para esperar, por conta — preenchido ao ver um 429.</summary>
    private static readonly Dictionary<string, TimeSpan> _ultimoRetry = new(StringComparer.OrdinalIgnoreCase);

    private async Task<AiAccountUsage> LerContaAsync(AiProfile perfil, CancellationToken cancel)
    {
        string token;
        string? plano;

        try
        {
            var credenciais = Path.Combine(perfil.Pasta, ".credentials.json");
            if (!File.Exists(credenciais))
                return new AiAccountUsage(perfil, AiUsageState.NoCredentials, Array.Empty<AiGauge>(), null);

            // O arquivo é do Claude Code, que pode estar gravando nele neste instante: a
            // leitura compartilhada evita a exceção de arquivo em uso, e uma leitura que
            // pegue o arquivo pela metade cai no catch e vira "não deu para ler" — a próxima
            // atualização acerta.
            await using var fluxo = new FileStream(credenciais, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete);
            var oauth = (await JsonSerializer.DeserializeAsync<CredentialsFile>(fluxo, Json, cancel)
                                             .ConfigureAwait(false))?.ClaudeAiOauth;

            if (oauth is null || string.IsNullOrWhiteSpace(oauth.AccessToken))
                return new AiAccountUsage(perfil, AiUsageState.NoCredentials, Array.Empty<AiGauge>(), null);

            // o plano é a única coisa que sai daqui além do token, e não é segredo
            plano = oauth.SubscriptionType;

            // `expiresAt` vem em milissegundos desde 1970. Uma folga de um minuto evita
            // gastar uma ida à rede para receber 401 de um token que vence agora.
            if (oauth.ExpiresAt > 0 &&
                DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt) <= DateTimeOffset.UtcNow.AddMinutes(1))
                return new AiAccountUsage(perfil, AiUsageState.Expired, Array.Empty<AiGauge>(), plano);

            token = oauth.AccessToken;
        }
        catch (Exception ex)
        {
            // sem o texto da exceção, que poderia carregar conteúdo do arquivo: aqui só
            // interessa o tipo
            Log.Write($"cota de IA ({perfil.Id}): não deu para ler as credenciais ({ex.GetType().Name})");
            return new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), null,
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

                // 429 é "pare de perguntar". O cabeçalho `Retry-After` diz por quanto tempo
                // quando o servidor se dá ao trabalho; quando não diz, vale o nosso castigo
                // fixo. Quem guarda isso é o `ComCacheAsync`, que decide o que fazer com a
                // leitura inteira.
                if (resposta.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var pedido429 = resposta.Headers.RetryAfter?.Delta
                                    ?? (resposta.Headers.RetryAfter?.Date is { } quando
                                        ? quando - DateTimeOffset.Now : null);

                    _ultimoRetry[perfil.Id] = pedido429 is { } d && d > TimeSpan.Zero && d < TimeSpan.FromHours(2)
                                              ? d : Backoff;
                }

                Log.Write($"cota de IA ({perfil.Id}): a API respondeu {(int)resposta.StatusCode}");
                return new AiAccountUsage(perfil, estado, Array.Empty<AiGauge>(), plano,
                                          $"a API respondeu {(int)resposta.StatusCode}");
            }

            await using var corpo = await resposta.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            var uso = await JsonSerializer.DeserializeAsync<UsageResponse>(corpo, Json, cancel)
                                          .ConfigureAwait(false);

            var medidores = Montar(uso);
            if (medidores.Count == 0)
                return new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), plano,
                                          "a resposta não trouxe nenhuma cota");

            Log.Trace($"cota de IA ({perfil.Id}): " +
                      string.Join(" | ", medidores.Select(m => $"{m.Nome} {m.Percent:0}%")));
            return new AiAccountUsage(perfil, AiUsageState.Ok, medidores, plano);
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA ({perfil.Id}): falha ao consultar a API ({ex.GetType().Name})");
            return new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), plano,
                                      "não deu para falar com a API");
        }
    }

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
    /// Só os campos de identidade. O <c>.claude.json</c> tem dezenas de outros (histórico de
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
