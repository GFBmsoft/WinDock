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

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} falha(s)");
return falhas == 0 ? 0 : 1;
