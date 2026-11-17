using WinDock.Services;
using static WinDock.Interop.Native;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

// ══ quais arquivos entram ═══════════════════════════════════════════════════════════════════
Console.WriteLine("── Filter ──");

var pasta = new[]
{
    @"D:\W\b.MP4", @"D:\W\a.gif", @"D:\W\Capela.png", @"D:\W\foto.JPG", @"D:\W\notas.txt",
    @"D:\W\clip.webm", @"D:\W\sem-extensao", @"D:\W\Zelda.jpeg", @"D:\W\c.mov",
};

var animados = WallpaperRules.Filter(pasta, WallpaperKind.Animated);
Check("animados: vídeo e GIF, em ordem alfabética, maiúscula não importa",
      string.Join(",", animados.Select(Path.GetFileName)) == "a.gif,b.MP4,c.mov,clip.webm",
      string.Join(",", animados.Select(Path.GetFileName)));

var estaticos = WallpaperRules.Filter(pasta, WallpaperKind.Static);
Check("estáticos: só imagem que o Windows aceita",
      string.Join(",", estaticos.Select(Path.GetFileName)) == "Capela.png,foto.JPG,Zelda.jpeg",
      string.Join(",", estaticos.Select(Path.GetFileName)));

var todos = WallpaperRules.Filter(pasta, WallpaperKind.All);
Check("os dois: sete, sem o .txt e o sem extensão", todos.Count == 7, todos.Count.ToString());
Check("GIF é animado, PNG não", WallpaperRules.IsAnimated("x.GIF") && !WallpaperRules.IsAnimated("x.png"));

// ══ a rotação ═══════════════════════════════════════════════════════════════════════════════
Console.WriteLine("── Next ──");

var lista = new List<string> { "a", "b", "c" };
var sorte = new Random(7);
Check("em ordem: o seguinte", WallpaperRules.Next(lista, "a", false, sorte) == "b");
Check("em ordem: do último volta ao primeiro", WallpaperRules.Next(lista, "c", false, sorte) == "a");
Check("em ordem: atual fora da lista vai ao primeiro", WallpaperRules.Next(lista, "zz", false, sorte) == "a");
Check("em ordem: sem atual vai ao primeiro", WallpaperRules.Next(lista, null, false, sorte) == "a");
Check("em ordem: maiúscula não importa", WallpaperRules.Next(lista, "B", false, sorte) == "c");
Check("lista vazia: nenhum", WallpaperRules.Next(new List<string>(), "a", true, sorte) is null);
Check("um só: ele mesmo, nas duas ordens",
      WallpaperRules.Next(new List<string> { "x" }, "x", true, sorte) == "x" &&
      WallpaperRules.Next(new List<string> { "x" }, "x", false, sorte) == "x");

var vistos = new HashSet<string>();
var repetiu = false;
for (var i = 0; i < 300; i++)
{
    var n = WallpaperRules.Next(lista, "b", true, sorte)!;
    if (n == "b") repetiu = true;
    vistos.Add(n);
}
Check("aleatória: nunca repete o atual", !repetiu);
Check("aleatória: alcança todos os outros", vistos.SetEquals(new[] { "a", "c" }), string.Join(",", vistos));

vistos.Clear();
for (var i = 0; i < 300; i++) vistos.Add(WallpaperRules.Next(lista, "fora", true, sorte)!);
Check("aleatória, atual fora da lista: qualquer um dos três", vistos.Count == 3, string.Join(",", vistos));

// ══ quanto da área de trabalho está à mostra ════════════════════════════════════════════════
Console.WriteLine("── VisibleFraction ──");

var util = R(0, 24, 2560, 1020);   // a área útil desta máquina: barra de cima e dock fora
Check("nada aberto: tudo à mostra", WallpaperRules.VisibleFraction(util, new List<RECT>()) == 1.0);
Check("uma maximizada: nada à mostra",
      WallpaperRules.VisibleFraction(util, new List<RECT> { R(0, 24, 2560, 1020) }) == 0.0);

// o mosaico com folga de 8 px: duas janelas lado a lado e as frestas entre elas e na borda
var mosaico = new List<RECT> { R(8, 32, 1276, 1012), R(1284, 32, 2552, 1012) };
var fresta = WallpaperRules.VisibleFraction(util, mosaico);
Check("mosaico: as frestas de 8 px contam como coberto", fresta < WallpaperRules.MinVisible, fresta.ToString("P1"));

var metade = WallpaperRules.VisibleFraction(util, new List<RECT> { R(0, 24, 1280, 1020) });
Check("meia tela livre: metade à mostra", Math.Abs(metade - 0.5) < 0.001, metade.ToString("P1"));

var fora = WallpaperRules.VisibleFraction(util, new List<RECT> { R(-1920, 0, 0, 1080) });
Check("janela no outro monitor não cobre este", fora == 1.0, fora.ToString("P1"));

var canto = WallpaperRules.VisibleFraction(util, new List<RECT> { R(0, 24, 2560, 1020), R(2400, 900, 2560, 1020) });
Check("janelas sobrepostas não contam duas vezes", canto == 0.0, canto.ToString("P1"));

Check("área vazia: zero", WallpaperRules.VisibleFraction(R(0, 0, 0, 0), new List<RECT>()) == 0.0);

// ══ GIF ═════════════════════════════════════════════════════════════════════════════════════
Console.WriteLine("── GIF ──");

Check("atraso 0 e 1 viram 100 ms, como no navegador",
      WallpaperRules.GifDelay(0).TotalMilliseconds == 100 && WallpaperRules.GifDelay(1).TotalMilliseconds == 100);
Check("atraso 4 = 40 ms", WallpaperRules.GifDelay(4).TotalMilliseconds == 40);

Check("GIF de 2,64 s: 8 voltas para passar de 20 s",
      WallpaperRules.Repetitions(TimeSpan.FromSeconds(2.64), TimeSpan.FromSeconds(20)) == 8);
Check("GIF de 30 s: uma volta só", WallpaperRules.Repetitions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20)) == 1);
Check("GIF sem tempo: uma volta", WallpaperRules.Repetitions(TimeSpan.Zero, TimeSpan.FromSeconds(20)) == 1);

// tela 4×2; o primeiro quadro cobre tudo de vermelho, o segundo pinta só (1,0)-(3,1) de azul,
// com o pixel do meio transparente
byte[] Px(int w, int h, byte b, byte g, byte r, byte a)
{
    var p = new byte[w * h * 4];
    for (var i = 0; i < p.Length; i += 4) { p[i] = b; p[i + 1] = g; p[i + 2] = r; p[i + 3] = a; }
    return p;
}
string Cor(byte[] tela, int w, int x, int y)
{
    var o = (y * w + x) * 4;
    return tela[o + 3] == 0 ? "-" : tela[o + 2] == 255 ? "R" : tela[o] == 255 ? "B" : "?";
}
string Mapa(byte[] tela, int w, int h) =>
    string.Join("/", Enumerable.Range(0, h).Select(y => string.Concat(Enumerable.Range(0, w).Select(x => Cor(tela, w, x, y)))));

var tela = new byte[4 * 2 * 4];
WallpaperRules.Paint(tela, 4, 2, Px(4, 2, 0, 0, 255, 255), 0, 0, 4, 2);
Check("quadro inteiro pinta tudo", Mapa(tela, 4, 2) == "RRRR/RRRR", Mapa(tela, 4, 2));

var azul = Px(2, 1, 255, 0, 0, 255);
azul[7] = 0;   // o segundo pixel do quadro, transparente
WallpaperRules.Paint(tela, 4, 2, azul, 1, 0, 2, 1);
Check("quadro parcial: só o retângulo dele, e o transparente deixa ver o de baixo",
      Mapa(tela, 4, 2) == "RBRR/RRRR", Mapa(tela, 4, 2));

WallpaperRules.Paint(tela, 4, 2, Px(3, 3, 255, 0, 0, 255), 2, 1, 3, 3);
Check("quadro que passa da borda é cortado, sem estourar", Mapa(tela, 4, 2) == "RBRR/RRBB", Mapa(tela, 4, 2));

WallpaperRules.Clear(tela, 4, 2, 1, 0, 2, 1);
Check("descarte 2 limpa só o retângulo do quadro", Mapa(tela, 4, 2) == "R--R/RRBB", Mapa(tela, 4, 2));

WallpaperRules.Clear(tela, 4, 2, 3, 1, 5, 5);
Check("descarte que passa da borda é cortado", Mapa(tela, 4, 2) == "R--R/RRB-", Mapa(tela, 4, 2));

var opaca = WallpaperRules.Opaque(tela);
Check("opaca: o transparente vira preto e a tela original não muda",
      opaca[7] == 255 && opaca[4] == 0 && opaca[5] == 0 && opaca[6] == 0 && tela[7] == 0);

// ══ nome do vídeo convertido ════════════════════════════════════════════════════════════════
Console.WriteLine("── CacheName ──");

var quando = new DateTime(2026, 11, 16, 16, 15, 0, DateTimeKind.Utc);
var n1 = WallpaperRules.CacheName(@"D:\W\a.gif", 1000, quando);
Check("o mesmo arquivo dá o mesmo nome, maiúscula não importa",
      n1 == WallpaperRules.CacheName(@"d:\w\A.GIF", 1000, quando));
Check("tamanho diferente, nome diferente", n1 != WallpaperRules.CacheName(@"D:\W\a.gif", 1001, quando));
Check("data diferente, nome diferente", n1 != WallpaperRules.CacheName(@"D:\W\a.gif", 1000, quando.AddSeconds(1)));
Check("termina em .mp4 e não tem caractere estranho",
      n1.EndsWith(".mp4") && n1[..^4].All(char.IsAsciiHexDigitLower), n1);

// ══ imagem com vida: onde o Windows põe a imagem ═══════════════════════════════════════════
Console.WriteLine("── LivingRules.FitOf / Place ──");

Check("ajustes do Windows: Preencher, Ajustar, Ampliar, Centralizar",
      LivingRules.FitOf("10", "0") == WallpaperFit.Fill && LivingRules.FitOf("6", "0") == WallpaperFit.Fit &&
      LivingRules.FitOf("2", "0") == WallpaperFit.Stretch && LivingRules.FitOf("0", "0") == WallpaperFit.Center);
Check("sem valor gravado vale o padrão do Windows 11, Preencher",
      LivingRules.FitOf(null, null) == WallpaperFit.Fill && LivingRules.FitOf("", null) == WallpaperFit.Fill);
Check("Lado a lado e Estender: a dock não sabe onde cada vela foi parar",
      LivingRules.FitOf("0", "1") is null && LivingRules.FitOf("22", "0") is null && LivingRules.FitOf("99", "0") is null);

var (fs, fsy, fx, fy) = LivingRules.Place(WallpaperFit.Fill, 1938, 812, 2560, 1080);
Check("Capela no monitor largo, Preencher: a altura manda, sobra 8,8 px de cada lado (o mesmo pixel do Windows)",
      Math.Abs(fs - 1080.0 / 812) < 1e-9 && fs == fsy && Math.Abs(fx - (2560 - 1938 * fs) / 2) < 1e-9 && fx < 0 && fy == 0,
      $"{fs} {fx} {fy}");
var (gs, _, gx, _) = LivingRules.Place(WallpaperFit.Fill, 1938, 812, 1920, 1080);
Check("no monitor 16:9 a mesma escala, cortando mais dos lados", Math.Abs(gs - fs) < 1e-9 && gx < fx, $"{gs} {gx}");
var (js, _, jx, jy) = LivingRules.Place(WallpaperFit.Fit, 1938, 812, 1920, 1080);
Check("Ajustar: a imagem inteira cabe, com faixa em cima e embaixo",
      Math.Abs(js - 1920.0 / 1938) < 1e-9 && Math.Abs(jx) < 1e-9 && jy > 0, $"{js} {jx} {jy}");
var (ex, ey, ex0, ey0) = LivingRules.Place(WallpaperFit.Stretch, 1000, 1000, 2560, 1080);
Check("Ampliar: cada eixo na sua escala, a partir do canto", ex == 2.56 && ey == 1.08 && ex0 == 0 && ey0 == 0);
var (cs, _, cx, cy) = LivingRules.Place(WallpaperFit.Center, 1000, 800, 2560, 1080);
Check("Centralizar: tamanho real, no meio", cs == 1 && cx == 780 && cy == 140, $"{cs} {cx} {cy}");

byte[] Imagem(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> cor)
{
    var px = new byte[w * h * 4];
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
    {
        var (b, g, r, a) = cor(x, y);
        var p = (y * w + x) * 4;
        px[p] = b; px[p + 1] = g; px[p + 2] = r; px[p + 3] = a;
    }
    return px;
}

// ══ as luzes e o céu ════════════════════════════════════════════════════════════════════════
Console.WriteLine("── LivingRules.Analyze ──");

// fundo preto; um bloco de "pedra" escura e saturada; dentro dele um céu enevoado (cinza-azulado,
// com detalhe, como a paisagem atrás dos arcos) e as coisas claras que a análise tem de separar
const int CW = 400, CH = 240;
var pedraSorte = new Random(5);
var cena = Imagem(CW, CH, (x, y) =>
{
    if (x < 40 || x >= 360 || y < 20 || y >= 220) return (0, 0, 0, 255);                       // fundo
    if (x >= 220 && x < 340 && y >= 30 && y < 130)                                                // céu
    {
        var v = 120 + (x * 7 + y * 13) % 30 - 15;
        return ((byte)(v + 12), (byte)v, (byte)(v - 6), 255);
    }
    var t = pedraSorte.Next(25);
    return ((byte)(20 + t / 3), (byte)(30 + t / 2), (byte)(70 + t), 255);                         // pedra
});

void Pinta(int x0, int y0, int w, int h, (byte B, byte G, byte R) c)
{
    for (var y = y0; y < y0 + h; y++)
    for (var x = x0; x < x0 + w; x++)
    {
        var p = (y * CW + x) * 4;
        cena[p] = c.B; cena[p + 1] = c.G; cena[p + 2] = c.R;
    }
}

Pinta(80, 100, 6, 14, (150, 215, 250));    // chama em pé, miolo quase branco
Pinta(81, 102, 4, 6, (235, 250, 255));
Pinta(150, 140, 7, 16, (145, 212, 249));   // outra chama, miolo branco puro
Pinta(152, 142, 3, 8, (255, 255, 255));
Pinta(110, 60, 2, 30, (120, 225, 252));    // fio de ouro: claro, quente, mas fino
Pinta(170, 60, 24, 6, (150, 220, 250));    // auréola deitada
Pinta(60, 180, 3, 3, (200, 250, 255));     // pontinho claro pequeno demais
Pinta(300, 160, 6, 14, (255, 255, 255));   // luz branca e fria em pé: lâmpada, lua, janela — não é vela

var mapa = LivingRules.Analyze(cena, CW, CH);
var luzes = mapa.Lights.OrderBy(l => l.X).ToList();
Check("acha só as duas chamas", luzes.Count == 2, string.Join(" ", luzes.Select(l => $"({l.X:0},{l.Y:0})")));
Check("no lugar delas",
      luzes.Count == 2 && Math.Abs(luzes[0].X - 83) < 2 && Math.Abs(luzes[0].Y - 107) < 3
                       && Math.Abs(luzes[1].X - 153.5) < 2 && Math.Abs(luzes[1].Y - 148) < 3,
      string.Join(" ", luzes.Select(l => $"({l.X:0.0},{l.Y:0.0})")));
Check("halo maior que a chama e cor quente", luzes.All(l => l.Radius >= 16 && l.R > l.B));

byte Mascara(byte[] m, int x, int y) => m[(y / LivingRules.MaskStep) * mapa.MaskWidth + x / LivingRules.MaskStep];
Check("céu achado no meio da paisagem enevoada", Mascara(mapa.Sky, 280, 80) > 200, Mascara(mapa.Sky, 280, 80).ToString());
Check("nada de céu na pedra nem no fundo preto",
      Mascara(mapa.Sky, 100, 180) == 0 && Mascara(mapa.Sky, 10, 10) == 0 && Mascara(mapa.Sky, 380, 80) == 0);
Check("a fração de céu bate com o bloco (~12% da imagem)", mapa.SkyShare > 0.08 && mapa.SkyShare < 0.16, mapa.SkyShare.ToString("P1"));
Check("cena marcada onde há pedra, e não no fundo", Mascara(mapa.Body, 100, 180) > 200 && Mascara(mapa.Body, 10, 10) == 0);

var preta = LivingRules.Analyze(new byte[50 * 30 * 4], 50, 30);
Check("imagem toda preta: nada a acender, nada de céu", preta.Lights.Count == 0 && preta.SkyShare == 0);

// ══ neblina e partículas ════════════════════════════════════════════════════════════════════
Console.WriteLine("── neblina e partículas ──");

var neblina = LivingRules.FogTexture(256, 64, 3);
double Coluna(int a, int b)
{
    double d = 0;
    for (var y = 0; y < 64; y++) d += Math.Abs(neblina[y * 256 + a] - neblina[y * 256 + b]);
    return d / 64;
}
var vizinhas = Enumerable.Range(0, 255).Average(x => Coluna(x, x + 1));
Check("a neblina repete de lado: a última coluna emenda na primeira como duas vizinhas",
      Coluna(255, 0) <= vizinhas * 3 + 1, $"emenda {Coluna(255, 0):0.0}, vizinhas {vizinhas:0.0}");
Check("tem fiapo e tem vão", neblina.Count(a => a == 0) > 256 && neblina.Count(a => a > 180) > 256);
Check("a mesma semente dá a mesma neblina", LivingRules.FogTexture(256, 64, 3).SequenceEqual(neblina));

var parts = LivingRules.Particles(mapa, 9);
Check("a mesma semente dá as mesmas partículas", LivingRules.Particles(mapa, 9).SequenceEqual(parts));
var brasas = parts.Where(p => p.Ember).ToList();
Check("brasas saem das luzes e sobem",
      brasas.Count > 0 && brasas.All(b => luzes.Any(l => Math.Abs(b.X0 - l.X) <= l.Radius && Math.Abs(b.Y0 - l.Y) <= 4) && b.Y1 < b.Y0));
var poeira = parts.Where(p => !p.Ember).ToList();
Check("poeira nasce onde há cena, nunca no fundo preto",
      poeira.Count >= 12 && poeira.All(p => Mascara(mapa.Body, (int)p.X0, (int)p.Y0) > 0), poeira.Count.ToString());
Check("cada uma espera um tempo antes da primeira volta, para não nascerem juntas",
      parts.All(p => p.Delay >= 0 && p.Delay <= p.Seconds) && parts.Select(p => p.Delay).Distinct().Count() > parts.Count / 2);
Check("poucas: cada partícula que se mexe custa um redesenho no dwm", parts.Count <= 54, parts.Count.ToString());

var (cbx, cby, cbw, cbh) = mapa.SkyBox;
Check("o retângulo do céu cobre o bloco do céu e não a imagem toda",
      cbx * LivingRules.MaskStep <= 224 && (cbx + cbw) * LivingRules.MaskStep >= 336 &&
      cby * LivingRules.MaskStep <= 34 && (cby + cbh) * LivingRules.MaskStep >= 126 &&
      cbw * cbh < mapa.MaskWidth * mapa.MaskHeight / 3,
      $"{cbx},{cby} {cbw}x{cbh} casas");
Check("sem céu, retângulo vazio", preta.SkyBox.Width == 0);

// ══ cada quadro ═════════════════════════════════════════════════════════════════════════════
Console.WriteLine("── quadros ──");

var chaves = LivingRules.FlickerKeys(new Random(4), 8, 0.5f);
Check("tremulação fica entre o mínimo e 1",
      Enumerable.Range(0, 400).Select(i => LivingRules.Flicker(chaves, i / 97.0)).All(v => v >= 0.5f && v <= 1f));
Check("a volta da tremulação emenda sem salto",
      Math.Abs(LivingRules.Flicker(chaves, 0.9999) - LivingRules.Flicker(chaves, 0)) < 0.01 &&
      Math.Abs(LivingRules.Flicker(chaves, 3.0) - LivingRules.Flicker(chaves, 0)) < 1e-6);

var brasa = new LivingParticle(true, 100, 200, 104, 150, 3, 2, 0.8f, 5, 2);
Check("antes da espera dela, invisível", LivingRules.ParticleAt(brasa, 1.5).Opacity == 0);
var (bx0, by0, bo0) = LivingRules.ParticleAt(brasa, 2);
Check("nasce no ponto de partida, ainda apagada", Math.Abs(bx0 - 100) < 1e-3 && Math.Abs(by0 - 200) < 1e-3 && bo0 == 0);
var meio = LivingRules.ParticleAt(brasa, 2 + 2.5);
Check("no meio da volta subiu metade e aparece", Math.Abs(meio.Y - 175) < 1e-3 && meio.Opacity > 0.6f, $"{meio.Y} {meio.Opacity}");
var quaseFim = LivingRules.ParticleAt(brasa, 2 + 4.99);
Check("no fim da volta chegou lá em cima e quase sumiu", quaseFim.Y < 151 && quaseFim.Opacity < 0.02f, $"{quaseFim.Y} {quaseFim.Opacity}");
Check("a volta seguinte recomeça embaixo", Math.Abs(LivingRules.ParticleAt(brasa, 2 + 5 + 0.01).Y - 200) < 1);
Check("a opacidade nunca passa da dela",
      Enumerable.Range(0, 500).All(i => LivingRules.ParticleAt(brasa, i * 0.037).Opacity <= 0.8f + 1e-5));

// ══ figuras: o pedaço que respira ═══════════════════════════════════════════════════════════
Console.WriteLine("── figuras ──");

var sorteio = new Random(11);
var ruido = Imagem(37, 23, (_, _) => ((byte)sorteio.Next(256), (byte)sorteio.Next(256), (byte)sorteio.Next(256), 255));
Check("ampliação: escala 1 sem corte devolve a imagem igual",
      LivingRules.Resample(ruido, 37, 23, 1, 1, 0, 0, 37, 23).SequenceEqual(ruido));
var pedaco = LivingRules.Resample(ruido, 37, 23, 1, 1, 5, 3, 10, 8);
Check("ampliação: escala 1 com corte devolve o pedaço certo",
      pedaco.AsSpan(0, 4).SequenceEqual(ruido.AsSpan((3 * 37 + 5) * 4, 4)) &&
      pedaco.AsSpan((7 * 10 + 9) * 4, 4).SequenceEqual(ruido.AsSpan((10 * 37 + 14) * 4, 4)));
var lisa = LivingRules.Resample(Imagem(20, 10, (_, _) => (40, 90, 200, 255)), 20, 10, 1.33, 1.6, 0, 0, 26, 16);
Check("ampliação: cor lisa continua lisa, até na borda, mesmo com escalas diferentes nos eixos",
      Enumerable.Range(0, 26 * 16).All(i => lisa[i * 4] == 40 && lisa[i * 4 + 1] == 90 && lisa[i * 4 + 2] == 200 && lisa[i * 4 + 3] == 255));
var degrau = LivingRules.Resample(Imagem(10, 4, (x, _) => x < 5 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255)),
                                  10, 4, 3, 3, 0, 0, 30, 12);
Check("ampliação: pré-multiplicado, a cor nunca passa do alfa nem no repique da bicúbica",
      Enumerable.Range(0, 30 * 12).All(i => degrau[i * 4] <= degrau[i * 4 + 3] && degrau[i * 4 + 2] <= degrau[i * 4 + 3]));
var xadrez = LivingRules.Resample(Imagem(64, 64, (x, y) => ((x + y) % 2 == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255))),
                                  64, 64, 0.25, 0.25, 0, 0, 16, 16);
var cinzas = Enumerable.Range(0, 16 * 16).Select(i => (int)xadrez[i * 4]).ToList();
Check("redução: xadrez de 1 px vira cinza, e não serrilhado", cinzas.Min() > 100 && cinzas.Max() < 155, $"{cinzas.Min()}..{cinzas.Max()}");

// um quadrado de 20 px dentro de um retângulo de 30: borda esfumada de 2 px, base de 6
var quadrado = new (float X, float Y)[] { (5, 5), (25, 5), (25, 25), (5, 25) };
var mascara = LivingRules.OutlineMask(quadrado, 30, 30, 2f, 6f);
byte M(int x, int y) => mascara[y * 30 + x];
Check("máscara: fora do contorno é zero", M(2, 2) == 0 && M(27, 15) == 0 && M(15, 27) == 0);
Check("máscara: no meio, cheia", M(15, 12) == 255, M(15, 12).ToString());
Check("máscara: a borda dos lados esfuma em poucos pixels", M(5, 12) < 128 && M(8, 12) == 255, $"{M(5, 12)} {M(8, 12)}");
Check("máscara: a base esfuma mais, até sumir no contorno de baixo",
      M(15, 18) == 255 && M(15, 21) > 0 && M(15, 21) < 255 && M(15, 24) < M(15, 21), $"{M(15, 18)} {M(15, 21)} {M(15, 24)}");
Check("máscara: menos de três pontos não é contorno", LivingRules.OutlineMask(new (float, float)[] { (0, 0), (5, 5) }, 10, 10, 1, 1).All(a => a == 0));

// o idle do Penitente no jogo: 16 quadros em 1,2 s — parado 0–3, meio 4, em cima 5–11, meio 12, parado 13–15
var idle = Enumerable.Range(0, 16).Select(q => LivingRules.IdleOffset((q + 0.5) * 1.2 / 16, 1.2, 3)).ToList();
Check("idle: o ritmo do jogo, parado, meio caminho, em cima, meio caminho, parado",
      string.Join("", idle) == "0000233333332000", string.Join("", idle));
Check("idle: a volta seguinte repete", LivingRules.IdleOffset(1.2 + 0.6, 1.2, 3) == 3 && LivingRules.IdleOffset(2.4, 1.2, 3) == 0);
Check("idle: sem amplitude ou sem período, parado", LivingRules.IdleOffset(0.6, 1.2, 0) == 0 && LivingRules.IdleOffset(0.6, 0, 3) == 0);

// pano: 10 × 20, cada coluna numerada no azul (coluna 0 = 10, 1 = 20...)
var pano = Imagem(10, 20, (x, _) => ((byte)(10 * (x + 1)), (byte)0, (byte)0, (byte)255));
Check("pano: preso em cima e no lado preso, nada anda",
      Enumerable.Range(0, 10).All(x => LivingRules.ClothShift(x, 0, 10, 20, 0.3, 3, 30, ClothSide.Left) == 0) &&
      Enumerable.Range(0, 20).All(y => LivingRules.ClothShift(9, y, 10, 20, 0.3, 3, 30, ClothSide.Left) == 0));
var pontas = Enumerable.Range(0, 40).Select(i => LivingRules.ClothShift(0, 19, 10, 20, i / 40.0, 3, 30, ClothSide.Left)).ToList();
Check("pano: a ponta solta de baixo vai e volta até a amplitude", pontas.Max() > 2.9 && pontas.Min() < -2.9, $"{pontas.Min():0.0}..{pontas.Max():0.0}");
Check("pano: lado solto à direita espelha", LivingRules.ClothShift(9, 19, 10, 20, 0.25, 3, 30, ClothSide.Right) == LivingRules.ClothShift(0, 19, 10, 20, 0.25, 3, 30, ClothSide.Left));
var quadroPano = LivingRules.ClothFrame(pano, 10, 20, 0.25, 3, 30, ClothSide.Left);
byte NoPano(byte[] q, int x, int y) => q[(y * 10 + x) * 4];
Check("pano: a linha de cima sai igual, e cada pixel vem de uma coluna inteira (sem borrar)",
      Enumerable.Range(0, 10).All(x => NoPano(quadroPano, x, 0) == NoPano(pano, x, 0)) &&
      Enumerable.Range(0, 200).All(i => quadroPano[i * 4] % 10 == 0));
Check("bandeira: solta dos dois lados, a barra inteira anda junto",
      Enumerable.Range(0, 10).All(x => LivingRules.ClothShift(x, 19, 10, 20, 0.25, 3, 30, ClothSide.Both) ==
                                       LivingRules.ClothShift(0, 19, 10, 20, 0.25, 3, 30, ClothSide.Left)) &&
      LivingRules.ClothShift(5, 0, 10, 20, 0.25, 3, 30, ClothSide.Both) == 0);

Check("corpo preso embaixo: a linha de baixo não anda, a de cima balança até a amplitude",
      Enumerable.Range(0, 10).All(x => LivingRules.ClothShift(x, 19, 10, 20, 0.3, 3, 60, ClothSide.Both, anchorBottom: true) == 0) &&
      Enumerable.Range(0, 40).Select(i => LivingRules.ClothShift(5, 0, 10, 20, i / 40.0, 3, 60, ClothSide.Both, anchorBottom: true)).Max() > 2.9);
var corpo = LivingRules.ClothFrame(pano, 10, 20, 0.25, 3, 60, ClothSide.Both, anchorBottom: true);
Check("corpo preso embaixo: o quadro mantém a última linha igual",
      Enumerable.Range(0, 10).All(x => NoPano(corpo, x, 19) == NoPano(pano, x, 19)));

// os pesos da respiração: parado em cima (gola), inteiro no meio (ombros), parado embaixo (cinto)
var pesos = LivingRules.RowWeights(20, 2, 4, 14);
Check("pesos: zero acima do topo e na última linha, um no meio, sem pulo",
      pesos[0] == 0 && pesos[2] == 0 && pesos[6] == 1 && pesos[13] == 1 && pesos[19] == 0 &&
      pesos.Zip(pesos.Skip(1)).All(p => Math.Abs(p.First - p.Second) <= 0.26), string.Join(" ", pesos.Select(v => v.ToString("0.00"))));
var linhas20 = Imagem(4, 20, (_, y) => ((byte)(10 + 10 * y), (byte)0, (byte)0, (byte)255));
Check("deslocar com peso zero devolve igual", LivingRules.ShiftFrame(linhas20, 4, 20, new double[20], -2).SequenceEqual(linhas20));
var assentado = LivingRules.ShiftFrame(linhas20, 4, 20, pesos, -2);
byte L20(byte[] q, int y) => q[y * 16];
Check("assentado: em cima e embaixo igual, o meio 2 linhas abaixo, sem buraco e sem inverter",
      L20(assentado, 0) == L20(linhas20, 0) && L20(assentado, 19) == L20(linhas20, 19) &&
      L20(assentado, 10) == L20(linhas20, 8) &&
      Enumerable.Range(0, 20).All(y => L20(assentado, y) != 0) &&
      Enumerable.Range(1, 19).All(y => L20(assentado, y) >= L20(assentado, y - 1)),
      string.Join(",", Enumerable.Range(0, 20).Select(y => L20(assentado, y))));

// a respiração: assenta e volta, devagar nas pontas, passando por todos os degraus
var assenta = Enumerable.Range(0, 24).Select(q => LivingRules.SinkOffset(q * 2.2 / 24, 2.2, 3)).ToList();
Check("respiração: parte do repouso, chega a 3 px no meio da volta e volta",
      assenta[0] == 0 && assenta[12] == 3 && assenta.Max() == 3 && assenta.Min() == 0 && LivingRules.SinkOffset(2.2, 2.2, 3) == 0,
      string.Join("", assenta));
Check("respiração: sem pulo, um degrau por vez", assenta.Zip(assenta.Skip(1)).All(p => Math.Abs(p.First - p.Second) <= 1),
      string.Join("", assenta));
Check("respiração: fica mais tempo perto das pontas que no meio do caminho (devagar nas pontas)",
      assenta.Count(v => v == 0) + assenta.Count(v => v == 3) > assenta.Count(v => v is 1 or 2), string.Join("", assenta));

// pedaço de 4 × 10 com cada linha numerada no canal azul (linha 0 = 10, 1 = 20...), o topo da
// figura na linha 2
var faixas = Imagem(4, 10, (_, y) => ((byte)(10 * (y + 1)), (byte)0, (byte)0, (byte)255));
byte Linha(byte[] q, int y) => q[y * 4 * 4];
Check("quadro 0 é o pedaço igual", LivingRules.BreathFrame(faixas, 4, 10, 2, 0).SequenceEqual(faixas));
var esticado = LivingRules.BreathFrame(faixas, 4, 10, 2, 2);
Check("esticado: o topo sobe 2 linhas, a base fica, e o meio repete linha (sem borrar)",
      Linha(esticado, 0) == Linha(faixas, 2) && Linha(esticado, 9) == Linha(faixas, 9) &&
      Enumerable.Range(0, 10).All(y => faixas.Where((_, i) => i % 16 == 0).Contains(Linha(esticado, y)) || Linha(esticado, y) == 0),
      string.Join(",", Enumerable.Range(0, 10).Select(y => Linha(esticado, y))));
var achatado = LivingRules.BreathFrame(faixas, 4, 10, 2, -2);
Check("achatado: o topo desce 2 linhas (acima dele, o que estava acima no pedaço), a base fica",
      Linha(achatado, 4) == Linha(faixas, 2) && Linha(achatado, 9) == Linha(faixas, 9) &&
      Linha(achatado, 0) == 0 && Linha(achatado, 1) == 0,
      string.Join(",", Enumerable.Range(0, 10).Select(y => Linha(achatado, y))));
Check("achatado e esticado nunca invertem a ordem das linhas",
      Enumerable.Range(1, 9).All(y => Linha(achatado, y) == 0 || Linha(achatado, y) >= Linha(achatado, y - 1)) &&
      Enumerable.Range(1, 9).All(y => Linha(esticado, y) >= Linha(esticado, y - 1)));

Check("brilho: só passa durante a passada", LivingRules.Sweep(0.5, 7, 0.9, 0) is > 0.5 and < 0.6 && LivingRules.Sweep(2, 7, 0.9, 0) is null);
Check("brilho: anda do punho para a ponta", LivingRules.Sweep(0.1, 7, 0.9, 0) < LivingRules.Sweep(0.8, 7, 0.9, 0));
Check("brilho: volta a passar a cada período", LivingRules.Sweep(7.45, 7, 0.9, 0) is > 0.49 and < 0.51);

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} falha(s)");
return falhas == 0 ? 0 : 1;
