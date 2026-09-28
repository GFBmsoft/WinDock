using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinDock.Services;

/// <summary>Um pacote do winget com versão nova esperando.</summary>
public sealed record WingetPackage(string Name, string Id, string Current, string Available);

/// <summary>
/// O que há para atualizar nesta máquina: o que o Windows Update já sabe e o que o winget vê.
///
/// Os dois vêm juntos porque a pergunta é uma só — "tem coisa para atualizar?" —, e são contados
/// separados porque as respostas se resolvem em lugares diferentes.
/// </summary>
public sealed record UpdateStatus(
    int Windows,
    IReadOnlyList<WingetPackage> Winget,
    DateTime When,
    string? WindowsError = null,
    string? WingetError = null)
{
    public static readonly UpdateStatus Empty =
        new(0, Array.Empty<WingetPackage>(), DateTime.MinValue);

    public int Total => Windows + Winget.Count;
    public bool Any => Total > 0;
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

    public static async Task<UpdateStatus> ReadAsync()
    {
        var (windows, erroWindows) = await Task.Run(ReadWindows).ConfigureAwait(false);
        var (pacotes, erroWinget) = await Task.Run(ReadWinget).ConfigureAwait(false);

        return new UpdateStatus(windows, pacotes, DateTime.Now, erroWindows, erroWinget);
    }

    // ── Windows Update ──────────────────────────────────────

    /// <summary>
    /// Quantas atualizações o agente do Windows Update tem para esta máquina.
    ///
    /// Pela automação COM do próprio agente (<c>Microsoft.Update.Session</c>), e com
    /// <c>Online = false</c>: assim a busca responde do que o Windows já varreu, em milissegundos e
    /// sem tocar na rede. Com <c>true</c> a mesma chamada sai para os servidores da Microsoft e
    /// pode levar minutos — tempo demais para um ícone de barra, e uma ida à rede que a pessoa não
    /// pediu. O que o Windows sabe é o que ele vai instalar: é essa a informação que o ícone
    /// precisa dar.
    ///
    /// A ligação é por <c>dynamic</c> porque não há interop referenciado: o projeto não carrega
    /// dependência externa, e uma DLL de interop para três chamadas seria mais peso do que vale.
    /// </summary>
    private static (int Count, string? Error) ReadWindows()
    {
        try
        {
            var tipo = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (tipo is null) return (0, "o agente do Windows Update não respondeu");

            dynamic? sessao = Activator.CreateInstance(tipo);
            if (sessao is null) return (0, "o agente do Windows Update não respondeu");

            dynamic busca = sessao.CreateUpdateSearcher();
            busca.Online = false;

            dynamic resultado = busca.Search("IsInstalled=0 and Type='Software' and IsHidden=0");
            int quantas = resultado.Updates.Count;

            Log.Trace($"atualizações: Windows Update tem {quantas}");
            return (quantas, null);
        }
        catch (Exception ex)
        {
            // acontece de verdade: políticas de empresa desligam o agente, e em máquina sem
            // atualização configurada o COM responde com erro em vez de zero
            Log.Trace("atualizações: Windows Update não respondeu — " + ex.Message);
            return (0, "não deu para perguntar ao Windows Update");
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
            Log.Trace($"atualizações: winget tem {pacotes.Count}");
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
    /// </summary>
    internal static IReadOnlyList<WingetPackage> Parse(string saida)
    {
        var linhas = saida.Replace("\r", "").Split('\n');
        var pacotes = new List<WingetPackage>();

        int[]? colunas = null;
        string? cabecalho = null;

        foreach (var linha in linhas)
        {
            if (colunas is null)
            {
                // a régua de hifens: o cabeçalho é a linha logo antes dela
                if (linha.StartsWith("---", StringComparison.Ordinal) && cabecalho is not null)
                {
                    colunas = Columns(cabecalho);
                    if (colunas.Length < 4) return pacotes;   // tabela que não é a de upgrade
                }
                else if (linha.Trim().Length > 0)
                {
                    cabecalho = linha;
                }

                continue;
            }

            if (linha.Trim().Length == 0) break;   // acabou a tabela; o que vem depois é o resumo

            var nome = Field(linha, colunas, 0);
            var id = Field(linha, colunas, 1);
            var atual = Field(linha, colunas, 2);
            var nova = Field(linha, colunas, 3);

            if (nome.Length == 0 || nova.Length == 0) continue;

            pacotes.Add(new WingetPackage(nome, id, atual, nova));
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
    /// Abre um terminal com o <c>winget upgrade --all</c> pronto para rodar.
    ///
    /// Num terminal, e não escondido: a atualização faz perguntas (aceitar um contrato, fechar um
    /// programa que está aberto), pede elevação em alguns pacotes e pode demorar. Rodar isso sem
    /// janela seria a dock mexendo na máquina às escuras.
    /// </summary>
    public static void UpgradeAll()
    {
        Log.Write("atualizações: terminal aberto com winget upgrade --all");
        Start(new ProcessStartInfo("cmd.exe", "/k winget upgrade --all") { UseShellExecute = true });
    }

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
