using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinDock.Services;

/// <summary>Um arquivo anexado à Release.</summary>
public sealed record ReleaseAsset(string Name, long Size, string Url);

/// <summary>A Release mais recente do WinDock no GitHub.</summary>
public sealed record WinDockRelease(string Tag, string Url, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// A dock se atualizando pela Release do GitHub — o mesmo esquema do GRepos.
///
/// O Windows não deixa sobrescrever um .exe em execução, mas deixa **renomeá-lo**: é nisso que
/// a troca se apoia, e é o que dispensa um .bat esperando a dock fechar. A sequência é atual →
/// <c>.old</c>, novo → atual, reabre, e o <c>.old</c> some na abertura seguinte. O caminho do
/// executável não muda, então a tarefa de logon continua apontando para o lugar certo.
/// </summary>
public static class SelfUpdateService
{
    public const string Repo = "GFBmsoft/WinDock";

    /// <summary>Sufixo do executável antigo, apagado na abertura seguinte.</summary>
    public const string OldSuffix = ".old";

    /// <summary>
    /// O argumento com que a dock nova é aberta pela troca: ela espera a velha largar a instância
    /// única, em vez de concluir que já há uma dock e sair.
    /// </summary>
    public const string AfterUpdateArg = "--depois-de-atualizar";

    /// <summary>
    /// Uma consulta por dia. A API sem login aceita sessenta por hora por endereço, e a cota é
    /// dividida com tudo o mais nesta máquina — o GRepos, o navegador, o terminal.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinDock", "1.0"));
        return http;
    }

    /// <summary>
    /// A versão em execução, como o build carimbou: <c>1.0.0.32</c>, ou <c>1.0.0.32-dev.3</c> com
    /// commits depois da tag. O "+hash" que o SDK às vezes acrescenta fica de fora.
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?
                               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                               .InformationalVersion ?? string.Empty;
            return info.Split('+')[0];
        }
    }

    /// <summary>
    /// A tag é mais nova que a versão em uso? Compara só os quatro números: um
    /// <c>1.0.0.32-dev.5</c> foi compilado **depois** da 1.0.0.32, então não está atrás dela.
    /// Sem versão carimbada (<c>1.0.0.0</c>, build sem git) nunca há o que oferecer.
    /// </summary>
    public static bool IsNewer(string current, string tag)
    {
        var atual = Numbers(current);
        var nova = Numbers(tag);
        return atual is not null && nova is not null && atual > new Version(0, 0, 0, 0) && nova > atual;
    }

    private static Version? Numbers(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        var limpo = texto.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        return limpo.Split('.').Length == 4 && Version.TryParse(limpo, out var v) ? v : null;
    }

    /// <summary>A Release mais recente, ou <c>null</c> quando a pergunta não deu certo.</summary>
    public static async Task<WinDockRelease?> LatestAsync()
    {
        try
        {
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", prazo.Token)
                                       .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // 403 é a cota da API esgotada; 404, nenhuma Release publicada
                Log.Write($"nova versão: o GitHub respondeu {(int)resp.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(prazo.Token).ConfigureAwait(false));
            var raiz = doc.RootElement;

            var arquivos = new List<ReleaseAsset>();
            if (raiz.TryGetProperty("assets", out var lista))
                foreach (var a in lista.EnumerateArray())
                    arquivos.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? string.Empty,
                                                  a.GetProperty("size").GetInt64(),
                                                  a.GetProperty("browser_download_url").GetString() ?? string.Empty));

            return new WinDockRelease(raiz.GetProperty("tag_name").GetString() ?? string.Empty,
                                      raiz.GetProperty("html_url").GetString() ?? string.Empty,
                                      arquivos);
        }
        catch (Exception ex)
        {
            // sem rede, a atualização é só uma conveniência que fica para amanhã
            Log.Trace("nova versão: não deu para perguntar ao GitHub — " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Esta dock leva o .NET dentro dela? Num executável self-contained o runtime sai da própria
    /// pasta do app; no que depende do .NET instalado, sai de <c>dotnet\shared</c>. É o que decide
    /// qual dos dois arquivos da Release baixar: trocar um pelo outro funcionaria, mas o standalone
    /// é várias vezes maior, e o outro não abriria numa máquina sem o runtime.
    /// </summary>
    public static bool IsSelfContained =>
        !RuntimeEnvironment.GetRuntimeDirectory()
                           .Contains(@"\shared\Microsoft.NETCore.App\", StringComparison.OrdinalIgnoreCase);

    /// <summary>O arquivo da Release no mesmo sabor desta dock.</summary>
    public static ReleaseAsset? PickAsset(WinDockRelease release)
    {
        var nome = IsSelfContained ? $"WinDock-{release.Tag}-standalone.exe" : $"WinDock-{release.Tag}.exe";
        return release.Assets.FirstOrDefault(a => string.Equals(a.Name, nome, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A troca só vale num executável de arquivo único e numa pasta gravável. Num build de pasta,
    /// trocar só o .exe deixaria as DLLs ao lado desencontradas; numa pasta sem permissão a
    /// renomeação falharia no meio. Nos dois casos resta abrir a página da Release.
    /// </summary>
    public static bool CanSwap(out string exe)
    {
        exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0) return false;

        // num arquivo único o assembly de entrada não tem .dll em disco para apontar
        if (!string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location)) return false;

        var pasta = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(pasta)) return false;

        try
        {
            // permissão no Windows não se deduz pelo caminho: descobre-se gravando
            var teste = Path.Combine(pasta, ".windock-" + Path.GetRandomFileName());
            using (File.Create(teste)) { }
            File.Delete(teste);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Baixa o arquivo para um temporário ao lado do executável — na mesma unidade, porque a troca
    /// é por renomeação, e renomear não cruza volumes.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, string folder, IProgress<double>? progress)
    {
        if (!asset.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("a Release aponta para um endereço que não é HTTPS");

        var destino = Path.Combine(folder, asset.Name + ".baixando");
        if (File.Exists(destino)) File.Delete(destino);

        using (var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var origem = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var saida = File.Create(destino);

            var buffer = new byte[81920];
            long lido = 0;
            int n;
            while ((n = await origem.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await saida.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                lido += n;
                if (total > 0) progress?.Report((double)lido / total);
            }
        }

        // sem assinatura para conferir, o tamanho anunciado é a checagem que resta — e pega o
        // download cortado, que é a falha provável
        var baixado = new FileInfo(destino).Length;
        if (asset.Size > 0 && baixado != asset.Size)
        {
            File.Delete(destino);
            throw new InvalidOperationException($"o download veio incompleto ({baixado:N0} de {asset.Size:N0} bytes)");
        }

        return destino;
    }

    /// <summary>
    /// Põe o novo no lugar do atual. O atual vira <c>.old</c> em vez de ser apagado: ele está em
    /// execução, e é também o caminho de volta se algo falhar no meio.
    /// </summary>
    public static void Swap(string exe, string downloaded)
    {
        var antigo = exe + OldSuffix;
        if (File.Exists(antigo)) File.Delete(antigo);

        File.Move(exe, antigo);
        try
        {
            File.Move(downloaded, exe);
        }
        catch (Exception)
        {
            File.Move(antigo, exe);   // melhor a versão antiga que nenhuma
            throw;
        }
    }

    /// <summary>Abre a dock nova, avisada de que a velha ainda está saindo.</summary>
    public static void Relaunch(string exe) =>
        Process.Start(new ProcessStartInfo(exe, AfterUpdateArg) { UseShellExecute = true });

    /// <summary>
    /// Apaga o executável deixado pela troca anterior. Roda na abertura, quando ele já não está em
    /// uso; se ainda estiver preso, fica para a próxima.
    /// </summary>
    public static void CleanOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var antigo = exe + OldSuffix;
            if (File.Exists(antigo)) File.Delete(antigo);
        }
        catch (Exception)
        {
            // ainda preso; some na próxima abertura
        }
    }

    public static void OpenReleasePage(string? url) =>
        Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(url)
            ? $"https://github.com/{Repo}/releases/latest"
            : url) { UseShellExecute = true });
}
