using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Que arquivos da pasta entram no papel de parede da dock.</summary>
public enum WallpaperKind
{
    /// <summary>Só os que se mexem: vídeo e GIF, tocados atrás dos ícones.</summary>
    Animated,
    /// <summary>Só imagens paradas, postas como o papel de parede do próprio Windows.</summary>
    Static,
    /// <summary>Os dois, misturados na rotação.</summary>
    All,
}

/// <summary>
/// As contas do papel de parede que não dependem de janela nenhuma: quais arquivos contam, a ordem
/// da rotação, quanto da área de trabalho está à mostra e a montagem dos quadros de um GIF. Ficam
/// separadas para o <c>tests/WallpaperCheck</c> conferir sem abrir a dock.
/// </summary>
internal static class WallpaperRules
{
    /// <summary>
    /// Vídeo que o Media Foundation do Windows abre sozinho, e o GIF, que a dock converte em vídeo.
    /// O .webm depende da extensão de VP9 da Loja; sem ela o arquivo falha ao abrir e a rotação
    /// segue para o próximo.
    /// </summary>
    public static readonly IReadOnlySet<string> AnimatedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".webm", ".gif" };

    /// <summary>O que o Windows aceita como papel de parede.</summary>
    public static readonly IReadOnlySet<string> StaticExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".tif", ".tiff" };

    public static bool IsAnimated(string path) => AnimatedExtensions.Contains(Path.GetExtension(path));
    public static bool IsStatic(string path) => StaticExtensions.Contains(Path.GetExtension(path));
    public static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string path, WallpaperKind kind) => kind switch
    {
        WallpaperKind.Animated => IsAnimated(path),
        WallpaperKind.Static => IsStatic(path),
        _ => IsAnimated(path) || IsStatic(path),
    };

    /// <summary>Os arquivos do tipo pedido, em ordem alfabética do nome — a ordem da rotação.</summary>
    public static List<string> Filter(IEnumerable<string> files, WallpaperKind kind) =>
        files.Where(f => Matches(f, kind))
             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
             .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>
    /// O próximo da rotação. Em ordem, o seguinte ao atual (e o primeiro, se o atual saiu da
    /// lista). Na aleatória, qualquer um que não seja o atual — repetir o mesmo seria uma troca
    /// que ninguém vê.
    /// </summary>
    public static string? Next(IReadOnlyList<string> list, string? current, bool shuffle, Random random)
    {
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        var i = IndexOf(list, current);
        if (!shuffle) return list[i < 0 ? 0 : (i + 1) % list.Count];

        var sorteio = random.Next(i < 0 ? list.Count : list.Count - 1);
        return list[i >= 0 && sorteio >= i ? sorteio + 1 : sorteio];
    }

    public static int IndexOf(IReadOnlyList<string> list, string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Que fração de <paramref name="area"/> nenhuma janela cobre, por uma grade de pontos.
    ///
    /// Grade, e não a conta exata de regiões, de propósito: o mosaico deixa folgas de poucos
    /// pixels entre as janelas, e uma grade de ~80 px quase nunca cai nelas — que é o que se quer,
    /// porque uma fresta de 8 px mostrando o vídeo não vale o vídeo tocando.
    /// </summary>
    public static double VisibleFraction(RECT area, IReadOnlyList<RECT> covers, int columns = 32, int rows = 16)
    {
        if (area.Width <= 0 || area.Height <= 0) return 0;

        int livres = 0, total = 0;
        for (var r = 0; r < rows; r++)
        {
            var y = area.Top + (int)((r + 0.5) * area.Height / rows);
            for (var c = 0; c < columns; c++)
            {
                var x = area.Left + (int)((c + 0.5) * area.Width / columns);
                total++;
                var coberto = false;
                foreach (var w in covers)
                    if (x >= w.Left && x < w.Right && y >= w.Top && y < w.Bottom) { coberto = true; break; }
                if (!coberto) livres++;
            }
        }
        return (double)livres / total;
    }

    /// <summary>
    /// Abaixo disto a área de trabalho conta como coberta e o vídeo pausa. Na grade de 32×16 é
    /// menos de 11 pontos: uma tira estreita entre janelas, não um pedaço que se olhe.
    /// </summary>
    public const double MinVisible = 0.02;

    // ── GIF ──────────────────────────────────────────────────

    /// <summary>
    /// O tempo de um quadro do GIF, que vem em centésimos. Zero e um são lidos como 100 ms, como
    /// fazem os navegadores: há GIFs gravados assim esperando justamente isso, e levar o número a
    /// sério os faria correr dez vezes mais rápido do que quem fez viu.
    /// </summary>
    public static TimeSpan GifDelay(int centiseconds) =>
        TimeSpan.FromMilliseconds(centiseconds < 2 ? 100 : centiseconds * 10);

    /// <summary>
    /// Quantas voltas do GIF entram no vídeo para ele durar pelo menos <paramref name="minimum"/>.
    /// O laço do player tem uma emenda a cada volta do arquivo; repetido dentro do vídeo, um GIF de
    /// dois segundos emenda de tantos em tantos segundos, e não a cada dois.
    /// </summary>
    public static int Repetitions(TimeSpan loop, TimeSpan minimum)
    {
        if (loop <= TimeSpan.Zero) return 1;
        return Math.Clamp((int)Math.Ceiling(minimum.TotalMilliseconds / loop.TotalMilliseconds), 1, 500);
    }

    /// <summary>
    /// Pinta um quadro do GIF na tela montada (BGRA, linha de cima primeiro), na posição dele. O
    /// pixel transparente (alfa zero) deixa ver o que já estava: é assim que um quadro pequeno
    /// atualiza só um pedaço da imagem.
    /// </summary>
    public static void Paint(byte[] canvas, int canvasWidth, int canvasHeight,
                             byte[] frame, int x, int y, int width, int height)
    {
        // pixel a pixel como uint (BGRA em little-endian: o alfa é o byte de cima). A primeira
        // versão copiava cada pixel com Buffer.BlockCopy — 2,7 milhões de chamadas por quadro de
        // 2560×1080 — e a conversão de um GIF de 24 quadros levava 20 s
        var tela = MemoryMarshal.Cast<byte, uint>(canvas.AsSpan());
        var quadro = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        var x0 = Math.Max(0, -x);
        var x1 = Math.Min(width, canvasWidth - x);
        if (x1 <= x0) return;

        for (var fy = 0; fy < height; fy++)
        {
            var cy = y + fy;
            if (cy < 0 || cy >= canvasHeight) continue;
            var origem = quadro.Slice(fy * width, width);
            for (var fx = x0; fx < x1; fx++)
            {
                var p = origem[fx];
                if (p >> 24 != 0) tela[cy * canvasWidth + x + fx] = p;
            }
        }
    }

    /// <summary>O descarte "voltar ao fundo" (2): o retângulo do quadro volta a ser transparente.</summary>
    public static void Clear(byte[] canvas, int canvasWidth, int canvasHeight, int x, int y, int width, int height)
    {
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(canvasWidth, x + width);
        if (x1 <= x0) return;
        for (var cy = Math.Max(0, y); cy < Math.Min(canvasHeight, y + height); cy++)
            Array.Clear(canvas, (cy * canvasWidth + x0) * 4, (x1 - x0) * 4);
    }

    /// <summary>Uma cópia da tela com tudo opaco: o que ficou transparente vira preto.</summary>
    public static byte[] Opaque(byte[] canvas)
    {
        var q = (byte[])canvas.Clone();
        foreach (ref var p in MemoryMarshal.Cast<byte, uint>(q.AsSpan())) p |= 0xFF000000;
        return q;
    }

    /// <summary>
    /// O nome do vídeo convertido de um GIF: muda quando o arquivo muda (tamanho ou data), e com
    /// a versão da conversão, para uma conversão melhorada não usar o vídeo da antiga.
    /// </summary>
    public static string CacheName(string fullPath, long length, DateTime lastWriteUtc)
    {
        const string versao = "v1";
        var chave = $"{versao}|{fullPath.ToUpperInvariant()}|{length}|{lastWriteUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chave));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".mp4";
    }
}
