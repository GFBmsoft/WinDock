# Aprendizados do WinDock

O que **não** funciona no Windows 11 atual, o que funciona no lugar, e por quê. Tudo aqui foi
verificado na máquina — não é dedução a partir de documentação. Este arquivo existe para evitar
que a mesma pedra seja pisada de novo daqui a seis meses.

Build 26200 (Windows 11), .NET 8, WPF, sem dependências externas.

---

## Barra de tarefas do Windows

**Não dá para esconder só os ícones dos apps.** Eles são desenhados em XAML dentro da barra; a
antiga `MSTaskListWClass` ainda existe mas já vem oculta. Não há janela Win32 para mexer.

**`SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)` não faz nada.** `ABM_GETSTATE` continua zero
depois da chamada, com a barra visível ou escondida, com `hWnd` nulo ou válido, com a dock
aberta ou fechada. Testado nas quatro combinações.

**O byte de auto-hide do `StuckRects3` também não vale mais** — alterá-lo não muda nada, nem
depois de reiniciar o Explorer.

**O que funciona: `SPI_SETWORKAREA` sem `SPIF_SENDCHANGE`.** Contraintuitivo: *com* o aviso, o
shell recalcula na hora e desfaz o pedido; *sem* ele, a área de trabalho fica como pedimos.

**"Esconder" a barra é apagá-la, não escondê-la.** O `SW_HIDE` funciona, mas cobra caro em tudo
que precisa dela de volta: o painel de ícones ocultos só abre com a barra de pé, e cada ida e volta
faz o shell recalcular a área de trabalho (medido: `(0,24)` → `(0,32)` → `(0,24)` a cada abertura
do cartão da bandeja). Com `WS_EX_LAYERED` + alfa zero e `WS_EX_TRANSPARENT` ela fica **de pé,
invisível e atravessável pelo mouse**: não há transição nenhuma, e a leitura da bandeja a encontra
onde precisa sem mexer em nada. O espaço dela continua sendo devolvido pelo `Reclaim` — quem cobra
o espaço é a appbar registrada, e isso a camada não muda. Vale para os dois modos, o "esconder" e
o "recolher automático" (onde "aparecer" virou ficar opaca de novo).

**A barra reaparece sozinha.** O shell a reexibe em várias ocasiões — abrir o painel de wi-fi ou
o de notificações, por exemplo — e ela volta **por cima de tudo**, roubando cliques de quem
estiver embaixo. No modo "esconder" é preciso vigiar e reesconder (`TaskbarService`).

**Ela continua registrada como AppBar mesmo escondida.** O shell empurra outras AppBars para
baixo dela: a barra de cima do WinDock nascia em `y=32` com 32 px mortos acima. Daí o
`AppBar.IgnoreOtherBars`, que pula o `ABM_QUERYPOS` e fixa a posição desejada.

**Duas barras na mesma borda: o shell só conta a mais alta.** Devolver os pixels da nativa
levava junto a faixa da nossa. Por isso o cálculo tem um piso (`TaskbarService.TopReserve`).

## Bandeja de ícones

**A bandeja legada morreu.** Não existe mais `ToolbarWindow32` dentro de `TrayNotifyWnd`, nem a
janela `NotifyIconOverflowWindow`. Ler os ícones por `TB_GETBUTTON` + `ReadProcessMemory` — a
técnica clássica — não é mais possível.

**O caminho que resta é a automação de interface**, e ele funciona: `AutomationElement.FromHandle`
na `Shell_TrayWnd` devolve zero elementos, mas na janela filha `DesktopWindowContentBridge` a
árvore aparece inteira — botões dos apps, "Mostrar Ícones Ocultos", "Rede", "Volume". Dá para
enumerar e invocar. A imagem do ícone a automação não fornece: teria de vir de `PrintWindow` na
barra (funciona mesmo escondida) recortado pelo `BoundingRectangle` de cada elemento.

## Janela maximizada e a faixa reservada

**Uma janela maximizada ocupa `área útil ± 8 px`.** Os 8 px são a borda de redimensionamento
invisível (`SM_CYSIZEFRAME` + `SM_CXPADDEDBORDER`). Com a barra de cima em 26 px, o Delphi
maximizado fica em `(-8,18)-(2568,1048)` — **e isso está certo**, é o mesmo que o Chrome faz.

**A IDE do Delphi perde os 8 px do topo do próprio título quando maximizada.** O combo de layout
fica sem a borda de cima, colado na barra. Restaurada, a mesma caixa aparece inteira e com folga.

**Reservar 8 px a mais não conserta** — tentado e revertido. A janela desce 8 px, mas o conteúdo do
título desce junto: texto e borda inferior continuam a +4 e +18 px do início do título, exatamente
como antes. O que se ganha é uma faixa de papel de parede entre a barra e a janela, e 8 px a menos
de tela. O RAD Studio simplesmente não pinta a região da borda invisível (ela aparece como papel de
parede quando não há nada por cima), então não há como devolver esses pixels de fora do processo.

**E não é culpa de quem está no topo da tela.** Com o WinDock fechado e a barra do Windows no
topo (32 px), a janela vai para `(-8,24)` — os mesmos 8 px atrás da barra nativa. O corte
acompanha o estado maximizado, não a barra.

## Bandeja de ícones (o que funcionou)

**O flyout de ícones ocultos é a única fonte que diz a verdade.** Ele é a bandeja; qualquer outra
coisa é aproximação.

**O caminho pelo registro é uma armadilha bonita.** `HKCU\Control Panel\NotifyIconSettings` tem
uma subchave por ícone com `ExecutablePath`, `InitialTooltip` e um `IconSnapshot` que é um **PNG
de 20×20 pronto**, com transparência. É barato, não pisca, dá o desenho de graça — e **está
errado**: é um histórico, não um retrato. Cruzado com os processos vivos ainda mostrava **seis
ícones onde o Windows mostrava três** (programas rodando sem ícone na bandeja; o explorer sempre
está lá). O `LastWriteTime` das chaves não ajuda a separar: quase todas carregam o mesmo carimbo
de uma migração do Windows.

**Nem existe identificador comum entre o registro e o flyout.** Todos os botões do flyout
pertencem ao explorer e se chamam `NotifyItemIcon`; casar por texto **falha justamente no
Defender**, cujo executável se descreve em inglês ("Windows Security notification icon") enquanto
o botão se anuncia no idioma do sistema ("Segurança do Windows"). Isso deixa de ser um problema
quando a lista *é* o flyout: a posição na enumeração já é a identidade.

**O flyout abre por automação — mas só com a barra do Windows visível.** Com ela escondida o
`Invoke` no "Mostrar Ícones Ocultos" não faz nada. Mostrar por ~300 ms, abrir, e esconder de novo
funciona: **o flyout continua de pé depois de a barra sumir** — por alguns segundos, e não
indefinidamente. Dá tempo de fotografar e de acionar; não dá para deixá-lo aberto esperando.

**A janela do flyout continua existindo depois de fechada, só invisível.** Foi *o* bug: o
`FindWindow` pela classe achava esse fantasma, o código concluía "já está aberto", nunca clicava
no chevron, e todo clique em ícone caía no caminho de erro. `IsWindowVisible` faz parte da
pergunta, não é detalhe.

**`WS_EX_LAYERED` com alfa zero deixa uma janela do shell invisível sem escondê-la** — e isso
apaga a piscada inteira. Com alfa zero ela continua existindo, continua respondendo à automação e
continua sendo fotografável pelo `PrintWindow`; só não é desenhada. `SetWindowLongPtr` numa janela
do explorer funciona (mesmo nível de integridade). Restaure o estilo original **sempre**: deixar o
painel de ícones ocultos invisível estragaria a bandeja de verdade, a que a pessoa abre pela barra
do Windows.

**Que essa janela sobreviva fechada, aqui, é uma vantagem:** dá para deixá-la transparente *antes*
de o shell mostrá-la. Só a primeiríssima abertura de cada sessão do explorer pisca.

**O vigia que reesconde a barra do Windows briga com quem precisa dela de pé.** Ele roda a cada
150 ms; no meio da leitura da bandeja, escondia a barra, o painel não abria e o clique não fazia
nada — de vez em quando, o que é o pior tipo de bug. Quem mostra a barra tem de pausar o vigia
(`TaskbarService.Suspend`, um contador, não um booleano).

**Mostrar a barra do Windows mexe na área de trabalho, e isso redimensiona toda janela
maximizada.** A área útil pula de 24 para 32 assim que a barra aparece — a mudança de tamanho que
se via ao abrir a bandeja.

**E reagir a isso piora tudo.** Houve aqui um relógio repondo `SPI_SETWORKAREA` a cada 25 ms
enquanto a barra estava de pé. Enquanto a barra do Windows está na tela o shell insiste em cobrar
o espaço dela, então cada reposição comprava uma cobrança nova: medido, **oito** mudanças de área
numa única abertura — oito redimensionamentos — em vez de uma. Perguntar antes de mandar
(`SPI_GETWORKAREA` primeiro) não salvou: a área está errada quase sempre, e o martelo batia do
mesmo jeito. Pior, essas chamadas atravessam o shell no exato instante em que a automação lê o
painel dele — os botões vinham com retângulo vazio, a foto saía preta, a leitura foi a **16 s** e
os ícones apareciam sem desenho.

**Corrigir depois também não resolve, e aqui está a chave: o que redimensiona janela não é o
valor da área, é o anúncio.** Nossos pedidos vão sem `SPIF_SENDCHANGE` e ninguém se mexe; o do
shell vai com aviso, e todas se mexem. Qualquer correção posterior é um anúncio a mais — foi o que
aconteceu com uma tentativa de repor o valor no fim e mandar um `WM_SETTINGCHANGE` nosso: duas
mudanças por abertura em vez de oito, mas ainda duas.

**O que funciona é chegar antes do shell.** Com a barra ainda escondida, põe-se a área no valor
que ele *vai* querer — o dela com a barra ocupando o espaço. O pedido é silencioso, então nada se
mexe; quando a barra aparece, o shell recalcula, encontra o valor que já queria e não tem o que
anunciar. No fim, com a barra escondida de novo, o valor da dock volta do mesmo jeito silencioso.
Medido: **nenhuma** mudança de área em 20 s de observação, e a leitura caiu para **178 ms** (sem o
vaivém, o shell também responde mais rápido à automação).

**A automação de interface quer STA com bomba de mensagens.** De uma thread do pool (MTA) os
retângulos vêm vazios; de uma STA sem `Dispatcher.Run` a chamada trava. Uma thread STA com bomba,
criada **junto com a barra** e não no primeiro clique — quem esperava a thread nascer era a
thread da interface, e a barra travava.

**Uma leitura de cada vez.** Cada abertura custa uns 600 ms; clicar de novo no meio empilhava
leituras e a barra passava a responder a cliques de minutos atrás. Pedido que chega com leitura
em curso é descartado, não enfileirado — e quem pediu precisa ser avisado do descarte, ou fica
esperando um retorno que não vem.

**Não faça a interface esperar pela leitura.** Mostrar o cartão com o que já se tinha e corrigir
quando a leitura volta derruba a abertura de 600 ms para uns 70. A primeira leitura da sessão,
que é o dobro disso, fica para um aquecimento seis segundos depois de a barra subir.

**Os botões do painel aparecem na árvore de automação antes de a janela pintar.** Fotografar
nesse instante devolve uma cor só. Um tempo fixo de 150 ms cobria isso e era pago inteiro toda
vez; esperar até a foto ter mais de uma cor custa o que precisar — em geral bem menos — e não
quebra quando a máquina está mais lenta que o chute.

**Mande a condição para a automação, não filtre em C#.** `FindAll(TreeScope.Descendants,
Condition.TrueCondition)` seguido de um `Where` traz dezenas de elementos pela fronteira do
processo. Um `PropertyCondition` no `AutomationIdProperty` faz o mesmo trabalho do outro lado.

**`ToolTipService.IsEnabled = false` não fecha a dica que já está aberta** — só impede a
próxima. Para tirar de cena uma dica visível, tire a dica do controle (`ToolTip = null`) e
devolva depois. Com o cartão abrindo em 70 ms e a dica em 400, era ela que chegava por cima.

**`PopupAnimation="Fade"` num popup que abre rápido parece lentidão.** A esmaecida dura mais que
a abertura, e o efeito é "ficou translúcido e depois apareceu".

**Abrir o painel do Windows rouba o foco, e não dá para devolvê-lo no meio.** Ele vem para
primeiro plano; a janela de quem estava trabalhando pisca a barra de título. Devolver o foco
durante a leitura não serve: o painel se dispensa ao perder o foco, que é justamente o que se
precisa dele. Dá para devolver **no fim** (guardando o `GetForegroundWindow` de antes), mas os
~600 ms de piscada acontecem.

**Então o remédio é ler menos, não ler mais rápido.** Uma validade curta na última leitura
(5 s) faz o gesto de abrir e fechar em seguida — o mais comum — não reler nada: sem leitura, sem
roubo de foco, sem piscada. E o cartão se fecha sozinho em 3 s, então o vaivém cabe inteiro
dentro da validade.

**Um clique enquanto o painel do Windows está aberto vai para ele, não para a barra.** Era o
"parece que trava" ao fechar: o primeiro clique era consumido dispensando o painel invisível, e
só o seguinte chegava ao botão.

**`PrintWindow` com `PW_RENDERFULLCONTENT` fotografa o flyout inteiro**, e o
`BoundingRectangle` de cada botão recorta os ícones. Use um DIB (`CreateDIBSection`) para ler os
pixels direto: `CreateBitmapSourceFromHBitmap` jogaria fora o canal alfa.

**A janela precisa estar em camada (`WS_EX_LAYERED`) para o `PrintWindow` funcionar nela.** Sem
a camada a foto sai **preta** — uma cor só. Com alfa zero o DWM guarda a superfície de
redirecionamento, e é dela que a foto sai. O efeito colateral bonito é que a mesma camada que
faz a foto existir é a que some com o painel da tela.

**E a camada tem de estar lá antes de a janela aparecer.** Aplicada depois, a superfície não é
criada retroativamente: a primeira abertura de cada sessão do explorer sai preta, porque nessa
hora a janela ainda não existia para ser marcada. A segunda sai completa.

**Repor a área de trabalho durante a leitura faz a automação devolver `BoundingRectangle`
vazio.** Foi o mais difícil de achar: o `SPI_SETWORKAREA` de 25 em 25 ms (que existia para as
janelas maximizadas não se mexerem) deixava os botões do painel sem retângulo, e sem retângulo
não há onde recortar. Hoje esse relógio não existe mais — veja "Mostrar a barra do Windows mexe
na área de trabalho" —, mas a regra que ele ensinou fica: **nenhuma chamada ao shell enquanto a
automação estiver lendo o painel dele.**

**Há ícones que a automação entrega sem nome, e isso parece bandeja duplicada.** Todos caíam no
mesmo rótulo genérico, e dois programas diferentes viravam duas linhas iguais no cartão. A espera
tem dois tempos e prazos diferentes: a lista fica pronta quando a **contagem repete** de uma
sondagem para a outra (um botão que ainda vai nascer não avisa que vem — era essa a razão de a
contagem oscilar entre três, quatro e cinco de uma abertura para outra), e o **nome** vem depois,
com um prazo curto próprio de 400 ms.

**O nome do botão é a dica de mouse do programa, e quem não publica dica não tem nome — ponto.**
Sondado: o botão é `SystemTray.NormalButton`, `AutomationId=NotifyItemIcon`, pai
`Windows.UI.Input.InputSite.WindowClass`, único filho um `Image` sem nome, e o `ProcessId` é o do
**explorer** — nada do programa dono atravessa. O registro confirma de fora
(`HKCU\Control Panel\NotifyIconSettings\<hash>\InitialTooltip`): o Discord, por exemplo, tem o
valor vazio, e o próprio Windows também não sabe nomeá-lo. Esperar por esses nomes é esperar por
nada — daí o prazo curto, e a lembrança de que naquela composição de bandeja há anônimos, para as
leituras seguintes não pagarem os 400 ms de novo (~200 ms contra 1.000 ms da primeira).

**O recorte do ícone tem de ser centralizado no botão.** Ele é quadrado e o botão não é: ancorado
no canto, o ícone sai deslocado para um lado e comido do outro — na barra isso aparece como um
desenho levemente torto.

**`GdiFlush()` entre o `PrintWindow` e o `Marshal.Copy`** — o GDI acumula desenho em lote e sem
descarregar os bits do DIB ainda são os originais. (Aqui não era a causa, mas é a próxima
suspeita numa foto vazia.)

**O fundo do flyout é opaco, não transparente.** Recortado e colado numa barra translúcida, cada
ícone viraria um quadradinho escuro. A cor do canto do recorte é sempre moldura: tudo que se
parece com ela (tolerância ~40 na soma dos canais) vira transparente, e o resultado cola limpo.

**O botão do flyout é bem maior que o desenho.** Recortá-lo inteiro deixa o ícone minúsculo na
barra; um recuo de 25% de cada lado pega só o ícone.

**"O ícone sumiu" quase sempre é o programa trocando o próprio ícone, não a leitura errando.**
Aconteceu em 04/09/2026: o logo do Discord virou um microfone riscado no cartão, e parecia
recorte deslocado. Não era — o Discord troca o ícone de bandeja quando o microfone está mudo.

O jeito de separar as duas coisas **sem abrir nada e sem depender de quem estava olhando**, em
três medidas baratas:

1. **O rastro diz o que a leitura achou.** `leitura terminou em N ms com 2 ícones: Discord |
   Segurança do Windows`. Se os *nomes* estão certos, a lista está certa e a dúvida é só do
   desenho.
2. **Conte os candidatos.** Um `Get-Process` pelos suspeitos habituais mostra quantos programas
   têm ícone de bandeja. Bateu com o número lido, ninguém sumiu.
3. **Use o `IconSnapshot` do registro como *contraste*, nunca como gabarito.** Ele é histórico
   (veja acima), e é justamente por isso que serve: se o cartão desenha algo **diferente** do
   snapshot e o desenho do cartão bate com o rótulo lido agora, o cartão está mostrando o
   estado atual e o registro é que está velho.

Foi a medida 3 que fechou o caso, e pelo ícone *errado*: o snapshot da Segurança do Windows era
um escudo com alerta amarelo, o cartão desenhou escudo com check verde, e o rótulo lido era
"Nenhuma ação necessária" — o cartão concordando com o rótulo e discordando do registro. Provado
o mecanismo num ícone, valia para o outro.

**Uma sonda só de leitura da bandeja vale ter à mão.** `AutomationElement.FromHandle` na
`Shell_TrayWnd` e um caminhar recursivo listam a bandeja inteira sem abrir painel, sem mexer na
barra e sem clicar — o oposto do `TrayService`, que precisa levantar meio shell. Foi ela que
mostrou que o microfone da barra era o ícone **Privacidade** do Windows ("Microfone em uso por:
Discord"), e não o do Discord, desfazendo a segunda suspeita.

**`ExecutablePath` do registro vem com a pasta em GUID** (`{1AC14E77-…}\Taskmgr.exe`) — se um dia
o registro voltar a ser útil, `SHGetKnownFolderPath` resolve qualquer uma; escrever a tabela de
GUIDs à mão só cobriria as quatro que se conhece.

**O botão direito num ícone se faz pelo teclado, não pelo mouse.** A automação sabe acionar
(`InvokePattern`) e isso é o clique **esquerdo**; para o direito não há padrão de automação. O
caminho que funciona é o que o Windows dá a quem não usa mouse: `AutomationElement.SetFocus()` no
botão e a tecla **Menu** (`VK_APPS`). Medido: o menu do Discord nasce ~280 ms depois da tecla, e o
do Pageant (menu Win32 clássico, classe `#32768`) no mesmo tempo.

**Clicar de verdade foi tentado antes e é um beco sem saída**: mirar o retângulo com
`mouse_event` sequestra o cursor da pessoa — ele salta para o canto da tela e volta, à vista — e
ainda assim o menu não vinha.

**O "segundo cartão" era o painel de verdade do Windows vazando por uma corrida com o Esc.**
Depois de uma leitura normal (a que a seta dispara), o método que fala com o shell manda `Esc`
para fechar o painel de ícones ocultos — mas o fechamento por `Esc` é uma **animação do shell**,
não uma troca de estado instantânea. O código soltava a camada que esconde essa janela
(`WS_EX_LAYERED` + alfa zero) **logo em seguida**, sem esperar a animação terminar. Na janela de
tempo entre o `Esc` e o shell realmente processar o fechamento, a janela do painel ainda estava
de pé e agora sem camada nenhuma — ficava opaca e visível de verdade, no canto certo da bandeja
do Windows (embaixo do relógio/wi-fi/bluetooth), com os mesmos ícones do nosso cartão. Vista ao
lado do cartão do WinDock, parecia um segundo cartão abrindo sozinho.

Era uma corrida, não um erro sempre igual: medido isoladamente, o vazamento aparecia em 2 de 3
leituras. A correção espera o painel terminar de fechar de verdade (`WaitForOverflowToClose`,
que já existia para outro caminho — acionar um ícone — mas não rodava depois do `Esc` de uma
leitura comum) antes de soltar a camada. Confirmado depois: 20 leituras seguidas sem vazamento
num teste isolado, e um teste em processo único a 2 ms de resolução clicando na seta de verdade
(via automação, sem mexer no mouse) mostrando a janela sempre `visible=True` **junto com**
`layered=True`, nunca uma sem a outra.

**O cursor pulava de verdade — era o próprio Windows, reagindo à tecla Menu.** Testado e
confirmado: abrir um menu pela tecla Menu (`VK_APPS`) é o mesmo gesto de um `Shift+F10` em
qualquer desktop, e o Windows tem por hábito **mover o cursor até o menu** quando ele abre desse
jeito — para casar mouse e teclado no que acabou de aparecer. Não é um efeito colateral nosso; é
o SO reagindo à própria tecla que mandamos.

A pegadinha que atrasou achar isso: um teste isolado que chamava `SetFocus` + `VK_APPS`
diretamente, sem passar pelo `WithShell`/`OpenOverflow` de verdade, não reproduzia o salto —
porque sem o painel de ícones ocultos aberto de verdade por trás, a tecla não tinha para onde ir,
e nenhum menu chegava a abrir. Um teste "isolado" que pula a montagem inteira prova a ausência de
um efeito, não a ausência da causa.

A correção não evita o salto — não dá, é o Windows quem decide mexer no cursor ao processar a
tecla. Em vez disso, guarda a posição do cursor **antes** de mandar a tecla e devolve **depois**
de o menu já estar posicionado, com `SetCursorPos`. Diferente do `mouse_event` que foi tentado e
descartado antes (que sequestrava o cursor para abrir o menu, sem ter para onde voltar depois),
aqui a devolução é limpa: o menu continua funcionando (não depende do cursor estar em cima dele),
e o cursor volta para exatamente onde a pessoa o deixou. Medido: salto para (2413,69), devolução
234 ms depois, de volta ao pixel exato de onde saiu.

**O menu esticava para o lado errado.** `Place()` alinhava a borda **direita** do menu com a
borda direita do ícone (`x = anchor.Right - width`). Parece razoável até reparar no tamanho: o
ícone tem uns 40 px de largura, o menu do Discord passa de 200. Esticar a partir da borda direita
de um ícone estreito jogava o menu **180 px para a esquerda** — por cima dos outros ícones do
cartão e além deles. Com o cartão já fechado a essa altura (`CloseAllPopups()` roda antes do menu
aparecer), o resultado parecia "o menu apareceu longe, sem relação com nada" — foi o que os prints
EV06 a EV09 mostravam, mesmo depois da correção de monitor (essa nunca foi a causa: o monitor e a
área de trabalho estavam certos o tempo todo, confirmado no log linha a linha).

O Windows de verdade estica para a **direita** a partir do ícone (comparação direta, EV10). Meio
óbvio em retrospecto — é o que qualquer menu ou dropdown faz — mas só ficou claro comparando um
print do WinDock com um da bandeja de verdade lado a lado, com o mesmo ícone. `x` agora começa em
`anchor.Left` e só vira para a esquerda se não couber à direita da tela.

**A lição de processo, não só de código**: números batendo no log (âncora certa, área de trabalho
certa, posição "ficou" depois de 120 ms) não provam que o resultado parece certo — só provam que a
conta que se decidiu fazer foi executada sem erro. Foi preciso um print do comportamento de
referência (o Windows real) ao lado do nosso para achar que a conta em si estava olhando para o
lado errado.

**O monitor errado, numa máquina com mais de um.** `Place()` calculava a tela onde encaixar o
menu a partir de `MonitorFromWindow(window, ...)` — o monitor de onde o **menu** nasceu, não de
onde está a nossa âncora. O menu sempre nasce perto da bandeja de verdade do Windows
(`Shell_TrayWnd`), que pode estar num monitor diferente do painel do WinDock numa máquina com
mais de um vídeo — e nesta aqui está: um secundário em `X=-1920` e o principal em `X=0..2560`.
Quando os dois não coincidem, a conta de posição mistura coordenadas de duas telas diferentes e
o resultado cai num lugar sem relação com nenhuma das duas — foi isso que apareceu como o menu
"deslocado" (EV07). A correção usa `MonitorFromPoint` no centro da **âncora**, não
`MonitorFromWindow` na janela do menu. Confirmado depois: âncora em (2196,36)-(2238,78), menu
posicionado em (2016,84)-(2238,302) — a 6 px de distância vertical da âncora, duas vezes seguidas.

**O menu nasce onde o programa quer, mas dá para trazê-lo.** Quem desenha o menu de um ícone é o
programa dono, e ele o põe junto da bandeja do Windows — do outro lado da tela, para quem clicou na
nossa barra. Gesto num canto e resposta no outro é o que faz o recurso parecer quebrado mesmo
funcionando. A janela do menu **é movível**: basta reconhecê-la (é a janela visível que nasceu
entre o retrato de antes e o de depois, fora do nosso processo) e reposicioná-la com
`SetWindowPos` + `SWP_NOACTIVATE`. Funciona nos dois tipos, medido: o menu do Discord
(`Chrome_WidgetWin_1`) e o menu clássico do Pageant (`#32768`) foram ambos colados na âncora,
alinhados pela borda direita.

**O que fazia parecer que nada funcionava era o Esc.** Depois de acionar, o painel de ícones
ocultos continua aberto, e o `Esc` que o fecha **não tem endereço**: vai para quem está em
primeiro plano, que nesse instante é o menu recém-aberto. O menu nascia e morria 400 ms depois,
sem deixar vestígio na tela nem no log. A saída é fechar o painel **sem tecla nenhuma** —
`ShowWindow(flyout, SW_HIDE)`.

**O preço disso**: escondida, a janela deixa o shell achando que o painel dele continua aberto, e
a seta *alterna*. Por isso o `OpenOverflow` aciona a seta até duas vezes: quando a primeira só
serviu para desfazer esse engano, é a segunda que abre. Na prática o shell se recuperou sozinho
nos testes seguintes, mas a segunda tentativa é barata e cobre o caso.

**Diagnóstico**: quem recebe a tecla é a janela em **primeiro plano**, não quem tem o foco na
automação. As duas precisam ser a mesma coisa, e o rastro registra as duas — é o que separa "a
tecla foi para o lugar errado" de "este programa não tem menu".

## A bandeja "como o Windows faz" — o caminho que não existe mais

**`ITrayNotify` é o caminho de dentro, e está morto no Windows 11.** É a interface COM que o
próprio Explorer usa para a bandeja — é dela que sai a lista do "Ícones da área de notificação" do
painel de controle — e ela entrega `NOTIFYITEM` com o `hIcon`, a dica, o `hWnd` e o `uID` de cada
ícone. Com isso não haveria automação, nem `PrintWindow`, nem barra levantada, e o clique seria uma
mensagem direta ao programa dono. Medido nesta máquina (build 26200):

- `CoCreateInstance(CLSID_TrayNotify, CLSCTX_LOCAL_SERVER, IID D133CE13…)` = `S_OK`, e
  `RegisterCallback` é **aceito** — mas nunca chega um único evento. A bandeja XAML do Windows 11
  não alimenta mais essa interface;
- o IID do Windows 7 (`FB852B2C…`) devolve `E_NOINTERFACE`: não existe mais.

Dois detalhes que confundem quem for repetir o teste: **peça `CLSCTX_LOCAL_SERVER`** — com
`CLSCTX_ALL` o CLR pode criar uma instância dentro do próprio processo, que nasce vazia e parece a
mesma coisa; e a vtable no Win11 é a de dois argumentos (`RegisterCallback(cb, out handle)`) — a de
um argumento só, que a documentação da comunidade atribui ao Win8, estoura em violação de acesso.

Sobra o que já existe (automação + painel de ícones ocultos) ou injetar código no explorer.exe,
como fazem ExplorerPatcher e Windhawk. Não há terceiro caminho.

## O custo de abrir a bandeja — medido, e o que sobrou dele

Com um vigia amostrando a cada 20 ms a área de trabalho, a barra do Windows, o foco e o retângulo
de cada janela maximizada, uma abertura do cartão custava:

- a área de trabalho ia de `(0,24)` a `(0,32)` e voltava — os 8 px da barra do Windows;
- a barra do Windows ficava de pé 126 ms;
- a janela da pessoa perdia o foco por 126 ms (é a barra de título apagando e acendendo, o que se
  vê como "a tela redesenhou").

**Nenhuma janela maximizada era redimensionada** — isso o `PinnedWorkArea` já resolvia, e a
medição confirma: o pré-ajuste da área acontece antes de a barra aparecer, e sem `SPIF_SENDCHANGE`
ninguém se mexe.

**A oscilação e o aparecer/sumir foram eliminados apagando a barra pela camada** em vez do
`SW_HIDE` (veja `TaskbarService.SetTransparent`). De pé o tempo todo, porém invisível e
atravessável pelo mouse, ela não tem transição nenhuma — a leitura a encontra onde precisa e não
mexe em nada. Depois: zero mudança de área, zero movimento de janela, a barra nunca aparece.

**Sobrou a piscada de foco (~126 ms), e ela tem um piso.** Devolver o foco logo depois de a lista
ficar pronta encurtou de 126 para 95 ms na versão anterior. Devolver **antes** dessa espera foi
tentado e não funciona: o painel perde a ativação e para de montar a lista — a leitura vai a 4 s e
volta com zero ícones. O XAML do shell só preenche os botões enquanto o painel está ativo.

**A espera pelo fechamento tinha prazo, e prazo não é garantia.** A correção acima (esperar o
`Esc` terminar antes de soltar a camada) tinha um teto de 400 ms — suficiente num sistema
tranquilo, mas não sob carga. Medido depois de sessões de cliques rápidos e sustentados na seta
(dezenas em poucos segundos, o suficiente para o cache de 5 s expirar várias vezes): a animação
do `Esc` passou de 670 ms em mais de uma leitura. O relógio estourava, e o `using` soltava a
camada do mesmo jeito — com a janela ainda de pé de verdade. Por isso o vazamento não era a cada
leitura: só aparecia depois de várias trocas rápidas de aberto/fechado, quando o sistema estava
ocupado o bastante para o `Esc` não terminar a tempo (confirmado com um segundo print do usuário,
"depois de 8 cliques").

A correção final não aposta em tempo nenhum: depois da espera, confere se o painel **ainda está
de pé** e, se estiver, força o esconder de verdade (`SW_HIDE`, o mesmo caminho de acionar um
ícone — instantâneo, não depende de nenhuma animação terminar) antes de deixar o `using` soltar a
camada. Testado sob a mesma pressão que expôs o bug — mais de 180 cliques em sessões de 25 a 30 s
a 200-350 ms de intervalo — sem vazar uma vez.

## O foco ficava preso no Explorer depois de um clique esquerdo

**"A tela redesenha" e "apps maximizados flicam ao clicar" eram o mesmo problema: o clique
esquerdo num ícone nunca devolvia o primeiro plano.** Abrir o painel de ícones ocultos para achar
o botão do ícone traz o **explorer** para primeiro plano — mesmo invisível, ele rouba a ativação
de quem estava na frente. Isso sempre foi assim; o que faltava era devolver depois. Para ícones
cujo clique abre a própria janela (o Discord, por exemplo), o problema não aparecia — a janela do
programa assumia o primeiro plano por conta própria e escondia o sintoma. Para ícones sem janela
própria (o Pageant é o caso), **o primeiro plano ficava preso no explorer para sempre**: medido,
661 ms parado ali e depois **nunca** voltava sozinho — a janela maximizada que estava na frente
ficava desativada (borda/barra de título "inativa") até a pessoa clicar nela de novo à mão. Era
isso que se via como "a tela redesenha ao clicar num ícone da bandeja": a desativação (e a
reativação manual depois) de uma janela grande é visualmente cara, principalmente em apps que
redesenham bastante ao ganhar/perder foco (IDEs, navegadores).

A correção: `TrayService.Invoke` guarda quem estava em primeiro plano antes do clique e, se
depois de tudo o primeiro plano continuar em qualquer coisa do processo `explorer`, devolve para
quem estava antes. A condição importa: só devolve se **ninguém de verdade** tomou a frente — um
clique que abre a janela do próprio programa (Discord) não é interrompido, só o caso em que o
clique não tinha para onde ir.

**O menu (botão direito) nunca teve esse problema.** Testado: com o menu aberto, o primeiro plano
vai para o processo **dono do ícone** (o Discord, não o explorer), e ao fechar o menu (Esc), o
Windows devolve o primeiro plano sozinho para quem estava antes — sem precisar de nada da nossa
parte. Por isso `ShowMenu` continua sem devolver foco: fazer isso a mais cedo fecharia o menu que
acabou de abrir (menus nativos do Windows se fecham quando o dono perde a ativação).

**O que não dá para eliminar**: enquanto o painel de ícones ocultos precisa estar de pé para a
automação encontrar o botão certo, a janela que estava na frente fica genuinamente desativada por
essa janela de tempo (hoje ~150 a 700 ms, dependendo de quão rápido o Explorer processa). A
correção acima garante que ela **volta** — não elimina os milissegundos em que ficou fora.

**O salto de cursor no menu não tem como sumir de vez — só três caminhos foram tentados, e
nenhum é livre disso.**

1. `mouse_event` mirando o ícone: sequestra o cursor, ele salta e volta à vista, e ainda assim
   o menu não abria.
2. `WM_RBUTTONDOWN`/`WM_RBUTTONUP` **postados por mensagem** (`PostMessage`), sem tecla nenhuma,
   nas coordenadas de cliente do ícone: **não move o cursor** — mas também **não abre o menu**.
   Confirmado repetidas vezes: "clique direito mandado ao ícone" no log, seguido sempre de
   "nenhum menu apareceu em 800 ms". O painel de ícones ocultos é XAML/WinUI moderno, e não
   reage a mensagem de mouse simulada por `PostMessage` — só entrada de verdade (`SendInput`) ou
   o atalho de teclado chegam até o hit-test dele. Isso bate com o motivo de `mouse_event` (que
   *é* entrada de verdade) ter funcionado tecnicamente antes, só que sequestrando o cursor.
3. `SetFocus` + tecla Menu (`VK_APPS`, o mesmo que Shift+F10): o único que abre o menu de forma
   confiável. O Windows move o cursor sozinho até o menu quando ele abre por teclado — não é
   nada que a gente manda, é o sistema reagindo à tecla — mas dá para devolver o cursor depois
   que o menu já está posicionado (`SetCursorPos`), o que pelo menos evita que ele fique parado
   longe de onde a pessoa estava.

**Ainda assim, o vaivém aparece na tela** — o cursor pula até o ícone e volta, rápido (~150-250 ms
medidos), mas visível. Não é um bug com conserto pendente: é o preço de abrir um menu do sistema
sem usar o mouse de verdade, e as duas alternativas testadas (clicar de verdade, clicar por
mensagem) são piores — uma rouba o cursor sem devolver, a outra nem abre o menu.

**Um quarto caminho, testado a fundo com o pedido explícito do usuário de "fazer igual ao
Explorer": `IUIAutomationElement3::ShowContextMenu`.** É um método feito especificamente para
abrir menu de contexto por automação, existe desde o Windows 8.1 — mas só na COM nativa
(`UIAutomationClient.dll`), não na API gerenciada (`System.Windows.Automation`) que o resto do
projeto usa. Exigiu interop manual: `CUIAutomation8` (CLSID `E22AD333-...`), `IUIAutomation`
(IID `30CBE57D-...`), e o QueryInterface de `IUIAutomationElement` (IID `D22108AA-...`) para
`IUIAutomationElement3` (IID `8471DF34-...`) — essa última precisa dos 82+6 membros herdados
declarados como métodos "buracos" antes de `ShowContextMenu` (membro 89), porque o vtable do COM
é posicional, não por nome; a assinatura de cada buraco não importa, só a contagem.

**Retorna `S_OK` e não abre nada** — nem para o ícone do Discord (XAML moderno) nem para o do
Pageant (menu clássico do Windows, `#32768`). Testado com o painel de ícones ocultos genuinamente
aberto (dentro do `WithShell` de verdade, não um teste isolado com painel simulado) — mesmo assim,
zero janelas novas em nenhum dos dois casos. A documentação da própria Microsoft avisa que isso
pode acontecer quando o elemento (ou o pai, para quem o método cai de volta) não tem um provedor
de menu de contexto implementado — parece ser o caso dos dois tipos de ícone testados aqui.

Como o método sempre retorna sucesso mesmo sem funcionar, só dá para saber que falhou **medindo**:
conferindo se uma janela nova apareceu depois, com um prazo curto antes de cair para a tecla Menu.
Isso funcionou (o *fallback* sempre pegou o menu certinho), mas custava 400 ms de espera **em
toda chamada**, sem nunca ter sucesso — então foi removido de novo. Não vale reabrir esse caminho
sem uma pista nova de que algum provedor de contexto esteja realmente implementado para esses
ícones.

**Conclusão depois de quatro tentativas** (`mouse_event`, `WM_RBUTTONDOWN`/`UP` por mensagem,
`ShowContextMenu` nativo, e a tecla Menu que fica valendo): a tecla Menu com um salto de cursor
único (sem devolução — a devolução foi tentada e tirada por criar vaivém) é o piso real do que dá
pra fazer sem usar o mouse de verdade. "Fazer igual ao Explorer" nesse ponto especificamente exige
um clique físico de mouse de verdade — e Explorer também move o cursor quando você abre o mesmo
menu pelo teclado (Shift+F10); a diferença é só que ninguém repara, porque ninguém usa atalho de
teclado pra bandeja no dia a dia.

**Um quinto caminho: mandar o clique para a janela certa de dentro do painel, não para a de
fora.** A primeira tentativa de clique por mensagem (item 2 acima) mandava para a janela de
**fora** do painel — a que só hospeda o conteúdo. Painéis XAML/WinUI modernos têm uma janela
filha própria que processa entrada de verdade, classe `Windows.UI.Input.InputSite.WindowClass`
(a mesma ideia da "ponte" que a barra do Windows usa para o próprio conteúdo — veja o
`WaitForChevron`). Implementado: achar essa janela (com espera, pois não nasce no mesmo instante
que a lista fica pronta), mandar `WM_MOUSEMOVE` antes (o pipeline de ponteiro do XAML precisa de
um "entrar" antes de aceitar o "clicar") e depois `WM_RBUTTONDOWN`/`WM_RBUTTONUP`.

**Não funcionou — a janela InputSite simplesmente não existe** no ponto do fluxo em que o menu
precisa ser aberto, nem esperando até 300 ms por ela. Pode ser que ela só apareça sob outras
circunstâncias (o painel realmente em foco, por exemplo, o que a leitura evita de propósito), ou
que esta versão específica do shell não a crie da forma esperada. Removido de novo.

**Cinco caminhos tentados ao todo agora.** Nenhum abre o menu sem tecla nenhuma. A tecla Menu
continua sendo o único que funciona de forma confiável, com o salto de cursor único que vem de
brinde. Esta é uma limitação real do que dá para fazer de fora do processo do Explorer sem
injetar código nele (o caminho do Windhawk/ExplorerPatcher) — não um bug esperando conserto.

**O salto de cursor não é universal — depende de como o programa dono do ícone implementa o
próprio menu, e é o Explorer compensando, não o Windows em geral.** Descoberto comparando dois
ícones com a **mesma classe** de menu (`#32768`, o popup clássico do Windows): o ícone de
"Segurança do Windows" (`SecurityHealthSystray.exe`, da própria Microsoft) abre o menu pela tecla
Menu **sem mover o cursor**; o do Pageant (PuTTY), com o mesmíssimo teste, sempre move.

A explicação mais provável: existe um protocolo mais novo de bandeja (a partir de
`NOTIFYICON_VERSION_4`) em que a mensagem de "abra seu menu" já carrega a posição do ícone —
programas modernos e bem-feitos leem dali. Programas mais simples (Pageant é um utilitário
pequeno e antigo do PuTTY) só sabem perguntar `GetCursorPos()` para decidir onde desenhar o
próprio menu — e é *só para esses* que o Explorer precisa mover o cursor de verdade antes de
mandar a tecla, como muleta de compatibilidade, senão o menu do programa nasceria em qualquer
lugar que o cursor real estivesse.

**Não abre um caminho novo para o WinDock.** O salto é uma decisão do Explorer sobre **o
programa dono do ícone**, não algo que a nossa automação controla ou possa evitar por fora —
não há como fazer o Pageant (ou o Discord, que quase certamente cai no mesmo caso por ser
Electron com binding simples de bandeja) ler a posição da mensagem em vez de perguntar ao
cursor. Fica só como explicação satisfatória do "por que alguns ícones pulam e outros não" —
confirma que não é bug nosso, é implementação de terceiros.

## Áudio

**`IsSystemSoundsSession()` devolve `S_OK` (0) quando *é* a sessão de sons do sistema** — não o
contrário. Ler como booleano invertido esconderia todos os apps e mostraria só o "Sons do sistema".

**`IAudioSessionControl2` precisa repetir os nove métodos do `IAudioSessionControl`** antes dos
próprios: COM chama por posição na tabela, e a herança em C# não a reproduz.

**O mixer fica vazio quase sempre, e está certo.** Sessão de programa que parou de tocar vira
"expirada" e some — é o mesmo que o painel do Windows faz. Para testar é preciso *criar* uma
sessão: um `SoundPlayer.PlayLooping` num processo à parte serve, e com as caixas mudas nem faz
barulho.

**Um programa aparecia duas vezes, e não era o microfone.** O Discord mostrava duas barras. A
suspeita natural (saída + entrada) estava errada, e medir custou menos que argumentar: as duas
sessões estão na **saída** — pids 4956 (inativa) e 5272 (ativa). A sessão de microfone existe,
mas mora no dispositivo de *entrada*, que `Sessions()` nunca enumera. Programas feitos de vários
processos (Discord, Chrome, Teams) abrem um fluxo por processo, e cada fluxo é uma sessão.

**A chave para juntá-los é o `SessionIdentifier`, e ela é exata.** Nas duas sessões do Discord
ele veio **idêntico byte a byte**; o que difere é o `SessionInstanceIdentifier`, que carrega o pid
num sufixo `|1%b<pid>`. É o mesmo critério do mixer do Windows — por isso lá o Discord sempre
apareceu uma vez só.

```
sid       …\Discord.exe%b{00000000-0000-0000-0000-000000000000}          ← igual nos dois
instância …\Discord.exe%b{00000000-…-000000000000}|1%b4956               ← só aqui difere
instância …\Discord.exe%b{00000000-…-000000000000}|1%b5272
```

**Agrupar exige escrever em todas as sessões, mas ler de uma só.** O controle passou a guardar
uma lista de `ISimpleAudioVolume`: a leitura sai da primeira, a escrita vai para todas — senão
mexer no controle abaixaria metade do som do programa. E quem representa o grupo (nome e ícone,
que saem do processo) tem de ser a sessão **ativa**: o processo auxiliar pode já ter morrido, e
aí o nome viria vazio.

**Para provar o agrupamento não espere o Discord colaborar.** Na hora de conferir, a sessão
inativa já tinha expirado e sobrara uma só — o teste teria "passado" sem exercitar nada. Dois
`powershell.exe` tocando um wav ao mesmo tempo dão a condição de propósito (mesmo executável,
logo mesma chave): medido, 2 fluxos numa linha só.

## Miniaturas de janela (DWM)

**O DWM não desenha miniatura dentro de janela em camada.** `AllowsTransparency="True"` no WPF
faz exatamente isso com a janela, e o painel de preview aparece vazio — sem erro. Para cantos
arredondados sem transparência, `DWMWA_WINDOW_CORNER_PREFERENCE` com `DWMWCP_ROUND`.

(É o oposto do que vale para o `PrintWindow`, que **precisa** da camada. As duas APIs pedem
coisas contrárias da mesma propriedade.)

**A miniatura é composta por cima da janela, não dentro do layout.** O XAML só reserva o espaço;
o retângulo de destino é dado em coordenadas de cliente e calculado depois do layout.

**`SizeToContent` só mede depois do `Show`.** Posicionar antes usa altura zero e o painel nasce
meio fora da tela. Mostrar fora da tela (`Left = -20000`), medir, e só então mover.

**`DwmQueryThumbnailSourceSize` dá a proporção certa** — melhor que `GetWindowRect`, que inclui a
moldura invisível e deixaria a miniatura levemente esticada.

**Soltar as miniaturas (`DwmUnregisterThumbnail`) faz parte**: sem isso o compositor continua
desenhando janelas que já foram fechadas.

**A dica de mouse tem de ser desligada na entrada do mouse, não quando o preview aparece.** As
duas têm mais ou menos o mesmo atraso; desligar depois chega tarde e a dica fica por cima do
painel, repetindo o que ele mostra.

## Janelas e foco

**A dock usa `WS_EX_NOACTIVATE`** para não roubar o foco ao ser clicada. Sem isso, o
"clique de novo minimiza" nunca funcionaria: no instante do clique quem estaria em foco seria a
própria dock.

**A consequência**: nada que a dock abra vem para a frente sozinho. Painel de configurações,
launcher e afins precisam de um empurrão — `WindowService.Focus`, que anexa o input da thread em
primeiro plano antes do `SetForegroundWindow`. Anexar à thread **do alvo** (e não à do
foreground) não funciona.

**`Popup.StaysOpen="False"` não fecha ao clicar fora** numa janela `NOACTIVATE`: depende de
captura de mouse, que não acontece. É preciso vigiar `GetAsyncKeyState` — e usar o bit `0x0001`
("apertado desde a última consulta") além do `0x8000`, senão um clique de 60 ms passa entre duas
leituras de 100 ms.

**Um popup se fecha sozinho ao apertar o botão que o abriu**, antes do evento `Click` chegar. Ler
`IsOpen` no clique faz o painel reabrir; o estado tem de ser lido no `PreviewMouseDown`.

**`Win+A` e `Win+N` sintéticos não abrem os painéis** quando a barra está escondida — o atalho só
foca a barra. Os endereços do shell funcionam sem depender de foco: `ms-availablenetworks:` e
`ms-actioncenter:`.

**Fechar esses painéis**: `ControlCenterWindow` (processo `ShellHost`) e `Windows.UI.Core.CoreWindow`
(processo `ShellExperienceHost`). A classe `CoreWindow` sozinha não serve — é a de qualquer app da
Store; confirme pelo processo antes de mandar `Esc`.

**O clique num ícone parava de responder por causa do foco guardado.** O `DockModel` guarda a
última janela em primeiro plano que não era da dock (`_foreground`), porque no instante do clique o
Windows pode responder "a dock". Só que esse valor **congela**: ele só é relido numa atualização da
dock, e a atualização depende de o evento `EVENT_SYSTEM_FOREGROUND` passar pelo filtro `Concerns`.
Um app com ilha XAML — o Windows Terminal — anuncia a troca de foco pela **janela de conteúdo**, que
não é raiz e não estava na lista conhecida: o evento era descartado, o valor guardado ficava na
janela anterior, e o clique seguinte caía num vão sem saída — não era "em foco" o bastante para
minimizar, e o `ForceForeground` desistia calado porque a janela **já** estava à frente. Resultado:
clique sem efeito e sem uma linha no log, até o timer de 3 s destravar sozinho. No rastro isso
aparece como uma sequência de "clique na dock em 'X'" **sem** nenhuma "dock atualizada" atrás.

Três coisas juntas consertam, e vale manter as três: perguntar o foco **real**
(`GetForegroundWindow`) além do guardado ao decidir entre minimizar e trazer para frente; subir até
`GA_ROOT` no filtro de eventos **só** para `EVENT_SYSTEM_FOREGROUND` (nos de criar/mostrar/esconder
isso reabriria a enxurrada de controles que o filtro existe para barrar); e não deixar nenhum
caminho de ativação sair calado — janela morta e "já estava à frente" agora deixam rastro.

**Tela cheia**: janelas topmost ficam por cima até de quem está em tela cheia (era a dock sobre a
sessão RDP). `FullScreenWatcher` esconde a dock quando a janela em primeiro plano cobre o monitor,
e também quando é uma sessão remota (`TscShellContainerClass`, `RAIL_WINDOW`) que passou por cima
da faixa da dock.

## Ícones

**O shell devolve os pixels de dois jeitos e não avisa qual.** A maioria vem premultiplicada pelo
alfa; os ícones das Configurações e de outros apps da Store vêm com a cor crua. Tratar cru como
premultiplicado pinta de branco tudo que deveria ser transparente — era a chapa branca atrás da
engrenagem. `IconService` decide olhando os pixels: em premultiplicado nenhum canal de cor passa
do alfa.

**Apps da Store não têm executável útil.** `SystemSettings.exe` traz o ícone genérico e nem sempre
abre. Ícone e lançamento vêm de `shell:AppsFolder\<AppUserModelID>` (via `explorer`).

**O WPF escolhe mal o quadro de um `.ico`**: pediu 44 px e usou o de 24, esticado. Para tamanhos
grandes, aponte para um PNG.

**Ícone que precisa sobreviver a 16 px precisa de outro desenho.** A pílula deitada da dock vira
um borrão; abaixo de 32 px o `.ico` usa uma versão mais cheia, com dois blocos e sem o traço.

**Engrossar glifo empilhando cópias deslocadas serrilha.** Cada cópia cai numa fração de pixel
diferente e o antialias se soma. O jeito certo é converter em geometria e desenhar com
preenchimento e caneta juntos (`src/Ui/GlyphIcon.cs`).

**A fonte de ícones do Windows (Segoe Fluent Icons) não tem versão preenchida** de wi-fi, volume e
sino — só contorno. Conferido renderizando treze candidatos.

## Bluetooth

**A API clássica (`bthprops`) só enxerga Bluetooth clássico.** O fone JBL aparecia; o teclado, que
é LE, não. `BluetoothFindFirstRadio` continua útil para saber se o **rádio está ligado** (só
devolve handle com o rádio ligado).

**Os aparelhos vêm da lista de dispositivos (`setupapi`)**, filtrando o identificador:
`BTHENUM\DEV_` para clássicos e `BTHLE\DEV_` para LE. O resto (serviços, perfis, enumeradores) é
ruído. Bônus: funciona com o rádio desligado.

**Ligar/desligar o rádio exige WinRT — e o alvo foi mudado para isso** (04/09/2026). Não existe
API clássica: `bthprops` sabe dizer que o rádio existe e mexer em visibilidade, mas não
alimentá-lo. `Windows.Devices.Radios.Radio` é o mesmo caminho do botão do painel do Windows, e
sair de `net8.0-windows` para `net8.0-windows10.0.19041.0` **não custou dependência nenhuma** — as
projeções vêm do próprio SDK do .NET, o `dotnet build` continua sem NuGet e sem aviso. Medido
antes de mexer no projeto: `RequestAccessAsync` = `Allowed`, e `GetRadiosAsync` lista `WiFi On` e
`Bluetooth On`.

**Não teste o desligar sem avisar quem está na máquina** — o teclado desta é Bluetooth, e
desligar o rádio deixa a pessoa sem teclado. O `SetOn` foi escrito e compilado sem nunca ter sido
disparado por aqui, de propósito.

**"Presente" não é "conectado", e era esse o bug silencioso.** A lista usava duas enumerações e
tratava `DIGCF_PRESENT` como conectado. Presente quer dizer que o Windows tem o aparelho
*montado*, e um fone continua montado por um tempo depois de desligar. A resposta certa é uma
propriedade, numa enumeração só:

| | chave | tipo | valor |
|---|---|---|---|
| conectado | `{83DA6326-97A6-4088-9453-A1923F573B29},15` | `DEVPROP_TYPE_BOOLEAN` | `0xFF` sim, `0` não |
| bateria | `{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2` | `DEVPROP_TYPE_BYTE` | 0–100; **>100 é "não sei"** |

Lidas com `SetupDiGetDevicePropertyW` (não confundir com `SetupDiGetDeviceRegistryProperty`, que
é a API antiga e não enxerga estas). Conferido nesta máquina: o teclado AULA-F99Pro (LE) devolve
`conectado=sim, bateria=93%`; o JBL Tune 720BT (clássico) devolve `conectado=não` e **não tem a
chave de bateria** — os dois aparecendo na lista de pareados, como se quer.

**Bateria é privilégio de quem publica o serviço** — na prática os LE. Em aparelho clássico a
chave ou não existe ou vem `255`. Por isso `Battery` é `int?`: ausente é diferente de zero, e a
coluna some em vez de mostrar "0%".

**Como se acha uma chave dessas sem chutar:** `SetupDiGetDevicePropertyKeys` lista *todas* as
propriedades que o Windows expõe para aquele dispositivo, e aí é só imprimir cada uma com o tipo
e o valor. Foi assim que o `0x5D` (93) apareceu. Cuidado ao decodificar: `DEVPROP_TYPE_BOOLEAN` é
`0x11` e `STRING` é `0x12` — trocar os dois faz toda string virar "True" e passa despercebido.

## Abrir um app

**Nem todo AppUserModelID de janela existe na pasta de aplicativos.** A janela do segundo perfil
do Chrome se anuncia como `Chrome.UserData.Profile2`, mas
`SHCreateItemFromParsingName("shell:AppsFolder\Chrome.UserData.Profile2")` devolve `0x80070002`
(arquivo não encontrado) — e `explorer.exe shell:AppsFolder\<id inexistente>` **não faz nada e
não reclama**: o explorer sobe, não acha, e sai calado. Era esse o botão morto.

**Deduzir "é app da Store" pelo formato do ID não funciona.** A regra antiga ("não tem `\`, logo é
AUMID, logo abre pela pasta de aplicativos") acertava o `Microsoft.WindowsStore_...!App` e errava
o perfil do Chrome, jogando fora de quebra o `--profile-directory`. Quem responde é o shell:
`SHCreateItemFromParsingName` diz sim ou não em milissegundos, e a resposta cabe num cache.

**Um argumento é prova de que o caminho é o que vale.** O lançamento por AUMID não tem onde levar
parâmetro; se o botão tem argumento, ele abre pelo executável, sempre.

**Erro engolido em silêncio custa caro aqui.** Todo `Process.Start` da dock estava dentro de um
`catch { }` mudo — clicar e não acontecer nada era indistinguível de bug de clique, de foco ou de
ícone. Agora as falhas de abrir e de trazer para frente vão para `%APPDATA%\WinDock\windock.log`.

**Janela de app elevado não obedece a dock.** WinDock roda em integridade média; contra uma janela
de integridade alta, `AttachThreadInput` e `SetForegroundWindow` falham sem erro visível. Resta
`SwitchToThisWindow`, que alcança parte desses casos — e o log, para o resto.

## Iniciar com o Windows

**A chave `Run` é atrasada de propósito.** O Windows enfileira os aplicativos de inicialização e
o primeiro só arranca cerca de dez segundos depois da área de trabalho. Numa dock isso aparece
como "demorou para iniciar" — e não é o app que está lento.

**A tarefa agendada com gatilho de logon fura essa fila.** `Schedule.Service` por COM tardio,
`TASK_LOGON_INTERACTIVE_TOKEN` com `RunLevel = 0`: um usuário comum registra tarefa na raiz para
si mesmo, sem elevação. Verificado nesta máquina — registrar, `GetTask`, apagar.

**Quatro segundos de folga no gatilho.** O gatilho dispara junto com o logon, antes do Explorer
estar de pé — e sem Explorer não há `Shell_TrayWnd` para registrar a AppBar.

**A leitura dos atalhos estava no caminho da primeira pintura.** São ~180 `.lnk` lidos por COM
(630 ms com o disco quente, bem mais no logon) e a primeira atualização da dock esperava por eles.
Foram para uma thread STA de fundo; a barra pinta com os fixados e os apps abertos entram depois.

## WPF

**Binding só enxerga propriedades de instância.** Dois glifos declarados como `const` chegavam
vazios na barra — sem erro, sem aviso.

**Dentro de um menu, o WPF ignora o estilo implícito de `Separator`** e procura a chave
`MenuItem.SeparatorStyleKey`. Sem ela o separador continua sendo o do tema claro.

**`HasDropShadow` do sistema vem com quina reta** e aparece por baixo de cantos arredondados.
Sombra própria dentro do template, com margem para ela caber.

**Os glifos da Segoe Fluent Icons se perdem numa edição.** No editor eles são um quadradinho
vazio; uma substituição de texto comeu o do botão de energia e ele ficou **invisível na barra
sem nada quebrar nem avisar**. Escreva-os como `""`, não como o caractere.

**O `Calendar` do WPF vem com o tema claro** e reestilizá-lo custa um `ControlTemplate` inteiro —
mais XAML do que a grade que ele desenharia. Seis semanas fixas num `UniformGrid` custam menos e
mantêm a altura do painel igual todo mês.

**Valor local vence gatilho de Style, e isso esconde bugs de cor.** A palavra "conectado" no
painel de bluetooth nunca chegou a colorir — nem no azul original. O `TextBlock` trazia
`Foreground="#FF9D9D9D"` como atributo, e um valor local tem precedência sobre o `Setter` de um
`DataTrigger`. O `Text` trocava (não era local) e a cor não, então **parecia** que o gatilho
funcionava. A cor padrão tem de sair do próprio `Style`, junto da do gatilho. Passou anos
despercebido porque havia um ponto colorido ao lado dando o mesmo sinal.

**O `ProgressBar` não desenha nada sem um `PART_Track` no template.** Um template com só o
`PART_Indicator` compila, roda e mostra a barra de fundo **cheia**: o WPF calcula a largura do
indicador medindo o track, e sem ele não calcula nada. Para uma barra simples de porcentagem sai
mais barato um `Rectangle` com a largura vinda do modelo (como `BatteryFill` e `VolumeFill` já
faziam) do que acertar as peças nomeadas.

**`ToolTipService.SetIsEnabled(false)` não fecha a dica que já está na tela** — só impede a
próxima. Ao rolar a roda sobre um ícone a dica sempre está aberta (o cursor ficou parado ali
tempo suficiente), e ela aparecia por baixo do mostrador de volume, dois cartões empilhados. Para
poder fechá-la, a dica precisa ser um objeto `<ToolTip x:Name="...">` e não um texto solto, e aí
são dois passos: `IsOpen = false` tira a atual, `SetIsEnabled(false)` segura a seguinte.

**`TextFormattingMode="Display"` não é a renderização do Windows 11.** Ele arredonda as métricas
para pixels inteiros; o Windows usa o equivalente ao `Ideal`, com métricas fracionárias. A fonte
já era a certa (`Segoe UI Variable Text`, conferido: resolve mesmo, não cai no fallback
`Segoe UI`) — o que destoava era só o modo. Detalhe que fechou a decisão: `MainWindow.xaml` nunca
declarou esse atributo, e o **padrão do WPF já é `Ideal`** — a barra de cima e a dock vinham
desenhando texto de jeitos diferentes sem ninguém notar.

**Um estilo compartilhado com `StaticResource` no template não deixa uma instância mudar de
cor.** O `FluentSlider` fixava `FluentAccent` dentro do `ControlTemplate`, então colorir só o
slider de volume mudaria todos os das Configurações junto. A saída é o template ler
`{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}` e o `Style` trazer o
azul como `Setter` padrão — quem quiser troca só para si. `TemplateBinding` não serve quando o
alvo está dentro do template de um `RepeatButton` aninhado: ali ele aponta para o botão, não para
o `Slider`.

**Painéis de duas famílias precisam de alguém que feche as duas.** A barra tem botões que abrem
popups nossos (bluetooth, volume, energia, calendário, bandeja) e botões que abrem painéis do
Windows (wi-fi, sino). Cada handler fechava só os da própria família, então clicar no wi-fi com o
bluetooth aberto deixava os dois na tela ao mesmo tempo. Um único `CloseOpenPanels()` — nossos
popups mais um `Esc` sintético quando o painel do shell estava de pé — resolve nos dois sentidos.
O `Esc` só pode ser mandado se o painel estava mesmo aberto: solto, ele vai parar em quem
estiver em primeiro plano.

## Caixas de diálogo e o tema do Windows

**O `MessageBox` do WPF ainda é a caixa do Windows XP por dentro** — moldura cinza, fonte pequena,
ícone amarelo — e num Windows 11 ela aparece como se fosse de outro programa. A caixa própria
(`ConfirmWindow`) leva a silhueta das do sistema: barra de título nativa, instrução principal em
corpo 20, explicação em corpo 14 e os botões numa faixa de rodapé com cor própria.

**A barra de título não segue o tema sozinha**: sem `DwmSetWindowAttribute` com
`DWMWA_USE_IMMERSIVE_DARK_MODE` ela vem branca por cima de uma caixa escura. É o mesmo aviso que o
painel de configurações já dava.

**A dock tem paleta própria; uma caixa de diálogo, não.** A barra é nossa e pode ser escura sempre;
uma caixa que nasce no meio da tela com a moldura do sistema tem de seguir o tema do Windows, ou
denuncia na hora que não é dele. O tema vem de `AppsUseLightTheme` — e não da chave irmã
`SystemUsesLightTheme`, que é só da barra de tarefas e do menu iniciar, e daria a resposta errada
para quem escolhe "escuro no sistema, claro nos apps".

**A cor de destaque não é a cor crua que a pessoa escolheu.** O Windows guarda em `AccentPalette`
oito tons de 4 bytes (RGB + um byte sempre zero), **do mais claro para o mais escuro**: Light3,
Light2, Light1, a cor base, Dark1, Dark2, Dark3, e na última a cor pura. Conferido nesta máquina: a
entrada 3 bate com `AccentColorMenu`. Nos botões de ação o Windows usa **Light2 no tema escuro e
Dark1 no claro** — usar a base deixa o botão escuro demais no tema escuro, que foi o primeiro
resultado aqui.

**Uma caixa aberta pela dock precisa do empurrão de foco.** A dock é `WS_EX_NOACTIVATE`: nada que
ela abre vem para a frente com o teclado junto, e sem `WindowService.Focus` a caixa aparece mas o
que a pessoa digita continua indo para o app anterior. O dono também importa — sem dono, a caixa
nasce **por baixo** da dock, que é topmost.

## Testar nesta máquina (continuação)

**`Invoke` da automação não move o mouse — e isso fecha os painéis da barra.** O vigia de "clique
fora" lê `GetAsyncKeyState`, vê um clique antigo, olha onde está o cursor (longe da barra) e
fecha o painel que acabou de abrir. Parecia bug do app e era do teste: para exercitar a barra é
preciso **clique de verdade** (`SetCursorPos` + `mouse_event`), devolvendo o cursor ao lugar
depois.

**`CloseMainWindow()` não fecha a dock** — ela não tem janela principal aos olhos do .NET. É
preciso `PostMessage(WM_CLOSE)` para *todas* as janelas do processo, e esperar: publicar antes de
o processo morrer falha com o arquivo travado.

**O `HelpText` da automação demora a acompanhar o binding.** Um teste que lia o tooltip logo
depois do clique para saber se a barra tinha alternado de estado leu o valor velho e concluiu que
o clique não funcionara. Para verificar estado da interface, conte elementos (existem ou não), não
leia texto que acabou de mudar.

## Testar nesta máquina

**Não dá para roubar o primeiro plano enquanto a pessoa trabalha** — e ainda bem. Três tentativas
de simular uma janela em tela cheia falharam porque o foreground ficou com o app do usuário. Testes
que dependem de foco precisam da máquina ociosa.

**`PrintWindow` com `PW_RENDERFULLCONTENT` captura a janela mesmo coberta ou com a sessão
bloqueada** — é assim que dá para conferir a barra e a dock sem disputar a tela.

**Cuidado com `| tail -2` no build.** Um erro de compilação ficou escondido e o app antigo seguiu
rodando por várias rodadas de "correções" que nunca chegaram ao binário. Olhe a linha de erros.

**`sed` e `perl` estragam caminhos do Windows e escapes `\uXXXX`** (`\U`, `\L` viram comandos de
caixa). Para editar C#/XAML com escapes, use a ferramenta de edição, não regex de shell.

## A bandeja: o que custa e o que interfere

**A "mensagem sobreposta" era a dica do próprio Windows.** Acionar a seta de ícones ocultos faz o
shell preparar o balão dele ("Mostrar ícones ocultos"), numa janela de classe
`Xaml_WindowedPopupClass` (título `PopupHost`). Ela aparece uns **200 ms depois** de a leitura já
ter terminado e tudo ter voltado ao lugar — e é uma janela à parte, que **não herda** o
`WS_EX_LAYERED` com que escondemos a barra. Por isso pousava em cima do nosso cartão, sem nada na
tela que explicasse de onde tinha vindo.

Pôr `WS_EX_TRANSPARENT` na barra do Windows **não resolve** (medido: o balão aparece igual). O que
resolve é varrer e esconder a janela depois, por ~1,5 s, olhando só a faixa de cima da tela. A
varredura precisa ser de 10 em 10 ms: com 50 ms o balão ainda pisca.

**O elemento da seta do Windows pode ser guardado entre leituras.** Procurá-lo custava ~1,1 s por
leitura — a barra tinha acabado de ser mostrada e a árvore de automação dela leva esse tempo para
ficar de pé. O elemento continua válido enquanto o explorer for o mesmo; se ele morrer, a chamada
estoura e a busca acontece como antes. Sozinho, isso tirou 1 s dos 2,6 s da leitura.

**O `FindAll` no painel recém-aberto custava ~1,2 s — e a culpa era nossa.** Escrevi aqui que era
"o shell montando a lista dele, e não há como fugir". Estava errado. A busca acontecia **debaixo
do martelo da área de trabalho**: o `PinnedWorkArea` repõe `SPI_SETWORKAREA` quarenta vezes por
segundo, e isso atrapalha a automação do mesmo jeito que já se sabia que atrapalhava os retângulos
dos ícones. Fora do martelo, a mesma chamada custa ~40 ms, e a leitura inteira caiu de 1,8 s para
**470 ms**. Fica a regra: **nada de automação enquanto o martelo estiver batendo** — e, no fim,
que o martelo não devia existir.

**Um gancho de WinEvent sem filtro é uma moagem contínua.** `EVENT_OBJECT_CREATE..HIDE` vale para
**toda janela do sistema**, e um único programa de interface rica gera dezenas por segundo sem
nada mudar na barra de tarefas: medido aqui, com o Delphi aberto, 98 eventos em 8 s — barras de
ferramentas, campos de edição, listas de combo nascendo e morrendo. Cada um mandava a dock se
refazer inteira: 60 linhas de "dock atualizada" por segundo, sempre com o mesmo resultado. Quem
paga é a thread da interface, e o preço aparece longe da causa — clique lento, cartão piscando,
leitura da bandeja travada, apps abertos demorando a aparecer.

Dois testes baratos, nesta ordem, resolvem: **já conheço esta janela?** (um `HashSet` das janelas
da última varredura — é a única resposta possível para o `DESTROY`, quando o identificador já
morreu) e **é de topo, sem dono e com título?** (`GetAncestor(GA_ROOT) == hWnd`,
`GetWindow(GW_OWNER) == 0`, `GetWindowTextLength() > 0`) — o mínimo para virar botão. Medido
depois: de ~60/s para ~1,4/s em repouso.

Ficam válidos os dois becos sem saída medidos no caminho: um `CacheRequest` com `Name` e
`BoundingRectangle` **não muda** o custo da busca (vale por outro motivo — as propriedades vêm
junto, em vez de uma ida e volta por propriedade depois), e aquecer o provedor com uma busca na
janela ainda fechada custa 100 ms e economiza zero.

**Acionar um ícone é uma leitura inteira de novo, e o botão guardado não serve.** Medido: com o
painel do Windows fechado, o `InvokePattern.Invoke()` no `AutomationElement` guardado da leitura
anterior **não estoura e não faz nada** — o provedor morreu junto com o flyout. Não há atalho:
clicar num ícone reabre o painel. O que dá para fazer é não atrapalhar — o Esc que fecha o painel
fecharia junto o menu que o ícone acabou de abrir, e devolver o foco à janela anterior o tiraria
desse menu.

**Ler e acionar não podem acontecer ao mesmo tempo.** O clique num ícone vem logo depois de abrir
o cartão, que é exatamente quando a releitura está em curso — e as duas abriam o painel do shell
em paralelo. Os dois caminhos têm de passar pela mesma thread: a leitura pode ser descartada
quando chega em cima de outra, mas o clique da pessoa espera a vez, nunca é jogado fora.

**Popup do WPF ancorado pela esquerda salta de lugar quando o conteúdo muda de tamanho.** O cartão
nasce com os ícones em cache e se corrige quando a leitura volta; se a contagem mudar, a largura
muda, e com `Placement="Bottom"` a borda direita anda — era o "às vezes muda o local". Com
`Placement="Custom"` e um retorno que alinha a borda **direita** à do botão, o cartão cresce para
dentro da tela e o lado que fica junto da seta não se mexe.

## Ler a prova antes de concluir

**Dois eventos de clique com 20 ms de intervalo não eram um clique duplicado.** Eram dois cliques
**de verdade** que ficaram na fila enquanto a interface esperava, entregues em sequência quando ela
voltou. O filtro de 250 ms que eu pus para "ignorar o evento repetido" comia justamente o segundo
clique — ou seja, *ele* é que fazia a seta não recolher. O `StaysOpen="True"` nos popups já tinha
resolvido a causa real (o WPF dispensava o cartão antes do handler rodar).

**Um teste que clica de 450 em 450 ms mede a fila, não o app.** Enquanto a leitura está em curso os
cliques se acumulam e chegam todos juntos no fim, e o resultado sai "NÃO RECOLHEU" para um app que
está certo. Com 1,2 s entre cliques — acima do custo da leitura e abaixo dos 3 s que fecham o
cartão sozinho — as quatro verificações passam de forma estável.

**A tela de bloqueio esconde a dock, e isso é o comportamento correto.** Meia hora de teste
terminou com todas as janelas do WinDock ocultas e nenhum clique chegando: o `FullScreenWatcher`
tinha visto `Windows.UI.Core.CoreWindow` ("Tela de Bloqueio padrão do Windows") ocupando o monitor
inteiro. Antes de caçar um bug em que "o app sumiu", confira qual é a janela em primeiro plano.

## Subir rápido

**Uma tarefa de 12 ms agendada em `DispatcherPriority.Background` custou 735 ms.** A primeira
atualização da dock — a que descobre quais apps já estão abertos — ia para o fim da fila "para a
barra aparecer primeiro com os fixados". Só que atrás dela na fila estavam a barra de cima
inteira, o AppBar e o esconder da barra do Windows. Chamada direto no construtor, o tempo até os
apps abertos aparecerem caiu de 1 120 ms para **630 ms**. Prioridade baixa só faz sentido quando o
trabalho é caro; para 12 ms, a fila é o custo.

**Um `lock` numa thread de aquecimento anula o aquecimento.** O `ShortcutService.Warm()` lê os
atalhos numa thread própria justamente para sair da frente, mas o `All()` que a primeira
atualização chamava esperava no mesmo `lock`. Quem só quer enfeitar o botão precisa de uma leitura
que aceite "ainda não" e de um aviso de quando ficar pronta.

**`PublishReadyToRun` não mudou nada aqui** (medido: 648 ms com, 628 ms sem). O arranque do
processo até o `OnStartup` é ~250 ms e a montagem do XAML da dock ~230 ms; o resto era fila, não
JIT. Não vale a complicação de fixar um `RuntimeIdentifier`.

**Estado de interface anotado no `MouseDown` deixa o botão cego para quem não usa mouse.** A seta
da bandeja lia "o cartão estava aberto?" de um campo preenchido no `OnPreviewMouseDown`. Pelo
teclado ou pela automação esse campo nunca era atualizado, e a seta só sabia fechar. O campo só
existia porque o WPF fechava o popup antes do handler; com `StaysOpen="True"` dá para perguntar ao
próprio popup, que é a fonte de verdade.

## Mosaico (tiling window manager)

**`RegisterHotKey` com Shift+seta quebra a seleção de texto no sistema inteiro.** Um hotkey
global é entregue à janela que o registrou *antes* de chegar ao app em foco — registrar
Shift+Esquerda/Direita/Cima/Baixo o tempo todo significa que nenhum programa, em lugar nenhum,
consegue mais selecionar texto com Shift+seta enquanto o WinDock estiver de pé. A correção: só
registrar esses hotkeys enquanto `TilingEnabled` está ligado (espelhando o padrão já usado pelo
Alt+Espaço do launcher, que liga/desliga com `SetLauncherHotkey`), e desregistrar na hora que a
pessoa desliga o mosaico no painel.

**Testar automação sem sessão interativa de verdade não alcança o foco real.** `SetForegroundWindow`
chamado de um processo sem input de usuário de verdade (PowerShell disparado por uma ferramenta)
é recusado pelo Windows silenciosamente — a chamada retorna sem erro, mas o foco não muda; quem
acaba em primeiro plano é o processo que já estava lá. O truque que funciona de dentro do próprio
app é `AttachThreadInput` (o `WindowService.ForceForeground` já faz isso), porque uma resposta a
um hotkey ou clique real carrega a permissão de trocar o foco — automação externa não carrega. Na
prática: dá para testar visualmente o layout (split, gap, posição) via `PrintWindow`/captura de
tela, mas não dá para simular de fora um Shift+seta ou Alt+C de verdade sem um teclado físico
apertando a tecla — isso fica para o teste manual da pessoa.

**Arrastar uma janela do mosaico não voltava sozinha para o lugar.** O `SetWinEventHook` só
cobria criação/destruição/minimizar/foreground; soltar o mouse depois de arrastar não disparava
nada, e a janela ficava fora do retângulo que o `Split()` calculou. Faltava o par
`EVENT_SYSTEM_MOVESIZESTART`/`MOVESIZEEND` — só o fim importa (reagir ao início prende a janela
antes mesmo de soltar o mouse).

**Flutuar (Alt+C) tirava a janela do mosaico mas não trazia para a frente.** `SetWindowPos` com
`HWND_TOP` resolve; sem isso a janela flutuante podia ficar atrás de alguma das tiles.

**`GetWindowRect` no contorno da janela flutuante trazia a margem invisível junto.** O mesmo
problema que o `Apply()` já resolvia pras tiles (a moldura de redimensionar do Windows 11 soma
uns 7px de cada lado que não aparecem na tela) também vale pro contorno: tem que medir pelo
`DWMWA_EXTENDED_FRAME_BOUNDS`, não pelo `GetWindowRect` cru.

**Nem todo diálogo de tamanho fixo é filtrado só por `GetWindowTextLength > 0`.** A barra de
progresso de copiar arquivo do Windows é uma janela raiz, sem dono, com título — passa por
todos os critérios que a dock já usa. O sinal que faltava: só entra no mosaico quem tem
`WS_THICKFRAME` ou `WS_MAXIMIZEBOX` no estilo (`GWL_STYLE`) — diálogo de verdade não tem
nenhum dos dois.

**E ainda assim, ter o estilo não é garantia de cooperar.** Um formulário Delphi com
`Constraints` preenchido pode ter `WS_MAXIMIZEBOX` no estilo (o VCL bota por padrão) e mesmo
assim ignorar o `SetWindowPos` que o mosaico manda — o resultado é a janela do tamanho de
sempre, sozinha, com o resto do retângulo que devia ser dela sobrando em preto atrás. Só dá
pra saber depois de tentar: aplicar a posição, medir de novo pelo `DWMWA_EXTENDED_FRAME_BOUNDS`,
e se o tamanho não bateu (com uma folga de ~20px pra arredondamento de DPI), tirar essa janela
do mosaico e recalcular sem ela — em vez de brigar com ela a cada `Refresh()`.

**Nem toda janela responde ao `DWMWA_EXTENDED_FRAME_BOUNDS`.** Alguma de estilo mais clássico
devolve erro nessa chamada, e sem uma saída de reserva pro `GetWindowRect` cru, essas ficavam
sem contorno nenhum — pior que o contorno maior por causa da margem invisível.

**O contorno só pulava pro lugar certo depois de soltar o mouse, com atraso visível.** O
`EVENT_SYSTEM_MOVESIZEEND` só dispara *uma vez*, no fim do arraste — pra acompanhar ao vivo
(quadro a quadro) precisa de outra coisa: um hook de `EVENT_OBJECT_LOCATIONCHANGE` **temporário**,
ligado só entre `MOVESIZESTART` e `MOVESIZEEND` da janela em foco, e desligado logo depois. Ligar
esse hook o tempo todo seria ruído demais (dispara pra qualquer mudança de posição de qualquer
janela do sistema); ligado só durante um arraste de cada vez, o custo é baixo e o contorno
acompanha em tempo real. Junto disso, trocar a prioridade da atualização do contorno de
`DispatcherPriority.Background` pra `.Send` (a mais alta) — o `Background` esperava atrás de
qualquer outra coisa na fila do WPF, e isso sozinho já era perceptível como atraso.

**Uma falha isolada no `FitsTarget()` bania janela cooperativa por falso positivo.** O
`SetWindowPos` pode não ter sido totalmente processado pelo DWM (composição por GPU, comum em
navegador) no exato instante em que o `Apply()` seguinte media de novo — a janela parecia
"teimosa" sem ser. A regra virou duas tentativas: falhar uma vez só marca como suspeita e agenda
nova conferência em 250ms (`DispatcherTimer` avulso, se autodestrói depois de disparar); só
falhando de novo é que vira `_stubborn` de verdade.

**Apps internos da BMSoft (Delphi, ex.: MDBSYS) podem ser teimosos de verdade, não por acidente.**
Depois do fix acima, ainda existe caso onde a janela recusa o redimensionamento nas duas
tentativas — não é bug de medição, é o formulário mesmo que não coopera. Pra esses, o contorno de
foco não deveria depender de pertencer a `_tiles`/`_floating`/`_stubborn`: virou "qualquer janela
de verdade (`Concerns()`) no monitor principal que estiver em foco ganha contorno", independente
de ela ter entrado ou não no mosaico — sem isso, uma janela nunca-processada (nem chegou a tentar
redimensionar, falhou antes ainda) ficava sem contorno e sem responder a nenhum atalho. O Alt+C
também passou a aceitar qualquer janela do monitor principal, não só quem já estava rastreada —
dá pra "resgatar" manualmente uma teimosa pro fluxo de flutuar.

**`EVENT_SYSTEM_MINIMIZESTART` dispara antes da animação acabar — `IsIconic()` podia ler falso
nesse instante.** Um `Refresh()` reagindo direto a esse evento (em vez de só ao `MINIMIZEEND`)
ainda considerava a janela "não minimizada" candidata ao mosaico, e o `Apply()` reaplicava o
retângulo de tile bem no meio da animação de minimizar — cancelando ela. Resultado: Alt+Z (ou o
botão nativo de minimizar) piscava a tela e a janela nunca minimizava de verdade. Mesma lição do
`MOVESIZESTART`/`MOVESIZEEND` (só o fim importa), só que dessa vez o custo de reagir cedo demais
não era só redundância — era cancelar a própria ação que o evento anunciava.

**O `Concerns()` do mosaico era mais fraco que o filtro Alt+Tab que a dock já usa.** Não checava
visibilidade nem `WS_EX_TOOLWINDOW`/janela cloaked — só título, dono e raiz. O overlay do Alt+Tab
do Windows (que aparece já só de *segurar* Alt, sem soltar em Tab) passava por esse filtro fraco:
ganhava contorno (depois que o contorno virou "qualquer `Concerns()` no monitor principal", uma
mudança de sessão anterior) e disparava recálculo à toa. A correção foi parar de reimplementar o
critério e chamar direto o `WindowService.IsAltTabWindow` — o mesmo que a dock usa pra montar a
lista de botões, já testado, em vez de manter duas versões que iam divergir de novo mais cedo ou
mais tarde.

**O mosaico virou multi-monitor depois do Ctrl+Shift+seta pra trocar de tela.** Fazia sentido
assim que existiu jeito de mandar uma janela pro monitor vizinho: sem contorno lá e sem dividir
espaço com quem já estava naquele monitor, a janela mandada pra lá ficava "solta" — nem
flutuando de verdade (isso é escolha da pessoa via Alt+C), nem no mosaico. A generalização:
`Refresh()` agrupa `_tiles` por monitor (`GroupBy(MonitorFromWindow)`) e roda um `Split()`
independente pra cada grupo — cada monitor tem o próprio grid, sem sobreposição entre eles. O
contorno e o Alt+C pararam de checar "é o monitor principal?" também. Pegadinha: o
`FindNeighbor()` do Shift+seta precisou passar a filtrar só o monitor da janela atual — sem
isso, na ponta do grid ele podia "roubar" uma janela do monitor vizinho pra trocar de lugar em
vez de deixar o Ctrl+Shift+seta mandar pra lá de verdade.

**Uma janela nova, aberta com uma flutuante já em foco, podia ficar órfã pro resto da sessão —
sem contorno e sem responder ao Alt+C.** O `EVENT_OBJECT_CREATE` dispara cedo demais na criação
da janela, antes do título e do estilo dela estarem prontos; o `Concerns()`/`IsResizable()`
chamado naquele instante recusava, e nenhum `Refresh()` era agendado. Depois disso só o
`EVENT_SYSTEM_FOREGROUND` disparava (quando ela realmente ganhava foco), mas esse evento sempre
só reposicionou o contorno — nunca tentava incluir a janela de novo no mosaico. Sem outro evento
avulso (abrir ou fechar outra coisa) pra forçar um novo `Refresh()`, a janela ficava conhecida só
do Windows, nunca do mosaico. A correção: o `EVENT_SYSTEM_FOREGROUND` agora também agenda um
`Refresh()` (debounced, igual aos outros eventos) quando a janela em foco ainda é desconhecida
(não está em `_tiles`, `_floating` nem `_stubborn`) e passa nos critérios — ela já teve tempo de
se estabelecer só de ter chegado a ganhar foco de verdade, então o mosaico se autocorrige em vez
de depender de outro evento acontecer por coincidência.

**Shift+seta também passou a atravessar monitor, igual o Ctrl+Shift+seta já fazia pra mover
janela.** Antes, na ponta do grid de um monitor, o `MoveFocus` simplesmente não fazia nada — só
o `SwapFocused` sabia continuar pro monitor vizinho. A correção extraiu a busca de monitor
vizinho (que já existia dentro do `MoveToAdjacentMonitor`) pro método compartilhado
`AdjacentMonitor`, e o `MoveFocus` ganhou o mesmo fallback: sem vizinho no grid atual, muda o
foco pra uma janela do mosaico no monitor vizinho — a mais próxima da borda por onde o foco
"entraria" vindo daquele lado, pra parecer continuação do grid em vez de pular pra qualquer
canto.

**O contorno do mosaico tinha atraso acompanhando uma janela flutuante em movimento — a causa
era o próprio jeito de posicionar o contorno, não os eventos que avisam do movimento.** O
`ShowAround` usava `Left`/`Top`/`Width`/`Height` do WPF, que trabalham em unidades independentes
de DPI (exigindo dividir o retângulo físico pela escala a cada chamada) e cada propriedade
dispara sua própria invalidação de layout — quatro idas ao sistema de layout do WPF por quadro,
em vez de uma. Trocado por um `SetWindowPos` direto no hwnd do contorno (a mesma chamada que o
resto do mosaico já usa pra mover janela de verdade), com o retângulo físico repassado sem
conversão nenhuma. `Show()`/`Hide()` do WPF só entram na primeira vez (pra criar o hwnd) — depois
disso é `ShowWindow(SW_SHOWNA)` puro, sem ativar.

**Minimizar uma de duas janelas splitadas (Alt+Z) não devolvia o espaço pra vizinha.** O
`EVENT_SYSTEM_MINIMIZEEND` passava pelo mesmo filtro `Concerns()` de todos os outros eventos —
mas é a própria janela que acabou de minimizar, e o estado dela (`IsWindowVisible`, cloaked)
pode ainda estar instável bem no instante em que a animação termina. Se `Concerns()` recusasse
nesse instante, nenhum `Refresh()` era agendado, e a vizinha ficava com o retângulo antigo (metade
da tela) em vez de ocupar o espaço todo. Corrigido tirando o `MINIMIZEEND` de trás do filtro —
igual já tinha sido feito pro `MOVESIZESTART`/`END` — já que o próprio `Refresh()` recalcula do
zero a partir de quem está de verdade elegível, então rodar ele mesmo quando não seria
estritamente necessário não tem custo nenhum.

**Shift+seta cruzando monitor "só ia na ida" — e o log de rastro (o arquivo `rastrear` ao lado
do config, ligando o `Log.Trace`) mostrou o porquê na hora, sem precisar adivinhar.** O
`FocusAdjacentMonitor` só procurava candidatos em `_rects` — que só guarda janela do grid.
Saindo de uma flutuante em direção a uma do grid funcionava (o destino está em `_rects`); saindo
do grid em direção a uma flutuante falhava (o destino nunca aparece nessa busca, já que
flutuante não tem entrada em `_rects`). Corrigido somando ao candidatos as janelas de
`_floating`/`_stubborn` que estão no monitor alvo, com o retângulo delas vindo direto de
`DwmGetWindowRect`/`GetWindowRect` (mesma dupla de sempre) em vez de `_rects`.

**Shift+seta saindo de uma janela flutuante ia direto pro monitor vizinho, ignorando quem estava
embaixo dela no mesmo monitor.** Uma flutuante cobre o mosaico por cima, mas ela não faz parte de
`_rects` — e o `MoveFocus` só sabia fazer duas coisas: achar vizinho no grid (`_rects`) ou pular
de monitor. Faltava o meio-termo: com o Chrome flutuando por cima do Terminal splitado no mesmo
monitor, e o Discord no outro monitor, Shift+seta pulava direto pro Discord em vez de revelar o
Terminal por baixo. A correção acrescenta um passo no meio — `FocusSameMonitor` — que primeiro
tenta achar outra janela (do grid ou flutuando/teimosa também) no mesmo monitor, batendo a
direção pedida quando dá, e caindo pra "qualquer uma mais próxima" quando a flutuante cobre o
mosaico inteiro e nenhuma bate a direção exata. Só quando não sobra ninguém no monitor atual é
que cai no `FocusAdjacentMonitor` de antes.

**Alt+W: fechar a janela em foco.** Mesmo WM_CLOSE educado que o botão de fechar da dock já
manda (`WindowService.Close`) — pede pro app fechar, não mata o processo. Registrado só enquanto
`TilingEnabled` está ligado, igual aos outros atalhos do mosaico (mesma razão: um hotkey global
capturado o tempo todo interferiria com o app em foco mesmo com o mosaico desligado).

**O contorno do mosaico cobria o menu de contexto do botão direito (EV08).** Era `Topmost="True"`
no WPF — ficava acima de tudo, sistema inteiro, o tempo todo, inclusive de um popup criado
depois dele (o menu de contexto do Explorer, por exemplo). Pra uma decoração puramente cosmética
isso é forte demais: o correto é ficar só logo acima da própria janela que ela contorna, nunca
acima de qualquer coisa que o Windows decida desenhar por cima depois. Corrigido tirando o
`Topmost` e passando a reordenar o contorno no `SetWindowPos` (`ShowAround`) direto acima do hwnd
rastreado a cada atualização, em vez de deixá-lo fixo no topo do z-order inteiro — um popup do
sistema criado depois (menu de contexto, tooltip, o que for) volta a poder cobrir o contorno
normalmente.

**Lista de exceções do mosaico — apps que não devem entrar no grid sozinhos ao abrir.** A
Calculadora do Windows não é uma janela "teimosa" (ela responde ao `SetWindowPos` normalmente,
`FitsTarget()` não pegaria nada errado) — só é um app pensado pra um tamanho fixo, que fica
esquisito espremido num pedaço do grid. Em vez de tentar adivinhar isso automaticamente (não tem
sinal de estilo confiável pra "prefiro não ser redimensionado, mas tecnicamente aceito"), a
pessoa escolhe pelo nome do executável nas configurações (`DockConfig.TilingExcludedApps`, uma
lista comum — "CalculatorApp.exe" já vem por padrão). O `Refresh()` exclui esses apps dos
candidatos do grid (mesmo filtro de `_floating`/`_stubborn`), mas eles continuam alcançáveis
manualmente pelo Alt+C, que já resgatava janelas "teimosas" — o mesmo caminho serve aqui sem
mudança nenhuma. Detalhe de UI: `List<string>` comum não avisa a tela quando ganha ou perde item
(só quando é trocada inteira), então a tela espelha num `ObservableCollection` local pra exibir,
e chama `DockConfig.NotifyTilingExcludedAppsChanged()` (dispara o `PropertyChanged` manualmente)
pra avisar o `TilingService` na hora, em vez de esperar o próximo evento de janela por acaso.

**A exclusão da Calculadora nunca funcionava — o nome padrão que coloquei estava errado.**
Confirmado via `Get-Process`/`Get-StartApps`: a janela de verdade da Calculadora pertence ao
processo `ApplicationFrameHost.exe`, não ao `CalculatorApp.exe` (esse existe, mas não é dono de
janela nenhuma) — o mesmo padrão de app "moderno" hospedado que já tinha aparecido com o
Terminal/Configurações antes. Excluir por nome de executável sozinho não dava: `ApplicationFrameHost.exe`
hospeda vários apps diferentes (Calculadora, Configurações, e por aí vai), então excluir esse
nome excluiria todos eles juntos. Corrigido comparando também pelo AppUserModelID — o mesmo
identificador que `TaskWindow.AppKey` já usa pra separar botão de app na dock — que aí sim
distingue "a Calculadora" (`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`) das Configurações.
Esse era também o motivo do "arrastar aplica o tiling": a janela nunca tinha sido excluída de
verdade, então qualquer `Refresh()` (inclusive o do fim do arraste) recolocava ela candidata.

**Shift+seta trocado por Ctrl+Alt+seta pra mover o foco entre janelas do mosaico.** Shift+seta
sozinho é a combinação universal de estender seleção de texto — um hotkey global nela quebra
copiar/selecionar texto em qualquer programa, sempre que o mosaico estiver ligado. O usuário
sentiu esse conflito na prática; escolheu Ctrl+Alt+seta como substituto (não usa Shift em nada,
zero chance de colidir com seleção de texto). Ctrl+Shift+seta (trocar a janela de lugar) e as
demais combinações não mudaram.

**Arrastar uma janela flutuante (ou "teimosa"/excluída) reaplicava o mosaico nas outras janelas
à toa.** O `EVENT_SYSTEM_MOVESIZEEND` sempre caía no `Refresh()` genérico do fim da função,
mesmo pra quem nunca esteve no grid — recalculava tudo e reaplicava (`SetWindowPos`) o retângulo
de quem já estava tiled, com um solavanco visível a cada arraste de uma flutuante, mesmo sem
nada ter mudado de verdade pro grid. Corrigido: o próprio handler do `MOVESIZEEND` agora sai
cedo (sem agendar `Refresh()`) quando quem foi arrastada está em `_floating`, `_stubborn` ou na
lista de exceções — só quem ainda está no grid precisa desse `Refresh()` pra voltar pro lugar
(o EV04 original). Rearranjar o grid continua só acontecendo por decisão explícita da pessoa
(Alt+C tirando a janela do float) ou por mudança real de quem está no grid (abrir, fechar,
minimizar).

**A correção do EV08 (menu de contexto) tinha invertido sem querer a visibilidade do próprio
contorno.** Passar o hwnd da janela rastreada como `hWndInsertAfter` no `SetWindowPos` não
garante o contorno ficar acima dela — a ordem exata que o Windows aplica com um hwnd específico
(em vez das constantes especiais) é mais sutil do que parecia, e na prática o contorno ia parar
atrás da própria janela: coberto, invisível, e a espessura configurada nas configurações não
tinha efeito nenhum de visível porque não havia nada visível pra mudar. Corrigido voltando pro
`HWND_TOP` a cada atualização — sem `Topmost` no XAML, essa janela não é mais topmost, então
`HWND_TOP` só traz ela pro topo da faixa comum (acima de qualquer janela normal, sempre) sem
brigar com quem for genuinamente topmost (o menu de contexto). Lição: `hWndInsertAfter` com um
hwnd específico não é tão previsível quanto parece — as constantes especiais (`HWND_TOP` etc.)
são o caminho testado.

**`HWND_TOP` sozinho no contorno ainda perdia a corrida contra a própria janela rastreada —
borda sumindo em flutuantes (EV09: borda fininha, quase só a borda interna visível no Chrome).**
`HWND_TOP` só garante ficar acima de quem já estava parado no instante da chamada; focar ou
arrastar a própria janela rastreada faz o Windows trazer ela pra frente por conta própria (parte
normal do comportamento de foco), e isso podia acontecer *depois* da nossa última reordenação do
contorno — sorte de corrida, não garantia. Corrigido tornando determinístico: a cada
`UpdateBorder()`, primeiro traz a janela em foco pro `HWND_TOP` (sem ativar, `SWP_NOACTIVATE` —
só reordena, focar de novo não muda nada de verdade já que ela já é a janela em foco), e só
depois o contorno pro `HWND_TOP`. Quem chama por último vence a frente da fila — sempre o
contorno, de propósito, nunca torcendo pra que a ordem anterior ainda valha.

**O contorno rente à janela podia ser "comido" pela sombra/canto arredondado que o DWM desenha
em volta de qualquer janela do Windows 11 — o mesmo sintoma por trás de "espessura 1" ao
rearranjar e "some" numa flutuante.** A pista veio do forge (o tiling manager de GNOME/Linux que
o usuário já usa e trouxe pra eu ler): lá, o contorno nunca é desenhado rente à janela —
`border.set_size(rect.width + inset*2, ...); border.set_position(rect.x - inset, ...)` — ele
cresce pra fora, ocupando o espaço do próprio gap, exatamente o que um comentário nosso (antigo,
nunca implementado de verdade) já dizia querer fazer: "o retângulo é um pouco maior que a área
da janela". Implementado via `Outset` (o inverso do `Inset` que já existia pro gap) — no grid,
limitado ao tamanho do próprio `TilingGap` (senão invadiria a vizinha; sem gap, fica rente como
antes); numa flutuante, sem esse limite (não tem vizinha de grid pra invadir).

**O mistério final do contorno sumindo (com todos os números batendo no log — tamanho, posição,
DPI, z-order, tudo certo) era a técnica de transparência em si.** A janela do contorno usava
`AllowsTransparency="True"` do WPF (janela "layered", composta em camada separada pelo
`UpdateLayeredWindow`) — e esse tipo de janela é conhecido por não reagir bem a ser
redimensionada via `SetWindowPos` puro, por fora do próprio sistema de `Width`/`Height` do WPF:
o hwnd de verdade tinha o tamanho novo (confirmado por `GetWindowRect`), mas a composição em
camada podia continuar presa numa versão antiga — daí números perfeitos no log e nada na tela.
Trocado por uma técnica mais antiga e mais robusta: `SetWindowRgn` (recorte de região do Win32)
numa janela comum, opaca, sem `AllowsTransparency` nenhuma. O recorte é a moldura em si — o
retângulo todo menos um retângulo interno do tamanho da espessura configurada
(`CreateRoundRectRgn` duas vezes + `CombineRgn` com `RGN_DIFF`) — e o Windows nunca pinta nem
entrega clique fora da região, então o miolo mostra o que está por baixo sem precisar de canal
alfa nenhum. Essa técnica não depende do WPF pra nada além de desenhar a própria moldura
(BorderBrush), e por isso não sofre do mesmo descompasso entre "hwnd redimensionado por fora" e
"composição em camada desatualizada".

**Alt+Tab do Windows não pintava a borda direito — a animação do próprio seletor fechando
corria contra o nosso `HWND_TOP`.** O foco já passa pra janela escolhida antes da animação de
fechar o seletor do Alt+Tab terminar, e essa animação pode reordenar o z-order de novo depois da
nossa primeira tentativa de trazer o contorno pro topo. Corrigido com uma segunda conferência
150ms depois de todo `EVENT_SYSTEM_FOREGROUND` — mesma ideia do recheck de 250ms que já existia
pra janela "suspeita" no `Refresh()`, só que pro contorno e num prazo mais curto (é uma animação
de UI, não um redimensionamento de app). Sobre o forge (GNOME/Linux): não tem nada que ajude
diretamente aqui — o Alt+Tab dele é gerenciado pelo próprio compositor Mutter, sem o conceito de
"seletor topmost fechando e disputando z-order" que existe no Win32; a lição de aplicar um
recheck curto depois de um evento de foco veio da nossa própria solução anterior pro
`_suspect`/`_stubborn`, não do projeto de referência.

**Depois de cinco rodadas caçando evento por evento (z-order, DPI, layered window, Alt+Tab), o
contorno ainda sumia às vezes sem nenhum evento correspondente no log — parou de mudar de foco,
mas o contorno sumiu de qualquer forma.** Trocada a estratégia: em vez de tentar prever todo
jeito possível do z-order (ou da região recortada) desandar, um `_borderHeartbeat`
(`DispatcherTimer` de 300ms, rodando o tempo todo enquanto `TilingEnabled`) chama `UpdateBorder()`
sozinho, sem depender de evento nenhum do Windows. Um `SetWindowPos`/`SetWindowRgn` redundante
sem mudança nenhuma é baratíssimo; a alternativa era continuar caçando causa por causa pra sempre,
cada uma corrigindo só aquele caso específico. Isso tornou o recheck de 150ms específico do
Alt+Tab redundante — removido.

**O "batimento" deixou tudo pior — porque ele reafirmava a própria janela em foco no topo do
z-order a cada 300ms, não só o contorno.** `BringToTopThenBorder` trazia o alvo pro `HWND_TOP`
antes do contorno, pensado pra corrigir a corrida contra o Alt+Tab — mas rodando isso o tempo
todo, mesmo sem nada ter mudado, brigava com os próprios popups internos do app em foco (menu
suspenso, autocomplete, o que for): forçar a janela-mãe pro topo por cima do próprio filho dela é
o tipo de coisa que devia acontecer raramente, não a cada 300ms pra sempre. Corrigido voltando a
mexer só no contorno — `_border.ShowAround` já traz ele pro `HWND_TOP` sozinho a cada chamada,
sem tocar na janela alvo nenhuma vez. Sobra um risco pequeno (até 300ms de atraso pro contorno se
recuperar se o alvo se reafirmar acima dele nesse intervalo, em vez de correção imediata), mas é
bem menos invasivo que reordenar a janela da pessoa sem ela ter pedido.

**O contorno virou uma janela Win32 crua — sem WPF nenhum por trás.** Depois de corrigir z-order,
DPI, janela "layered" e ainda assim o sumiço intermitente continuar acontecendo mesmo com um
`WM_PAINT` em GDI pintando "certo", sobrava uma suspeita: mesmo um `Window` do WPF sem conteúdo
XAML nenhum ainda carrega um `HwndSource` com compositor por trás (DirectComposition), e esse
compositor pode redesenhar a própria superfície (vazia) por cima do que o `WM_PAINT` acabou de
pintar — as duas coisas competindo pelo mesmo hwnd, uma ganhando às vezes, a outra às vezes.
Trocado por uma janela criada direto por `RegisterClassEx`/`CreateWindowEx`, sem `HwndSource`,
sem `Window`, sem XAML: só existe o que o `WM_PAINT` pintar, sem compositor nenhum do WPF por
trás pra competir. O arquivo `TilingBorderWindow.xaml` foi apagado (a classe não é mais um
`Window` do WPF) e o `.xaml.cs` virou `TilingBorderWindow.cs`, uma classe comum. A pintura (cor)
e o recorte de forma (`SetWindowRgn`, já em uso desde a correção anterior) continuam separados:
a região decide a forma, o `WM_PAINT` decide a cor, os dois sempre em dia com o tamanho que
acabou de ser aplicado.

**A causa raiz de TODA a saga do contorno sumindo era UIPI (User Interface Privilege
Isolation), não a técnica de desenho.** Depois de cinco reescritas completas (WPF Border,
SetWindowRgn, GDI puro numa janela crua sem WPF, color-key) todas "funcionando" perfeitamente
pelos números — tamanho, posição, z-order, `WM_PAINT` disparando, `FillRect` retornando sucesso
— e ainda assim nada aparecendo na tela, a pista final veio de reparar que TODOS os casos que
falhavam envolviam o Windows Terminal ou o Notepad++ **rodando como Administrador**. O Windows
aplica UIPI entre processos de integridade diferente: uma janela nossa sem elevação não consegue
ficar de verdade por cima de uma janela elevada, não importa quantas vezes o `SetWindowPos` diga
"funcionou" — o SO simplesmente recusa deixar renderizar por cima na prática. Confirmado rodando
o WinDock.exe manualmente como administrador: o contorno passou a aparecer imediatamente, sem
mudar uma linha de código.

Corrigido pedindo elevação sempre, via `app.manifest`
(`requestedExecutionLevel level="requireAdministrator"`). O preço conhecido, e aceito
deliberadamente: o Explorer (que roda sem elevação) não consegue mais soltar arquivo nenhum por
arrastar numa janela nossa — o mesmo UIPI, na direção contrária, e o motivo original (documentado
em `StartupService.cs`) de o projeto ter escolhido rodar sem elevação antes. A tarefa agendada do
"iniciar com o Windows" também precisou de ajuste: `TASK_RUNLEVEL_HIGHEST` (antes era
`TASK_RUNLEVEL_LUA`) — sem isso a tarefa não conseguiria nem iniciar um executável cujo manifesto
exige administrador. Rodar elevado via tarefa agendada não pede consentimento do UAC a cada
logon (é o único jeito de autoelevar sem diálogo); já a chave Run (plano B) vai passar a pedir
UAC a cada logon, sem alternativa — um efeito colateral aceito, não corrigido.

Lição maior: quando várias técnicas completamente diferentes falham da mesma forma exata, o
problema provavelmente não está em nenhuma delas — está numa camada mais abaixo, comum a todas.
Cinco reescritas de renderização foram gastas perseguindo sintomas antes de parar pra perguntar
"o que é igual em TODOS os casos que falham?"

**Depois da elevação corrigir o problema grande, sobrou um resíduo bem mais específico (EV14):
o contorno de uma flutuante ficava por baixo quando ela estava encostada rente a outra janela.**
O crescimento pra fora do contorno (outset) numa flutuante não tinha limite nenhum — diferente do
grid, onde é limitado ao próprio `TilingGap` — porque flutuante não tem "vizinha de grid" pra
invadir. Mas se a pessoa arrasta a flutuante pra ficar encostada bem rente a outra janela
qualquer (fora do mosaico), esse crescimento sem limite passava a invadir o território dela — e
nesse pedaço invadido não tem garantia de ficar por cima. Reduzido pra 1px nas flutuantes: o
mínimo que ainda tira o traço de cima da sombra/canto arredondado do DWM (o motivo de existir
esse crescimento) sem se meter muito no território de quem estiver do lado.

**O contorno virou a borda nativa do Windows 11 (`DWMWA_BORDER_COLOR`), não mais uma janela
nossa por cima.** A pergunta certa ("não tens como isolar por aplicação o contorno?") veio depois
da EV14/15/16 mostrarem, sem dúvida nenhuma, que o contorno perdia especificamente contra
QUALQUER outra janela real por baixo (nunca contra área vazia da tela) — mesmo já elevado, mesmo
com seis técnicas de overlay diferentes já tentadas. Isso só fazia sentido como um problema de
categoria: "uma janela separada tentando ficar sempre por cima de tudo" não tem como vencer essa
disputa de forma garantida, nenhuma técnica de desenho resolve isso porque o problema nunca foi
a técnica — foi o próprio conceito de overlay.

A saída: `DwmSetWindowAttribute` com `DWMWA_BORDER_COLOR` (Windows 11 22H2+) pede pro DWM
recolorir a borda que ELE MESMO já desenha em volta da janela — não é overlay nenhum, é parte da
própria janela, no mesmo lugar dela no z-order, sempre. Sem disputa de z-order porque não tem
mais duas janelas competindo. O preço: sem controle de espessura nem raio de canto — só a cor
continua configurável (`TilingBorderThickness` virou um liga/desliga: zero esconde, qualquer
outro valor mostra). `TilingBorderWindow` (agora sem XAML, sem `SetWindowRgn`, sem GDI, sem
`WM_PAINT`) ficou um wrapper fino: `ShowAround(hwnd)` pinta a borda daquele hwnd e devolve a da
anterior pro padrão; `Hide()` devolve a atual pro padrão.

**Mesmo com a borda nativa, ainda sumia às vezes — porque `ShowAround` só pintava na primeira
vez que via aquele hwnd.** `if (hwnd == _current) return;` parecia uma otimização óbvia (não
repintar à toa), mas o Windows pode "esquecer" o `DWMWA_BORDER_COLOR` pedido sozinho — num
redimensionamento, por exemplo — e sem repintar de novo depois, o contorno ficava sumido sem
chance nenhuma de se corrigir. Removido o atalho: repinta sempre, mesmo pra quem já era o hwnd
atual — o "batimento" (que já rodava o tempo todo) agora serve pra isso também, e
`DwmSetWindowAttribute` é barato o bastante pra chamar várias vezes por segundo sem custo
perceptível.

**O mosaico ganhou uma árvore de verdade, e com ela proporções que sobrevivem.** Até aqui não
existia estrutura nenhuma: `Refresh()` recalculava o arranjo do zero a cada evento, sempre em
metades iguais, alternando o eixo do corte por profundidade (`depth % 2`). Isso travava três
coisas ao mesmo tempo — não havia onde guardar "esta janela ocupa 60% do pai", então redimensionar
uma tile era impossível; abrir ou fechar qualquer janela devolvia tudo para 50/50; e janela nova
entrava sempre no fim de uma lista, não ao lado de quem estava em foco. O modelo novo
(`src/Services/Tiling/LayoutTree.cs`) é uma raiz por monitor, containers que repartem o espaço, e
cada nó guardando quanto ocupa do pai. O `Refresh()` passou a **reconciliar** essa árvore com o
que está aberto (entra quem é novo, sai quem sumiu) em vez de reconstruí-la — é exatamente isso
que faz uma proporção escolhida à mão sobreviver a abrir e fechar janela.

**O percentual é do lugar, não da janela.** Ctrl+Shift+seta troca duas janelas de posição e os
percentuais ficam onde estão: quem vai para o lugar maior fica maior. O projeto de referência
(forge, o tiling do GNOME) marca isso no código com um comentário só — `// do not reset percent
when swapped` — e é a decisão certa: mover uma janela é mudar onde ela está, não que tamanho ela
tem.

**Toda mudança de estrutura tem que zerar os percentuais dos irmãos.** Com um irmão a mais ou a
menos, os percentuais antigos já não somam 1 — mantê-los deixa sobra ou falta de espaço no pai, e
o erro só aparece algumas inserções depois, quando já não dá para saber de onde veio. O forge
chama isso de `resetSiblingPercent`; aqui é chamado no fim de todo `Insert` e todo `Remove`.

**Remover uma janela tem que dissolver o container que ficou com um filho só.** Sem isso a árvore
acumula níveis que não correspondem a divisão nenhuma na tela, e o cálculo passa a repartir espaço
entre containers de um filho — o arranjo continua parecendo certo por um tempo e depois começa a
responder ao redimensionamento de um jeito que não bate com o que se vê.

**Converter pixels em fração contra o retângulo do container dá um erro silencioso de 1%.** O
retângulo do pai inclui os gaps que ficam entre os filhos, mas o espaço realmente repartido é
menor que ele — pedir "aumenta 100 px" rendia 99. Aparece só quando se confere o número; a olho
nu passa. Corrigido guardando no container, durante o cálculo, quanto sobrou para repartir
(`Node.SplitSize`) e convertendo contra isso. O forge tem um `TODO` aberto na mesma vizinhança
(`// make sure the totalSize = the sizes total`) por um motivo parecido: cada fatia arredondada
para baixo deixa pixels sobrando no fim. Aqui a última fatia leva o resto exato da divisão, senão
fica uma fresta de 1 a 3 px na borda direita ou inferior do monitor.

**Uma janela nova reparte o espaço da janela em foco, não o do monitor.** Inserir sempre como irmã
direta da raiz parece o caminho simples, mas com quatro janelas dá quatro colunas finas de altura
cheia — pior que o arranjo antigo. O certo é criar um container no lugar da janela de referência e
pôr as duas lá dentro, com o corte escolhido pela forma do espaço que está sendo repartido: mais
largo que alto corta na vertical, mais alto que largo na horizontal. É o "auto split" do forge, e
reproduz o mesmo arranjo que o `depth % 2` fazia antes — só que agora a decisão é do espaço real,
não da profundidade na recursão.

**Pegadinha: a janela de referência pode ainda não ter retângulo nenhum.** Duas janelas abrindo em
sequência rápida não têm um recálculo entre elas, e o retângulo da primeira ainda é zero quando a
segunda chega — a pergunta "mais larga ou mais alta?" respondia "larga" para um retângulo 0x0, e o
corte saía no sentido errado num monitor em pé. A saída de reserva é a forma do monitor, que é a
mesma pergunta um nível acima.

**Minimizada continua na árvore, só não ocupa espaço.** Tirar a janela da estrutura ao minimizar
faz ela reentrar no fim ao ser restaurada, em outro lugar do arranjo. Mantendo o nó e apenas
pulando-o no cálculo, ela volta exatamente para onde estava — e as vizinhas ocupam o espaço dela
enquanto isso. Mesma decisão do forge (`getTiledChildren`), e a mais barata das duas.

**`MonitorFromWindow` de janela minimizada não responde nada útil.** O Windows guarda a janela num
canto fora da tela enquanto ela está minimizada, e a conferência de "mudou de monitor?" passava a
achar que toda janela minimizada tinha trocado de tela — tirando e repondo ela na árvore a cada
recálculo, o que anulava justamente o ponto acima. Minimizadas ficam de fora dessa conferência.

**Ctrl+Alt+Shift+seta para redimensionar; Alt+Shift+seta seria mais curto e é território
arriscado.** `Alt+Shift` é o atalho de trocar o layout de teclado do Windows. Depois da lição do
`Shift+seta` quebrando a seleção de texto no sistema inteiro, não vale economizar um modificador
num hotkey global que fica registrado o tempo todo enquanto o mosaico está ligado.

**Arrastar a divisória de uma tile agora vira proporção, em vez de ser descartado.** Antes o
`Refresh()` seguinte devolvia a janela ao tamanho antigo — não havia onde registrar a intenção.
Só o *tamanho* conta, nunca a posição: arrastar pela barra de título move as duas bordas na mesma
medida e não muda largura nem altura, e nesse caso a janela volta para o lugar como sempre voltou.
Qual das duas bordas se mexeu mais é o que diz de que lado o espaço saiu.

**O grid inteiro passou a ser movido de uma vez (`BeginDeferWindowPos`).** Uma janela por vez, cada
uma se redesenhava no lugar novo enquanto as vizinhas ainda estavam no antigo — um tremor visível,
que passou a acontecer muito mais depois que redimensionar virou uma sequência de pressionadas de
tecla e não um evento isolado.

**A árvore não fala com o Windows, e isso é o que a torna conferível.** Toda a decisão de
retângulos ficou num arquivo sem um único P/Invoke. Como automação externa não consegue trocar o
foco (`SetForegroundWindow` de um processo sem input real de usuário é recusado em silêncio — o
que torna impossível simular os atalhos do mosaico de fora), essa separação é o único jeito de
conferir o cálculo sem depender de alguém apertando tecla. Daí o `tests/TreeCheck` — um projeto de
console `net8.0` puro que **compila o `LayoutTree.cs` de verdade** (`<Compile Include>` apontando
para a fonte, não uma cópia: cópia divergiria no primeiro ajuste, e uma conferência que não roda
sobre o código de produção não confere nada) junto de um `RECT` de mentira, e verifica arranjo,
gaps, proporções sobrevivendo a minimizar e restaurar, sobreposição e limites — 23 conferências.
Roda com `dotnet run --project tests/TreeCheck` e sai com código 1 se alguma falhar. Compilar a
fonte dentro do projeto de teste também é o que dá acesso ao que é `internal` lá, sem precisar
abrir nada no projeto principal; em troca, o `WinDock.csproj` precisou de um `<Compile
Remove="tests\**" />`, senão o glob recursivo do SDK puxaria esses arquivos para dentro da dock.

Duas das conferências pegaram defeitos que passariam despercebidos: a conversão de pixels para
fração contra o retângulo cheio do container (erro de 1%, invisível a olho nu) e a orientação do
corte saindo errada quando a janela de referência ainda não tinha retângulo. O que continua
exigindo teclado físico é só a parte que fala com o sistema: os hotkeys, o foco, e as janelas
obedecerem (ou não) ao tamanho pedido.

**Fechar a dock por `WM_CLOSE` de um script comum não funciona mais — a elevação vale nos dois
sentidos.** Depois que o `app.manifest` passou a exigir administrador, um `PostMessage(WM_CLOSE)`
disparado de um PowerShell sem elevação é descartado em silêncio pelo mesmo UIPI que causou a saga
do contorno: um processo de integridade menor não manda mensagem para um de integridade maior. E
matar o processo não é alternativa — o `OnClosed` é quem devolve a barra do Windows e solta a área
de trabalho fixada; sem ele, a barra fica escondida e a faixa reservada some junto. O caminho é
rodar o `PostMessage` de dentro de um processo elevado (`Start-Process -Verb RunAs`). Detalhe que
custa tempo: a dock não tem `MainWindowHandle` (é `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE`), então
`$_.MainWindowHandle` vem zero — é preciso enumerar as janelas do processo pelo PID.

**Recompilar com a dock de pé falha com o executável travado.** `MSB3027`/`MSB3021` — o
`WinDock.exe` fica bloqueado pelo processo em execução. Faz parte do ciclo: fechar (do jeito acima),
compilar, subir de novo.

**Uma janela "sumindo" do mosaico quase sempre tem explicação boa, e o rastro devia dizer qual.**
Nesta sessão, um terminal que aparecia na tela e não entrava no grid parecia defeito do código novo;
medido de fora, estava minimizado de verdade (`IsIconic`, `showCmd=2`, posição `-32000`), e sair do
grid era exatamente o comportamento certo. O que faltava era o `Refresh()` dizer quantas candidatas
achou, quantas estavam minimizadas, que retângulo cada uma recebeu, e — para quem ficou de fora —
qual dos filtros a barrou (flutuando, teimosa, sem `WS_THICKFRAME`, ou na lista de exceções). Com
esse rastro, a mesma investigação passa a levar uma leitura de log em vez de meia dúzia de
enumerações escritas à mão. Foi ele também que mostrou, sem ambiguidade, os formulários Delphi da
BMSoft (`Cadastros`, `BMsoft - Notas Fiscais`) ficando de fora por não terem borda de
redimensionar — o que já era o comportamento esperado, mas antes só dava para deduzir.

**Espessura do contorno: existe um atributo do DWM para isso, e ele é negado em janela alheia.**
A pergunta voltou depois da árvore de layout ("dá para voltar a escolher a espessura?"). A
documentação do `DWMWINDOWATTRIBUTE` ganhou o `DWMWA_BORDER_MARGINS` (valor 40, *"will be supported
in an upcoming update to Windows 11 build 26100"*) — settable, e define a posição da borda como
distância para dentro de cada aresta. Esta máquina é build 26200, então valia medir em vez de
supor. Medido:

| chamada | janela do próprio processo | janela de outro processo |
|---|---|---|
| `34` `DWMWA_BORDER_COLOR` | `S_OK` | `S_OK` |
| `99` (atributo inexistente) | `E_INVALIDARG` | `E_INVALIDARG` |
| `40` `DWMWA_BORDER_MARGINS` | `E_INVALIDARG` | **`E_ACCESSDENIED`** |

A comparação com o atributo inexistente é o que dá o veredito: um atributo desconhecido devolve
`E_INVALIDARG` mesmo em janela alheia, enquanto o `40` devolve `E_ACCESSDENIED` — o DWM **reconhece**
o atributo e recusa aplicá-lo a janela de outro processo. Elevação não muda (testado com a mesma
elevação que a dock usa). E um gerenciador de janelas só mexe em janela alheia. O `DWMWA_BORDER_COLOR`
é a exceção deliberada dessa família: é o único explicitamente permitido de fora, e é justamente por
isso que a solução atual funciona. Conclusão: **espessura via DWM não existe para este caso de uso**,
e a decisão de trocar o overlay pela borda nativa continua certa.

**O que a árvore mudou de verdade nessa história: agora dá para reservar o espaço da borda.** O
motivo pelo qual todo overlay falhava era perder o z-order contra qualquer janela real por baixo —
"nunca contra área vazia da tela". Com o layout guardado, o cálculo pode encolher cada tile em N px
e desenhar o contorno **no espaço reservado**, que é vazio por construção: o overlay deixaria de
disputar z-order com quem quer que seja. É o único caminho conhecido para espessura configurável, e
tem preço honesto: vale só para janelas do grid (flutuantes, "teimosas" e as Delphi continuariam com
a borda nativa de 1 px), cada janela fica N px menor de cada lado, e traz de volta uma janela de
overlay — a mesma que custou cinco reescritas. Não implementado; registrado para quando a espessura
valer esse preço.

**Realce por barra de título: funciona tecnicamente, e foi descartado ao ser visto.**
`DWMWA_CAPTION_COLOR` (35) e `DWMWA_TEXT_COLOR` (36) são aceitos em janela alheia iguais ao
`BORDER_COLOR` — `S_OK` nos dois, efeito imediato, a barra inteira muda de cor. Implementado como
`TilingHighlightCaption`, testado na tela, e **removido**: uma faixa colorida da largura da janela
é grande demais para dizer apenas "o foco está aqui" — a linha de 1 px comunica o mesmo sem pesar.
Fica registrado porque o caminho é real e pode servir a outro propósito; para *este*, não serve. A
lição maior é sobre método: era barato de implementar e só dava para julgar olhando, então
implementar e olhar foi mais rápido que discutir. E a decisão de reverter, uma vez vista a tela,
foi imediata — o custo de ter tentado é quase zero perto de conviver com o resultado errado.

Dois detalhes que a implementação exigiu, e que valem para qualquer coisa parecida:

- **A cor do texto tem de acompanhar a do fundo.** A barra aceita qualquer cor, e o título do
  Windows continuaria preto sobre um azul escuro. Preto ou branco pela luminância percebida
  (`0.299 R + 0.587 G + 0.114 B`), não pela média dos canais — o olho enxerga muito mais o verde
  que o azul, então um amarelo e um azul de mesmo valor numérico não pedem o mesmo texto por cima.
- **Parar de pintar não desfaz o que já foi pintado.** O estado vive na janela alheia, então
  desligar o realce tem de *repintar para o padrão*, não simplesmente pular a janela — senão a
  última pintada fica colorida para sempre. Mesma lição do `ShowAround` repintando sempre. É por
  isso que o `Reset()` continua devolvendo a barra de título ao padrão mesmo agora que nada mais a
  pinta: sem essa rede, uma janela colorida por aquela versão não teria quem a normalizasse.

**A janela nova entrava sempre no fim da raiz — e a referência de foco era a culpada.** Depois da
árvore, três janelas ainda viravam três colunas iguais em vez de a terceira repartir o espaço da
segunda. A causa: o `Refresh()` perguntava `GetForegroundWindow()` para saber ao lado de quem
inserir, mas no instante em que ele roda quem está em primeiro plano é **a própria janela que
acabou de abrir** — ainda desconhecida da árvore. `Find(beside)` devolvia nulo e toda inserção caía
no `root.Append`. A correção guarda o último foco *que está no mosaico* (`_lastTileFocus`),
atualizado no `EVENT_SYSTEM_FOREGROUND` e no fim do `Refresh()` — este segundo ponto é necessário
porque o evento de foco da janela nova acontece antes de ela existir na árvore, e sem ele a
referência nunca avançaria. E, ao inserir várias de uma vez, cada uma passa a ser a referência da
seguinte, em vez de todas entrarem ao lado da mesma.

Confirmado no rastro, que é o que tornou isso diagnosticável em uma leitura em vez de dedução:
`entrando 1F0950 ao lado de 4025C` → `4025C 637x1012 | 1F0950 638x1012 | 360CBE 1277x1012` (repartiu
só o espaço da referência), e a seguinte `entrando 300DC0 ao lado de 1F0950` → `638x505` para as
duas, cortando no outro eixo porque aquele espaço já era mais alto que largo.

**Encolher uma janela abaixo do mínimo dela a expulsava do mosaico.** O Windows Terminal não desce
de 466 px de largura. Bastava apertá-lo com Ctrl+Alt+Shift+seta até pedir 423: ele devolvia 466, os
43 px de diferença estouravam a tolerância de 20 px do `FitsTarget`, e na segunda medição o mosaico
concluía "esta janela não obedece" e a bania pelo resto da sessão — o pior desfecho possível para o
que era só um limite legítimo. A detecção de "teimosa" tinha sido pensada para formulários Delphi
que ignoram o `SetWindowPos` venha o que vier; o redimensionamento manual criou um segundo jeito,
muito mais fácil, de cair nela.

A correção separa os dois casos pelo **sinal da diferença**: janela que devolve um tamanho *maior*
que o pedido está dizendo qual é o mínimo dela, não desobedecendo. Esse mínimo é anotado no nó
(`SetMinimum`, e só cresce — quem recusou 423 não volta a aceitar 423, e baixar o valor por causa de
uma medição solta faria o mosaico tentar espremê-la de novo, sem fim), o cálculo passa a respeitá-lo,
e o espaço que falta sai de quem tem folga. Janela que devolve um tamanho *menor* continua sendo
tratada como antes.

Três detalhes que a implementação pediu:

- **O mínimo de um container depende do sentido do corte.** Cortando no mesmo eixo, os filhos ficam
  em fila e os mínimos somam; cortando no outro, eles se sobrepõem naquele eixo e vale o maior.
- **Repartir respeitando mínimos é iterativo.** Fixar quem não cabe muda a conta dos outros, então
  o laço repete até ninguém mais estourar o próprio piso. E quando nem a soma dos mínimos cabe na
  tela, cada um leva o seu e a sobreposição é inevitável — ainda é melhor que espremer alguém a zero.
- **O `Refresh()` passou a se rechamar em dois caminhos** (janela expulsa, mínimo recém-aprendido).
  Os dois só crescem, então a cadeia termina sozinha; mesmo assim ganhou um teto de profundidade,
  porque uma janela que devolvesse tamanhos diferentes a cada medição travaria a dock, e um teto é
  mais barato que confiar no bom comportamento alheio.

**Fechar uma de três janelas não devolvia o espaço às outras duas.** O `EVENT_OBJECT_DESTROY` chega
quando o hwnd já está morto — e ele caía no mesmo filtro `Concerns()` de todos os outros eventos,
que exige janela visível e com título (`IsAltTabWindow`). O filtro recusava, nenhum recálculo era
agendado, e o arranjo só se acertava quando outra coisa qualquer acontecia. É a terceira vez que
este mesmo erro aparece de formas diferentes (antes com `MOVESIZESTART`/`END` e com `MINIMIZEEND`):
**a pergunta certa para um evento de saída não é "esta janela nos interessa?" — já não dá para saber
— e sim "esta janela era nossa?"**, que a árvore ainda responde depois de a janela morrer.

Nota de método, aprendida ao testar: `Process.CloseMainWindow()` não fecha o Bloco de Notas do
Windows 11 — o `MainWindowHandle` desses processos vem zerado. Um teste que "roda sem erro" e não
fecha nada dá a impressão de que a correção falhou, quando o que falhou foi o teste. Enumerar a
janela pelo título e mandar `WM_CLOSE` no hwnd funciona.

**Janela nova: dividir o espaço da que está em foco, ou repartir tudo por igual?** As duas foram
implementadas; a segunda ficou. O modelo estilo i3 — a janela nova entra num nível novo e reparte
só o espaço da que estava em foco — produz com três janelas um `1277 | 637 | 637`, e com quatro,
quadrantes. Foi escolhido primeiro justamente para evitar que muitas janelas virassem tiras
estreitas. Em uso, numa ultrawide de 2560 px, o usuário achou pior: três janelas iguais de 850 px
valem mais que uma grande e duas pela metade.

Trocado por: toda janela entra **no mesmo nível das outras** e o espaço se reparte por igual. O
preço, conhecido e aceito, é que a largura cai junto com o número de janelas (quatro viram 637 px
cada) em vez de virar quadrante — numa tela larga isso demora a incomodar, e o Ctrl+Alt+Shift+seta
reajusta o que ficar apertado. Entrar *ao lado da referência*, e não no fim da fila, continua
valendo: é o que faz a janela aparecer perto de onde a pessoa estava olhando.

A troca custou apagar código, não escrever: sumiram a criação de container, a escolha de eixo pela
forma da janela de referência e a saída de reserva para quando ela ainda não tem retângulo. A
árvore continua sabendo aninhar (o `Resize` sobe níveis, o `Collapse` dissolve container de um
filho só, o mínimo de um container soma ou maximiza conforme o eixo) — hoje ela só não usa isso,
porque o arranjo escolhido é plano. Isso é de propósito: a generalidade não custa nada enquanto
está guardada, e é o que permitiria voltar atrás sem reescrever.

Lição de processo, a mesma da barra de título: as duas alternativas eram baratas de implementar e
impossíveis de julgar no papel. Mostrar as duas com os números reais da tela da pessoa
(`850 | 850 | 852` contra `1277 | 637 | 638`) resolveu em uma pergunta o que a discussão não
resolveria.
