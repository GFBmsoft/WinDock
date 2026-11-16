using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WinDock.Services;

/// <summary>
/// Converte um GIF em vídeo .mp4 (H.264), uma vez, para o papel de parede animado tocá-lo pelo
/// mesmo caminho dos vídeos.
///
/// Por que converter, em vez de tocar os quadros: o GIF guarda cada quadro em 256 cores e
/// comprimido, mas para mostrá-lo cada quadro precisa existir inteiro, em BGRA — 11 MB num GIF de
/// 2560×1080. Guardar todos (medido em 16/11/2026: 24 quadros, 654 MB no processo) ou decodificar a
/// cada exibição (10–20 ms de CPU por quadro) pesa muito mais que tocar um vídeo, que a placa
/// decodifica sozinha. A conversão leva alguns segundos e fica guardada em
/// <c>%LOCALAPPDATA%\WinDock\papel-animado</c>; o nome muda se o GIF mudar.
///
/// O caminho é todo do Windows: o decodificador de imagens (WIC) lê os quadros, a tela de cada
/// quadro é montada aqui, segundo o descarte do GIF, e um <see cref="MediaStreamSource"/> entrega
/// os quadros prontos ao <see cref="MediaTranscoder"/>, que grava o .mp4.
/// </summary>
internal static class GifVideo
{
    public static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDock", "papel-animado");

    /// <summary>O vídeo pronto de <paramref name="gif"/>: o guardado, ou um novo, convertido agora.</summary>
    public static async Task<string> CachedAsync(string gif)
    {
        var info = new FileInfo(gif);
        var destino = Path.Combine(CacheDir, WallpaperRules.CacheName(info.FullName, info.Length, info.LastWriteTimeUtc));
        if (File.Exists(destino) && new FileInfo(destino).Length > 0)
        {
            try { File.SetLastWriteTimeUtc(destino, DateTime.UtcNow); } catch { /* só serve à limpeza */ }
            return destino;
        }

        Directory.CreateDirectory(CacheDir);
        var provisorio = destino + ".tmp";
        var relogio = Stopwatch.StartNew();
        try
        {
            var resumo = await ConvertAsync(info.FullName, provisorio);
            File.Move(provisorio, destino, overwrite: true);
            Log.Write($"papel de parede: '{info.Name}' virou vídeo em {relogio.ElapsedMilliseconds} ms " +
                      $"({resumo}, {new FileInfo(destino).Length / 1024} KB)");
        }
        finally
        {
            try { if (File.Exists(provisorio)) File.Delete(provisorio); } catch { }
        }

        Prune();
        return destino;
    }

    /// <summary>Apaga os vídeos que ninguém usou em 60 dias: GIF trocado ou apagado deixa o seu para trás.</summary>
    private static void Prune()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(CacheDir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-60)) File.Delete(f);
        }
        catch { /* limpeza; tenta de novo na próxima conversão */ }
    }

    private sealed record Quadro(int X, int Y, int Disposal, TimeSpan Duracao);

    private static async Task<string> ConvertAsync(string gif, string destino)
    {
        var arquivo = await StorageFile.GetFileFromPathAsync(gif);
        using var entrada = await arquivo.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, entrada);

        int largura = await Numero(dec.BitmapContainerProperties, "/logscrdesc/Width", (int)dec.PixelWidth);
        int altura = await Numero(dec.BitmapContainerProperties, "/logscrdesc/Height", (int)dec.PixelHeight);

        // H.264 só aceita lados pares; a coluna ou linha a mais fica preta e some no corte da tela
        var w = largura + (largura & 1);
        var h = altura + (altura & 1);

        var quadros = new List<Quadro>();
        for (uint i = 0; i < dec.FrameCount; i++)
        {
            var f = await dec.GetFrameAsync(i);
            quadros.Add(new Quadro(
                await Numero(f.BitmapProperties, "/imgdesc/Left", 0),
                await Numero(f.BitmapProperties, "/imgdesc/Top", 0),
                await Numero(f.BitmapProperties, "/grctlext/Disposal", 0),
                WallpaperRules.GifDelay(await Numero(f.BitmapProperties, "/grctlext/Delay", 0))));
        }
        if (quadros.Count == 0) throw new InvalidDataException("o GIF não tem quadros");

        var volta = TimeSpan.FromTicks(quadros.Sum(q => q.Duracao.Ticks));
        var voltas = WallpaperRules.Repetitions(volta, TimeSpan.FromSeconds(20));
        var qps = (uint)Math.Clamp(Math.Round(quadros.Count / volta.TotalSeconds), 1, 60);

        var entradaVideo = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
        entradaVideo.FrameRate.Numerator = qps;
        entradaVideo.FrameRate.Denominator = 1;
        // MF_MT_DEFAULT_STRIDE positivo: as linhas vêm de cima para baixo. Sem ele o Media
        // Foundation lê o BGRA de baixo para cima, como no RGB antigo, e o vídeo sai de ponta-cabeça.
        entradaVideo.Properties[new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6")] = (uint)(w * 4);

        var fonte = new MediaStreamSource(new VideoStreamDescriptor(entradaVideo))
        {
            CanSeek = false,
            BufferTime = TimeSpan.Zero,
            Duration = TimeSpan.FromTicks(volta.Ticks * voltas),
        };

        var tela = new byte[w * h * 4];
        byte[]? guardada = null;
        var k = 0;
        var tempo = TimeSpan.Zero;
        Exception? falha = null;

        fonte.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        fonte.SampleRequested += async (_, e) =>
        {
            var adiamento = e.Request.GetDeferral();
            try
            {
                if (k >= quadros.Count * voltas) { e.Request.Sample = null; return; }   // fim do vídeo

                var i = k % quadros.Count;
                if (i == 0) Array.Clear(tela);   // cada volta recomeça da tela vazia, como no navegador
                var q = quadros[i];

                var f = await dec.GetFrameAsync((uint)i);
                var dados = await f.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var px = dados.DetachPixelData();
                int fw = (int)f.PixelWidth, fh = (int)f.PixelHeight;

                if (q.Disposal == 3) guardada = (byte[])tela.Clone();
                WallpaperRules.Paint(tela, w, h, px, q.X, q.Y, fw, fh);

                var amostra = MediaStreamSample.CreateFromBuffer(WallpaperRules.Opaque(tela).AsBuffer(), tempo);
                amostra.Duration = q.Duracao;
                tempo += q.Duracao;
                k++;

                if (q.Disposal == 2) WallpaperRules.Clear(tela, w, h, q.X, q.Y, fw, fh);
                else if (q.Disposal == 3 && guardada is not null) tela = guardada;

                e.Request.Sample = amostra;
            }
            catch (Exception ex)
            {
                falha ??= ex;
                e.Request.Sample = null;
            }
            finally { adiamento.Complete(); }
        };

        var perfil = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        perfil.Audio = null;
        perfil.Video.Width = (uint)w;
        perfil.Video.Height = (uint)h;
        perfil.Video.FrameRate.Numerator = qps;
        perfil.Video.FrameRate.Denominator = 1;
        // ~0,25 bit por pixel por quadro: o desenho de pixel dos GIFs não borra, e o arquivo fica
        // em poucos MB (o GIF de 2560×1080 a 10 qps dá ~7 Mbit/s)
        perfil.Video.Bitrate = (uint)Math.Clamp(w * (double)h * qps * 0.25, 2_000_000, 25_000_000);

        using (var saida = File.Create(destino))
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
            var preparo = await transcoder.PrepareMediaStreamSourceTranscodeAsync(fonte, saida.AsRandomAccessStream(), perfil);
            if (!preparo.CanTranscode)
                throw new InvalidOperationException($"o Windows não aceitou converter o GIF ({preparo.FailureReason})");
            await preparo.TranscodeAsync();
        }

        if (falha is not null) throw new InvalidDataException("um quadro do GIF não pôde ser lido: " + falha.Message, falha);
        return $"{quadros.Count} quadros {largura}x{altura}, {volta.TotalSeconds:0.0#} s × {voltas}";
    }

    /// <summary>Um número dos metadados, ou o padrão quando o GIF não o traz.</summary>
    private static async Task<int> Numero(BitmapPropertiesView props, string chave, int padrao)
    {
        try
        {
            var lido = await props.GetPropertiesAsync(new[] { chave });
            return lido.TryGetValue(chave, out var v) && v.Value is not null ? Convert.ToInt32(v.Value) : padrao;
        }
        catch { return padrao; }
    }
}
