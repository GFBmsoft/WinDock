using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <param name="Plano">
/// O plano como o cartão mostra ("Max 5x"), do <c>.claude.json</c>; nulo quando ele não diz.
/// </param>
public sealed record AiProfile(string Id, string Pasta, string? Nome, string? Email,
                               string? Organizacao, string? Papel, string? Plano = null)
{
    /// <summary>O nome que a pessoa reconhece: o dela, senão a conta, senão a pasta.</summary>
    public string Titulo => !string.IsNullOrWhiteSpace(Nome) ? Nome!
                          : !string.IsNullOrWhiteSpace(Email) ? Email!
                          : Id;
}

/// <summary>O que se leu de uma conta.</summary>
/// <param name="Erro">
/// Por que não deu certo. Com <paramref name="Gauges"/>, por que eles são de uma leitura anterior.
/// </param>
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
///
/// <para><b>A vez de perguntar é combinada com o ctop e o GTerm</b>, pelo arquivo que eles já
/// dividiam (<see cref="AiUsageShared"/>): o número que um deles leu há menos de
/// <see cref="Fresh"/> vale para a dock, e o castigo de um <c>429</c> vale para os três. Esse
/// arquivo é a única coisa que este serviço grava, e ele não é do Claude Code: só tem os
/// números.</para>
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
    /// (<c>~/.claude.json</c>), ao lado da pasta. Os dois lugares são lidos, e quando os dois
    /// existem vale o gravado por último.</para>
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

        // **Os dois lugares podem existir ao mesmo tempo, e aí vale o gravado por último.** Em
        // 07/10/2026 a pasta padrão desta máquina tinha um `.claude.json` dentro dela, parado
        // desde a véspera e sem o tipo da organização, ao lado do `~/.claude.json` vivo, que
        // dizia "claude_max". Pegar "o de dentro primeiro" achava o parado, ficava sem plano e
        // caía no "pro" da credencial — veja `NomeDoPlano` para o porquê de ela não servir.
        var lidos = new[] { Path.Combine(pasta, ".claude.json"), Path.Combine(Home, $"{id}.json") }
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(c => Ler(c, id, pasta))
            .Where(p => p is not null)
            .ToList();

        if (lidos.Count == 0) return new AiProfile(id, pasta, null, null, null, null);

        var perfil = lidos[0]!;
        if (perfil.Plano is not null) return perfil;

        // o mais novo não diz o plano: vale o do outro, desde que seja da mesma conta
        var plano = lidos.Skip(1).FirstOrDefault(p => p!.Plano is not null &&
                        string.Equals(p.Email, perfil.Email, StringComparison.OrdinalIgnoreCase))?.Plano;
        return perfil with { Plano = plano };
    }

    /// <summary>O perfil de um <c>.claude.json</c>; nulo quando ele não traz conta ou não abre.</summary>
    private static AiProfile? Ler(string caminho, string id, string pasta)
    {
        try
        {
            var carimbo = File.GetLastWriteTimeUtc(caminho);
            if (_identidades.TryGetValue(caminho, out var guardada) && guardada.Quando == carimbo)
                return guardada.Perfil is null ? null : guardada.Perfil with { Id = id, Pasta = pasta };

            using var fluxo = new FileStream(caminho, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            var oauth = JsonSerializer.Deserialize<StateFile>(fluxo, Json)?.OauthAccount;

            // `fullName` é o nome completo e `displayName` costuma ser o primeiro nome; o
            // cartão é estreito, então o curto vem primeiro quando existe.
            var perfil = oauth is null ? null
                       : new AiProfile(id, pasta,
                                       Primeiro(oauth.DisplayName, oauth.FullName),
                                       oauth.EmailAddress, oauth.OrganizationName,
                                       oauth.OrganizationRole,
                                       NomeDoPlano(oauth.OrganizationType, oauth.OrganizationRateLimitTier));

            _identidades[caminho] = (carimbo, perfil);
            return perfil;
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA: não deu para ler a conta em {id} ({ex.GetType().Name})");
            return null;
        }
    }

    private static readonly Dictionary<string, (DateTime Quando, AiProfile? Perfil)> _identidades = new();

    private static string? Primeiro(params string?[] valores) =>
        valores.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// O plano, do jeito que se lê: "claude_max" com "default_claude_max_5x" vira "Max 5x".
    ///
    /// <para><b>Vem do <c>.claude.json</c>, e não da credencial, porque a credencial mente
    /// depois de uma troca de plano.</b> O <c>subscriptionType</c> do
    /// <c>.credentials.json</c> é gravado no login e sobrevive às renovações do token: em
    /// 05/10/2026 a conta padrão desta máquina já era Max 5x havia dias, o token tinha sido
    /// renovado naquela manhã, e o campo seguia dizendo "pro". O perfil do
    /// <c>.claude.json</c> é rebuscado pelo Claude Code a cada uso, e trazia o plano
    /// certo.</para>
    /// </summary>
    private static string? NomeDoPlano(string? tipo, string? faixa)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return null;

        var nome = tipo.StartsWith("claude_", StringComparison.OrdinalIgnoreCase) ? tipo[7..] : tipo;
        nome = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(nome.Replace('_', ' '));

        // o multiplicador é o fim da faixa ("…_max_5x", "…_max_20x"); as outras não têm um
        var vezes = System.Text.RegularExpressions.Regex.Match(faixa ?? string.Empty, @"_(\d+x)$");
        return vezes.Success ? $"{nome} {vezes.Groups[1].Value}" : nome;
    }

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
    /// <param name="consultar">
    /// Falso para só olhar o arquivo comum, sem ir à rede: é a volta de minuto em minuto, que
    /// traz para o cartão o número que o ctop ou o GTerm acabaram de ler. Nesse caso, quando
    /// nada mudou, volta o próprio <see cref="Current"/> — o mesmo objeto, para a barra não se
    /// redesenhar à toa.
    /// </param>
    public async Task<AiUsage> ReadAsync(IReadOnlyCollection<string> escolhidas, bool consultar = true,
                                         CancellationToken cancel = default)
    {
        var perfis = Profiles();
        if (escolhidas.Count > 0)
            perfis = perfis.Where(p => escolhidas.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        // em paralelo: são idas à rede independentes, e enfileirá-las faria o cartão de duas
        // contas demorar o dobro do de uma
        var leituras = await Task.WhenAll(perfis.Select(p => LerAsync(p, consultar, cancel))).ConfigureAwait(false);

        if (!consultar && leituras.All(l => !l.Novo) &&
            Current.Contas.Select(c => c.Perfil.Id).SequenceEqual(perfis.Select(p => p.Id)))
            return Current;

        var resultado = new AiUsage(leituras.Select(l => l.Leitura).ToList(), DateTime.Now);
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

        /// <summary>A resposta da API que deu <see cref="Ultima"/>, inteira: é ela que vai para o arquivo comum.</summary>
        public JsonObject? Bruto;

        public DateTime EsperarAte;
        public string? Porque;

        /// <summary>O que se devolveu da última vez — é o que a volta sem rede repete quando nada mudou.</summary>
        public AiAccountUsage? Devolvida;

        /// <summary><see cref="Devolvida"/> com as janelas que já zeraram em 0%: o que o cartão viu por último.</summary>
        public AiAccountUsage? Mostrada;

        /// <summary>
        /// A data do arquivo de credenciais quando a API a deu por vencida; nula com a credencial
        /// valendo. Mudou, é o Claude Code que renovou.
        /// </summary>
        public DateTime? Vencida;
    }

    private readonly Dictionary<string, Estado> _estados = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quem manda o pedido à API. Só os testes trocam, por uma resposta de mentira.</summary>
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _enviar;

    public AiUsageService() : this(null) { }

    internal AiUsageService(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? enviar) =>
        _enviar = enviar ?? ((pedido, cancel) => Http.SendAsync(pedido, cancel));

    /// <summary>
    /// Quanto uma leitura vale antes de valer a pena perguntar de novo — a nossa ou a de outro
    /// programa, no arquivo comum.
    ///
    /// **Isto existe porque a API cortou.** Cada abertura do cartão pedia uma leitura por
    /// conta, e abrir e fechar o cartão algumas vezes seguidas — que é o que se faz ao testar
    /// qualquer coisa nele — virou quatro pedidos em 34 segundos e um <c>429 Too Many
    /// Requests</c> (log de 24/09, 16:03). O cartão já abre com o que tem; perguntar de novo
    /// só faz sentido quando o número teve tempo de mudar, e cota não muda em segundos. São os
    /// mesmos dois minutos do GTerm, para que entre os dois saia uma pergunta só.
    /// </summary>
    internal static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Quanto se espera depois de um <c>429</c>, quando o servidor não diz quanto esperar.
    ///
    /// Cinco minutos, os mesmos do ctop e do GTerm: a espera vai para o arquivo comum e vale
    /// para os três, então ela precisa ser uma só. O cartão continua mostrando os últimos
    /// números bons nesse intervalo, então a espera não deixa ninguém sem informação.
    /// </summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Quanto se espera depois de um erro que não é o <c>429</c> (rede fora, login recusado):
    /// um minuto, como o GTerm, só para não insistir no mesmo segundo.
    /// </summary>
    private static readonly TimeSpan BackoffCurto = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A leitura de uma conta: do arquivo comum quando alguém leu há pouco, da API quando é a
    /// vez da dock, e respeitando a espera depois de um erro — a nossa ou a de outro programa.
    ///
    /// <para><b>E uma leitura que falha nunca apaga os números que já havia.</b> É a mesma
    /// regra da bandeja, aprendida lá: o cartão mostrar um "a API respondeu 429" no lugar das
    /// barras é pior que mostrar as barras de dois minutos atrás com um aviso. Sem isto, o
    /// primeiro corte da API deixava o cartão inteiro vazio — que foi exatamente o que se viu
    /// quando ele apareceu.</para>
    /// </summary>
    /// <returns>A leitura, e se ela trouxe número novo.</returns>
    internal async Task<(AiAccountUsage Leitura, bool Novo)> LerAsync(AiProfile perfil, bool consultar,
                                                                       CancellationToken cancel)
    {
        Estado estado;
        lock (_estados)
        {
            if (!_estados.TryGetValue(perfil.Id, out estado!)) _estados[perfil.Id] = estado = new Estado();
        }

        var (leitura, novo) = await LerDaVezAsync(estado, perfil, consultar, cancel).ConfigureAwait(false);

        // Uma janela que já zerou não tem mais aquele número, venha ele de onde vier: é o que
        // fazia a dock abrir de manhã com os 5 horas de ontem (18/11/2026). E zerar muda o que
        // se mostra sem ninguém ter lido nada — daí o "novo", senão a volta sem rede devolveria
        // a leitura de antes e a barra seguiria com o número que já não vale.
        var vigente = Vigente(leitura, DateTime.Now);
        if (estado.Mostrada is { } antes && !antes.Gauges.SequenceEqual(vigente.Gauges)) novo = true;
        estado.Mostrada = vigente;
        return (vigente, novo);
    }

    /// <summary>
    /// A leitura com as janelas que já zeraram em 0%. O prazo fica, para o cartão dizer quando
    /// zerou.
    ///
    /// <para>Zero é o que a janela tinha ao zerar, e não o que ela tem agora: o que se usou
    /// depois só uma leitura nova diz. Mas é o único número verdadeiro que se tem dela — o de
    /// antes é de outra janela —, e o aviso de leitura velha continua embaixo das barras.</para>
    /// </summary>
    internal static AiAccountUsage Vigente(AiAccountUsage leitura, DateTime agora)
    {
        if (!leitura.Gauges.Any(g => g.ResetaEm <= agora && g.Percent > 0)) return leitura;

        return leitura with
        {
            Gauges = leitura.Gauges.Select(g => g.ResetaEm <= agora ? g with { Percent = 0 } : g).ToList()
        };
    }

    /// <summary>
    /// Quando o arquivo de credenciais foi gravado pela última vez. É por ele que a dock sabe que
    /// o Claude Code renovou o token — ela nunca lê o token para isso.
    /// </summary>
    private static DateTime CarimboDaCredencial(AiProfile perfil)
    {
        try { return File.GetLastWriteTimeUtc(Path.Combine(perfil.Pasta, ".credentials.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private async Task<(AiAccountUsage Leitura, bool Novo)> LerDaVezAsync(Estado estado, AiProfile perfil,
                                                                          bool consultar, CancellationToken cancel)
    {
        var agora = DateTime.Now;

        // A credencial vencida não espera os dez minutos da próxima pergunta. Ela vence de noite
        // (dura oito horas) e o Claude Code a renova no primeiro uso do dia: sem isto, a dock que
        // abriu antes dele ficava até dez minutos com o número de ontem, mesmo com o token novo
        // já no arquivo (18/11/2026: leu às 07:49:07, o token chegou às 07:50:40).
        if (!consultar && estado.Vencida is { } vista && CarimboDaCredencial(perfil) != vista)
        {
            Log.Trace($"cota de IA ({perfil.Id}): o arquivo de credenciais mudou, lendo de novo");
            consultar = true;
        }

        // os números guardados valem, mas a identidade é a de agora: quem trocou de plano ou
        // de login não espera a próxima ida à rede para ver o cabeçalho mudar
        if (estado.Ultima is not null) estado.Ultima = estado.Ultima with { Perfil = perfil };

        // 1. o que o ctop ou o GTerm leram desta conta depois da nossa última leitura
        var comum = AiUsageShared.Ler(perfil.Id);
        var novo = Aproveitar(estado, perfil, comum);

        if (estado.Ultima is { } guardada && agora - estado.LidaEm < Fresh)
            return (Devolver(estado, Envelhecida(guardada, estado.LidaEm, null)), novo);

        // 2. alguém levou um não da API há pouco — nós ou outro programa — e a espera vale
        var doArquivo = comum?.ProximaTentativa ?? DateTime.MinValue;
        var esperarAte = doArquivo > estado.EsperarAte ? doArquivo : estado.EsperarAte;
        if (agora < esperarAte)
        {
            var porque = (doArquivo >= estado.EsperarAte ? comum?.Erro : estado.Porque) ?? "a conta limitou as consultas";
            return (Devolver(estado, estado.Ultima is { } velha
                        ? Envelhecida(velha, estado.LidaEm, porque)
                        : new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), null,
                                             $"{porque} — tentando de novo às {esperarAte:HH:mm}")),
                    novo);
        }

        // 3. a volta sem rede para aqui: o número que havia continua valendo, sem aviso — nada
        // falhou, só não é a vez de perguntar
        if (!consultar)
        {
            if (novo && estado.Ultima is { } trazida)
                return (Devolver(estado, Envelhecida(trazida, estado.LidaEm, null)), true);

            return (estado.Devolvida is { } repetida ? repetida with { Perfil = perfil }
                    : new AiAccountUsage(perfil, AiUsageState.Unknown, Array.Empty<AiGauge>(), null), false);
        }

        // 4. a vez é da dock
        var consulta = await ConsultarAsync(perfil, cancel).ConfigureAwait(false);
        var leitura = consulta.Leitura;
        var lida = DateTime.Now;

        if (leitura.State == AiUsageState.Expired)
        {
            if (estado.Vencida is null)
                Log.Write($"cota de IA ({perfil.Id}): credencial vencida; a leitura volta quando o Claude Code renovar");
            estado.Vencida = CarimboDaCredencial(perfil);
        }

        if (leitura.State == AiUsageState.Ok)
        {
            estado.Ultima = leitura;
            estado.LidaEm = lida;
            estado.Bruto = consulta.Bruto;
            estado.EsperarAte = default;
            estado.Porque = null;
            estado.Vencida = null;

            AiUsageShared.Gravar(perfil.Id, new AiUsageShared(lida, consulta.Bruto, lida, null));
            return (Devolver(estado, leitura), true);
        }

        // a API foi consultada e disse não: a espera vai para o arquivo, e vale para os outros
        if (consulta.Espera is { } espera)
        {
            // Quem leu enquanto a nossa pergunta estava no ar acertou, e o número dele vale mais
            // que o nosso não: gravar o erro por cima apagaria a leitura boa do outro.
            if (Aproveitar(estado, perfil, AiUsageShared.Ler(perfil.Id)) && estado.Ultima is { } dele)
                return (Devolver(estado, Envelhecida(dele, estado.LidaEm, null)), true);

            estado.EsperarAte = lida + espera;
            estado.Porque = consulta.ErroComum;

            // o número bom que houver segue no arquivo: o nosso, senão o que já estava lá
            var (em, dados) = estado.Bruto is not null
                              ? (estado.LidaEm, estado.Bruto)
                              : (comum?.Em ?? DateTime.MinValue, comum?.Dados);
            AiUsageShared.Gravar(perfil.Id, new AiUsageShared(em, dados, estado.EsperarAte, consulta.ErroComum));
            Log.Write($"cota de IA ({perfil.Id}): esperando {Math.Ceiling(espera.TotalMinutes):0} min antes de perguntar de novo");

            // sem números velhos para mostrar, o cartão fica só com esta frase — então ela
            // precisa dizer o que aconteceu e quando melhora, e não um número de protocolo
            if (consulta.Status == 429 && estado.Ultima is null)
                leitura = leitura with { Erro = $"muitas consultas seguidas — tentando de novo às {estado.EsperarAte:HH:mm}" };
        }

        return (Devolver(estado, estado.Ultima is { } antiga
                                 ? Envelhecida(antiga, estado.LidaEm, leitura.Erro)
                                 : leitura),
                false);
    }

    /// <summary>
    /// Toma para si o número do arquivo comum, se ele for mais novo que o da dock e trouxer
    /// alguma cota. Devolve se tomou.
    /// </summary>
    private static bool Aproveitar(Estado estado, AiProfile perfil, AiUsageShared? comum)
    {
        if (comum?.Dados is not { } dados || comum.Em <= estado.LidaEm) return false;

        var medidores = Medidores(dados);
        if (medidores.Count == 0) return false;

        // o plano da credencial só aparece quando o perfil não diz o dele (veja o AiCard); sem
        // leitura nossa anterior, ele vem do arquivo da credencial, que é pequeno
        var plano = estado.Ultima?.Plano ?? LerCredencial(perfil).Plano;

        estado.Ultima = new AiAccountUsage(perfil, AiUsageState.Ok, medidores, plano);
        estado.LidaEm = comum.Em;
        estado.Bruto = dados;

        // outro leu depois de nós com um token que valia: não há mais o que esperar do arquivo
        // de credenciais
        estado.Vencida = null;
        return true;
    }

    private static AiAccountUsage Devolver(Estado estado, AiAccountUsage leitura)
    {
        estado.Devolvida = leitura;
        return leitura;
    }

    /// <summary>Os números de antes, marcados com a hora em que foram lidos.</summary>
    private static AiAccountUsage Envelhecida(AiAccountUsage guardada, DateTime quando, string? erro) =>
        guardada with { Erro = erro, Desde = quando };

    /// <summary>O que uma ida à API deu.</summary>
    /// <param name="Bruto">A resposta inteira, quando deu certo — vai para o arquivo comum.</param>
    /// <param name="Espera">
    /// Quanto ninguém deve perguntar de novo, quando a API foi consultada e disse não; nulo
    /// quando nem se chegou a ela (sem credencial, sessão vencida no arquivo).
    /// </param>
    /// <param name="ErroComum">O porquê, nas palavras que o GTerm mostra no cartão dele.</param>
    private sealed record Consulta(AiAccountUsage Leitura, JsonObject? Bruto = null, TimeSpan? Espera = null,
                                   string? ErroComum = null, int Status = 0);

    /// <summary>O que a credencial diz: se dá para usar, o token e o plano.</summary>
    private readonly record struct Credencial(AiUsageState Estado, string? Token, string? Plano);

    private static Credencial LerCredencial(AiProfile perfil)
    {
        try
        {
            var credenciais = Path.Combine(perfil.Pasta, ".credentials.json");
            if (!File.Exists(credenciais)) return new Credencial(AiUsageState.NoCredentials, null, null);

            // O arquivo é do Claude Code, que pode estar gravando nele neste instante: a
            // leitura compartilhada evita a exceção de arquivo em uso, e uma leitura que
            // pegue o arquivo pela metade cai no catch e vira "não deu para ler" — a próxima
            // atualização acerta.
            using var fluxo = new FileStream(credenciais, FileMode.Open, FileAccess.Read,
                                             FileShare.ReadWrite | FileShare.Delete);
            var oauth = JsonSerializer.Deserialize<CredentialsFile>(fluxo, Json)?.ClaudeAiOauth;

            if (oauth is null || string.IsNullOrWhiteSpace(oauth.AccessToken))
                return new Credencial(AiUsageState.NoCredentials, null, null);

            // o plano é a única coisa que sai daqui além do token, e não é segredo
            var plano = oauth.SubscriptionType;

            // `expiresAt` vem em milissegundos desde 1970. Uma folga de um minuto evita
            // gastar uma ida à rede para receber 401 de um token que vence agora.
            if (oauth.ExpiresAt > 0 &&
                DateTimeOffset.FromUnixTimeMilliseconds(oauth.ExpiresAt) <= DateTimeOffset.UtcNow.AddMinutes(1))
                return new Credencial(AiUsageState.Expired, null, plano);

            return new Credencial(AiUsageState.Ok, oauth.AccessToken, plano);
        }
        catch (Exception ex)
        {
            // sem o texto da exceção, que poderia carregar conteúdo do arquivo: aqui só
            // interessa o tipo
            Log.Write($"cota de IA ({perfil.Id}): não deu para ler as credenciais ({ex.GetType().Name})");
            return new Credencial(AiUsageState.Failed, null, null);
        }
    }

    private async Task<Consulta> ConsultarAsync(AiProfile perfil, CancellationToken cancel)
    {
        var credencial = LerCredencial(perfil);
        var plano = credencial.Plano;

        // O porquê vai junto mesmo quando o cartão tem frase própria para o estado: com números
        // de antes na tela, ele é o aviso embaixo das barras. Sem ele, a sessão vencida mostrava
        // os números de ontem como se fossem de agora (18/11/2026).
        if (credencial.Estado != AiUsageState.Ok || credencial.Token is not { } token)
            return new Consulta(new AiAccountUsage(perfil, credencial.Estado, Array.Empty<AiGauge>(), plano,
                                                   credencial.Estado switch
                                                   {
                                                       AiUsageState.Failed => "não deu para ler as credenciais",
                                                       AiUsageState.Expired => "sessão expirada",
                                                       _ => "sem credencial"
                                                   }));

        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            pedido.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            pedido.Headers.Add("anthropic-beta", OauthBeta);
            pedido.Headers.UserAgent.ParseAdd("WinDock/1.0");

            using var resposta = await _enviar(pedido, cancel).ConfigureAwait(false);
            var status = (int)resposta.StatusCode;

            if (!resposta.IsSuccessStatusCode)
            {
                // 401 aqui quer dizer token recusado — na prática, vencido do lado de lá
                // mesmo que a data no arquivo diga que não.
                var estado = resposta.StatusCode == System.Net.HttpStatusCode.Unauthorized
                             ? AiUsageState.Expired : AiUsageState.Failed;

                // 429 é "pare de perguntar". O cabeçalho `Retry-After` diz por quanto tempo
                // quando o servidor se dá ao trabalho; quando não diz, vale o castigo fixo.
                // Quem guarda isso é o `LerAsync`, que decide o que fazer com a leitura inteira.
                var (espera, porque) = status switch
                {
                    429 => (Pedida(resposta) ?? Backoff, "a conta limitou as consultas"),
                    401 => (BackoffCurto, "login vencido: abra o Claude"),
                    _ => (BackoffCurto, "sem resposta da conta")
                };

                Log.Write($"cota de IA ({perfil.Id}): a API respondeu {status}");
                return new Consulta(new AiAccountUsage(perfil, estado, Array.Empty<AiGauge>(), plano,
                                                       $"a API respondeu {status}"),
                                    null, espera, porque, status);
            }

            var texto = await resposta.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            var bruto = JsonNode.Parse(texto) as JsonObject;

            var medidores = bruto is null ? Array.Empty<AiGauge>() : Medidores(bruto);
            if (medidores.Count == 0)
                return new Consulta(new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), plano,
                                                       "a resposta não trouxe nenhuma cota"));

            Log.Trace($"cota de IA ({perfil.Id}): " +
                      string.Join(" | ", medidores.Select(m => $"{m.Nome} {m.Percent:0}%")));
            return new Consulta(new AiAccountUsage(perfil, AiUsageState.Ok, medidores, plano), bruto, Status: status);
        }
        catch (Exception ex)
        {
            Log.Write($"cota de IA ({perfil.Id}): falha ao consultar a API ({ex.GetType().Name})");
            return new Consulta(new AiAccountUsage(perfil, AiUsageState.Failed, Array.Empty<AiGauge>(), plano,
                                                   "não deu para falar com a API"),
                                null, BackoffCurto, "sem resposta da conta");
        }
    }

    /// <summary>Quanto a API pediu para esperar, quando pediu algo que faça sentido.</summary>
    private static TimeSpan? Pedida(HttpResponseMessage resposta)
    {
        var pedido = resposta.Headers.RetryAfter?.Delta
                     ?? (resposta.Headers.RetryAfter?.Date is { } quando ? quando - DateTimeOffset.Now : null);

        return pedido is { } d && d > TimeSpan.Zero && d < TimeSpan.FromHours(2) ? d : null;
    }

    /// <summary>Os medidores de uma resposta da API — a recém-chegada ou a do arquivo comum.</summary>
    private static IReadOnlyList<AiGauge> Medidores(JsonObject dados)
    {
        try
        {
            return Montar(dados.Deserialize<UsageResponse>(Json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return Array.Empty<AiGauge>();
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
        [JsonPropertyName("organizationType")] public string? OrganizationType { get; set; }
        [JsonPropertyName("organizationRateLimitTier")] public string? OrganizationRateLimitTier { get; set; }
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
