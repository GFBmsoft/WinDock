namespace WinDock.Services;

/// <summary>
/// Tira a moldura escura dos ícones da família do usuário (GRepos, GTerm, GTest, GConv, GSQL…),
/// para eles ficarem como os desenhados da dock: só o desenho, sem fundo.
///
/// <para>Pedido de 16/11/2026: com os ícones desenhados ligados, os desenhos (pasta, engrenagem,
/// os logos) apareciam sem fundo e os G* com um quadrado escuro atrás — o ícone do próprio
/// programa, que o <c>tools\gerar-icone.ps1</c> de cada um grava com a moldura dentro: um quadrado
/// arredondado (raio de 54 em 244) cheio de uma cor escura e contornado por outra, um pouco mais
/// clara. A cor muda de programa para programa (<c>#1B1F2A</c>, <c>#161A21</c> no GRepos e no GTerm,
/// <c>#1E1E2E</c> no GTest), por isso ela é descoberta em cada ícone, e não fixada aqui.</para>
///
/// <para>O fundo sai por "cor para alfa": cada pixel é lido como o desenho misturado à cor do
/// fundo, e fica só a parte do desenho, com a transparência que ela tinha. A borda suavizada dos
/// traços não fica com franja escura, e os preenchimentos translúcidos dos desenhos (a cor do traço
/// a 19%) continuam translúcidos. O contorno e a borda suavizada do quadrado saem pela geometria.</para>
///
/// <para>Só pixels, sem WPF: o <c>tests/IconFrameCheck</c> confere com ícones montados na hora.</para>
/// </summary>
internal static class IconFrame
{
    /// <summary>Raio do canto da moldura em relação ao lado dela, como no gerar-icone.ps1 (54 em 244).</summary>
    private const double CornerRatio = 54.0 / 244;

    /// <summary>Espessura do contorno em relação ao lado (4 em 244).</summary>
    private const double OutlineRatio = 4.0 / 244;

    /// <summary>
    /// O desenho sem a moldura, em BGRA sem pré-multiplicação, num quadrado de
    /// <paramref name="side"/> com o desenho ocupando <paramref name="occupy"/> do lado — a mesma
    /// folga dos desenhados. Nulo quando o ícone não tem a moldura da família, e aí ele fica como
    /// está: quadrado claro, sem canto redondo, desenho sem fundo.
    /// </summary>
    public static byte[]? Strip(byte[] bgra, int width, int height, double occupy, out int side)
    {
        side = 0;
        if (bgra.Length < width * height * 4) return null;

        // ── a moldura: a caixa do que é opaco, quase quadrada, com os cantos arredondados ──
        int l = width, t = height, r = -1, b = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (A(bgra, width, x, y) >= 128)
                {
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    if (y > b) b = y;
                }
        if (r < 0) return null;

        int bw = r - l + 1, bh = b - t + 1;
        if (bw < 16 || bh < 16 || Math.Abs(bw - bh) > Math.Max(2, bw / 10)) return null;

        // um quadrado de canto vivo (o ícone azul das Configurações, um terminal escuro) não é a moldura
        if (A(bgra, width, l, t) >= 128 || A(bgra, width, r, t) >= 128 ||
            A(bgra, width, l, b) >= 128 || A(bgra, width, r, b) >= 128) return null;

        // ── a cor do fundo: a mais comum numa faixa logo dentro do contorno, no meio de cada lado ──
        double s = Math.Max(bw, bh);
        var d0 = Math.Max(1, (int)Math.Round(s * 0.05));
        var d1 = Math.Max(d0 + 1, (int)Math.Round(s * 0.11));
        int k0 = (int)(s * 0.3), k1 = (int)(s * 0.7);

        var amostras = new List<(int R, int G, int B)>();
        for (var d = d0; d < d1; d++)
            for (var k = k0; k <= k1; k++)
            {
                Sample(l + d, t + k);
                Sample(r - d, t + k);
                Sample(l + k, t + d);
                Sample(l + k, b - d);
            }
        if (amostras.Count < 8) return null;

        var balde = amostras.GroupBy(c => (c.R >> 3, c.G >> 3, c.B >> 3)).OrderByDescending(g => g.Count()).First();
        var fundo = (R: (int)balde.Average(c => c.R), G: (int)balde.Average(c => c.G), B: (int)balde.Average(c => c.B));

        if (Math.Max(fundo.R, Math.Max(fundo.G, fundo.B)) > 64) return null;   // fundo claro: não é a família
        if (amostras.Count(c => Near(c, fundo)) < amostras.Count / 2) return null;

        var noFundo = 0;
        for (var y = t; y <= b; y++)
            for (var x = l; x <= r; x++)
            {
                var o = (y * width + x) * 4;
                if (bgra[o + 3] >= 200 && Near((bgra[o + 2], bgra[o + 1], bgra[o]), fundo)) noFundo++;
            }
        if (noFundo < bw * bh / 4) return null;   // o fundo tem de ser boa parte do quadrado

        // ── tira o fundo, o contorno e a borda do quadrado ──
        var saida = new byte[width * height * 4];
        var raio = s * CornerRatio;
        var contorno = Math.Max(1.0, s * OutlineRatio);
        double cx = l + bw / 2.0, cy = t + bh / 2.0, hx = bw / 2.0, hy = bh / 2.0;

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                int a = bgra[o + 3];
                if (a == 0) continue;

                // distância até a borda do quadrado arredondado: negativa dentro dele
                var qx = Math.Abs(x + 0.5 - cx) - (hx - raio);
                var qy = Math.Abs(y + 0.5 - cy) - (hy - raio);
                var dist = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2))
                           + Math.Min(Math.Max(qx, qy), 0) - raio;
                if (dist > -(contorno + 1.0)) continue;

                int pb = bgra[o], pg = bgra[o + 1], pr = bgra[o + 2];
                var alfa = Math.Max(Fraction(pr, fundo.R), Math.Max(Fraction(pg, fundo.G), Fraction(pb, fundo.B)));
                if (alfa < 0.06) continue;

                saida[o] = Unmix(pb, fundo.B, alfa);
                saida[o + 1] = Unmix(pg, fundo.G, alfa);
                saida[o + 2] = Unmix(pr, fundo.R, alfa);
                saida[o + 3] = (byte)Math.Round(a * alfa);
            }

        return Fit(saida, width, height, occupy, out side);

        void Sample(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var o = (y * width + x) * 4;
            if (bgra[o + 3] >= 200) amostras.Add((bgra[o + 2], bgra[o + 1], bgra[o]));
        }
    }

    private static int A(byte[] bgra, int width, int x, int y) => bgra[(y * width + x) * 4 + 3];

    private static bool Near((int R, int G, int B) c, (int R, int G, int B) f) =>
        Math.Abs(c.R - f.R) <= 14 && Math.Abs(c.G - f.G) <= 14 && Math.Abs(c.B - f.B) <= 14;

    /// <summary>Quanto do desenho é preciso, num canal, para sair do fundo até esta cor.</summary>
    private static double Fraction(int p, int fundo) =>
        p > fundo ? (p - fundo) / (255.0 - fundo)
        : p < fundo && fundo > 0 ? (fundo - p) / (double)fundo
        : 0;

    /// <summary>A cor do desenho antes de ser misturada ao fundo.</summary>
    private static byte Unmix(int p, int fundo, double alfa) =>
        (byte)Math.Clamp(Math.Round((p - fundo) / alfa + fundo), 0, 255);

    /// <summary>
    /// O desenho num quadrado só dele, centrado, ocupando <paramref name="occupy"/> do lado — sem a
    /// moldura sobra a folga que ela ocupava, e o desenho ficaria pequeno no botão.
    /// </summary>
    private static byte[]? Fit(byte[] px, int width, int height, double occupy, out int side)
    {
        side = 0;
        int l = width, t = height, r = -1, b = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (px[(y * width + x) * 4 + 3] >= 24)
                {
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    if (y > b) b = y;
                }
        if (r < 0) return null;   // a moldura não tinha desenho dentro

        int cw = r - l + 1, ch = b - t + 1;
        side = Math.Max((int)Math.Ceiling(Math.Max(cw, ch) / Math.Clamp(occupy, 0.5, 1.0)), Math.Max(cw, ch));
        var saida = new byte[side * side * 4];
        int dx = (side - cw) / 2, dy = (side - ch) / 2;
        for (var y = 0; y < ch; y++)
            Buffer.BlockCopy(px, ((t + y) * width + l) * 4, saida, ((dy + y) * side + dx) * 4, cw * 4);
        return saida;
    }
}
