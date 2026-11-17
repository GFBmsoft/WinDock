namespace WinDock.Services;

/// <summary>Os ajustes do papel de parede do Windows em que a dock sabe pôr os efeitos.</summary>
internal enum WallpaperFit { Fill, Fit, Stretch, Center }

/// <summary>Uma luz achada na imagem (chama de vela, lampião, brasa), em pixels da imagem.</summary>
internal readonly record struct LivingLight(float X, float Y, float Radius, byte R, byte G, byte B, float Strength);

/// <summary>
/// Um ponto que se mexe: brasa subindo de uma luz, ou grão de poeira à deriva. Posições em pixels
/// da imagem, tempos em segundos; quem desenha converte para a tela.
/// </summary>
internal readonly record struct LivingParticle(bool Ember, float X0, float Y0, float X1, float Y1,
                                               float Wobble, float Size, float Opacity, double Seconds, double Delay);

/// <summary>Como uma figura se mexe.</summary>
internal enum FigureMotion
{
    /// <summary>O idle de personagem em pixel art: o tronco sobe inteiro, de uma vez, e volta.</summary>
    Idle,
    /// <summary>Pano ao vento: uma onda que desce pelo pano, presa em cima e solta embaixo.</summary>
    Cloth,
    /// <summary>Respiração: o tronco assenta devagar e volta, encolhendo o peito numa faixa larga.</summary>
    Breath,
}

/// <summary>De que lado o pano está solto: a capa presa junto à perna, a bandeira solta dos dois.</summary>
internal enum ClothSide { Left, Right, Both }

/// <summary>
/// Uma parte da imagem que se mexe sozinha (pedido de 17/11/2026: "aplique movimento no penitent
/// one"). Traçada à mão, por imagem, porque a análise acha velas e céu mas não separa um
/// personagem do fundo — o manto vermelho do Penitente e o tapete vermelho embaixo dele são a
/// mesma cor. <see cref="Outline"/> é o contorno da parte; <see cref="Amount"/>, quanto ela anda
/// (a subida ou a descida do tronco, ou a onda do pano na ponta solta); <see cref="Ramp"/>, no
/// tronco, a faixa de baixo onde ele emenda no que fica parado; <see cref="Free"/>, no pano, o
/// lado solto; e <see cref="BladeFrom"/>/<see cref="BladeTo"/>, a lâmina por onde um brilho corre,
/// se houver. Posições em pixels da imagem.
/// </summary>
internal sealed record LivingFigure(string Name, FigureMotion Motion, (float X, float Y)[] Outline, float Amount,
                                    float Ramp, double Seconds, ClothSide Free,
                                    (float X, float Y)? BladeFrom, (float X, float Y)? BladeTo, float TopRamp = 0,
                                    bool AnchorBottom = false);

/// <summary>O que dá vida a uma imagem parada, achado nos pixels dela por <see cref="LivingRules.Analyze"/>.</summary>
internal sealed class LivingMap
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required List<LivingLight> Lights { get; init; }

    /// <summary>Onde passa a neblina (o céu, o fundo enevoado), alfa 0–255 numa grade de <see cref="LivingRules.MaskStep"/> px.</summary>
    public required byte[] Sky { get; init; }

    /// <summary>Onde há cena, e não o fundo liso em volta dela; mesma grade.</summary>
    public required byte[] Body { get; init; }

    public required int MaskWidth { get; init; }
    public required int MaskHeight { get; init; }

    /// <summary>A cor média do céu achado: a neblina sai dela, clareada.</summary>
    public required (byte R, byte G, byte B) SkyColor { get; init; }

    /// <summary>Que fração da imagem é céu; quase nada, e a neblina nem é montada.</summary>
    public required double SkyShare { get; init; }

    /// <summary>
    /// O retângulo da grade que contém todo o céu (em casas); largura zero quando não há céu. A
    /// neblina é desenhada só aí: na imagem inteira, mesmo com a máscara zerando o resto, o dwm
    /// redesenhava a tela toda a cada quadro.
    /// </summary>
    public required (int X, int Y, int Width, int Height) SkyBox { get; init; }
}

/// <summary>
/// As contas do "papel de parede com vida" (pedido de 17/11/2026): a imagem parada fica como está,
/// como papel de parede do próprio Windows, e por cima dela a dock acende as luzes, passa neblina
/// no céu e solta brasas e poeira. Nada aqui sabe de janela ou de placa de vídeo: são os pixels da
/// imagem entrando e posições, tamanhos e tempos saindo, para o <c>tests/WallpaperCheck</c>
/// conferir sem abrir a dock.
///
/// <para>As luzes e o céu são achados pelos próprios pixels, e não marcados à mão, para valer em
/// qualquer imagem da pasta. Os números foram afinados na "Capela Gótica Isométrica ao Luar" e nos
/// quartos isométricos do mesmo estilo (pixel art sobre fundo preto); numa foto comum o efeito
/// fica mais discreto, porque quase nada passa nos filtros.</para>
/// </summary>
internal static class LivingRules
{
    /// <summary>Lado, em pixels da imagem, de cada casa das máscaras do céu e da cena.</summary>
    public const int MaskStep = 4;

    // ── luzes: a chama acesa, e não o ouro ou a pedra clara em volta dela ──
    // Medido na Capela (17/11/2026): na pixel art tudo tem contorno escuro, então o contraste com o
    // entorno não separa nada (110–200 em chama, ouro, estátua e armadura). O que separa é a forma:
    // a chama com o pavio é uma mancha de 20 px ou mais, em pé e com o miolo quase branco; o brilho
    // do ouro vem em fios de 1–2 px de largura ou manchas pequenas, a auréola e o elmo vêm deitados,
    // e a estátua clara tem média mais baixa (~205 contra 214–221 das chamas).
    private const float LightMinLum = 190;
    private const int LightMinWarmth = 40;      // vermelho menos azul: chama é amarela ou laranja, nunca branca fria
    private const int LightMinBlobWarmth = 25;  // a mancha inteira, com o miolo quase branco junto
    private const float LightMinPeak = 240;
    private const float LightMinMean = 210;
    private const int LightMinPixels = 20;
    private const int LightMinWidth = 3;
    private const float LightMinTallness = 1.2f;  // altura sobre largura
    private const int LightMaxWidth = 48, LightMaxHeight = 64;   // maior que isto é janela clara, céu, reflexo
    private const float LightMergeX = 10, LightMergeY = 24;     // a chama e o corpo da vela, um embaixo do outro
    private const int MaxLights = 48;

    // ── céu: a média de uma região de ~20 px, clara o bastante e de cor apagada ──
    // A paisagem atrás dos arcos tem detalhe (castelo, torres), então "liso" não serve; o que a
    // separa do primeiro plano é a névoa: saturação média 0,22–0,36 com luminância 62–142, contra
    // pedra, tapete e bandeira com saturação 0,37–0,90 ou luminância abaixo de 50.
    private const float SkyMinLum = 55, SkyMaxLum = 235;
    private const double SkyMaxSaturation = 0.36;
    private const int SkyRegionRadius = 2;      // em casas da grade: 5×5 casas, 20×20 px
    private const int SkyMinBlocks = 40;

    private const float BodyMinLum = 16;

    // ── partículas: poucas, porque cada uma que se mexe obriga o dwm a redesenhar em volta dela ──
    private const int MaxEmbers = 24;
    private const int MinDust = 12, MaxDust = 30;

    // ══ a análise ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acha as luzes, o céu e a cena de uma imagem em BGRA (linha de cima primeiro, 4 bytes por
    /// pixel, alfa pré-multiplicado ou opaca).
    /// </summary>
    public static LivingMap Analyze(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("imagem vazia");
        var n = width * height;
        if (bgra.Length < n * 4) throw new ArgumentException("menos pixels do que o tamanho diz", nameof(bgra));

        var lum = new float[n];
        for (int i = 0, p = 0; i < n; i++, p += 4)
            lum[i] = 0.114f * bgra[p] + 0.587f * bgra[p + 1] + 0.299f * bgra[p + 2];

        var mw = (width + MaskStep - 1) / MaskStep;
        var mh = (height + MaskStep - 1) / MaskStep;
        var corpo = BodyMask(lum, width, height, mw, mh);
        var (ceu, cor, parte) = SkyMask(bgra, lum, width, height, mw, mh, corpo);

        int x0 = mw, y0 = mh, x1 = -1, y1 = -1;
        for (var c = 0; c < ceu.Length; c++)
        {
            if (ceu[c] <= 4) continue;
            int cx = c % mw, cy = c / mw;
            if (cx < x0) x0 = cx; if (cx > x1) x1 = cx;
            if (cy < y0) y0 = cy; if (cy > y1) y1 = cy;
        }

        return new LivingMap
        {
            SkyBox = x1 < 0 ? (0, 0, 0, 0) : (x0, y0, x1 - x0 + 1, y1 - y0 + 1),
            Width = width,
            Height = height,
            Lights = FindLights(bgra, lum, width, height),
            Sky = ceu,
            Body = corpo,
            MaskWidth = mw,
            MaskHeight = mh,
            SkyColor = cor,
            SkyShare = parte,
        };
    }

    private sealed class Blob
    {
        public int Count, MinX = int.MaxValue, MaxX = int.MinValue, MinY = int.MaxValue, MaxY = int.MinValue;
        public long SumX, SumY, SumR, SumG, SumB;
        public float Peak, SumLum;

        public float CenterX => SumX / (float)Count + 0.5f;
        public float CenterY => SumY / (float)Count + 0.5f;

        public void Merge(Blob o)
        {
            Count += o.Count; SumX += o.SumX; SumY += o.SumY; SumR += o.SumR; SumG += o.SumG; SumB += o.SumB;
            SumLum += o.SumLum;
            MinX = Math.Min(MinX, o.MinX); MaxX = Math.Max(MaxX, o.MaxX);
            MinY = Math.Min(MinY, o.MinY); MaxY = Math.Max(MaxY, o.MaxY);
            Peak = Math.Max(Peak, o.Peak);
        }
    }

    /// <summary>
    /// Manchas de pixels claros e quentes, ligadas pelos oito vizinhos. Mancha grande demais não é
    /// chama (é janela clara, reflexo, céu ao sol) e fica de fora; as partes de uma mesma chama, que
    /// a pixel art costuma desenhar separadas, viram uma luz só.
    /// </summary>
    private static List<LivingLight> FindLights(byte[] px, float[] lum, int w, int h)
    {
        var n = w * h;
        var marca = new bool[n];
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            int b = px[p], g = px[p + 1], r = px[p + 2];
            // o corpo da chama é amarelo ou laranja; o miolo pode ser quase branco, e sem ele a
            // mancha perderia justo o pico. Quem decide se o conjunto é quente é a mancha inteira
            marca[i] = (lum[i] >= LightMinLum && r >= g && g >= b && r - b >= LightMinWarmth)
                       || (lum[i] >= LightMinPeak && r >= b);
        }

        var manchas = new List<Blob>();
        var pilha = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (!marca[i]) continue;
            marca[i] = false;
            var topo = 0;
            pilha[topo++] = i;
            var m = new Blob();
            while (topo > 0)
            {
                var k = pilha[--topo];
                int x = k % w, y = k / w, p = k * 4;
                m.Count++; m.SumX += x; m.SumY += y;
                m.SumB += px[p]; m.SumG += px[p + 1]; m.SumR += px[p + 2];
                if (x < m.MinX) m.MinX = x; if (x > m.MaxX) m.MaxX = x;
                if (y < m.MinY) m.MinY = y; if (y > m.MaxY) m.MaxY = y;
                if (lum[k] > m.Peak) m.Peak = lum[k];
                m.SumLum += lum[k];

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        var j = ny * w + nx;
                        if (!marca[j]) continue;
                        marca[j] = false;
                        pilha[topo++] = j;
                    }
                }
            }

            int largura = m.MaxX - m.MinX + 1, altura = m.MaxY - m.MinY + 1;
            if (m.Count >= LightMinPixels && m.Peak >= LightMinPeak && m.SumLum / m.Count >= LightMinMean
                && (m.SumR - m.SumB) / m.Count >= LightMinBlobWarmth
                && largura >= LightMinWidth && altura >= largura * LightMinTallness
                && largura <= LightMaxWidth && altura <= LightMaxHeight)
                manchas.Add(m);
        }

        var luzes = new List<Blob>();
        foreach (var m in manchas.OrderByDescending(m => m.Count))
        {
            var perto = luzes.FirstOrDefault(l =>
                MathF.Abs(l.CenterX - m.CenterX) <= LightMergeX && MathF.Abs(l.CenterY - m.CenterY) <= LightMergeY);
            if (perto is null) luzes.Add(m);
            else perto.Merge(m);
        }

        return luzes
            .OrderByDescending(l => l.Count)
            .Take(MaxLights)
            .Select(l => new LivingLight(
                l.CenterX, l.CenterY,
                Math.Clamp(5f * MathF.Sqrt(l.Count), 16f, 64f),
                (byte)(l.SumR / l.Count), (byte)(l.SumG / l.Count), (byte)(l.SumB / l.Count),
                Math.Clamp(MathF.Sqrt(l.Count) / 8f, 0.35f, 1f)))
            .ToList();
    }

    /// <summary>Que parte de cada casa da grade tem cena (não é o fundo escuro e liso em volta).</summary>
    private static byte[] BodyMask(float[] lum, int w, int h, int mw, int mh)
    {
        var m = new byte[mw * mh];
        for (var my = 0; my < mh; my++)
        for (var mx = 0; mx < mw; mx++)
        {
            int cheios = 0, total = 0;
            for (var y = my * MaskStep; y < Math.Min(h, (my + 1) * MaskStep); y++)
            for (var x = mx * MaskStep; x < Math.Min(w, (mx + 1) * MaskStep); x++)
            {
                total++;
                if (lum[y * w + x] > BodyMinLum) cheios++;
            }
            m[my * mw + mx] = (byte)(total == 0 ? 0 : cheios * 255 / total);
        }
        Blur(m, mw, mh, 1);
        return m;
    }

    /// <summary>
    /// O céu e a paisagem enevoada: casas da grade cuja região em volta (~20 px) é, na média, clara
    /// o bastante e de cor apagada. Sobra só o que forma região grande, porque pedaços soltos (uma
    /// armadura, uma estátua clara) não são céu; a borda sai esfumada e nunca passa da cena para o
    /// fundo preto.
    /// </summary>
    private static (byte[] Sky, (byte, byte, byte) Color, double Share) SkyMask(
        byte[] px, float[] lum, int w, int h, int mw, int mh, byte[] corpo)
    {
        var casas = mw * mh;
        var somaL = new double[casas];
        var somaS = new double[casas];
        var somaR = new long[casas];
        var somaG = new long[casas];
        var somaB = new long[casas];
        var conta = new int[casas];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int i = y * w + x, p = i * 4, b = px[p], g = px[p + 1], r = px[p + 2];
            int maior = Math.Max(r, Math.Max(g, b)), menor = Math.Min(r, Math.Min(g, b));
            var c = (y / MaskStep) * mw + x / MaskStep;
            somaL[c] += lum[i];
            somaS[c] += maior == 0 ? 0 : (maior - menor) / (double)maior;
            somaR[c] += r; somaG[c] += g; somaB[c] += b;
            conta[c]++;
        }

        var ceu = new bool[casas];
        for (var cy = 0; cy < mh; cy++)
        for (var cx = 0; cx < mw; cx++)
        {
            var c = cy * mw + cx;
            if (corpo[c] < 128) continue;
            double l = 0, s = 0;
            var n = 0;
            for (var vy = Math.Max(0, cy - SkyRegionRadius); vy <= Math.Min(mh - 1, cy + SkyRegionRadius); vy++)
            for (var vx = Math.Max(0, cx - SkyRegionRadius); vx <= Math.Min(mw - 1, cx + SkyRegionRadius); vx++)
            {
                var v = vy * mw + vx;
                l += somaL[v]; s += somaS[v]; n += conta[v];
            }
            if (n == 0) continue;
            l /= n; s /= n;
            ceu[c] = l >= SkyMinLum && l <= SkyMaxLum && s <= SkyMaxSaturation;
        }

        // só as regiões grandes ficam
        var minimo = Math.Max(SkyMinBlocks, (int)(0.004 * casas));
        var visto = new bool[casas];
        var fica = new bool[casas];
        var fila = new int[casas];
        for (var c = 0; c < casas; c++)
        {
            if (!ceu[c] || visto[c]) continue;
            int ini = 0, fim = 0;
            fila[fim++] = c;
            visto[c] = true;
            while (ini < fim)
            {
                var k = fila[ini++];
                int kx = k % mw, ky = k / mw;
                Vizinho(kx - 1, ky); Vizinho(kx + 1, ky); Vizinho(kx, ky - 1); Vizinho(kx, ky + 1);
            }
            if (fim >= minimo)
                for (var t = 0; t < fim; t++) fica[fila[t]] = true;

            void Vizinho(int vx, int vy)
            {
                if (vx < 0 || vy < 0 || vx >= mw || vy >= mh) return;
                var v = vy * mw + vx;
                if (!ceu[v] || visto[v]) return;
                visto[v] = true;
                fila[fim++] = v;
            }
        }

        var mascara = new byte[casas];
        long r0 = 0, g0 = 0, b0 = 0, n0 = 0, dentro = 0;
        for (var c = 0; c < casas; c++)
        {
            if (!fica[c]) continue;
            mascara[c] = 255;
            dentro++;
            r0 += somaR[c]; g0 += somaG[c]; b0 += somaB[c]; n0 += conta[c];
        }

        Blur(mascara, mw, mh, 2);
        Blur(mascara, mw, mh, 2);
        for (var c = 0; c < casas; c++) mascara[c] = (byte)(mascara[c] * corpo[c] / 255);

        var cor = n0 == 0 ? ((byte)160, (byte)160, (byte)170) : ((byte)(r0 / n0), (byte)(g0 / n0), (byte)(b0 / n0));
        return (mascara, cor, dentro / (double)casas);
    }

    /// <summary>Desfoque de caixa, horizontal e depois vertical, no próprio vetor.</summary>
    public static void Blur(byte[] m, int w, int h, int radius)
    {
        if (radius <= 0) return;
        var tmp = new int[m.Length];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int s = 0, n = 0;
            for (var k = Math.Max(0, x - radius); k <= Math.Min(w - 1, x + radius); k++) { s += m[y * w + k]; n++; }
            tmp[y * w + x] = s / n;
        }
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int s = 0, n = 0;
            for (var k = Math.Max(0, y - radius); k <= Math.Min(h - 1, y + radius); k++) { s += tmp[k * w + x]; n++; }
            m[y * w + x] = (byte)(s / n);
        }
    }

    // ══ na tela ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Como o Windows ajusta o papel de parede ao monitor, lido de <c>WallpaperStyle</c> e
    /// <c>TileWallpaper</c> em <c>HKCU\Control Panel\Desktop</c> (os mesmos valores que as
    /// Configurações dele gravam). Nulo para "Lado a lado" e "Estender": neles a imagem se repete ou
    /// atravessa os monitores, e a dock não sabe onde cada vela foi parar. Sem valor gravado, vale o
    /// padrão do Windows 11, Preencher.
    /// </summary>
    public static WallpaperFit? FitOf(string? style, string? tile) => (style?.Trim(), tile?.Trim()) switch
    {
        (null or "", _) => WallpaperFit.Fill,
        ("10", _) => WallpaperFit.Fill,
        ("6", _) => WallpaperFit.Fit,
        ("2", _) => WallpaperFit.Stretch,
        ("0", "1") => null,          // lado a lado
        ("0", _) => WallpaperFit.Center,
        _ => null,                   // 22 = estender pelos monitores, e o que vier depois
    };

    /// <summary>
    /// Onde a imagem cai no monitor, do mesmo jeito que o Windows a põe: escala em cada eixo e o
    /// canto de cima à esquerda. Os efeitos se apoiam nisso, porque a imagem embaixo deles é o
    /// papel de parede do próprio Windows (conferido em 17/11/2026: o Preencher daqui e o dele
    /// caem no mesmo pixel).
    /// </summary>
    public static (double ScaleX, double ScaleY, double X, double Y) Place(WallpaperFit fit, int imageWidth, int imageHeight,
                                                                           int areaWidth, int areaHeight)
    {
        double ex = (double)areaWidth / imageWidth, ey = (double)areaHeight / imageHeight;
        var (sx, sy) = fit switch
        {
            WallpaperFit.Fill => (Math.Max(ex, ey), Math.Max(ex, ey)),
            WallpaperFit.Fit => (Math.Min(ex, ey), Math.Min(ex, ey)),
            WallpaperFit.Stretch => (ex, ey),
            _ => (1.0, 1.0),
        };
        return (sx, sy, (areaWidth - imageWidth * sx) / 2, (areaHeight - imageHeight * sy) / 2);
    }

    // ══ figuras: o pedaço que respira ══════════════════════════════════════════════════════

    /// <summary>
    /// A imagem nas escalas <paramref name="scaleX"/> × <paramref name="scaleY"/>, só o pedaço de
    /// <paramref name="outWidth"/> × <paramref name="outHeight"/> que começa em
    /// (<paramref name="cropX"/>, <paramref name="cropY"/>), já na escala. Bicúbica (Catmull-Rom):
    /// é o que dá o mesmo pixel do papel de parede do Windows (conferido em 17/11/2026 na Capela,
    /// recorte a recorte), e a figura que respira precisa ser idêntica à imagem embaixo dela, senão
    /// aparece como um remendo. Ao reduzir, o filtro alarga com a escala, senão sai serrilhado.
    /// </summary>
    public static byte[] Resample(byte[] src, int srcWidth, int srcHeight, double scaleX, double scaleY,
                                  int cropX, int cropY, int outWidth, int outHeight)
    {
        var (ix, wx, tx) = Taps(srcWidth, scaleX, cropX, outWidth);
        var (iy, wy, ty) = Taps(srcHeight, scaleY, cropY, outHeight);

        int r0 = int.MaxValue, r1 = int.MinValue;
        foreach (var i in iy) { if (i < r0) r0 = i; if (i > r1) r1 = i; }
        var linhas = r1 - r0 + 1;

        // primeiro na horizontal, só as linhas da fonte que a vertical vai usar
        var meio = new float[linhas * outWidth * 4];
        for (var r = 0; r < linhas; r++)
        {
            var origem = (r0 + r) * srcWidth * 4;
            var destino = r * outWidth * 4;
            for (var x = 0; x < outWidth; x++)
            {
                float b = 0, g = 0, rr = 0, a = 0;
                for (var t = 0; t < tx; t++)
                {
                    var peso = wx[x * tx + t];
                    if (peso == 0) continue;
                    var p = origem + ix[x * tx + t] * 4;
                    b += src[p] * peso; g += src[p + 1] * peso; rr += src[p + 2] * peso; a += src[p + 3] * peso;
                }
                var d = destino + x * 4;
                meio[d] = b; meio[d + 1] = g; meio[d + 2] = rr; meio[d + 3] = a;
            }
        }

        var saida = new byte[outWidth * outHeight * 4];
        for (var y = 0; y < outHeight; y++)
        {
            var destino = y * outWidth * 4;
            for (var x = 0; x < outWidth * 4; x += 4)
            {
                float b = 0, g = 0, rr = 0, a = 0;
                for (var t = 0; t < ty; t++)
                {
                    var peso = wy[y * ty + t];
                    if (peso == 0) continue;
                    var p = (iy[y * ty + t] - r0) * outWidth * 4 + x;
                    b += meio[p] * peso; g += meio[p + 1] * peso; rr += meio[p + 2] * peso; a += meio[p + 3] * peso;
                }
                var alfa = Byte(a);
                saida[destino + x + 3] = alfa;
                // pré-multiplicado: a cor nunca passa do alfa (a bicúbica passa um pouco nas bordas)
                saida[destino + x] = Math.Min(Byte(b), alfa);
                saida[destino + x + 1] = Math.Min(Byte(g), alfa);
                saida[destino + x + 2] = Math.Min(Byte(rr), alfa);
            }
        }
        return saida;
    }

    private static byte Byte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>
    /// Para cada pixel de saída, de que pixels da fonte ele sai e com que peso. A borda repete o
    /// último pixel, como faz o Windows, em vez de escurecer.
    /// </summary>
    private static (int[] Index, float[] Weight, int Count) Taps(int srcLen, double scale, int crop, int outLen)
    {
        var largura = Math.Max(1.0, 1.0 / scale);
        var alcance = 2.0 * largura;
        var n = (int)Math.Ceiling(alcance * 2) + 1;
        var indice = new int[outLen * n];
        var peso = new float[outLen * n];
        for (var o = 0; o < outLen; o++)
        {
            var centro = (o + crop + 0.5) / scale - 0.5;
            var primeiro = (int)Math.Floor(centro - alcance) + 1;
            double total = 0;
            for (var t = 0; t < n; t++)
            {
                var i = primeiro + t;
                var k = CatmullRom((i - centro) / largura);
                indice[o * n + t] = Math.Clamp(i, 0, srcLen - 1);
                peso[o * n + t] = (float)k;
                total += k;
            }
            if (total != 0)
                for (var t = 0; t < n; t++) peso[o * n + t] = (float)(peso[o * n + t] / total);
        }
        return (indice, peso, n);
    }

    private static double CatmullRom(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
        if (x < 2) return ((a * x - 5 * a) * x + 8 * a) * x - 4 * a;
        return 0;
    }

    /// <summary>
    /// A máscara do contorno de uma figura, alfa 0–255 em <paramref name="width"/> ×
    /// <paramref name="height"/>, com o contorno em pixels desse retângulo. A borda esfuma em
    /// <paramref name="feather"/> px, e a base, onde o pedaço que respira encontra o corpo que fica
    /// parado, em <paramref name="bottomFeather"/>: a cópia que sobe se dissolve no original, sem
    /// degrau no meio do peito.
    /// </summary>
    public static byte[] OutlineMask(IReadOnlyList<(float X, float Y)> outline, int width, int height,
                                     float feather, float bottomFeather)
    {
        var m = new byte[width * height];
        if (outline.Count < 3) return m;
        var baixo = outline.Max(p => p.Y);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            if (!Inside(outline, px, py)) continue;
            var a = Math.Min(1f, Distance(outline, px, py) / Math.Max(0.01f, feather));
            a *= Math.Min(1f, (baixo - py) / Math.Max(0.01f, bottomFeather));
            m[y * width + x] = (byte)Math.Clamp(MathF.Round(Smooth(Math.Clamp(a, 0, 1)) * 255), 0, 255);
        }
        return m;
    }

    private static bool Inside(IReadOnlyList<(float X, float Y)> p, float x, float y)
    {
        var dentro = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            if ((p[i].Y > y) != (p[j].Y > y) &&
                x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                dentro = !dentro;
        }
        return dentro;
    }

    private static float Distance(IReadOnlyList<(float X, float Y)> p, float x, float y)
    {
        var menor = float.MaxValue;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            float ax = p[j].X, ay = p[j].Y, bx = p[i].X, by = p[i].Y;
            float dx = bx - ax, dy = by - ay;
            var t = dx == 0 && dy == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
            float qx = ax + t * dx - x, qy = ay + t * dy - y;
            menor = Math.Min(menor, MathF.Sqrt(qx * qx + qy * qy));
        }
        return menor;
    }

    /// <summary>
    /// Quantos pixels o tronco está acima do repouso no idle, no ritmo do idle do Penitente no
    /// jogo (o vídeo que ele mandou em 17/11/2026, 16 quadros a 13,3 por segundo, medido quadro a
    /// quadro): parado em 7/16 da volta, meio caminho num quadro, em cima em 7/16, meio caminho de
    /// novo. Sobe inteiro, de uma vez — esticar em rampa fazia o capirote parecer crescer ("parece
    /// que só aumenta para cima").
    /// </summary>
    public static int IdleOffset(double seconds, double period, int amplitude)
    {
        if (period <= 0 || amplitude <= 0) return 0;
        var quadro = (int)(Frac(seconds / period) * 16);
        var meio = (int)Math.Round(amplitude / 2.0, MidpointRounding.AwayFromZero);
        return quadro switch
        {
            4 or 12 => meio,
            >= 5 and <= 11 => amplitude,
            _ => 0,
        };
    }

    /// <summary>
    /// Quantos pixels o tronco assentou na respiração, de 0 a <paramref name="amplitude"/>: desce e
    /// volta em <paramref name="period"/> segundos, devagar nas pontas (cosseno), passando por todos
    /// os degraus. É o "mais natural" que ele pediu depois do idle do jogo, que sobe de uma vez e
    /// parecia esticar o Penitente para cima: ao soltar o ar, o corpo assenta, e não cresce.
    /// </summary>
    public static int SinkOffset(double seconds, double period, int amplitude)
    {
        if (period <= 0 || amplitude <= 0) return 0;
        var u = Frac(seconds / period);
        return (int)Math.Round(amplitude * (1 - Math.Cos(u * 2 * Math.PI)) / 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Um quadro do idle: o pedaço sobe <paramref name="k"/> pixels (ou desce, com k negativo)
    /// inteiro até a linha <paramref name="top"/>, e daí até a última linha a subida vai a zero —
    /// é a emenda no que fica parado (a cintura, no Penitente). De linha inteira em linha inteira
    /// (uma linha repetida, ou pulada, na emenda), sem borrar, como a pixel art faz.
    /// </summary>
    public static byte[] BreathFrame(byte[] bgra, int width, int height, int top, int k)
    {
        var quadro = new byte[bgra.Length];
        var ultima = height - 1;
        var faixa = Math.Max(1, ultima - top);
        if (k <= -faixa) return quadro;
        var linha = width * 4;
        for (var y = 0; y < height; y++)
        {
            // a linha de origem s anda k·(ultima − s)/faixa: de baixo, parada, até o topo, k inteiro.
            // Invertido: que linha de origem cai no destino y. Acima do topo, tudo anda k
            var s = (y * (double)faixa + k * ultima) / (faixa + k);
            if (s < top) s = y + k;
            var origem = (int)Math.Round(s, MidpointRounding.AwayFromZero);
            if (origem < 0 || origem >= height) continue;   // além do recorte: transparente
            Buffer.BlockCopy(bgra, origem * linha, quadro, y * linha, linha);
        }
        return quadro;
    }

    /// <summary>
    /// Quanto cada linha do recorte acompanha o movimento na respiração, de 0 a 1: nada acima de
    /// <paramref name="top"/>, entrando suave em <paramref name="topRamp"/> linhas (a gola, embaixo
    /// do capirote, que fica parado), inteira no meio (os ombros) e saindo suave a partir de
    /// <paramref name="bottomStart"/> até a última linha (o peito, acima do cinto). Com as duas
    /// pontas paradas nada alto se alonga — o capirote andando, mesmo devagar, "esticava" o
    /// Penitente para cima (retorno dele em 17/11/2026, duas vezes).
    /// </summary>
    public static double[] RowWeights(int height, int top, int topRamp, int bottomStart)
    {
        var w = new double[height];
        var ultima = height - 1;
        for (var y = 0; y < height; y++)
        {
            var entra = topRamp <= 0 ? (y >= top ? 1 : 0) : Math.Clamp((y - top) / (double)topRamp, 0, 1);
            var sai = ultima <= bottomStart ? 1 : Math.Clamp((ultima - y) / (double)(ultima - bottomStart), 0, 1);
            w[y] = Math.Min(entra, sai);
        }
        return w;
    }

    /// <summary>
    /// Um quadro em que cada linha anda <paramref name="k"/> × o peso dela (k &gt; 0 sobe, k &lt; 0
    /// desce), de linha inteira em linha inteira: onde as linhas se afastam, uma se repete; onde se
    /// juntam, uma é pulada. Sem borrar.
    /// </summary>
    public static byte[] ShiftFrame(byte[] bgra, int width, int height, double[] weight, int k)
    {
        var quadro = new byte[bgra.Length];
        var linha = width * 4;
        var destino = new double[height];
        for (var s = 0; s < height; s++) destino[s] = s - k * weight[s];
        for (var y = 0; y < height; y++)
        {
            int melhor = -1;
            var erro = double.MaxValue;
            for (var s = 0; s < height; s++)
            {
                var e = Math.Abs(destino[s] - y);
                if (e < erro) { erro = e; melhor = s; }
            }
            if (melhor < 0 || erro > 0.75) continue;   // nenhuma linha cai aqui: transparente
            Buffer.BlockCopy(bgra, melhor * linha, quadro, y * linha, linha);
        }
        return quadro;
    }

    /// <summary>
    /// Quanto o pano anda para o lado no ponto (<paramref name="x"/>, <paramref name="y"/>) do
    /// recorte, na fase <paramref name="phase"/> (0 a 1) da onda: nada em cima (onde ele está preso)
    /// nem no lado preso, <paramref name="amplitude"/> na ponta solta de baixo; a onda desce pelo
    /// pano, como o vento empurrando. A bandeira, pendurada no varão, é solta dos dois lados. Com
    /// <paramref name="anchorBottom"/>, o preso é embaixo e o solto em cima: é o balanço do
    /// Penitente, parado no cinto — o movimento de pano que ele achou natural na capa e nas
    /// bandeiras, depois de três respirações verticais que pareciam esticá-lo.
    /// </summary>
    public static double ClothShift(int x, int y, int width, int height, double phase, double amplitude,
                                    double wavelength, ClothSide free, bool anchorBottom = false)
    {
        var desce = height <= 1 ? 1 : Math.Clamp(y / (double)(height - 1), 0, 1);
        if (anchorBottom) desce = 1 - desce;
        var lado = width <= 1 ? 1 : Math.Clamp(x / (double)(width - 1), 0, 1);
        lado = free switch { ClothSide.Left => 1 - lado, ClothSide.Right => lado, _ => 1 };
        return amplitude * desce * lado * Math.Sin(2 * Math.PI * (phase - y / Math.Max(1, wavelength)));
    }

    /// <summary>
    /// Um quadro do pano: cada pixel vem do que está <see cref="ClothShift"/> ao lado dele, em
    /// pixels inteiros — sem borrar. O que sai do recorte fica transparente.
    /// </summary>
    public static byte[] ClothFrame(byte[] bgra, int width, int height, double phase, double amplitude,
                                    double wavelength, ClothSide free, bool anchorBottom = false)
    {
        var quadro = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var origem = x - (int)Math.Round(ClothShift(x, y, width, height, phase, amplitude, wavelength, free, anchorBottom),
                                             MidpointRounding.AwayFromZero);
            if (origem < 0 || origem >= width) continue;
            Buffer.BlockCopy(bgra, (y * width + origem) * 4, quadro, (y * width + x) * 4, 4);
        }
        return quadro;
    }

    /// <summary>
    /// Onde está o brilho que corre a lâmina (0 no punho, 1 na ponta), ou nulo quando não está
    /// passando: uma passada de <paramref name="duration"/> segundos a cada <paramref name="period"/>.
    /// </summary>
    public static double? Sweep(double seconds, double period, double duration, double phase)
    {
        if (period <= 0 || duration <= 0) return null;
        var dentro = Frac(seconds / period + phase) * period;
        return dentro < duration ? dentro / duration : null;
    }

    // ══ a neblina ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// O alfa da neblina: ruído em quatro oitavas, cortado para virar fiapos e não um véu. Repete
    /// na horizontal (a coluna depois da última é a primeira), para correr de lado sem emenda.
    /// </summary>
    public static byte[] FogTexture(int width, int height, int seed)
    {
        var rnd = new Random(seed);
        var v = new float[width * height];
        var amp = 1f;
        foreach (var colunas in new[] { 3, 6, 12, 24 })
        {
            var linhas = Math.Max(2, colunas * height / width + 1);
            var grade = new float[colunas * (linhas + 1)];
            for (var i = 0; i < grade.Length; i++) grade[i] = (float)rnd.NextDouble();

            for (var y = 0; y < height; y++)
            {
                var gy = y * (float)linhas / height;
                int y0 = (int)gy;
                var fy = Smooth(gy - y0);
                for (var x = 0; x < width; x++)
                {
                    var gx = x * (float)colunas / width;
                    int x0 = (int)gx, x1 = (x0 + 1) % colunas;
                    var fx = Smooth(gx - x0);
                    var a = Lerp(grade[y0 * colunas + x0 % colunas], grade[y0 * colunas + x1], fx);
                    var b = Lerp(grade[(y0 + 1) * colunas + x0 % colunas], grade[(y0 + 1) * colunas + x1], fx);
                    v[y * width + x] += Lerp(a, b, fy) * amp;
                }
            }
            amp *= 0.5f;
        }

        // a soma das oitavas fica quase toda perto do meio, e a faixa muda com a semente: cortar
        // num número fixo deixava, em algumas imagens, a neblina quase transparente no valor médio
        // e só visível nos picos (medido na Capela em 17/11/2026: o céu passava minutos limpo).
        // Normalizada pela faixa real, toda semente dá névoa contínua, com fiapos e vãos
        float menor = float.MaxValue, maior = float.MinValue;
        foreach (var x in v) { if (x < menor) menor = x; if (x > maior) maior = x; }
        var faixa = Math.Max(1e-6f, maior - menor);

        var alfa = new byte[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            var t = Math.Clamp(((v[i] - menor) / faixa - 0.25f) / 0.5f, 0f, 1f);
            alfa[i] = (byte)(Smooth(t) * 255);
        }
        return alfa;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ══ brasas e poeira ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// As brasas sobem das luzes mais fortes (uma ou duas por luz); a poeira flutua devagar onde há
    /// cena, nunca sobre o fundo preto em volta. Sorteio com semente: a mesma imagem tem sempre as
    /// mesmas partículas.
    /// </summary>
    public static List<LivingParticle> Particles(LivingMap map, int seed)
    {
        var rnd = new Random(seed);
        float R(double min, double max) => (float)(min + rnd.NextDouble() * (max - min));
        var lista = new List<LivingParticle>();

        foreach (var l in map.Lights.OrderByDescending(l => l.Strength))
        {
            var quantas = l.Strength >= 0.8f ? 2 : 1;
            for (var k = 0; k < quantas && lista.Count < MaxEmbers; k++)
            {
                var x0 = l.X + R(-l.Radius * 0.15, l.Radius * 0.15);
                var y0 = l.Y - R(0, 3);
                var segundos = R(3.5, 7);
                lista.Add(new LivingParticle(true, x0, y0, x0 + R(-10, 10), y0 - R(25, 70),
                                             R(2, 6), 2, R(0.6, 0.95), segundos, R(0, segundos)));
            }
        }

        var lugares = new List<int>();
        for (var c = 0; c < map.Body.Length; c++)
            if (map.Body[c] > 200) lugares.Add(c);
        if (lugares.Count == 0) return lista;

        var poeira = Math.Clamp(lugares.Count / 600, MinDust, MaxDust);
        for (var i = 0; i < poeira; i++)
        {
            var c = lugares[rnd.Next(lugares.Count)];
            var x0 = (c % map.MaskWidth + R(0, 1)) * MaskStep;
            var y0 = (c / map.MaskWidth + R(0, 1)) * MaskStep;
            var segundos = R(10, 20);
            lista.Add(new LivingParticle(false, x0, y0, x0 + R(-30, 30), y0 + R(-24, 8),
                                         R(4, 10), 2, R(0.12, 0.3), segundos, R(0, segundos)));
        }
        return lista;
    }

    // ══ cada quadro ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Quantos quadros por segundo a cena desenha. Doze é o ritmo da animação em pixel art, e é o
    /// que deixa a cena barata: medido em 17/11/2026, as mesmas animações entregues ao compositor a
    /// 60 quadros levavam a parte 3D do dwm de 4% a 44% e a CPU dele de 7% a 27% de um núcleo.
    /// </summary>
    public const int FramesPerSecond = 12;

    /// <summary>Sorteia os valores de uma volta de tremulação, entre <paramref name="lowest"/> e 1.</summary>
    public static float[] FlickerKeys(Random rnd, int count, float lowest)
    {
        var k = new float[count];
        for (var i = 0; i < count; i++) k[i] = lowest + (float)rnd.NextDouble() * (1 - lowest);
        return k;
    }

    /// <summary>
    /// A tremulação em <paramref name="turn"/> (0 a 1 de uma volta): passa de um valor sorteado ao
    /// seguinte com suavidade, e o último emenda no primeiro, então a volta não tem salto.
    /// </summary>
    public static float Flicker(float[] keys, double turn)
    {
        var u = Frac(turn) * keys.Length;
        var i = (int)u;
        if (i >= keys.Length) i = keys.Length - 1;
        return Lerp(keys[i], keys[(i + 1) % keys.Length], Smooth((float)(u - i)));
    }

    /// <summary>
    /// Onde está a partícula e quanto aparece, <paramref name="seconds"/> depois de a cena acender
    /// (em pixels da imagem). Antes da espera dela, invisível; depois, em volta: sobe (ou deriva)
    /// balançando de lado, aparece rápido, some devagar.
    /// </summary>
    public static (float X, float Y, float Opacity) ParticleAt(LivingParticle p, double seconds)
    {
        var passado = seconds - p.Delay;
        if (passado < 0 || p.Seconds <= 0) return (p.X0, p.Y0, 0);
        var u = (float)Frac(passado / p.Seconds);
        var x = p.X0 + (p.X1 - p.X0) * u + p.Wobble * MathF.Sin(u * 2 * MathF.PI);
        var y = p.Y0 + (p.Y1 - p.Y0) * u;
        var o = u < 0.15f ? p.Opacity * u / 0.15f
              : u < 0.7f ? p.Opacity * (1 - 0.25f * (u - 0.15f) / 0.55f)
              : p.Opacity * 0.75f * (1 - u) / 0.3f;
        return (x, y, Math.Max(0, o));
    }

    public static double Frac(double v) => v - Math.Floor(v);
}
