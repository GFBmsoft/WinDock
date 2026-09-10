# WinDock

Uma dock para Windows no espírito do [Dash to Dock](https://extensions.gnome.org/extension/307/dash-to-dock/),
para usar a borda inferior enquanto a barra de tarefas fica no topo.

C# + WPF (.NET 8), sem dependências externas.

<img src="assets/windock.png" width="96" alt="Ícone do WinDock" />

Para instalar, atualizar, remover ou destravar alguma coisa, veja o
**[manual](docs/MANUAL.md)**. Este arquivo explica como a dock funciona por dentro, e os
**[aprendizados](docs/APRENDIZADOS.md)** guardam o que **não** funciona no Windows 11 e o que
funciona no lugar — tudo verificado na máquina, para ninguém tentar de novo o mesmo caminho.

## Rodar

```powershell
dotnet build
.\bin\Debug\net8.0-windows\WinDock.exe
```

`WinDock.exe --settings` abre já com o painel de configurações — serve para um atalho.

Para sair: botão direito em qualquer ícone > **Sair do WinDock**. Ao sair a dock devolve
os pixels reservados e a área de trabalho volta ao tamanho normal.

## O que já faz

- **Pílula centralizada**: só ela é visível e clicável; o resto da faixa é transparente
- **Reserva a faixa da tela** (AppBar do shell), então nenhuma janela maximizada fica por baixo
- **Painel de configurações** que aplica ao vivo, enquanto o slider é arrastado
- **Apps abertos** agrupados por executável, com um traço por janela
- **Apps fixados**, persistidos em `%APPDATA%\WinDock\config.json`
- **Clique**: ativa; se já estiver em foco, minimiza; com várias janelas, alterna entre elas
  — vale igual para fixado e para aberto
- **Botão direito**: lista de janelas, nova instância, fixar/desafixar, fechar, configurações, sair — no menu escuro do Windows 11
- **Arrastar para reordenar**, com a ordem salva na hora
- **Iniciar com o Windows** por uma opção no painel
- **Traço aceso** no app em primeiro plano; o realce de fundo só responde ao mouse
- **Não rouba o foco** (`WS_EX_NOACTIVATE`): clicar na dock não tira o app da frente
- **Busca de aplicativos** em `Alt+Espaço`, com comandos de energia e "executar"
- **Esconder ou recolher a barra do Windows** enquanto a dock roda
- **Barra de cima** opcional: relógio, data, bateria, volume e os painéis do Windows
- **Ícones da bandeja** dos programas rodando em segundo plano, alcançáveis com a barra do
  Windows escondida
- **Volume por aplicativo**, um controle para cada programa com som
- **Menu de energia** e **calendário do mês**, na própria barra
- Atualização por `SetWinEventHook` (evento do shell), não por varredura em loop

## Configuração

Botão direito em qualquer ícone > **Configurações...**, ou `WinDock.exe --settings`.
Cada controle vale na hora; o arquivo é salvo ao fechar o painel.

| opção | padrão | o que faz |
|---|---|---|
| Borda da tela | `Bottom` | `Top`, `Bottom`, `Left`, `Right` — nas laterais os ícones empilham |
| Espessura | `38` | altura da faixa, em pixels físicos |
| Margens da pílula | `0` | folga em cada lado: superior, inferior, esquerda, direita |
| Reservar a faixa | `true` | encolher a área de trabalho (`false` = flutua por cima) |
| Centralizar a pílula | `true` | senão encosta no início da faixa |
| Opacidade | `90` | 0 = pílula invisível, 100 = sólida |
| Cantos | `19` | metade da espessura dá a cápsula perfeita |
| Folga do ícone | `5` | menor = ícone maior dentro do botão |
| Tamanho do ícone | `0` | `0` = automático pelo tamanho do botão; ou um valor fixo |
| Folga da pílula | `4` | respiro entre a pílula e os botões |
| Cor de fundo | `#202124` | hex; a opacidade é aplicada em cima |
| Cor do indicador | `#6CB6FF` | hex do traço do app aberto |
| Mostrar apps abertos | `true` | se `false`, só os fixados aparecem |
| Barra do Windows | `Keep` | `Keep`, `AutoHide`, `Hidden` — ver abaixo |
| Buscar com Alt+Espaço | `true` | liga o launcher |
| Barra superior | `false` | relógio, data, bateria, volume e atalhos do Windows |
| Altura da barra superior | `30` | em pixels físicos |
| Cor / opacidade da barra | `#202124` / `95` | independentes da dock |
| Relógio no centro | `false` | em vez do canto direito |

O arquivo fica em `%APPDATA%\WinDock\config.json` e também pode ser editado à mão
(nesse caso vale na próxima abertura).

### O painel

O desenho é o do app **Configurações do Windows 11**: cada opção em seu cartão, título e
explicação à esquerda, controle à direita; seções separadas, conteúdo rolável e um rodapé fixo
com os botões. A barra de título é pintada de escuro pelo `DWMWA_USE_IMMERSIVE_DARK_MODE` —
ela é do Windows, não do WPF, e viria branca em cima de um painel escuro.

Os controles são desenhados à mão em `src/Ui/Fluent.xaml`: o WPF não traz nada de WinUI, e o
projeto não carrega dependência externa. São o interruptor (a bolinha atravessa e o trilho fica
azul), o slider de trilho fino com o miolo que encolhe ao arrastar, a lista suspensa com popup
arredondado e sombra, a caixa de texto que acende só a linha de baixo no foco, os botões comum
e de destaque, e a barra de rolagem — a mesma que a busca usa.

As explicações que antes ficavam soltas na tela ("as mudanças valem na hora", "arraste os ícones
para mudar a ordem") moram no **`?`** ao lado do título, num balão escuro — o `ToolTip` padrão do
WPF também é claro e de quinas retas, então ele tem estilo próprio no mesmo arquivo.

Dois detalhes que só aparecem em uso: marcar o "Iniciar com o Windows" no construtor fazia o
WPF **rolar até ele**, então o painel abria no meio da lista e agora volta ao topo no `Loaded`;
e como a dock não ativa nada (`WS_EX_NOACTIVATE`), o painel abria **atrás** da janela em que
você estava — hoje ele recebe o mesmo empurrão de foco do launcher.

### O menu do botão direito

Segue o mesmo desenho: fundo escuro, cantos de 8 px, sombra própria, realce arredondado no item
sob o mouse e o atalho alinhado à direita (`InputGestureText`, como o `Alt+Espaço` da busca) —
no lugar do menu branco de quinas retas que o WPF traz.

A sombra é desenhada dentro do template, com `HasDropShadow` desligado: a do sistema vem com
quina reta e apareceria por baixo dos cantos arredondados. E como o menu é montado item a item
em código, os estilos de `ContextMenu` e `MenuItem` são implícitos — assim cada item nasce no
estilo certo sem precisar carregá-lo.

O separador é a exceção, e a armadilha: **dentro de um menu o WPF ignora o estilo implícito de
`Separator`** e procura a chave `MenuItem.SeparatorStyleKey`. Sem ela o risco continua sendo o
do tema claro — começando colado na borda esquerda e sobrando na direita. Por isso o mesmo
estilo é registrado duas vezes, sob essa chave e como implícito.

## A barra de cima

Esconder a barra do Windows leva o relógio junto. A barra de cima devolve o que faz falta, sem
tentar imitar o que o Windows já faz bem:

- **relógio e data**, no canto ou no centro (opção), em português, na mesma cor e peso — com a
  barra translúcida, a data em cinza sumia;
- **bateria** desenhada como a pilha do Windows, com o miolo enchendo conforme a carga. A cor
  segue os outros ícones (branca) e só muda quando diz alguma coisa: **vermelha abaixo de 20%**
  (mesmo na tomada) e **verde enquanto carrega**, voltando ao branco em 100%. A porcentagem fica
  na dica do mouse; a seção some sozinha em máquina sem bateria;
- **volume**, com o alto-falante da fonte de ícones do próprio Windows: a roda do mouse ajusta
  direto no ícone, e o clique abre um controle com o nível (Core Audio), o mudo e as **saídas de
  áudio** ligadas — clicar numa delas troca a padrão. Abaixo vem o **mixer por aplicativo**: um
  controle e um mudo para cada programa com som, pelas sessões do Core Audio. A lista some quando
  nada está tocando, como no painel do Windows — sessão de programa que parou de tocar expira e
  sai sozinha;
- **calendário**, ao clicar na data: o mês desenhado à mão, com hoje no círculo azul e setas para
  folhear. O `Calendar` do WPF viria com o tema claro, e reestilizá-lo custaria um
  `ControlTemplate` inteiro — mais XAML do que a grade que ele desenharia;
- **energia**: bloquear, encerrar sessão, suspender, hibernar, reiniciar e desligar. A lista mora
  no `PowerService`, e não no menu, porque a busca do `Alt+Espaço` usa a mesma — duas cópias
  divergiriam, e o pior caso é o comando errado atrás do rótulo certo. A ordem coloca o que
  interrompe o trabalho por último, longe da borda onde o cursor chega primeiro;
- **bluetooth**, aceso quando o rádio está ligado e apagado quando não. O clique abre a lista de
  aparelhos pareados, os conectados primeiro, e cada linha tem um **✕** para tirá-la da barra
  (fica guardado em `BluetoothHidden`, e um link devolve os ocultos);
- **wi-fi e notificações**, que abrem os painéis do próprio Windows: `ms-availablenetworks:` e
  `ms-actioncenter:`. Mandar as teclas `Win+A` e `Win+N` parecia mais direto e **não funciona** —
  clicar na barra deixa o foco na barra de tarefas escondida, e nesse estado o atalho só foca a
  barra em vez de abrir o painel; o endereço não depende de foco nenhum.

### Os ícones da bandeja

Esconder a barra do Windows leva junto a bandeja — e com ela o antivírus, o AnyDesk e todo
programa que só existe ali. A opção **Ícones da bandeja** traz esses ícones para a barra de cima.

A bandeja legada morreu no Windows 11 (veja os [aprendizados](docs/APRENDIZADOS.md)): não existe
mais `ToolbarWindow32` dentro de `TrayNotifyWnd`, e ler os botões por `TB_GETBUTTON` +
`ReadProcessMemory` deixou de ser possível. A fonte é o **painel de ícones ocultos do próprio
Windows** — a automação de interface enumera os botões dele, o `PrintWindow` tira o desenho de
cada ícone, e o clique aciona o botão de verdade lá dentro.

**Uma seta abre um cartão com os ícones**, no desenho do painel do Windows, e ele **some sozinho
depois de três segundos parado** (a contagem reinicia com o mouse dentro — fechar debaixo do
cursor de quem está mirando um ícone seria pior que não fechar). Abrir o cartão relê a bandeja do
zero, e é aí que está a graça: o que aparece é sempre o estado do momento, nunca um retrato
velho — um programa que fechou some do cartão.

**O cartão abre em uns 70 ms** e a releitura acontece por trás; quando ela volta, a lista se
corrige sozinha. Esperar pela leitura antes de mostrar qualquer coisa — que era como estava —
fazia o clique parecer travado, porque ler custa uns 600 ms.

A primeira leitura de cada sessão é a cara (a janela do painel do Windows ainda não existe,
nasce sem camada, a foto sai preta e é preciso ler de novo). Ela acontece **sozinha, seis
segundos depois de a barra subir**, quando ninguém está esperando — o que só é possível agora
que ler ficou invisível e não mexe mais na área de trabalho.

Só uma leitura de cada vez (enfileirar cliques fazia a barra responder a cliques de minutos
atrás), e **a última leitura vale por cinco segundos**. Essa validade não é economia de tempo: é
que abrir o painel do Windows traz **ele** para primeiro plano, e a janela de quem está
trabalhando pisca a barra de título. Devolver o foco no meio da leitura não adianta — o painel se
dispensa ao perder o foco, que é justamente o que se precisa dele —, então ele é devolvido no
fim, e a piscada é evitada não relendo. Abrir e fechar o cartão em seguida, que é o gesto comum,
não relê nada.

A leitura **só acontece quando a pessoa abre a seta**, que é exatamente quando o Windows também
mostraria esses ícones. É por isso que o estado da seta não fica guardado: lembrá-lo significaria
ler a bandeja durante o logon, no momento mais disputado da máquina, para preencher uma área que
talvez ninguém vá olhar. Recolhida, a barra sobe sem tocar no shell.

### Fazer isso sem piscar a tela

Ler a bandeja obriga a barra do Windows a ficar de pé por uns 350 ms — escondida, o painel de
ícones ocultos não abre. Na primeira versão isso aparecia: a barra nativa saltava por cima da
nossa e o painel dela piscava no canto, a cada clique na seta.

O que resolve é **`WS_EX_LAYERED` com alfa zero**. A janela continua existindo, continua
respondendo à automação e continua sendo fotografável pelo `PrintWindow` — só não é desenhada na
tela. As duas janelas do shell (a barra e o painel) ficam assim durante a operação e voltam ao
estilo original no fim, sempre, inclusive se algo explodir no meio. E como a janela do painel
sobrevive fechada, dá para deixá-la invisível *antes* de o shell mostrá-la; só na primeiríssima
vez de cada sessão do explorer ela ainda não existe e aparece por um instante.

Faltava uma peça: o vigia que reesconde a barra do Windows roda a cada 150 ms e a escondia **no
meio da leitura** — o painel não abria e o clique na seta simplesmente não fazia nada, de vez em
quando. Ele agora fica pausado enquanto a bandeja é lida (`TaskbarService.Suspend`).

Depois de aberto, o painel não depende mais da barra do Windows.

> **Uma versão anterior montava a lista pelo registro** (`HKCU\Control Panel\NotifyIconSettings`,
> que guarda por ícone o executável, a dica e um `IconSnapshot` em PNG) cruzado com os processos
> vivos. É barato e não pisca — mas o registro é um *histórico*, não um retrato: mostrava seis
> ícones onde o Windows mostrava três, incluindo programas que rodavam sem ter ícone nenhum na
> bandeja. E o `LastWriteTime` das chaves não separa vivo de morto (quase todas carregam o mesmo
> carimbo de uma migração). Um painel que não bate com o do lado não serve, então a lista passou
> a vir de onde ela é verdade.

O recorte de cada ícone tem uma sutileza: o fundo do painel do Windows é **opaco**, não
transparente. Colado na nossa barra viraria um quadradinho escuro atrás de cada ícone. A cor de
fundo é lida do canto do recorte — que é sempre moldura — e tudo que se parece com ela vira
transparente.

Cor, opacidade e altura são separadas das da dock: uma é faixa inteira, a outra é uma pílula
solta, e o que fica bom numa não fica na outra.

Os painéis de volume e bluetooth fecham ao clicar fora — o que **não** vem de graça: o
`StaysOpen="False"` do WPF depende de captura de mouse, e a barra é `WS_EX_NOACTIVATE`. A
verificação é feita na mão, com o cuidado de ler o estado no *apertar* do botão: o popup se fecha
sozinho antes do clique chegar, e ler no `Click` fazia o painel reabrir.

A lista de aparelhos bluetooth **não** vem da API clássica de bluetooth: ela só enxerga
dispositivos clássicos, e um teclado LE ficava de fora. Vem da lista de dispositivos do Windows,
filtrando `BTHENUM\DEV_` (clássicos) e `BTHLE\DEV_` (LE) — o que também faz a lista funcionar com
o rádio desligado.

### Duas brigas com a barra nativa

**Ela empurrava a nossa para baixo.** A barra do Windows continua registrada como AppBar mesmo
escondida, e o shell posicionava a nossa logo abaixo dela — sobravam 32 px de terra de ninguém
no topo, que ainda por cima engoliam os cliques. Com a barra nativa escondida ou recolhida, a
nossa ignora essa consulta de posição e vai para o topo de verdade.

**Ela volta sozinha.** O shell reexibe a barra escondida em várias ocasiões — ao abrir o painel
de wi-fi ou o de notificações, por exemplo — e ela reaparece por cima de tudo, roubando os
cliques de quem estiver embaixo. No modo "Esconder", a WinDock agora vigia isso e a reesconde.

E as duas ocupam a mesma borda: o shell só conta a mais alta, então devolver os pixels da nativa
levava junto a faixa da nossa. Por isso o cálculo da área de trabalho tem um piso — a altura da
barra de cima, quando ela existe.

No modo **Recolher na borda** a barra de cima fica desligada: a nativa reaparece por cima ao
encostar o mouse no topo, e as duas brigariam pela mesma faixa.

## Indicador de app aberto

Cada janela aberta vira um **traço fino** embaixo do ícone, como na barra do Windows 11: curto e
apagado enquanto o app está só aberto, e **comprido e aceso** quando ele está em primeiro plano.
O traço estica só quando o app tem uma janela; com várias, eles ficam curtos para caberem lado a
lado e continuarem contando quantas são — no máximo quatro, e a contagem exata fica no tooltip e
no menu de contexto.

A cor sai da opção **Cor do indicador**; a versão apagada é a mesma cor com metade do alfa,
então basta escolher uma.

Quem está em primeiro plano é dito **só pelo traço**: o app em foco não ganha mais um fundo
claro atrás do ícone, que competia com o desenho do próprio app — pior ainda nos ícones claros.
O realce de fundo ficou só para o mouse.

O traço tem **faixa própria**, fora do ícone — antes ficava por cima dele, disputando espaço com
o desenho do app. Um `DockPanel` reserva essa faixa primeiro e deixa o resto para o ícone. Para o
ícone não encolher à toa, a faixa usa antes de tudo a folga que já existia embaixo dele
(`IconPadding`); só o que faltar sai do tamanho do ícone.

Ele fica sempre do lado da borda em que a dock está encostada: embaixo do ícone numa dock
inferior, em cima numa superior, ao lado (na vertical) nas laterais. Espessura e comprimento
acompanham o tamanho do botão (`DockTheme.IndicatorThickness` e companhia), então não é preciso
ajustar nada ao mudar a espessura da dock.

## Clicar sem perder o foco

A dock usa `WS_EX_NOACTIVATE`, como a barra de tarefas: clicar num ícone não faz dela a janela
em primeiro plano. Sem isso o "se já estiver em foco, minimiza" nunca aconteceria — no instante
do clique quem estaria em foco seria a própria dock, e o app clicado seria só reativado.

Como o foco ainda pode passar pela dock em outros momentos (menu de contexto, painel de
configurações), o `DockModel` guarda a última janela em primeiro plano **que não é da dock**, e é
essa que decide entre minimizar e ativar. Com várias janelas: se o app está em foco, o clique
passa para a próxima; se não está, volta para a última que estava sendo mostrada.

## Buscar aplicativos (Alt+Espaço)

No espírito do [PowerToys Run](https://learn.microsoft.com/pt-br/windows/powertoys/run): `Alt+Espaço`
abre uma caixa de busca no meio da tela, você digita, as setas escolhem e o `Enter` executa.
`Esc` fecha, e clicar fora também. O mesmo `Alt+Espaço` com a busca aberta fecha.

Com o campo vazio a janela é só a caixa de busca, sem lista — repetir ali os ícones que já estão
na dock não ajudaria ninguém. Digitando, os resultados vêm nesta ordem:

1. **o que já está na dock** (fixado ou aberto) — vale mais que um app só instalado, porque é
   o que você usa todo dia; abrir daqui ativa a janela existente em vez de abrir outra;
2. **os aplicativos instalados** — vêm de `shell:AppsFolder`, a mesma pasta virtual que o menu
   iniciar usa, então cobre tanto programas Win32 quanto apps da Store, cada um com o
   AppUserModelID que o abre (é a mesma identidade que a dock usa para agrupar, então o app
   aberto pelo launcher cai no botão certo);
3. **energia** — `Desligar`, `Reiniciar`, `Hibernar`, `Suspender`, `Bloquear`,
   `Encerrar sessão`. Só aparecem a partir de três letras e só quando o nome **começa** com o
   que foi digitado: desligar a máquina não pode ser resultado de um `Enter` apressado;
4. **executar comando** — sempre o último item: roda o texto como o `Win+R` faria, seja
   `cmd`, um caminho ou uma URL.

A busca ignora acentos e maiúsculas, e casa por começo do nome, começo de palavra ou letras na
ordem — `bmt` acha `BM Testes`.

A lista rola por pixel, não de item em item, e a barra de rolagem é um traço arredondado de
~6 px que clareia no hover — a `ScrollBar` padrão do WPF é clara, larga e tem setas nas pontas,
e num painel escuro chamava mais atenção que a própria lista. O trilho continua clicável para
rolar uma página; ele só não é desenhado.

### Por que ela responde na hora

Medido nesta máquina, com 140 aplicativos: montar o catálogo custa **~770 ms** e resolver os
ícones, **~8,9 s** — o shell é chamado uma vez por ícone. Pago na hora da busca, isso dava
**~106 ms** por consulta, e dava para sentir.

Então esse trabalho todo saiu do caminho: a dock aquece o catálogo e os ícones numa thread
própria (STA, prioridade mínima, três segundos depois de subir, para não disputar com o login),
os nomes já ficam guardados sem acento nem maiúscula, e a busca só espera 70 ms entre a tecla e
o resultado — digitar "chrome" refazia a lista seis vezes, cinco delas para um texto que já não
estava mais no campo. Com o cache quente, cada busca leva **menos de 2 ms**.

## Esconder a barra do Windows

Não dá para esconder **só** os ícones dos apps na barra nativa: no Windows 11 eles são
desenhados em XAML dentro da própria barra (a antiga `MSTaskListWClass` já vem oculta), então
não existe janela Win32 para mexer. O que a dock oferece é a barra inteira:

| modo | o que faz |
|---|---|
| `Keep` | não mexe |
| `AutoHide` | some, e volta enquanto o mouse estiver na faixa dela. Não se perde nada |
| `Hidden` | some enquanto a dock roda. Menu iniciar, chevron da bandeja e Wi-Fi/Bluetooth deixam de ter clique — restam os atalhos (Win, Win+A, Win+N) e o launcher |

Nos dois casos os pixels da barra voltam para a área de trabalho: sem isso ela sumiria da tela
mas continuaria cobrando a faixa, e as janelas maximizadas parariam num vazio. Ao sair, a
WinDock devolve a barra e manda o shell refazer a conta.

### Como, e o que não funciona

Duas saídas que parecem óbvias e **não funcionam** no Windows 11 atual — as duas testadas aqui,
não deduzidas:

- `SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)` não muda nada: o `ABM_GETSTATE` continua zero e
  a área de trabalho não se mexe, mesmo com a barra visível e um handle válido na `APPBARDATA`;
- o byte de auto-hide em `StuckRects3` não vale mais: alterá-lo não faz efeito nem depois de
  reiniciar o Explorer.

O que funciona é `SPI_SETWORKAREA` **sem** `SPIF_SENDCHANGE`. O detalhe é contraintuitivo: com o
aviso, o shell recalcula na hora e desfaz o pedido; sem ele, a área de trabalho fica como pedimos.
Só o lado onde a barra está é devolvido, então a faixa da dock continua reservada mesmo quando as
duas estão na mesma borda da tela. Como o shell refaz esse cálculo sempre que uma appbar muda,
a WinDock reaplica de tempos em tempos e quando a espessura da dock muda.

O auto-hide, por isso, é da própria WinDock: a barra fica escondida e volta quando o cursor entra
na faixa dela, verificado a cada 150 ms.

## Vários perfis do mesmo app (Chrome)

Dois perfis do Chrome viram dois botões, como na barra de tarefas. O que os separa não é o
executável — os dois perfis rodam no mesmo `chrome.exe`, e às vezes no mesmo processo — mas o
**AppUserModelID** que cada janela expõe (`Chrome` para o padrão, `Chrome.UserData.Profile2`
para os outros). A dock agrupa por ele, e cai no caminho do executável quando a janela não
define um.

Para saber o que abrir e qual ícone usar, na ordem:

1. **o atalho do Windows com o mesmo AppUserModelID** — varre os `.lnk` fixados na barra de
   tarefas, no menu iniciar e na área de trabalho. Dele vêm o nome, os argumentos e o ícone
   (inclusive o ícone com o avatar, quando o atalho do perfil define um);
2. **o `Local State` do Chrome** — quando o perfil não tem atalho, o próprio Chrome informa o
   nome do perfil, e daí saem o `--profile-directory` e a foto, desenhada no canto do ícone;
3. **o executável**, sem mais nada.

`Adicionar atalho...` no menu de contexto fixa um `.lnk` específico — é o caminho para fixar
"Chrome — perfil X" exatamente como está na barra de tarefas.

## Apps da Store (Configurações, Calculadora, Fotos…)

Esses apps não têm executável que sirva para nada: o `SystemSettings.exe` das **Configurações**
traz só o ícone genérico de aplicativo, e chamá-lo direto nem sempre abre a janela. Quem sabe
das duas coisas é a pasta de aplicativos do shell, pelo AppUserModelID — então um botão cuja
identidade é um AUMID (e que não aponta para um `.lnk`) tira o ícone de
`shell:AppsFolder\<AUMID>` e abre por lá, via `explorer`. Vale igual para o app aberto e para o
fixado, então dá para deixar as Configurações na dock como qualquer outro app.

A ordem do ícone é: o `.lnk`, quando existe (é dele que vem o avatar do perfil do Chrome); a
pasta de aplicativos, quando a identidade é um AUMID; e por último o executável.

### O detalhe que pintava tudo de branco

O shell devolve os pixels do ícone de dois jeitos, e não avisa qual: a maioria vem
**premultiplicada** pelo alfa, mas os das Configurações e de outros apps da Store vêm com a cor
crua. Tratar os crus como premultiplicados faz o WPF pintar de branco tudo que deveria ser
transparente — era a chapa branca atrás da engrenagem. O `IconService` agora decide olhando os
pixels: em premultiplicado nenhum canal de cor pode passar do alfa; achou um que passa, é cor
crua (`Bgra32`).

## Instalar e iniciar com o Windows

> O passo a passo completo — inclusive atualizar e remover — está no
> [manual](docs/MANUAL.md).

Gere o executável de release — um arquivo só, ~240 KB, sem instalador:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Copie `dist\WinDock.exe` para onde ele vai morar (por exemplo
`%LOCALAPPDATA%\Programs\WinDock\`), rode uma vez e marque **Iniciar com o Windows** no painel
de configurações. Isso cria a tarefa agendada `WinDock`, com gatilho ao fazer logon — se você
mover o arquivo depois, desmarque e marque de novo.

O publish acima depende do runtime .NET 8 Desktop na máquina (já vem com o SDK). Para um exe
que não dependa de nada, troque para `--self-contained true`: fica ~150 MB.

### Por que não como serviço

Serviço do Windows não serve para isto. Serviços rodam na sessão 0, isolada da área de trabalho
desde o Windows Vista: não têm acesso ao desktop do usuário, não conseguem criar janelas
visíveis nem registrar uma AppBar, e sobem antes do logon — quando ainda não existe sessão onde
desenhar. Um serviço também roda como SYSTEM, então não enxergaria seu `%APPDATA%`, seus
atalhos nem seus perfis do Chrome.

### Por que tarefa agendada e não a chave `Run`

O que um app com interface precisa é subir depois do logon, na sessão interativa e com o token
do usuário. A chave `HKCU\...\CurrentVersion\Run` faz isso e é a mais simples — era o que o
painel usava. Só que o Windows **segura de propósito** o que sobe por ela: os aplicativos de
inicialização entram numa fila do shell e o primeiro deles só arranca uns dez segundos depois
da área de trabalho aparecer. Para a maioria dos programas ninguém nota; para uma dock, que é
moldura da tela, o intervalo inteiro é uma tela sem barra.

A tarefa agendada com gatilho "ao fazer logon" não passa por essa fila. O painel cria a tarefa
`WinDock` (`TASK_LOGON_INTERACTIVE_TOKEN`, sem elevação — dock elevada não recebe arrastar-e-
soltar do Explorer), com quatro segundos de folga só para o Explorer estar de pé na hora de
registrar a AppBar. A chave `Run` continua como plano B, para o caso de a política da máquina
não deixar criar tarefas; quem já tinha o arranque ligado pela chave é migrado sozinho, uma vez.

Um atalho em `shell:startup` seria a terceira opção — mas ele cai na mesma fila da chave `Run`.

## Preview das janelas

Parar o mouse num botão que agrupa **duas ou mais janelas** mostra uma miniatura de cada uma,
ao vivo, como faz a barra de tarefas. Clicar numa delas traz aquela janela para a frente.

Com uma janela só não aparece nada: o clique no ícone já faz a coisa certa, e o painel seria um
passo a mais para o mesmo resultado. Os **dois perfis do Chrome não caem aqui** — eles são dois
botões separados, de uma janela cada; o preview é para o botão que agrupa várias.

Não desenhamos as miniaturas. O `DwmRegisterThumbnail` liga cada janela de origem à do preview
e o compositor passa a desenhar o conteúdo vivo nos retângulos que pedimos: nada de capturar,
redimensionar ou repintar por nossa conta, e é o mesmo mecanismo que o Windows usa. A proporção
de cada miniatura vem do `DwmQueryThumbnailSourceSize`, que sabe o tamanho real da origem melhor
que um `GetWindowRect` — ele desconta a moldura invisível.

Três detalhes que o mecanismo impõe:

- **A janela do preview não pode ser transparente.** `AllowsTransparency="True"` faz do WPF uma
  janela em camada, e o DWM se recusa a desenhar miniatura dentro de janela em camada — o painel
  apareceria vazio. Os cantos arredondados vêm do próprio DWM
  (`DWMWA_WINDOW_CORNER_PREFERENCE`), não da transparência.
- **O desenho fica por cima, não dentro do layout.** O XAML só reserva o espaço; o retângulo de
  cada miniatura é calculado depois, quando dá para perguntar onde cada espaço ficou.
- **A posição depende do tamanho, e o tamanho só existe depois de mostrar.** A janela nasce fora
  da tela e só então vai para o lugar; posicionar antes deixava o painel meio fora do monitor,
  porque a conta usava altura zero.

Há um atraso de 400 ms antes de aparecer e 300 ms antes de sumir — o mesmo que a barra de
tarefas faz. Sem o primeiro, atravessar a dock abriria um painel por ícone no caminho; sem o
segundo, o painel sumiria enquanto o mouse viaja do botão até ele.

## Reordenar os ícones

Arraste um ícone sobre outro dentro da pílula. Os vizinhos se afastam **durante** o gesto, como
na barra de tarefas: a troca acontece enquanto o cursor passa por eles, e não ao soltar. Antes a
dock só reordenava no `Drop`, então nada se mexia até largar o botão e o arraste parecia travado.

Três detalhes fazem o gesto parecer natural:

- o ícone que está na mão fica **apagado** no lugar de origem (`DockItem.IsDragging`), para
  ficar claro o que está sendo movido;
- os outros **deslizam** até a posição nova em vez de pularem — `src/Ui/Reorder.cs` anota onde
  cada um estava, deixa a mudança acontecer, empurra cada um de volta ao ponto antigo e solta;
  a animação de volta ao lugar certo é o que se vê (160 ms, desacelerando no fim);
- a troca só vale quando o cursor passa **do meio** do ícone vizinho — parando bem na divisa,
  os dois ficariam trocando de lugar sem parar.

A ordem dos fixados é gravada uma vez, ao soltar: durante o arraste ela muda a cada ícone
cruzado, e salvar a cada troca seria escrever o arquivo dezenas de vezes num gesto só. Ícones de
apps abertos que não estão fixados também se movem, mas só enquanto a janela existir — não há o
que salvar.

## Estrutura

| arquivo | responsabilidade |
|---|---|
| `src/Interop/Native.cs` | assinaturas Win32, sem estado |
| `src/Interop/AppBar.cs` | `SHAppBarMessage`: reserva a faixa e reposiciona quando o shell muda |
| `src/Models/DockItem.cs` | um botão: o app, suas janelas, ícone, estado e as bolinhas |
| `src/Services/WindowService.cs` | quais janelas viram botão (critério do Alt+Tab) e as ações sobre elas |
| `src/Services/DockModel.cs` | mantém os botões em sincronia com as janelas |
| `src/Services/DockTheme.cs` | traduz a config em tamanhos e brushes que o XAML consome |
| `src/Services/IconService.cs` | ícone do executável, com cache |
| `src/Services/ShortcutService.cs` | lê os `.lnk` do Windows: alvo, argumentos, ícone e AppUserModelID |
| `src/Services/ChromeProfiles.cs` | nome, argumento e foto de cada perfil, via `Local State` |
| `src/Services/Config.cs` | preferências; notifica mudanças para aplicar ao vivo |
| `src/Services/AppCatalog.cs` | os apps instalados (`shell:AppsFolder`) e o "executar comando" |
| `src/Services/Launcher.cs` | a busca do launcher: pontuação, ordem e comandos de energia |
| `src/Services/PanelModel.cs` | o que a barra de cima mostra: relógio, bateria, volume |
| `src/Services/VolumeService.cs` | volume do sistema pelo Core Audio (ler, mudar, mudo) |
| `src/Services/TaskbarService.cs` | esconde ou recolhe a barra do Windows, e a devolve ao sair |
| `src/Ui/Fluent.xaml` | o desenho do Windows 11: interruptor, slider, lista, botões, menu de contexto |
| `src/Ui/Reorder.cs` | faz os ícones deslizarem até a posição nova durante o arraste |
| `assets/windock.ico` | ícone do app: a pílula com os apps dentro; abaixo de 32 px, um desenho mais cheio |
| `assets/windock.png` | a mesma arte em 256 px, para o cabeçalho do painel |
| `src/Ui/HexBrushConverter.cs` | `"#202124"` → brush, para a amostra de cor |
| `src/Views/MainWindow.xaml` | a pílula, os botões e os traços de janela |
| `src/Views/LauncherWindow.xaml` | a caixa de busca do Alt+Espaço, com a barra de rolagem própria |
| `src/Views/PanelWindow.xaml` | a barra de cima |
| `src/Views/SettingsWindow.xaml` | o painel de configurações |


## Ainda não faz

- Jump lists do app (as do menu direito são só as janelas abertas)
- Multi-monitor: usa o monitor onde a dock nasce, sem uma dock por tela
- Auto-hide
- Menu de botão direito nos ícones da bandeja (o do próprio programa, que a bandeja nova não
  expõe fora do painel do Windows)
