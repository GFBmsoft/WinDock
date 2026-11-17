using System.Diagnostics;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace WinDock.Services;

/// <summary>
/// Uma parte que se mexe, pronta para um monitor: os quadros dela já na escala dele e esfumados na
/// borda, e onde ficam (<paramref name="X"/>, <paramref name="Y"/>). No idle, o quadro k é o tronco
/// k pixels acima (de 0 a <paramref name="Amplitude"/>); no pano, os quadros são uma volta da onda.
/// </summary>
internal sealed record FigureSprite(LivingFigure Figure, byte[][] Frames, int X, int Y, int Width, int Height, int Amplitude)
{
    /// <summary>Que quadro mostrar <paramref name="seconds"/> depois de a cena acender.</summary>
    public int FrameAt(double seconds, double phase) => Figure.Motion switch
    {
        FigureMotion.Idle => Math.Clamp(LivingRules.IdleOffset(seconds + phase * Figure.Seconds, Figure.Seconds, Amplitude), 0, Frames.Length - 1),
        FigureMotion.Breath => Math.Clamp(LivingRules.SinkOffset(seconds + phase * Figure.Seconds, Figure.Seconds, Amplitude), 0, Frames.Length - 1),
        _ => Math.Clamp((int)(LivingRules.Frac(seconds / Figure.Seconds + phase) * Frames.Length), 0, Frames.Length - 1),
    };
}

/// <summary>
/// Uma imagem parada pronta para ganhar vida: o que foi achado nos pixels dela
/// (<see cref="LivingRules.Analyze"/>) e as texturas da neblina. A imagem em si não fica: quem a
/// desenha é o Windows, como papel de parede, e a dock põe só os efeitos por cima — guardar e
/// subir a imagem ampliada para cada monitor custava 55 MB a mais na dock (medido em 17/11/2026).
/// Montada fora da thread do papel de parede, que não pode esperar (a fila dela está ligada à da
/// área de trabalho), e entregue pronta ao <see cref="AnimatedWallpaper"/>.
/// </summary>
internal sealed class LivingImage
{
    /// <summary>Tamanho de um ladrilho da neblina; a textura tem dois lado a lado, para correr sem emenda.</summary>
    public const int FogTileWidth = 512, FogTileHeight = 128;

    public required string Path { get; init; }

    /// <summary>O tamanho em que a imagem foi analisada; as posições do <see cref="Map"/> estão nele.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>O tamanho do arquivo, que é o que o Windows ajusta ao monitor.</summary>
    public required int OriginalWidth { get; init; }
    public required int OriginalHeight { get; init; }

    public required LivingMap Map { get; init; }
    public required List<LivingParticle> Particles { get; init; }

    /// <summary>A máscara do céu em BGRA pré-multiplicado (branco com o alfa da máscara).</summary>
    public required byte[] SkyBgra { get; init; }

    /// <summary>A neblina colorida, <c>2 × FogTileWidth</c> por <see cref="FogTileHeight"/>.</summary>
    public required byte[] FogBgra { get; init; }

    /// <summary>Semente dos sorteios da cena: a mesma imagem tem sempre a mesma tremulação.</summary>
    public required int Seed { get; init; }

    /// <summary>As figuras traçadas para esta imagem (<see cref="FiguresFolder"/>), em pixels da análise.</summary>
    public required List<LivingFigure> Figures { get; init; }

    /// <summary>
    /// Onde moram os contornos das figuras: um arquivo por imagem, <c>&lt;nome da imagem&gt;.json</c>.
    /// Na pasta da dock, e não na das imagens, para não pôr arquivo no meio dos papéis de parede.
    /// </summary>
    public static readonly string FiguresFolder =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock", "papel-com-vida");

    private readonly Dictionary<(int W, int H), List<FigureSprite>> _sprites = new();

    /// <summary>Os pedaços que respiram, prontos para um monitor desse tamanho (vazio se não houver).</summary>
    public IReadOnlyList<FigureSprite> SpritesFor(int width, int height) =>
        _sprites.TryGetValue((width, height), out var l) ? l : Array.Empty<FigureSprite>();

    /// <summary>
    /// Onde um ponto da análise cai num monitor de <paramref name="width"/> × <paramref name="height"/>,
    /// com o papel de parede ajustado por <paramref name="fit"/>: escala de pixel da análise para
    /// pixel da tela, em cada eixo, e o canto da imagem.
    /// </summary>
    public (double ScaleX, double ScaleY, double X, double Y) Place(WallpaperFit fit, int width, int height)
    {
        var (sx, sy, x, y) = LivingRules.Place(fit, OriginalWidth, OriginalHeight, width, height);
        return (sx * OriginalWidth / Width, sy * OriginalHeight / Height, x, y);
    }

    /// <summary>
    /// Lê <paramref name="path"/> e acha o que ganha vida nele; as figuras saem já recortadas para
    /// as <paramref name="screens"/>, com o papel de parede ajustado por <paramref name="fit"/>.
    /// </summary>
    public static async Task<LivingImage> LoadAsync(string path, IReadOnlyList<(int W, int H)> screens, WallpaperFit fit)
    {
        var relogio = Stopwatch.StartNew();
        var arquivo = await StorageFile.GetFileFromPathAsync(path);
        using var entrada = await arquivo.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(entrada);

        int ow = (int)dec.OrientedPixelWidth, oh = (int)dec.OrientedPixelHeight;
        if (ow <= 0 || oh <= 0) throw new InvalidDataException("imagem sem tamanho");

        // uma foto de 8000 px não precisa ser analisada inteira: reduz na leitura ao tamanho em
        // que ela aparece no maior monitor, com o filtro do próprio leitor, feito para reduzir
        var f = screens.Count == 0 ? 1.0
              : Math.Min(1.0, screens.Max(t => LivingRules.Place(WallpaperFit.Fill, ow, oh, t.W, t.H).ScaleX));
        var transformar = new BitmapTransform();
        int w = ow, h = oh;
        if (f < 1)
        {
            // escala igual nos dois eixos, então tanto faz se a rotação da foto vem antes ou depois
            transformar.ScaledWidth = (uint)Math.Max(1, Math.Round(dec.PixelWidth * f));
            transformar.ScaledHeight = (uint)Math.Max(1, Math.Round(dec.PixelHeight * f));
            transformar.InterpolationMode = BitmapInterpolationMode.Fant;
            var gira = ow != (int)dec.PixelWidth;
            w = (int)(gira ? transformar.ScaledHeight : transformar.ScaledWidth);
            h = (int)(gira ? transformar.ScaledWidth : transformar.ScaledHeight);
        }

        var dados = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transformar,
                                                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var px = dados.DetachPixelData();
        if (px.Length != w * h * 4)
            throw new InvalidDataException($"o leitor devolveu {px.Length} bytes para {w}×{h}");

        var semente = SeedOf(path);
        var extras = LoadExtras(path, (double)w / ow, (double)h / oh);
        var mapa = LivingRules.Analyze(px, w, h);
        // o arquivo da imagem pode desligar o que a análise acha: no Room (voxel, sem velas) o "céu"
        // era o tapete azul e a "vela", uma flor amarela
        if (!extras.Lights || !extras.Fog)
            mapa = new LivingMap
            {
                Width = mapa.Width, Height = mapa.Height, MaskWidth = mapa.MaskWidth, MaskHeight = mapa.MaskHeight,
                Body = mapa.Body, SkyColor = mapa.SkyColor,
                Lights = extras.Lights ? mapa.Lights : new(),
                Sky = mapa.Sky,
                SkyShare = extras.Fog ? mapa.SkyShare : 0,
                SkyBox = extras.Fog ? mapa.SkyBox : (0, 0, 0, 0),
            };
        var imagem = new LivingImage
        {
            Path = path,
            Width = w,
            Height = h,
            OriginalWidth = ow,
            OriginalHeight = oh,
            Map = mapa,
            Particles = extras.Particles ? LivingRules.Particles(mapa, semente) : new(),
            SkyBgra = SkyPixels(mapa),
            FogBgra = FogPixels(mapa, semente),
            Seed = semente,
            Figures = extras.Figures,
        };

        // os pixels morrem aqui: só o pedaço de cada figura fica, já na escala de cada monitor
        foreach (var (sw, sh) in screens.Distinct())
            imagem._sprites[(sw, sh)] = imagem.Figures.Select(fig => Sprite(px, imagem, fig, fit, sw, sh))
                                                     .OfType<FigureSprite>().ToList();

        Log.Trace($"papel de parede com vida: '{System.IO.Path.GetFileName(path)}' preparado em {relogio.ElapsedMilliseconds} ms " +
                  $"({w}×{h}, {mapa.Lights.Count} luzes, céu {mapa.SkyShare:P1}, {imagem.Figures.Count} figura(s))");
        return imagem;
    }

    /// <summary>
    /// O pedaço que respira de uma figura num monitor: recortado da imagem com a mesma ampliação
    /// que o Windows usa no papel de parede (o mesmo pixel, senão a cópia que sobe aparece como um
    /// remendo), esfumado na borda do contorno. Nulo se a figura não cai neste monitor.
    /// </summary>
    private static FigureSprite? Sprite(byte[] px, LivingImage img, LivingFigure fig, WallpaperFit fit, int mw, int mh)
    {
        var (ax, ay, x, y) = img.Place(fit, mw, mh);
        var pontos = fig.Outline.Select(p => ((float)(x + p.X * ax), (float)(y + p.Y * ay))).ToArray();
        int x0 = Math.Max(0, (int)Math.Floor(pontos.Min(p => p.Item1)) - 2);
        int y0 = Math.Max(0, (int)Math.Floor(pontos.Min(p => p.Item2)) - 2);
        int x1 = Math.Min(mw, (int)Math.Ceiling(pontos.Max(p => p.Item1)) + 2);
        int y1 = Math.Min(mh, (int)Math.Ceiling(pontos.Max(p => p.Item2)) + 2);
        if (x1 <= x0 || y1 <= y0) return null;

        int w = x1 - x0, h = y1 - y0;
        // o Windows desenha a imagem ampliada a partir do canto (x, y), que costuma cair entre
        // pixels; arredondado, o recorte bate com o dele pixel a pixel
        var pedaco = LivingRules.Resample(px, img.Width, img.Height, ax, ay,
                                          x0 + (int)Math.Round(-x), y0 + (int)Math.Round(-y), w, h);
        // no tronco a base não esfuma: o quadro é preso nela (não anda ali), e o pedaço emenda no
        // corpo sem costura. A borda, só 1 px: ao andar, a folga do contorno cobre o original, e
        // uma borda larga deixaria o original aparecer por baixo, meio transparente
        var tronco = fig.Motion is FigureMotion.Idle or FigureMotion.Breath;
        var mascara = LivingRules.OutlineMask(pontos.Select(p => (p.Item1 - x0, p.Item2 - y0)).ToArray(), w, h,
                                              feather: 1f, bottomFeather: tronco || fig.AnchorBottom ? 0.01f : 1f);
        for (var i = 0; i < mascara.Length; i++)
        {
            var a = mascara[i];
            if (a == 255) continue;
            for (var c = 0; c < 4; c++) pedaco[i * 4 + c] = (byte)(pedaco[i * 4 + c] * a / 255);
        }

        byte[][] quadros;
        int amplitude;
        if (tronco)
        {
            // o tronco anda inteiro até a emenda, e na emenda o deslocamento vai a zero. No idle ele
            // sobe (quadro k = k px acima); na respiração ele assenta (quadro k = k px abaixo)
            amplitude = Math.Max(1, (int)Math.Round(fig.Amount * ay));
            var baixo = (int)Math.Ceiling(pontos.Max(p => p.Item2)) - y0;
            var emenda = Math.Max(0, baixo - Math.Max(1, (int)Math.Round(fig.Ramp * ay)));
            if (fig.Motion == FigureMotion.Idle)
            {
                quadros = Enumerable.Range(0, amplitude + 1)
                                    .Select(k => k == 0 ? pedaco : LivingRules.BreathFrame(pedaco, w, h, emenda, k))
                                    .ToArray();
            }
            else
            {
                // respiração: parada em cima (a gola) e embaixo (o cinto), os ombros andam inteiros
                var topo = (int)Math.Floor(pontos.Min(p => p.Item2)) - y0;
                var pesos = LivingRules.RowWeights(h, topo, (int)Math.Round(fig.TopRamp * ay), emenda);
                quadros = Enumerable.Range(0, amplitude + 1)
                                    .Select(k => k == 0 ? pedaco : LivingRules.ShiftFrame(pedaco, w, h, pesos, -k))
                                    .ToArray();
            }
        }
        else
        {
            // uma volta da onda em tantos quadros quantos a cena desenha nesse tempo, até 16: com as
            // quatro bandeiras da Capela a 31–40 quadros cada, a dock ia a +65 MB (17/11/2026), e um
            // balanço lento não pede mais que isso
            amplitude = Math.Max(1, (int)Math.Round(fig.Amount * ax));
            var n = Math.Clamp((int)Math.Round(fig.Seconds * LivingRules.FramesPerSecond), 4, 16);
            quadros = Enumerable.Range(0, n)
                                // preso embaixo é um corpo, um caule, um rabo: a onda é tão longa que
                                // vira inclinação. Com onda curta o meio ia para um lado e a ponta para
                                // o outro, e num voxel o cubo da ponta do rabo do gato rachava ao meio
                                .Select(i => LivingRules.ClothFrame(pedaco, w, h, i / (double)n, fig.Amount * ax,
                                                                    h * (fig.AnchorBottom ? 8 : 1.5), fig.Free, fig.AnchorBottom))
                                .ToArray();
        }
        return new FigureSprite(fig, quadros, x0, y0, w, h, amplitude);
    }

    private sealed class FiguresDto
    {
        public List<FigureDto>? Figures { get; set; }
        /// <summary>Falso desliga o brilho das velas achadas (e as brasas, que saem delas).</summary>
        public bool Lights { get; set; } = true;
        /// <summary>Falso desliga a neblina no céu achado.</summary>
        public bool Fog { get; set; } = true;
        /// <summary>Falso desliga brasas e poeira.</summary>
        public bool Particles { get; set; } = true;
    }

    private sealed class FigureDto
    {
        public string? Name { get; set; }
        /// <summary>"Breath" (o tronco assenta e volta), "Idle" (sobe de uma vez, como no jogo) ou "Cloth" (pano ao vento).</summary>
        public string? Motion { get; set; }
        public float[][]? Outline { get; set; }
        /// <summary>Quanto anda, em pixels da imagem: a subida do tronco, ou a onda na ponta solta do pano.</summary>
        public float Amount { get; set; } = 2f;
        /// <summary>No tronco, a faixa de baixo (pixels da imagem) onde ele emenda no que fica parado.</summary>
        public float Ramp { get; set; } = 4f;
        /// <summary>Na respiração, a faixa de cima onde o movimento entra (a gola, sob a cabeça parada).</summary>
        public float TopRamp { get; set; }
        /// <summary>Uma volta; 1,2 s é a do idle do Penitente no jogo.</summary>
        public double Seconds { get; set; } = 1.2;
        /// <summary>No pano, o lado solto: "left", "right" ou "both" (bandeira pendurada no varão).</summary>
        public string? Free { get; set; }
        /// <summary>No pano, onde ele está preso: "top" (capa, bandeira) ou "bottom" (o corpo, parado no cinto).</summary>
        public string? Anchor { get; set; }
        public float[][]? Blade { get; set; }
    }

    private sealed record Extras(List<LivingFigure> Figures, bool Lights, bool Fog, bool Particles);

    /// <summary>
    /// O que <c>&lt;nome da imagem&gt;.json</c> diz da imagem: as figuras, passadas para pixels da
    /// análise (<paramref name="kx"/>, <paramref name="ky"/>), e quais efeitos automáticos ficam.
    /// Sem arquivo, ou com defeito (vira log), nenhuma figura e todos os automáticos.
    /// </summary>
    private static Extras LoadExtras(string path, double kx, double ky)
    {
        var arquivo = System.IO.Path.Combine(FiguresFolder, System.IO.Path.GetFileName(path) + ".json");
        var nada = new Extras(new(), true, true, true);
        if (!File.Exists(arquivo)) return nada;
        try
        {
            var dto = System.Text.Json.JsonSerializer.Deserialize<FiguresDto>(File.ReadAllText(arquivo),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (dto is null) return nada;
            var lista = new List<LivingFigure>();
            foreach (var f in dto.Figures ?? new())
            {
                if (f.Outline is not { Length: >= 3 } || f.Outline.Any(p => p.Length < 2)) continue;
                (float, float) P(float[] p) => ((float)(p[0] * kx), (float)(p[1] * ky));
                (float, float)? de = null, ate = null;
                if (f.Blade is { Length: 2 } b && b.All(p => p.Length >= 2)) { de = P(b[0]); ate = P(b[1]); }
                var movimento = f.Motion?.Trim().ToLowerInvariant() switch
                {
                    "cloth" => FigureMotion.Cloth,
                    "idle" => FigureMotion.Idle,
                    _ => FigureMotion.Breath,
                };
                var solto = f.Free?.Trim().ToLowerInvariant() switch
                {
                    "right" => ClothSide.Right,
                    "both" => ClothSide.Both,
                    _ => ClothSide.Left,
                };
                lista.Add(new LivingFigure(f.Name ?? "figura", movimento, f.Outline.Select(P).ToArray(),
                                           (float)(f.Amount * (movimento == FigureMotion.Cloth ? kx : ky)), (float)(f.Ramp * ky),
                                           Math.Max(0.3, f.Seconds), solto, de, ate, (float)(f.TopRamp * ky),
                                           string.Equals(f.Anchor?.Trim(), "bottom", StringComparison.OrdinalIgnoreCase)));
            }
            return new Extras(lista, dto.Lights, dto.Fog, dto.Particles);
        }
        catch (Exception ex)
        {
            Log.Write($"papel de parede com vida: não deu para ler as figuras de {arquivo}", ex);
            return nada;
        }
    }

    /// <summary>Só o retângulo do céu (<see cref="LivingMap.SkyBox"/>): é o tamanho da camada de neblina.</summary>
    private static byte[] SkyPixels(LivingMap m)
    {
        var (bx, by, bw, bh) = m.SkyBox;
        var px = new byte[bw * bh * 4];
        for (var y = 0; y < bh; y++)
        for (var x = 0; x < bw; x++)
        {
            var a = m.Sky[(by + y) * m.MaskWidth + bx + x];
            var p = (y * bw + x) * 4;
            px[p] = px[p + 1] = px[p + 2] = px[p + 3] = a;
        }
        return px;
    }

    /// <summary>
    /// A neblina na cor do céu achado, clareada: neblina cinza num céu de fim de tarde parece
    /// fumaça. Dois ladrilhos iguais lado a lado.
    /// </summary>
    private static byte[] FogPixels(LivingMap m, int seed)
    {
        var alfa = LivingRules.FogTexture(FogTileWidth, FogTileHeight, seed);
        var (cr, cg, cb) = m.SkyColor;
        int r = (cr * 2 + 226 * 3) / 5, g = (cg * 2 + 222 * 3) / 5, b = (cb * 2 + 232 * 3) / 5;

        var largura = FogTileWidth * 2;
        var px = new byte[largura * FogTileHeight * 4];
        for (var y = 0; y < FogTileHeight; y++)
        for (var x = 0; x < largura; x++)
        {
            var a = alfa[y * FogTileWidth + x % FogTileWidth];
            var p = (y * largura + x) * 4;
            px[p] = (byte)(b * a / 255);
            px[p + 1] = (byte)(g * a / 255);
            px[p + 2] = (byte)(r * a / 255);
            px[p + 3] = a;
        }
        return px;
    }

    /// <summary>FNV-1a do caminho em minúsculas: estável entre execuções, ao contrário do <c>GetHashCode</c>.</summary>
    private static int SeedOf(string path)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in path.ToLowerInvariant()) { h ^= c; h *= 16777619u; }
            return (int)(h & 0x7FFFFFFF);
        }
    }
}
