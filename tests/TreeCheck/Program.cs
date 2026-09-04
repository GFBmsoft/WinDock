using WinDock.Services.Tiling;
using static WinDock.Interop.Native;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

var area = new RECT { Left = 0, Top = 0, Right = 1000, Bottom = 600 };
var vazio = new HashSet<nint>();
nint mon = 1;

// ── uma janela ocupa tudo
var t = new LayoutTree();
t.Insert(1, mon, 0, SplitLayout.Horizontal);
var r = t.ComputeRects(mon, area, 10, vazio);
Check("uma janela ocupa a area toda", r[1].Width == 1000 && r[1].Height == 600, r[1].ToString());

// ── duas janelas: metade cada, com o gap saindo do meio
t.Insert(2, mon, 1, SplitLayout.Horizontal);
r = t.ComputeRects(mon, area, 10, vazio);
Check("duas janelas dividem igual", r[1].Width == 495 && r[2].Width == 495, $"{r[1]} | {r[2]}");
Check("o gap fica entre elas", r[2].Left - r[1].Right == 10, $"{r[1].Right} -> {r[2].Left}");
Check("nao sobra fresta na borda", r[2].Right == 1000, r[2].ToString());

// ── redimensionar tira do vizinho
t.Resize(1, SplitLayout.Horizontal, towardsEnd: true, 100);
r = t.ComputeRects(mon, area, 10, vazio);
Check("redimensionar alarga a primeira", r[1].Width == 595, r[1].ToString());
Check("redimensionar encolhe a vizinha", r[2].Width == 395, r[2].ToString());
Check("a soma continua batendo", r[2].Right == 1000 && r[1].Left == 0, $"{r[1]} | {r[2]}");

// ── a proporcao sobrevive a uma terceira janela? nao deve: a estrutura mudou
t.Insert(3, mon, 2, SplitLayout.Horizontal);
r = t.ComputeRects(mon, area, 10, vazio);
// a terceira entra no mesmo nivel das outras: todas repartem por igual, lado a lado. Entrar
// implica mudanca de estrutura, entao as proporcoes escolhidas antes voltam ao "divide igual"
Check("a terceira entra no mesmo nivel e as tres ficam iguais",
      r[1].Width == 326 && r[2].Width == 326 && r[3].Width == 328,
      $"{r[1]} , {r[2]} , {r[3]}");
Check("as tres ficam lado a lado, nenhuma empilhada",
      r[1].Top == r[2].Top && r[2].Top == r[3].Top && r[1].Left < r[2].Left && r[2].Left < r[3].Left,
      $"{r[1]} , {r[2]} , {r[3]}");

// ── proporcao SOBREVIVE a minimizar, que nao muda a estrutura
t.Resize(1, SplitLayout.Horizontal, towardsEnd: true, 90);
var antes = t.ComputeRects(mon, area, 10, vazio)[1].Width;
var comMinimizada = t.ComputeRects(mon, area, 10, new HashSet<nint> { 3 });
Check("minimizar redistribui o espaco entre as que sobraram",
      comMinimizada.Count == 2 && comMinimizada[2].Right == 1000, $"{comMinimizada.Count}");
var depois = t.ComputeRects(mon, area, 10, vazio)[1].Width;
Check("restaurar devolve a mesma proporcao de antes", antes == depois, $"{antes} vs {depois}");
Check("a janela restaurada volta ao mesmo lugar na ordem, nao para o fim",
      t.ComputeRects(mon, area, 10, vazio)[3].Left > t.ComputeRects(mon, area, 10, vazio)[2].Left);

// ── trocar de lugar leva o tamanho do lugar, nao da janela
var largura1 = t.ComputeRects(mon, area, 10, vazio)[1].Width;
t.Swap(1, 3);
r = t.ComputeRects(mon, area, 10, vazio);
Check("quem vai pro lugar maior fica maior", r[3].Width == largura1, $"{r[3].Width} vs {largura1}");

// ── fechar uma janela: o espaco vai pras que sobraram e nada se perde
t.Remove(3);
r = t.ComputeRects(mon, area, 10, vazio);
Check("fechar devolve o espaco todo", r.Count == 2 && r.Values.Max(v => v.Right) == 1000, $"{r.Count}");

// ── limite: ninguem pode ser espremido a nada
var t2 = new LayoutTree();
t2.Insert(10, mon, 0, SplitLayout.Horizontal);
t2.Insert(11, mon, 10, SplitLayout.Horizontal);
t2.ComputeRects(mon, area, 0, vazio);
var esticou = true;
for (var i = 0; i < 50; i++) { t2.ComputeRects(mon, area, 0, vazio); esticou &= true; t2.Resize(10, SplitLayout.Horizontal, true, 100); }
var r2 = t2.ComputeRects(mon, area, 0, vazio);
Check("a vizinha nunca e espremida a zero", r2[11].Width >= 45 && r2[10].Width < 1000, $"{r2[10].Width}|{r2[11].Width}");

// ── na ponta do grid nao ha de quem tirar
Check("resize na ponta devolve falso", !t2.Resize(10, SplitLayout.Horizontal, towardsEnd: false, 50));
Check("resize no eixo sem vizinho devolve falso", !t2.Resize(10, SplitLayout.Vertical, towardsEnd: true, 50));

// ── monitor em pe corta no outro sentido
var t3 = new LayoutTree();
var alto = new RECT { Left = 0, Top = 0, Right = 600, Bottom = 1000 };
t3.Insert(20, mon, 0, SplitLayout.Vertical);
t3.ComputeRects(mon, alto, 10, vazio);
t3.Insert(21, mon, 20, SplitLayout.Vertical);
var r3 = t3.ComputeRects(mon, alto, 10, vazio);
Check("monitor em pe empilha as janelas", r3[20].Top == 0 && r3[21].Top > r3[20].Bottom, $"{r3[20]} | {r3[21]}");

// ── dois monitores nao se misturam
var t4 = new LayoutTree();
t4.Insert(30, 1, 0, SplitLayout.Horizontal);
t4.Insert(31, 2, 30, SplitLayout.Horizontal);
Check("cada monitor tem o proprio grid",
      t4.ComputeRects(1, area, 10, vazio).Count == 1 && t4.ComputeRects(2, area, 10, vazio).Count == 1);
Check("a janela sabe em que monitor esta", t4.MonitorOf(31) == 2, t4.MonitorOf(31).ToString());

// ── quatro janelas viram quadrantes, e nada se sobrepoe
var q = new LayoutTree();
nint anterior = 0;
foreach (nint h in new nint[] { 40, 41, 42, 43 })
{
    q.Insert(h, mon, anterior, SplitLayout.Horizontal);
    q.ComputeRects(mon, area, 10, vazio);
    anterior = h;
}
var rq = q.ComputeRects(mon, area, 10, vazio);
Check("quatro janelas cabem todas", rq.Count == 4, rq.Count.ToString());
var larguras = rq.Values.Select(v => v.Width).Distinct().Count();
var alturas = rq.Values.Select(v => v.Height).Distinct().Count();
// quatro colunas iguais de altura cheia — o arranjo escolhido: estreitar todas por igual em vez
// de aninhar em quadrantes. Numa tela larga isso e o que se quer; o preco e a largura cair junto
Check("quatro janelas viram quatro colunas iguais, de altura cheia",
      larguras <= 2 && alturas == 1 && rq.Values.All(v => v.Height == 600),
      string.Join(" , ", rq.Values));
Check("as quatro tem praticamente a mesma largura",
      rq.Values.Max(v => v.Width) - rq.Values.Min(v => v.Width) <= 2,
      string.Join(" , ", rq.Values.Select(v => v.Width)));
var lista = rq.Values.ToList();
var sobrepoe = false;
for (var i = 0; i < lista.Count; i++)
    for (var j = i + 1; j < lista.Count; j++)
        if (lista[i].Left < lista[j].Right && lista[j].Left < lista[i].Right &&
            lista[i].Top < lista[j].Bottom && lista[j].Top < lista[i].Bottom) sobrepoe = true;
Check("nenhuma janela se sobrepoe a outra", !sobrepoe);
Check("o grid enche a area toda", rq.Values.Max(v => v.Right) == 1000 && rq.Values.Max(v => v.Bottom) == 600,
      $"{rq.Values.Max(v => v.Right)} x {rq.Values.Max(v => v.Bottom)}");

// ── tamanho mínimo: quem não encolhe além do próprio limite é respeitado, não expulso
var m = new LayoutTree();
m.Insert(50, mon, 0, SplitLayout.Horizontal);
m.ComputeRects(mon, area, 0, vazio);
m.Insert(51, mon, 50, SplitLayout.Horizontal);
m.ComputeRects(mon, area, 0, vazio);
m.SetMinimum(50, 466, 0);            // o Terminal não desce de 466px de largura
var rm = m.ComputeRects(mon, area, 0, vazio);
Check("sem apertar, o mínimo não atrapalha quem já cabe", rm[50].Width == 500, rm[50].ToString());

// aperta 50 muito além do que ele aceita
for (var i = 0; i < 10; i++) m.Resize(50, SplitLayout.Horizontal, towardsEnd: true, -50);
rm = m.ComputeRects(mon, area, 0, vazio);
Check("o redimensionamento para no mínimo da janela", rm[50].Width >= 466, rm[50].ToString());
Check("o vizinho fica com todo o resto", rm[50].Width + rm[51].Width == 1000, $"{rm[50].Width}+{rm[51].Width}");

// e o contrário: o vizinho também tem mínimo
m.SetMinimum(51, 400, 0);
for (var i = 0; i < 10; i++) m.Resize(50, SplitLayout.Horizontal, towardsEnd: true, 50);
rm = m.ComputeRects(mon, area, 0, vazio);
Check("nenhum dos dois é espremido abaixo do próprio mínimo",
      rm[50].Width >= 466 && rm[51].Width >= 400, $"{rm[50].Width}|{rm[51].Width}");

// mínimo que não cabe de jeito nenhum: cada um leva o seu, sem ninguém a zero
var t5 = new LayoutTree();
t5.Insert(60, mon, 0, SplitLayout.Horizontal);
t5.ComputeRects(mon, area, 0, vazio);
t5.Insert(61, mon, 60, SplitLayout.Horizontal);
t5.ComputeRects(mon, area, 0, vazio);
t5.SetMinimum(60, 800, 0); t5.SetMinimum(61, 800, 0);
var r5 = t5.ComputeRects(mon, area, 0, vazio);
Check("mínimos que não cabem: ninguém fica com zero", r5[60].Width == 800 && r5[61].Width == 800,
      $"{r5[60].Width}|{r5[61].Width}");

// ── fechar uma de três devolve o espaço às outras duas
var f3 = new LayoutTree();
nint ant = 0;
foreach (nint h in new nint[] { 70, 71, 72 }) { f3.Insert(h, mon, ant, SplitLayout.Horizontal); f3.ComputeRects(mon, area, 10, vazio); ant = h; }
var antes3 = f3.ComputeRects(mon, area, 10, vazio);
Check("três janelas no ar", antes3.Count == 3, antes3.Count.ToString());
f3.Remove(72);
var depois3 = f3.ComputeRects(mon, area, 10, vazio);
Check("fechar uma de três deixa duas", depois3.Count == 2, depois3.Count.ToString());
Check("as duas que sobraram ocupam a área toda",
      depois3.Values.Min(v => v.Left) == 0 && depois3.Values.Max(v => v.Right) == 1000,
      string.Join(" , ", depois3.Values));
Check("e ficam com metade cada, sem sobra de container vazio",
      depois3.Values.All(v => v.Width == 495), string.Join(" , ", depois3.Values));

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} falha(s)");
return falhas == 0 ? 0 : 1;
