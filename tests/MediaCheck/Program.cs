using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;
using WinDock.Services;

namespace WinDock.Services
{
    /// <summary>O MediaService só usa a lista de vistos e o Save; o de verdade arrasta a dock inteira.</summary>
    public sealed class DockConfig
    {
        public List<string> MediaAppsSeen { get; } = new();
        public void Save() { }
    }
}

namespace WinDock.Tests
{
    public static class Program
    {
        static int _falhas;

        static void Check(string nome, bool ok, string detalhe = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
            if (!ok) _falhas++;
        }

        /// <summary>Uma capa de 64 px de uma cor só: a cor do pixel do meio diz qual chegou.</summary>
        static byte[] Png(Color cor)
        {
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(cor), null, new Rect(0, 0, 64, 64));
            var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(v);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new System.IO.MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        static RandomAccessStreamReference Ref(byte[] png)
        {
            var s = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(s.GetOutputStreamAt(0)))
            {
                w.WriteBytes(png);
                w.StoreAsync().AsTask().Wait();
                w.FlushAsync().AsTask().Wait();
                w.DetachStream();
            }
            return RandomAccessStreamReference.CreateFromStream(s);
        }

        static string Capa(BitmapSource? c)
        {
            if (c is null) return "nenhuma";
            var px = new byte[4];
            new CroppedBitmap(c, new Int32Rect(c.PixelWidth / 2, c.PixelHeight / 2, 1, 1)).CopyPixels(px, 4, 0);
            return ((px[2] << 16) | (px[1] << 8) | px[0]) switch
            {
                0xD03030 => "vermelha",
                0x3060D0 => "azul",
                var k => $"#{k:X6}",
            };
        }

        [STAThread]
        public static int Main()
        {
            var d = Dispatcher.CurrentDispatcher;
            var vermelha = Png(Color.FromRgb(0xD0, 0x30, 0x30));
            var azul = Png(Color.FromRgb(0x30, 0x60, 0xD0));

            var player = new Windows.Media.Playback.MediaPlayer();
            player.CommandManager.IsEnabled = false;
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            var du = smtc.DisplayUpdater;
            du.Type = MediaPlaybackType.Music;

            void Publica(string titulo, byte[]? capa)
            {
                du.MusicProperties.Artist = "MediaCheck";
                du.MusicProperties.Title = titulo;
                du.Thumbnail = capa is null ? null : Ref(capa);
                du.Update();
            }

            // a barra: o que o PanelModel faz com cada Changed
            var media = new MediaService();
            var barra = MediaInfo.None;
            var avisos = 0;
            media.Changed += () => d.InvokeAsync(() => { barra = media.Current; avisos++; });

            d.InvokeAsync(async () =>
            {
                try
                {
                    Publica("Faixa 1", null);
                    await Task.Delay(1500);

                    // só esta sessão entra, como o filtro de programas faz com o Spotify
                    var mgr = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var aumid = mgr.GetSessions().Select(s => s.SourceAppUserModelId)
                                   .FirstOrDefault(a => a?.Contains("MediaCheck", StringComparison.OrdinalIgnoreCase) == true);
                    Check("a sessão de teste aparece no Windows", aumid is not null);
                    if (aumid is null) return;

                    media.SetAllowed(new[] { aumid });
                    await media.Start();
                    await Task.Delay(1500);
                    Check("faixa sem capa: barra com o título e sem capa",
                          barra.Title == "Faixa 1" && barra.Cover is null, $"'{barra.Title}' capa {Capa(barra.Cover)}");

                    // o defeito de 18/11: a capa vem num aviso à parte, com o mesmo título
                    Publica("Faixa 1", vermelha);
                    await Task.Delay(1500);
                    Check("capa que chega depois do título vai para a barra",
                          Capa(barra.Cover) == "vermelha", $"capa {Capa(barra.Cover)}");

                    // faixa nova com a capa da anterior, e a certa só depois
                    Publica("Faixa 2", vermelha);
                    await Task.Delay(800);
                    Publica("Faixa 2", azul);
                    await Task.Delay(1500);
                    Check("faixa nova não fica com a capa da anterior",
                          barra.Title == "Faixa 2" && Capa(barra.Cover) == "azul", $"'{barra.Title}' capa {Capa(barra.Cover)}");

                    // duas trocas no mesmo instante: vale a última
                    Publica("Faixa 3", vermelha);
                    Publica("Faixa 4", azul);
                    await Task.Delay(2500);
                    Check("duas trocas seguidas: fica a última",
                          barra.Title == "Faixa 4" && Capa(barra.Cover) == "azul", $"'{barra.Title}' capa {Capa(barra.Cover)}");

                    // parado: nem a conferência de 1,5 s nem as releituras avisam a barra à toa
                    var antes = avisos;
                    await Task.Delay(4000);
                    Check("nada mudando, nenhum aviso à barra", avisos == antes, $"{avisos - antes} avisos em 4 s");

                    // faixa que nunca tem capa: as conferências param, e sem avisar a barra
                    Publica("Faixa 5", null);
                    await Task.Delay(1000);
                    antes = avisos;
                    await Task.Delay(9000);
                    Check("faixa sem capa nenhuma: conferências param sem avisar",
                          barra.Title == "Faixa 5" && barra.Cover is null && avisos == antes,
                          $"'{barra.Title}' capa {Capa(barra.Cover)}, {avisos - antes} avisos");
                }
                catch (Exception ex) { Check("sem exceção", false, ex.ToString()); }
                finally
                {
                    smtc.IsEnabled = false;
                    media.Dispose();
                    player.Dispose();
                    d.InvokeShutdown();
                }
            });
            Dispatcher.Run();

            Console.WriteLine(_falhas == 0 ? "tudo certo" : $"{_falhas} falha(s)");
            return _falhas == 0 ? 0 : 1;
        }
    }
}
