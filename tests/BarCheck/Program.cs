using WinDock.Interop;
using WinDock.Services;
using static WinDock.Interop.Native;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
bool Igual(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
var vazio = new RECT();

// ══ Reclaimed: até onde a área de trabalho vai ══════════════════════════════════════════════
Console.WriteLine("── Reclaimed ──");

// ── a máquina de quem faz a dock: barra do Windows no topo (48), barra de cima do WinDock (24),
//    dock embaixo (40)
var tela = R(0, 0, 1920, 1080);
var barraTopo = R(0, 0, 1920, 48);
var dockBaixo = R(0, 1040, 1920, 1080);

var w = BarGeometry.Reclaimed(tela, R(0, 48, 1920, 1040), barraTopo, 24, DockEdge.Bottom, dockBaixo);
Check("barra no topo: a área sobe até a barra de cima do WinDock", Igual(w, R(0, 24, 1920, 1040)), w.ToString());

var w2 = BarGeometry.Reclaimed(tela, w, barraTopo, 24, DockEdge.Bottom, dockBaixo);
Check("barra no topo: a segunda passagem não mexe em nada", Igual(w2, w), w2.ToString());

w = BarGeometry.Reclaimed(tela, R(0, 48, 1920, 1040), barraTopo, 0, DockEdge.Bottom, dockBaixo);
Check("barra no topo, sem a barra de cima: a área vai ao topo da tela", w.Top == 0 && w.Bottom == 1040, w.ToString());

// ── o padrão do Windows: barra embaixo (48). Com Esconder a dock (40) está na borda, e o shell
//    só conta a mais grossa das duas: a área termina em 1032
var barraBaixo = R(0, 1032, 1920, 1080);

w = BarGeometry.Reclaimed(tela, R(0, 24, 1920, 1032), barraBaixo, 24, DockEdge.Bottom, dockBaixo);
Check("barra embaixo, dock na borda: a área desce só até a dock", w.Bottom == 1040 && w.Top == 24, w.ToString());

w2 = BarGeometry.Reclaimed(tela, w, barraBaixo, 24, DockEdge.Bottom, dockBaixo);
Check("barra embaixo: a segunda passagem não soma a barra de novo", Igual(w2, w), w2.ToString());

// dock mais grossa que a barra: o shell já desconta a dock inteira, e não há o que devolver
var dockGrossa = R(0, 1020, 1920, 1080);
w = BarGeometry.Reclaimed(tela, R(0, 24, 1920, 1020), barraBaixo, 24, DockEdge.Bottom, dockGrossa);
Check("dock mais grossa que a barra: a área fica onde está", w.Bottom == 1020, w.ToString());

// ── a EV07: tela de 1440, barra a 150% (72), dock de 40
var tela1440 = R(0, 0, 2560, 1440);
var barra72 = R(0, 1368, 2560, 1440);

// Recolher: a dock fica empilhada acima da faixa em que a barra reaparece
w = BarGeometry.Reclaimed(tela1440, R(0, 0, 2560, 1328), barra72, 0, DockEdge.Bottom, R(0, 1328, 2560, 1368));
Check("EV07, dock empilhada acima da barra: a área não passa por baixo da dock", w.Bottom == 1328, w.ToString());

// Esconder: a dock está na borda
w = BarGeometry.Reclaimed(tela1440, R(0, 0, 2560, 1368), barra72, 0, DockEdge.Bottom, R(0, 1400, 2560, 1440));
Check("EV07, dock na borda: a área termina no topo da dock", w.Bottom == 1400, w.ToString());

// a conta antiga, sem limite: é o defeito, e é o que acontece sem o lugar da dock
w = BarGeometry.Reclaimed(tela1440, R(0, 0, 2560, 1328), barra72, 0, DockEdge.Bottom, vazio);
Check("sem o lugar da dock (sem reserva, ou em outra tela) a área vai até onde a barra deixava", w.Bottom == 1400, w.ToString());

// ── a resolução caiu (1440 → 1080) e a dock mudou de lugar: vale o lugar de agora. Com o lugar
//    antigo (1400..1440) a área ia até 1080, por baixo da dock — o defeito 1 da revisão de 12/11
w = BarGeometry.Reclaimed(tela, R(0, 0, 1920, 1032), barraBaixo, 0, DockEdge.Bottom, dockBaixo);
Check("resolução menor, lugar novo da dock: a área para na dock", w.Bottom == 1040, w.ToString());

w = BarGeometry.Reclaimed(tela, R(0, 0, 1920, 1032), barraBaixo, 0, DockEdge.Bottom, R(0, 1400, 2560, 1440));
Check("(o lugar antigo levaria a área por baixo da dock: por isso ele é perguntado na hora)", w.Bottom == 1080, w.ToString());

// ── dock em outra borda não limita o lado da barra
w = BarGeometry.Reclaimed(tela, R(40, 0, 1920, 1032), barraBaixo, 0, DockEdge.Left, R(0, 0, 40, 1032));
Check("dock na lateral, barra embaixo: a área vai até o fim da tela", w.Bottom == 1080 && w.Left == 40, w.ToString());

// ── dock no topo, barra no topo: o piso é o fim da dock, que fica abaixo da barra de cima
w = BarGeometry.Reclaimed(tela, R(0, 64, 1920, 1080), barraTopo, 24, DockEdge.Top, R(0, 24, 1920, 64));
Check("dock no topo: a área começa onde a dock termina", w.Top == 64, w.ToString());

w = BarGeometry.Reclaimed(tela, R(0, 88, 1920, 1080), barraTopo, 24, DockEdge.Top, R(0, 24, 1920, 64));
Check("dock no topo, área ainda descontando a barra do Windows: volta para o fim da dock", w.Top == 64, w.ToString());

// ── barra na lateral (o Windows 10 deixava): os dois lados
var barraEsq = R(0, 0, 62, 1080);
w = BarGeometry.Reclaimed(tela, R(62, 0, 1920, 1040), barraEsq, 0, DockEdge.Bottom, dockBaixo);
Check("barra à esquerda: a área volta à borda esquerda", w.Left == 0 && w.Bottom == 1040, w.ToString());

w = BarGeometry.Reclaimed(tela, R(62, 0, 1920, 1080), barraEsq, 0, DockEdge.Left, R(0, 0, 40, 1080));
Check("barra e dock à esquerda: a área para na dock", w.Left == 40, w.ToString());

var barraDir = R(1858, 0, 1920, 1080);
w = BarGeometry.Reclaimed(tela, R(0, 0, 1858, 1080), barraDir, 0, DockEdge.Right, R(1880, 0, 1920, 1080));
Check("barra e dock à direita: a área para na dock", w.Right == 1880, w.ToString());

w2 = BarGeometry.Reclaimed(tela, w, barraDir, 0, DockEdge.Right, R(1880, 0, 1920, 1080));
Check("barra à direita: a segunda passagem não mexe", Igual(w2, w), w2.ToString());

// ── monitor secundário à esquerda do principal (coordenadas negativas)
var telaEsq = R(-1920, 0, 0, 1080);
w = BarGeometry.Reclaimed(telaEsq, R(-1920, 0, 0, 1032), R(-1920, 1032, 0, 1080), 0, DockEdge.Bottom, vazio);
Check("barra de tela secundária, sem dock nela: a área vai até o fim daquela tela", w.Bottom == 1080 && w.Left == -1920, w.ToString());

// ══ WithoutTaskbar: onde a dock fica com a barra do Windows escondida ═══════════════════════
Console.WriteLine("── WithoutTaskbar ──");

// ── esta máquina: barra no topo, dock embaixo — nada muda
var d = BarGeometry.WithoutTaskbar(dockBaixo, dockBaixo, tela, barraTopo, DockEdge.Bottom, 40, 24);
Check("barra no topo, dock embaixo: fica onde o shell respondeu", Igual(d, dockBaixo), d.ToString());

// ── a EV07: o shell empilha a dock acima da barra escondida (1328..1368); ela vai para a borda
d = BarGeometry.WithoutTaskbar(R(0, 1328, 2560, 1368), R(0, 1400, 2560, 1440), tela1440, barra72, DockEdge.Bottom, 40, 0);
Check("EV07, barra embaixo: a dock vai para a borda da tela", Igual(d, R(0, 1400, 2560, 1440)), d.ToString());

d = BarGeometry.WithoutTaskbar(R(0, 992, 1920, 1032), dockBaixo, tela, barraBaixo, DockEdge.Bottom, 40, 24);
Check("barra embaixo a 100%: a dock vai para a borda", Igual(d, dockBaixo), d.ToString());

// ── barra de outro programa entre a dock e a barra do Windows: quem segurou a dock foi ela
d = BarGeometry.WithoutTaskbar(R(0, 962, 1920, 1002), dockBaixo, tela, barraBaixo, DockEdge.Bottom, 40, 24);
Check("barra de outro programa na mesma borda: a dock continua acima dela", Igual(d, R(0, 962, 1920, 1002)), d.ToString());

// ── dock na lateral. Barra do Windows embaixo: a dock desce até o fim, e começa na barra de cima
var querLateral = R(0, 0, 40, 1080);
d = BarGeometry.WithoutTaskbar(R(0, 24, 40, 1032), querLateral, tela, barraBaixo, DockEdge.Left, 40, 24);
Check("dock à esquerda, barra embaixo: vai da barra de cima ao fim da tela", Igual(d, R(0, 24, 40, 1080)), d.ToString());

// o shell não descontou a barra de cima (ela se registra depois da dock): o piso vale assim mesmo
d = BarGeometry.WithoutTaskbar(R(0, 0, 40, 1032), querLateral, tela, barraBaixo, DockEdge.Left, 40, 24);
Check("dock à esquerda, shell sem saber da barra de cima: começa abaixo dela", Igual(d, R(0, 24, 40, 1080)), d.ToString());

// barra do Windows no topo: a faixa dela (48) engole a da barra de cima (24); desfeita, sobra o piso
d = BarGeometry.WithoutTaskbar(R(0, 48, 40, 1080), querLateral, tela, barraTopo, DockEdge.Left, 40, 24);
Check("dock à esquerda, barra no topo: começa na barra de cima, e não na do Windows", Igual(d, R(0, 24, 40, 1080)), d.ToString());

d = BarGeometry.WithoutTaskbar(R(1880, 48, 1920, 1080), R(1880, 0, 1920, 1080), tela, barraTopo, DockEdge.Right, 40, 24);
Check("dock à direita, barra no topo: começa na barra de cima", Igual(d, R(1880, 24, 1920, 1080)), d.ToString());

d = BarGeometry.WithoutTaskbar(R(0, 48, 40, 1080), querLateral, tela, barraTopo, DockEdge.Left, 40, 0);
Check("dock à esquerda, sem a barra de cima: de alto a baixo", Igual(d, querLateral), d.ToString());

// barra de outro programa no topo (30) e a do Windows embaixo: o topo fica onde ela deixou
d = BarGeometry.WithoutTaskbar(R(0, 30, 40, 1032), querLateral, tela, barraBaixo, DockEdge.Left, 40, 24);
Check("dock à esquerda, barra de outro programa no topo: respeitada", Igual(d, R(0, 30, 40, 1080)), d.ToString());

// ── dock no topo: nunca nos mesmos pixels da barra de cima (o defeito 2 da revisão de 12/11)
var querTopo = R(0, 0, 1920, 40);
d = BarGeometry.WithoutTaskbar(R(0, 48, 1920, 88), querTopo, tela, barraTopo, DockEdge.Top, 40, 24);
Check("dock no topo, barra no topo: logo abaixo da barra de cima", Igual(d, R(0, 24, 1920, 64)), d.ToString());

d = BarGeometry.WithoutTaskbar(querTopo, querTopo, tela, barraBaixo, DockEdge.Top, 40, 24);
Check("dock no topo, barra embaixo, shell sem saber da barra de cima: abaixo dela", Igual(d, R(0, 24, 1920, 64)), d.ToString());

d = BarGeometry.WithoutTaskbar(R(0, 48, 1920, 88), querTopo, tela, barraTopo, DockEdge.Top, 40, 0);
Check("dock no topo, sem a barra de cima: na borda", Igual(d, querTopo), d.ToString());

// ── sem barra do Windows nesse monitor (tela secundária sem barra): só o piso
d = BarGeometry.WithoutTaskbar(dockBaixo, dockBaixo, tela, vazio, DockEdge.Bottom, 40, 24);
Check("sem barra do Windows na tela: fica onde o shell respondeu", Igual(d, dockBaixo), d.ToString());

// ── escala: o shell e a janela da barra diferem em 1 px
d = BarGeometry.WithoutTaskbar(R(0, 1327, 2560, 1367), R(0, 1400, 2560, 1440), tela1440, barra72, DockEdge.Bottom, 40, 0);
Check("um pixel de diferença entre o shell e a janela da barra ainda conta como a barra", Igual(d, R(0, 1400, 2560, 1440)), d.ToString());

d = BarGeometry.WithoutTaskbar(R(0, 1320, 2560, 1360), R(0, 1400, 2560, 1440), tela1440, barra72, DockEdge.Bottom, 40, 0);
Check("oito pixels de diferença já é outra barra: a dock fica", Igual(d, R(0, 1320, 2560, 1360)), d.ToString());

// ── as duas contas juntas, no caso da EV07 com Esconder: a dock na borda e a área até ela
var dockEv07 = BarGeometry.WithoutTaskbar(R(0, 1328, 2560, 1368), R(0, 1400, 2560, 1440), tela1440, barra72, DockEdge.Bottom, 40, 0);
w = BarGeometry.Reclaimed(tela1440, R(0, 0, 2560, 1368), barra72, 0, DockEdge.Bottom, dockEv07);
Check("EV07 de ponta a ponta: a dock na borda e a área útil terminando onde ela começa",
      dockEv07.Bottom == 1440 && w.Bottom == dockEv07.Top, $"{w} | {dockEv07}");

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} conferência(s) falharam");
return falhas == 0 ? 0 : 1;
