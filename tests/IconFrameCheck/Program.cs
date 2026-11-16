using WinDock.Services;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

const int N = 64;
const double Ocupa = 0.86;

// Um ícone como o do gerar-icone.ps1, em 64 px: quadrado arredondado de 2 a 61 (raio 54/244 do
// lado), cheio de "fundo" e contornado por "borda"; o desenho é um quadrado branco de 16 px no
// meio, com uma coluna de borda suavizada (metade branco, metade fundo) e uma faixa translúcida.
byte[] Moldura((int R, int G, int B) fundo, (int R, int G, int B) borda, bool cantoVivo = false)
{
    var px = new byte[N * N * 4];
    double l = 2, r = 62, raio = cantoVivo ? 0 : 60 * 54.0 / 244, c = (l + r) / 2, h = (r - l) / 2;
    for (var y = 0; y < N; y++)
        for (var x = 0; x < N; x++)
        {
            var qx = Math.Abs(x + 0.5 - c) - (h - raio);
            var qy = Math.Abs(y + 0.5 - c) - (h - raio);
            var d = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - raio;
            if (d >= 0) continue;
            Set(px, x, y, d > -1.5 ? borda : fundo, 255);
        }

    for (var y = 24; y < 40; y++)
        for (var x = 24; x < 40; x++) Set(px, x, y, (255, 255, 255), 255);
    for (var y = 24; y < 40; y++) Set(px, 40, y, Mix((255, 255, 255), fundo, 0.5), 255);         // borda suavizada
    for (var x = 24; x < 40; x++) Set(px, x, 44, Mix((0xF0, 0x88, 0x3E), fundo, 48 / 255.0), 255); // preenchimento a 19%
    return px;
}

(int R, int G, int B) Mix((int R, int G, int B) a, (int R, int G, int B) f, double t) =>
    ((int)Math.Round(a.R * t + f.R * (1 - t)), (int)Math.Round(a.G * t + f.G * (1 - t)), (int)Math.Round(a.B * t + f.B * (1 - t)));

void Set(byte[] px, int x, int y, (int R, int G, int B) c, int a)
{
    var o = (y * N + x) * 4;
    px[o] = (byte)c.B; px[o + 1] = (byte)c.G; px[o + 2] = (byte)c.R; px[o + 3] = (byte)a;
}

(int R, int G, int B, int A) Get(byte[] px, int lado, int x, int y)
{
    var o = (y * lado + x) * 4;
    return (px[o + 2], px[o + 1], px[o], px[o + 3]);
}

/// onde foi parar um ponto do ícone original depois do recorte
(int X, int Y) Para(int lado, int x, int y)
{
    // o desenho vai de 24 a 40 (a coluna suavizada) e de 24 a 44 (a faixa): 17×21, centrado
    int cw = 17, ch = 21, dx = (lado - cw) / 2, dy = (lado - ch) / 2;
    return (x - 24 + dx, y - 24 + dy);
}

// ══ a moldura da família, nas cores de cada programa ════════════════════════════════════════
foreach (var (nome, fundo, borda) in new[]
{
    ("#1B1F2A (GConv, GSQL…)", (0x1B, 0x1F, 0x2A), (0x2A, 0x30, 0x3D)),
    ("#161A21 (GRepos, GTerm)", (0x16, 0x1A, 0x21), (0x2B, 0x33, 0x3F)),
    ("#1E1E2E (GTest)", (0x1E, 0x1E, 0x2E), (0x31, 0x32, 0x44)),
})
{
    Console.WriteLine($"── {nome} ──");
    var sem = IconFrame.Strip(Moldura(fundo, borda), N, N, Ocupa, out var lado);
    Check("acha a moldura", sem is not null);
    if (sem is null) continue;

    Check("o desenho ocupa 86% do quadrado", lado == (int)Math.Ceiling(21 / Ocupa), lado.ToString());

    var (cx, cy) = Para(lado, 32, 32);
    var meio = Get(sem, lado, cx, cy);
    Check("o meio do desenho continua branco e opaco", meio == (255, 255, 255, 255), meio.ToString());

    var canto = Get(sem, lado, 0, 0);
    Check("o canto ficou transparente", canto.A == 0, canto.ToString());

    var (sx, sy) = Para(lado, 40, 30);
    var suave = Get(sem, lado, sx, sy);
    Check("a borda suavizada vira branco a meio alfa, sem franja escura",
          Math.Abs(suave.A - 128) <= 3 && suave.R >= 250 && suave.G >= 250 && suave.B >= 250, suave.ToString());

    // cor para alfa escolhe o menor alfa que explica o pixel: a cor sai um pouco mais viva e o
    // alfa um pouco menor que os do desenho original, e posta de volta sobre o fundo dá o mesmo pixel
    var (fx, fy) = Para(lado, 30, 44);
    var faixa = Get(sem, lado, fx, fy);
    var original = Mix((0xF0, 0x88, 0x3E), fundo, 48 / 255.0);
    var deVolta = Mix((faixa.R, faixa.G, faixa.B), fundo, faixa.A / 255.0);
    Check("o preenchimento translúcido continua translúcido e, sobre o fundo de antes, dá o mesmo pixel",
          faixa.A is > 30 and < 70 && Math.Abs(deVolta.R - original.R) <= 2 && Math.Abs(deVolta.G - original.G) <= 2 &&
          Math.Abs(deVolta.B - original.B) <= 2,
          $"{faixa} -> {deVolta} (antes {original})");

    var restos = 0;
    for (var y = 0; y < lado; y++)
        for (var x = 0; x < lado; x++)
        {
            var p = Get(sem, lado, x, y);
            if (p.A > 0 && p.R < 120 && p.G < 120 && p.B < 120) restos++;
        }
    Check("não sobra pixel escuro do fundo nem do contorno", restos == 0, restos + " pixels");
}

// ══ o que não pode ser tocado ═══════════════════════════════════════════════════════════════
Console.WriteLine("── não é a moldura ──");

Check("quadrado arredondado claro (o azul das Configurações)",
      IconFrame.Strip(Moldura((0x00, 0x78, 0xD7), (0x00, 0x78, 0xD7)), N, N, Ocupa, out _) is null);

Check("quadrado escuro de canto vivo (um terminal)",
      IconFrame.Strip(Moldura((0x1B, 0x1F, 0x2A), (0x2A, 0x30, 0x3D), cantoVivo: true), N, N, Ocupa, out _) is null);

var soDesenho = new byte[N * N * 4];
for (var y = 10; y < 54; y++)
    for (var x = 10; x < 54; x++) Set(soDesenho, x, y, (0xFF, 0xC8, 0x57), 255);
for (var y = 20; y < 44; y++)
    for (var x = 20; x < 44; x++) Set(soDesenho, x, y, (0x20, 0x20, 0x20), 255);
Check("desenho sem fundo, de canto vivo", IconFrame.Strip(soDesenho, N, N, Ocupa, out _) is null);

Check("todo transparente", IconFrame.Strip(new byte[N * N * 4], N, N, Ocupa, out _) is null);
Check("pequeno demais", IconFrame.Strip(new byte[8 * 8 * 4], 8, 8, Ocupa, out _) is null);

var vazia = Moldura((0x1B, 0x1F, 0x2A), (0x2A, 0x30, 0x3D));
for (var y = 20; y < 46; y++)
    for (var x = 20; x < 46; x++) Set(vazia, x, y, (0x1B, 0x1F, 0x2A), 255);
Check("moldura sem desenho nenhum dentro: deixa como está", IconFrame.Strip(vazia, N, N, Ocupa, out _) is null);

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} falha(s)");
return falhas == 0 ? 0 : 1;
