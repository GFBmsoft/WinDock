using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinDock.Services;

/// <summary>
/// Um pacote do winget com versão nova esperando.
///
/// O <see cref="Explicit"/> marca os que o winget lista numa segunda tabela, sob a frase "exigem
/// uma segmentação explícita para atualização". Esses **não são tocados pelo <c>upgrade --all</c>**
/// — e é por isso que eles importam: sem essa marca, o cartão prometia resolver com um botão algo
/// que aquele botão não resolve, e o pacote reaparecia na lista seguinte, para sempre.
/// </summary>
public sealed record WingetPackage(string Name, string Id, string Current, string Available,
                                   bool Explicit = false)
{
    /// <summary>Como este pacote é identificado no silêncio. O id quando existe; o nome, se não.</summary>
    public string Key => Id.Length > 0 ? Id : Name;

    /// <summary>O que o "Atualizar tudo" faz com ele — ou não faz.</summary>
    public string Hint => Explicit
        ? "O winget não atualiza este no \"Atualizar tudo\" — precisa de comando próprio"
        : $"{Name} {Current} → {Available}";
}

/// <summary>
/// O que há para atualizar nesta máquina: o que o Windows Update já sabe e o que o winget vê.
///
/// Os dois vêm juntos porque a pergunta é uma só — "tem coisa para atualizar?" —, e são contados
/// separados porque as respostas se resolvem em lugares diferentes.
/// </summary>
public sealed record UpdateStatus(
    IReadOnlyList<string> Windows,
    IReadOnlyList<WingetPackage> Winget,
    DateTime When,
    string? WindowsError = null,
    string? WingetError = null)
{
    public static readonly UpdateStatus Empty =
        new(Array.Empty<string>(), Array.Empty<WingetPackage>(), DateTime.MinValue);

    /// <summary>
    /// Os pacotes que a pessoa mandou calar — encontrados, mas fora da conta.
    ///
    /// Existem porque há pacote que o winget oferece e nunca atualiza: o Discord anuncia uma versão
    /// nova, o instalador dele se atualiza sozinho por fora, e o número que fica registrado não é o
    /// que o winget espera. O ícone então acende todo dia por algo que nenhum comando resolve. Ficam
    /// guardados aqui, e não jogados fora, para o cartão poder dizer quantos são e devolvê-los.
    /// </summary>
    public IReadOnlyList<WingetPackage> Silenced { get; init; } = Array.Empty<WingetPackage>();

    public int Total => Windows.Count + Winget.Count;
    public bool Any => Total > 0;

    /// <summary>
    /// Reparte a lista do winget conforme quem está calado, sem perguntar nada de novo.
    ///
    /// É o que faz o "✕" numa linha apagar o ícone no mesmo instante: a consulta custa segundos, a
    /// resposta já está na mão, e o que mudou foi só de que lado da linha cada pacote está.
    /// </summary>
    public UpdateStatus WithSilenced(IReadOnlyCollection<string> keys)
    {
        var todos = Winget.Concat(Silenced)
                          .OrderBy(p => p.Name, StringComparer.CurrentCulture)
                          .ToList();

        bool Calado(WingetPackage p) => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase);

        return this with
        {
            Winget = todos.Where(p => !Calado(p)).ToList(),
            Silenced = todos.Where(Calado).ToList()
        };
    }

    /// <summary>
    /// Tira da lista o que o winget acabou de atualizar sem erro — um pacote, ou todos os que o
    /// <c>--all</c> alcança (os que exigem alvo explícito ficam).
    ///
    /// É a conta antecipada que faz o ícone mudar no instante em que o winget devolve o controle.
    /// A releitura que vem atrás confirma, e corrige se algum pacote não tiver saído de fato.
    /// </summary>
    public UpdateStatus WithoutUpgraded(string? id) => this with
    {
        Winget = Winget.Where(p => id is null
                                   ? p.Explicit
                                   : !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                       .ToList()
    };

    /// <summary>Alguma das duas perguntas não pôde ser feita — o que é diferente de não haver nada.</summary>
    public bool Failed => WindowsError is not null || WingetError is not null;

    /// <summary>O que deu errado, numa frase só, para o rodapé do cartão.</summary>
    public string? Error => WindowsError is not null && WingetError is not null
        ? $"{WindowsError}; {WingetError}"
        : WindowsError ?? WingetError;
}

/// <summary>
/// Procura o que está esperando para ser atualizado, sem instalar nada.
///
/// **Ler, e nunca instalar**, é a regra desta classe: atualização se aplica com a máquina no
/// estado que a pessoa escolher, e uma dock que começasse a instalar coisas por conta própria
/// seria o tipo de programa que se desinstala. O cartão leva a pessoa ao Windows Update ou abre
/// um terminal com o comando pronto — quem dá o Enter é ela.
/// </summary>
public static class UpdatesService
{
    /// <summary>Se o winget existe nesta máquina. Sem ele, só resta o Windows Update.</summary>
    public static bool HasWinget => _winget ??= Find("winget.exe") is not null;
    private static bool? _winget;

    /// <summary>
    /// Pergunta às duas fontes.
    ///
    /// O <paramref name="online"/> escolhe qual pergunta se faz ao Windows Update: a rápida, que
    /// responde do cache do agente em ~2,7 s, ou a de verdade, que vai à rede em ~11,8 s. As duas
    /// existem porque servem a momentos diferentes — veja <see cref="ReadWindows"/>.
    ///
    /// O <paramref name="winget"/> evita repetir a consulta do winget quando ela já foi feita há
    /// segundos: é um processo de console, e a lista dele não muda entre as duas fases de uma
    /// mesma conferência.
    /// </summary>
    public static async Task<UpdateStatus> ReadAsync(bool online = true,
                                                     IReadOnlyList<WingetPackage>? winget = null,
                                                     IReadOnlyCollection<string>? silenced = null)
    {
        var (windows, erroWindows) = await Task.Run(() => ReadWindows(online)).ConfigureAwait(false);

        string? erroWinget = null;
        if (winget is null) (winget, erroWinget) = await Task.Run(ReadWinget).ConfigureAwait(false);

        var status = new UpdateStatus(windows, winget, DateTime.Now, erroWindows, erroWinget);
        return silenced is null || silenced.Count == 0 ? status : status.WithSilenced(silenced);
    }

    // ── Windows Update ──────────────────────────────────────

    /// <summary>
    /// O que o Windows Update tem para esta máquina, pelo título de cada atualização.
    ///
    /// Pela automação COM do próprio agente (<c>Microsoft.Update.Session</c>), com
    /// <c>Online = true</c> — **a busca de verdade, a mesma que a tela de configurações faz**.
    ///
    /// Começou só com <c>false</c>, que responde do que o agente já varreu, e o argumento parecia
    /// bom: milissegundos, sem tocar na rede, e "o que o Windows já sabe é o que ele vai
    /// instalar". Em 28/09/2026 isso falhou exatamente como tinha de falhar — o cartão disse
    /// "nada esperando", a tela do Windows Update encontrou atualização de segurança, e a mesma
    /// consulta local passou a achá-la **depois**, porque a busca da tela é que encheu o cache. Um
    /// ícone que só sabe o que outra tela já descobriu não poupa a outra tela: ele depende dela.
    ///
    /// O preço medido é 11,8 s contra 2,7 s. As duas continuam existindo porque servem a momentos
    /// diferentes: **depois de instalar**, o agente já sabe o que sumiu, e a pergunta rápida
    /// responde certo — é o que faz o ícone apagar em três segundos em vez de doze. A pergunta
    /// lenta é a que descobre o que ainda não se sabe, e essa ninguém espera: roda em thread de
    /// fundo.
    ///
    /// A ligação é por <c>dynamic</c> porque não há interop referenciado: o projeto não carrega
    /// dependência externa, e uma DLL de interop para três chamadas seria mais peso do que vale.
    /// </summary>
    private static (IReadOnlyList<string> Titles, string? Error) ReadWindows(bool online)
    {
        try
        {
            var tipo = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (tipo is null) return (Array.Empty<string>(), "o agente do Windows Update não respondeu");

            dynamic? sessao = Activator.CreateInstance(tipo);
            if (sessao is null) return (Array.Empty<string>(), "o agente do Windows Update não respondeu");

            dynamic busca = sessao.CreateUpdateSearcher();
            busca.Online = online;

            dynamic resultado = busca.Search("IsInstalled=0 and Type='Software' and IsHidden=0");

            // os títulos, e não só a contagem: é o que faz o cartão bater com a tela do Windows.
            // Sem eles, "3 atualizações do Windows" não diz se é o cumulativo do mês ou a definição
            // do Defender que se instala sozinha em dois minutos
            var titulos = new List<string>();
            foreach (dynamic u in resultado.Updates) titulos.Add((string)u.Title);

            Log.Trace($"atualizações: Windows Update tem {titulos.Count} ({(online ? "busca online" : "cache do agente")})");
            return (titulos, null);
        }
        catch (Exception ex)
        {
            // acontece de verdade: políticas de empresa desligam o agente, e em máquina sem
            // atualização configurada o COM responde com erro em vez de zero
            Log.Trace("atualizações: Windows Update não respondeu — " + ex.Message);
            return (Array.Empty<string>(), "não deu para perguntar ao Windows Update");
        }
    }

    // ── winget ──────────────────────────────────────────────

    /// <summary>
    /// A lista do <c>winget upgrade</c>.
    ///
    /// Sem <c>--include-unknown</c> de propósito: com ele o winget acrescenta uma segunda tabela,
    /// de pacotes cuja versão instalada ele não consegue determinar, e essa lista não é acionável —
    /// atualizar por ali costuma reinstalar o que já está instalado.
    /// </summary>
    private static (IReadOnlyList<WingetPackage> Packages, string? Error) ReadWinget()
    {
        if (!HasWinget) return (Array.Empty<WingetPackage>(), null);

        try
        {
            var saida = Run("winget", "upgrade --accept-source-agreements --disable-interactivity",
                            TimeSpan.FromMinutes(2));
            if (saida is null) return (Array.Empty<WingetPackage>(), "o winget demorou demais");

            var pacotes = Parse(saida);
            var teimosos = pacotes.Count(p => p.Explicit);
            Log.Trace($"atualizações: winget tem {pacotes.Count}" +
                      (teimosos > 0 ? $" ({teimosos} exigindo alvo explícito)" : string.Empty));
            return (pacotes, null);
        }
        catch (Exception ex)
        {
            Log.Trace("atualizações: winget não respondeu — " + ex.Message);
            return (Array.Empty<WingetPackage>(), "não deu para perguntar ao winget");
        }
    }

    /// <summary>
    /// Tira os pacotes da tabela que o winget imprime.
    ///
    /// A tabela é texto formatado para gente, e traduzido: os títulos das colunas e a linha de
    /// resumo mudam com o idioma do Windows. O que não muda é a forma — cabeçalho, régua de
    /// hifens, uma linha por pacote, linha em branco, resumo — e o alinhamento em colunas.
    ///
    /// Daí a leitura ser **pelas posições do cabeçalho**, e não por "separado por dois ou mais
    /// espaços": um nome que enche a coluna inteira deixa um espaço só antes do id, e a separação
    /// por brancos juntaria os dois num campo só ("Microsoft PowerShell Microsoft.PowerShell").
    /// As posições saem das palavras do próprio cabeçalho, quaisquer que sejam elas — é por isso
    /// que o idioma não entra nesta conta.
    ///
    /// **São duas tabelas, e não uma.** Depois da lista normal o winget imprime outra, sob uma
    /// frase terminada em dois-pontos, com os pacotes que "exigem uma segmentação explícita para
    /// atualização" — os que o <c>upgrade --all</c> pula. A leitura parava na primeira linha em
    /// branco, o que dava dois erros de uma vez: quando havia a lista normal, os da segunda tabela
    /// sumiam do cartão; quando não havia (a máquina em dia, só o Discord teimando), a segunda
    /// tabela era lida **como se fosse a primeira**, e o cartão oferecia um botão que não ia
    /// resolvê-la. Agora as duas são lidas, e a de baixo vem marcada.
    ///
    /// O que separa uma da outra é os dois-pontos no fim da frase que antecede o cabeçalho — a
    /// forma da frase é a mesma em qualquer idioma, e o texto dela não entra na conta.
    /// </summary>
    internal static IReadOnlyList<WingetPackage> Parse(string saida)
    {
        var linhas = saida.Replace("\r", "").Split('\n');
        var pacotes = new List<WingetPackage>();

        int[]? colunas = null;
        string? cabecalho = null;
        string? anterior = null;
        var explicito = false;

        foreach (var linha in linhas)
        {
            if (colunas is null)
            {
                // a régua de hifens: o cabeçalho é a linha logo antes dela
                if (linha.StartsWith("---", StringComparison.Ordinal) && cabecalho is not null)
                {
                    colunas = Columns(cabecalho);
                    if (colunas.Length < 4) { colunas = null; cabecalho = null; continue; }

                    var frase = anterior?.Trim() ?? string.Empty;
                    explicito = frase.Length > 20 && frase.EndsWith(':');
                }
                else if (linha.Trim().Length > 0)
                {
                    anterior = cabecalho;
                    cabecalho = linha;
                }

                continue;
            }

            // acabou esta tabela. Pode vir outra depois — e é justamente a que interessa marcar
            if (linha.Trim().Length == 0)
            {
                colunas = null;
                cabecalho = null;
                anterior = null;
                continue;
            }

            var nome = Field(linha, colunas, 0);
            var id = Field(linha, colunas, 1);
            var atual = Field(linha, colunas, 2);
            var nova = Field(linha, colunas, 3);

            if (nome.Length == 0 || nova.Length == 0) continue;

            pacotes.Add(new WingetPackage(nome, id, atual, nova, explicito));
        }

        return pacotes;
    }

    /// <summary>Onde começa cada coluna, pelas palavras do cabeçalho.</summary>
    private static int[] Columns(string cabecalho) =>
        System.Text.RegularExpressions.Regex.Matches(cabecalho, @"\S+")
            .Select(m => m.Index)
            .ToArray();

    /// <summary>
    /// O pedaço da linha que pertence a uma coluna.
    ///
    /// A linha pode acabar antes da coluna — é o pacote sem origem, instalado fora do winget —,
    /// e nesse caso o campo é vazio em vez de erro.
    /// </summary>
    private static string Field(string linha, int[] colunas, int i)
    {
        if (i >= colunas.Length) return string.Empty;

        var inicio = colunas[i];
        if (inicio >= linha.Length) return string.Empty;

        var fim = i + 1 < colunas.Length ? Math.Min(colunas[i + 1], linha.Length) : linha.Length;
        return linha[inicio..fim].Trim();
    }

    // ── abrir o que resolve ─────────────────────────────────

    /// <summary>A página do Windows Update, onde se instala o que ele achou.</summary>
    public static void OpenWindowsUpdate() =>
        Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true });

    /// <summary>
    /// Atualiza pelo winget sem abrir janela nenhuma, e diz o que aconteceu.
    ///
    /// **Esta classe começou com uma regra: ler, e nunca instalar.** O terminal aberto era a forma
    /// de a pessoa ver o que ia acontecer e dar o Enter. O que o uso mostrou é que o terminal
    /// custava mais do que protegia: ele aparece por cima do trabalho, fica aberto depois de
    /// acabar (o <c>/k</c>), e — o pior — **a dock não sabia quando a atualização terminou**. O
    /// ícone ficava aceso até a reconferência de dois minutos, e a pessoa acabava clicando em
    /// "Conferir agora" para limpá-lo. Rodando a atualização aqui dentro, o fim é um evento: a
    /// conferência sai no mesmo instante em que o winget devolve o controle.
    ///
    /// O que sustentava a regra continua valendo em outra forma: a dock **só instala quando alguém
    /// clica**, o que falhou aparece no cartão em vez de sumir com a janela, e o
    /// <see cref="UpgradeAllInTerminal"/> continua ali para quando a pessoa quiser ver o processo
    /// inteiro.
    ///
    /// Os sinalizadores não são enfeite: sem <c>--accept-*-agreements</c> e
    /// <c>--disable-interactivity</c>, o winget para esperando uma resposta que ninguém vai ver, e
    /// o processo fica pendurado até o prazo estourar. O <c>--silent</c> pede ao instalador de
    /// cada pacote que não abra o assistente dele — alguns ignoram, e é por isso que o código do
    /// winget é lido no fim em vez de suposto.
    /// </summary>
    /// <param name="id">Um pacote só, ou nulo para todos.</param>
    public static async Task<string?> UpgradeAsync(string? id = null)
    {
        if (!HasWinget) return "o winget não está instalado nesta máquina";

        if (id is not null && !IsPackageId(id))
        {
            Log.Write($"atualizações: id fora do formato esperado, nada foi feito — {id}");
            return "esse pacote tem um id que não dá para passar ao winget";
        }

        var alvo = id is null ? "--all" : $"--exact --id {id}";
        var args = $"upgrade {alvo} --silent --accept-package-agreements " +
                   "--accept-source-agreements --disable-interactivity";

        Log.Write($"atualizações: winget {args}");

        var (saida, codigo) = await Task.Run(() => RunFull("winget", args, TimeSpan.FromMinutes(30)))
                                        .ConfigureAwait(false);

        if (codigo == 0)
        {
            Log.Write("atualizações: winget terminou sem erro");
            return null;
        }

        var motivo = LastLine(saida) ?? $"o winget terminou com o código 0x{codigo:X8}";
        Log.Write($"atualizações: winget falhou ({codigo}) — {motivo}");
        return motivo;
    }

    /// <summary>
    /// A última linha com conteúdo da saída do winget — é onde ele escreve o que deu errado.
    ///
    /// Vale mais do que o número: "o instalador falhou com código de saída 1602" (alguém cancelou)
    /// e "nenhuma versão aplicável encontrada" são coisas diferentes, e o número sozinho não conta
    /// nenhuma das duas.
    /// </summary>
    private static string? LastLine(string? saida)
    {
        if (string.IsNullOrWhiteSpace(saida)) return null;

        var linha = saida.Replace("\r", "").Split('\n')
                         .LastOrDefault(l => l.Trim().Length > 0)?.Trim();

        return linha is null || linha.Length == 0 ? null
             : linha.Length <= 160 ? linha : linha[..160] + "…";
    }

    /// <summary>
    /// Abre um terminal com o <c>winget upgrade --all</c>, e deixa a janela aberta no fim.
    ///
    /// É a saída para quando a atualização silenciosa falha: um instalador que insiste em abrir o
    /// assistente dele, um pacote que quer uma resposta. Ali a pessoa vê o processo inteiro e
    /// responde o que for preciso.
    /// </summary>
    public static void UpgradeAllInTerminal()
    {
        Log.Write("atualizações: terminal aberto com winget upgrade --all");
        Start(new ProcessStartInfo("cmd.exe", "/k winget upgrade --all") { UseShellExecute = true });
    }

    /// <summary>Um id de pacote do winget, e nada que uma linha de comando leia como comando.</summary>
    private static bool IsPackageId(string id) =>
        id.Length > 0 && id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '+');

    private static void Start(ProcessStartInfo info)
    {
        try { Process.Start(info); }
        catch (Exception ex) { Log.Write("atualizações: não deu para abrir", ex); }
    }

    // ── utilidades ──────────────────────────────────────────

    /// <summary>Roda um programa de console e devolve a saída, ou nulo se ele passou do prazo.</summary>
    private static string? Run(string exe, string args, TimeSpan prazo)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        p.Start();

        var saida = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit((int)prazo.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* já morreu */ }
            return null;
        }

        return saida;
    }

    /// <summary>
    /// Roda um programa de console e devolve a saída **e o código**, que é o que diz se deu certo.
    ///
    /// As duas saídas são lidas por eventos, e não por <c>ReadToEnd</c> em sequência: uma
    /// atualização de meia hora enche o buffer de uma delas, o programa para esperando alguém ler,
    /// e quem está esperando o fim espera para sempre. É o travamento clássico desta API, e aqui
    /// ele seria a dock inteira dizendo "atualizando…" até o prazo estourar.
    /// </summary>
    private static (string Saida, int Codigo) RunFull(string exe, string args, TimeSpan prazo)
    {
        var texto = new StringBuilder();

        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        void Junta(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (texto) texto.AppendLine(e.Data);
        }

        p.OutputDataReceived += Junta;
        p.ErrorDataReceived += Junta;

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit((int)prazo.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* já morreu */ }
                return ("a atualização passou de trinta minutos e foi interrompida", -1);
            }

            // sem argumento, espera também o fim da leitura das saídas — as últimas linhas, que
            // são justamente as que contam o que deu errado, chegam depois da saída do processo
            p.WaitForExit();

            lock (texto) return (texto.ToString(), p.ExitCode);
        }
        catch (Exception ex)
        {
            Log.Write("atualizações: não deu para rodar o winget", ex);
            return ("não deu para rodar o winget nesta máquina", -1);
        }
    }

    /// <summary>Acha um executável no PATH — é assim que se sabe se o winget existe aqui.</summary>
    private static string? Find(string exe)
    {
        var caminhos = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();

        foreach (var dir in caminhos)
        {
            if (dir.Length == 0) continue;
            try
            {
                var cheio = Path.Combine(dir, exe);
                if (File.Exists(cheio)) return cheio;
            }
            catch { /* uma entrada torta do PATH não derruba a busca */ }
        }

        return null;
    }
}
