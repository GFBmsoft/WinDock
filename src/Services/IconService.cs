using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Extrai o icone do executavel uma vez por app e guarda em cache.</summary>
public static class IconService
{
    // ConcurrentDictionary porque o aquecimento (AppCatalog.Warm) preenche o cache numa
    // thread propria enquanto a interface le dele
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O melhor icone para um botao da dock, na ordem que da o resultado mais fiel:
    ///
    /// 1. o atalho, quando o alvo e um .lnk — e dele que vem o icone do perfil do Chrome;
    /// 2. a pasta de aplicativos do shell, quando a identidade e um AppUserModelID — e o
    ///    unico jeito de ter o icone de um app da Store, cujo executavel (o SystemSettings
    ///    das Configuracoes, por exemplo) traz so o icone generico de aplicativo;
    /// 3. o executavel.
    /// </summary>
    public static BitmapSource? ForApp(string id, string launchPath) => Fill(RawForApp(id, launchPath));

    private static BitmapSource? RawForApp(string id, string launchPath)
    {
        if (launchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return For(launchPath);

        if (IsAumid(id) && Shell($@"shell:AppsFolder\{id}") is { } fromShell) return fromShell;

        // App da Store fixado pelo caminho, e o caminho envelheceu: a pasta do pacote leva a
        // versão no nome e some a cada atualização. O identificador do pacote não muda, e por ele
        // o shell entrega o ícone — sem depender de o arquivo existir nem de a dock ter subido
        // depois da atualização para consertar o caminho gravado.
        if (!File.Exists(launchPath) &&
            PackagedApps.AumidOf(launchPath) is { } doPacote &&
            Shell($@"shell:AppsFolder\{doPacote}") is { } fromPackage) return fromPackage;

        return For(launchPath);
    }

    /// <summary>
    /// O desenho do icone esticado ate as bordas do quadrado, para todos ocuparem o mesmo
    /// espaco na dock.
    ///
    /// Cada fonte entrega o icone com uma folga transparente diferente em volta: o do
    /// executavel costuma vir justo, o da pasta de aplicativos (Store, Configuracoes) vem com
    /// um sexto de margem, e alguns programas desenham o simbolo pequeno no meio de um quadro
    /// vazio. Lado a lado, no mesmo tamanho de botao, uns pareciam o dobro dos outros — foi o
    /// pedido de 01/10/2026, "padronizar tamanho de icones". Recortar a folga e o que iguala;
    /// o desenho em si nao e tocado, so o quadro em volta dele.
    ///
    /// So recorta quando a folga e de verdade (mais de um vigesimo de cada lado): um icone que
    /// ja encosta nas bordas fica como veio, sem reamostragem. E o resultado e sempre
    /// quadrado, com o desenho centrado — um icone largo e baixo nao e esticado na altura.
    /// </summary>
    private static BitmapSource? Fill(BitmapSource? source)
    {
        if (source is null) return null;
        if (Filled.TryGetValue(source, out var pronto)) return pronto;

        var result = source;
        try { result = Trim(source) ?? source; }
        catch { /* formato estranho: melhor o icone com folga do que icone nenhum */ }

        Filled.AddOrUpdate(source, result);
        return result;
    }

    /// <summary>Um por icone de origem: o mesmo app aparece fixado e aberto ao mesmo tempo.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> Filled = new();

    private static BitmapSource? Trim(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        if (w < 8 || h < 8) return null;

        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        // a caixa do que se ve: alfa abaixo de 24 e sombra ou antisserrilhado, nao desenho
        int left = w, top = h, right = -1, bottom = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < 24) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        if (right < 0) return null;   // todo transparente

        int cw = right - left + 1, ch = bottom - top + 1;
        var lado = Math.Max(cw, ch);
        if (lado >= Math.Max(w, h) * 0.9) return null;   // ja justo: nada a recortar

        // quadrado do tamanho do lado maior, com o desenho no meio
        var saida = new byte[lado * lado * 4];
        int dx = (lado - cw) / 2, dy = (lado - ch) / 2;
        for (var y = 0; y < ch; y++)
            Buffer.BlockCopy(pixels, (top + y) * stride + left * 4,
                             saida, (dy + y) * lado * 4 + dx * 4, cw * 4);

        var result = BitmapSource.Create(lado, lado, 96, 96, bgra.Format, null, saida, lado * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Um AppUserModelID nao tem separador de caminho; um executavel tem.</summary>
    public static bool IsAumid(string id) =>
        !string.IsNullOrEmpty(id) && !id.Contains('\\') && !id.Contains('/');

    /// <summary>
    /// A pasta de aplicativos do shell conhece este AppUserModelID?
    ///
    /// A pergunta importa porque nem todo AUMID de janela e lancavel por ali. Os perfis do
    /// Chrome sao o exemplo: a janela do "Profile 2" se anuncia como
    /// <c>Chrome.UserData.Profile2</c>, mas esse ID nao existe na pasta de aplicativos —
    /// quem abre esse perfil e o chrome.exe com <c>--profile-directory</c>. Deduzir pelo
    /// formato do ID nao serve; aqui o shell responde de verdade, e a resposta fica em cache.
    /// </summary>
    public static bool ShellExists(string aumid)
    {
        if (!IsAumid(aumid)) return false;
        if (Existing.TryGetValue(aumid, out var known)) return known;

        var exists = false;
        IShellItemImageFactory? item = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            exists = SHCreateItemFromParsingName($@"shell:AppsFolder\{aumid}", 0, ref iid, out item) == 0
                     && item is not null;
        }
        catch { exists = false; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }

        Existing[aumid] = exists;
        return exists;
    }

    /// <summary>Cache do <see cref="ShellExists"/>: uma pergunta ao shell por AUMID.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Existing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// De qual arquivo saiu o icone guardado por <see cref="For"/> e como esse arquivo estava.
    ///
    /// O cache so por caminho segurava o icone para sempre: um programa republicado no mesmo
    /// lugar com icone novo (ou que ainda nao tinha icone quando a dock o viu pela primeira
    /// vez) ficava com o desenho antigo — o generico do Windows, no caso — ate a dock ser
    /// reiniciada. Guardando a data de modificacao, o icone e relido quando o arquivo muda.
    /// Sao duas datas porque num atalho mudam coisas diferentes: a do .lnk quando ele passa a
    /// apontar para outro lugar, a do alvo quando o programa e trocado.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Alvo, long Caminho, long DoAlvo)> Origens =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Data de modificacao do arquivo; 0 quando ele nao existe (ou nao e arquivo).</summary>
    private static long Carimbo(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0; }
        catch { return 0; }
    }

    public static BitmapSource? For(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached) && Origens.TryGetValue(path, out var origem) &&
            origem.Caminho == Carimbo(path) && origem.DoAlvo == Carimbo(origem.Alvo)) return cached;

        // Num .lnk o shell devolve o icone com a setinha de atalho colada. Numa dock isso
        // fica ruim, entao o icone vem do que o atalho aponta: o .ico proprio quando ele
        // define um (e o caso do atalho de perfil do Chrome), senao o executavel alvo.
        var exePath = path;
        var iconIndex = 0;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var lnk = ShortcutService.Read(path);
            if (lnk is not null)
            {
                exePath = !string.IsNullOrWhiteSpace(lnk.IconPath) && File.Exists(lnk.IconPath)
                          ? lnk.IconPath : lnk.Target;
                iconIndex = lnk.IconIndex;
            }
        }

        BitmapSource? result = null;
        try
        {
            // um indice diferente de zero so pode ser resolvido por ExtractIconEx
            if (iconIndex != 0 && File.Exists(exePath))
                result = FromExtract(exePath, iconIndex);

            if (result is null)
            {
                var info = new SHFILEINFO();
                var h = SHGetFileInfo(exePath, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                      SHGFI_ICON | SHGFI_LARGEICON);
                if (h != 0 && info.hIcon != 0)
                {
                    result = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                                                                 BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                    DestroyIcon(info.hIcon);
                }
            }

            if (result is null && File.Exists(exePath))
                result = FromExtract(exePath, 0);
        }
        catch { result = null; }

        Cache[path] = result;
        Origens[path] = (exePath, Carimbo(path), Carimbo(exePath));
        return result;
    }

    private static BitmapSource? FromExtract(string file, int index)
    {
        if (ExtractIconEx(file, index, out var large, out var small, 1) == 0) return null;

        BitmapSource? result = null;
        var handle = large != 0 ? large : small;
        if (handle != 0)
        {
            result = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                                                         BitmapSizeOptions.FromEmptyOptions());
            result.Freeze();
        }
        if (large != 0) DestroyIcon(large);
        if (small != 0) DestroyIcon(small);
        return result;
    }

    /// <summary>
    /// Icone de qualquer item do shell pelo nome de análise — inclusive
    /// <c>shell:AppsFolder\&lt;AppUserModelID&gt;</c>, que e como os apps da Store aparecem.
    /// Vem com transparencia de verdade, entao os pixels sao copiados do DIB em vez de
    /// passar pelo CreateBitmapSourceFromHBitmap, que descarta o canal alfa.
    /// </summary>
    public static BitmapSource? Shell(string parsingName, int size = 48)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;

        var key = $"shell::{size}::{parsingName}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        BitmapSource? result = null;
        nint bitmap = 0;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out factory) == 0 && factory is not null)
            {
                factory.GetImage(new SIZE(size, size), SIIGBF_BIGGERSIZEOK, out bitmap);
                if (bitmap != 0 && GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) != 0
                    && info.bmBits != 0 && info.bmBitsPixel == 32)
                {
                    var length = info.bmWidthBytes * info.bmHeight;
                    var pixels = new byte[length];
                    Marshal.Copy(info.bmBits, pixels, 0, length);

                    result = BitmapSource.Create(info.bmWidth, info.bmHeight, 96, 96,
                                                 PixelFormat(pixels), null, pixels,
                                                 info.bmWidthBytes);
                    result.Freeze();
                }
            }
        }
        catch { result = null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }

        Cache[key] = result;
        return result;
    }

    /// <summary>
    /// Descobre se os pixels vieram premultiplicados pelo alfa. O shell devolve os dois
    /// tipos: a maioria dos icones vem premultiplicada, mas os das Configuracoes e de
    /// outros apps da Store vem com a cor crua. Chamar de premultiplicado o que nao e
    /// pinta de branco tudo que deveria ser transparente — era a chapa branca atras da
    /// engrenagem das Configuracoes.
    ///
    /// A prova e simples: em premultiplicado nenhum canal de cor pode passar do alfa.
    /// </summary>
    private static System.Windows.Media.PixelFormat PixelFormat(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (pixels[i] > alpha || pixels[i + 1] > alpha || pixels[i + 2] > alpha)
                return PixelFormats.Bgra32;
        }

        return PixelFormats.Pbgra32;
    }

    /// <summary>Carrega um PNG/JPG solto (a foto do perfil do Chrome, por exemplo).</summary>
    public static BitmapSource? Image(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // nao segura o arquivo aberto
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
        }
        catch { result = null; }

        Cache[path] = result;
        return result;
    }
}
