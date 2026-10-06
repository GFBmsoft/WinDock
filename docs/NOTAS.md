# Notas da versão

<!--
  O histórico que aparece em Configurações › Notas da versão. Este arquivo vai embutido no
  executável (veja o WinDock.csproj) e é lido pelo ReleaseNotes.cs, que só entende duas coisas:

    ## 1.0.0.44 — 06/10/2026     uma versão, com a data
    - texto                       uma mudança dela

  A mais nova em cima. Toda tag nova ganha a sua entrada aqui ANTES de ser criada — é o commit da
  tag que leva a nota junto. Escrito para quem usa a dock: o que mudou na tela, e não no código.
-->

## 1.0.0.44 — 06/10/2026
- Notas da versão nas Configurações: o histórico do que mudou em cada versão.
- Clique do meio no alto-falante da barra liga e desliga o mudo, sem abrir o cartão; o mostrador e o cartão dizem "Mudo".
- Subir o volume devolve o som quando ele estava mudo.
- As dicas dos ícones da barra não aparecem mais por cima de um cartão aberto nem do mostrador de volume.
- A barra ficou mais leve: medir a rede, ler o volume e atualizar os ícones da dock custavam um engasgo por segundo.
- Ligar o mudo não segura mais a barra enquanto a placa de som responde.
- A primeira notificação do dia volta a ganhar balão; e o balão não nasce mais no meio da tela quando o sino não está na barra.

## 1.0.0.43 — 05/10/2026
- Mosaico: a janela nova vai direto para o lugar, sem esperar outro evento para se encaixar.
- O ícone da dock é relido quando o programa muda sem mudar de pasta.

## 1.0.0.42 — 05/10/2026
- Cota de IA: conta padrão (a estrela), que é a que a barra mostra e a primeira do cartão.
- O plano da conta é lido de onde ele se atualiza, e não fica mais com o nome antigo depois de uma troca.

## 1.0.0.41 — 02/10/2026
- Alt+Espaço abre a busca embaixo da lupa quando ela está no modo compacto.

## 1.0.0.40 — 02/10/2026
- Busca (a lupa) e Locais (as pastas mais usadas) no canto esquerdo da barra, e itens que podem mudar de canto.
- O nome da música rola inteiro, na barra e no cartão.
- Os controles de música encolhem junto com os ícones compactos.

## 1.0.0.39 — 02/10/2026
- Cartões compactos de volume, Wi-Fi, bluetooth e bateria, com uma seta para abrir o resto.
- Cartão de Wi-Fi próprio, com as redes ao alcance.
- Placa de vídeo no cartão de sistema, e a porcentagem da cota de IA ao lado do robô.
- Notificações separadas por site (WhatsApp, Gmail), com desenho por origem e balão breve.

## 1.0.0.38 — 01/10/2026
- O sino abre um cartão com as notificações da Central do Windows.

## 1.0.0.37 — 01/10/2026
- Cartão de energia com tempo ligado, bateria e plano de energia.
- A semana da bateria num quadriculado, hora por hora.

## 1.0.0.36 — 01/10/2026
- Configurações divididas em páginas, com navegação à esquerda.
- Barra em ilhas.
- As ferramentas avisam quando o comando não abre, em vez de fechar caladas.

## 1.0.0.35 — 01/10/2026
- Ferramentas na barra: os comandos que você mais usa, num cartão.
- Paleta de cores nas Configurações.
- O indicador de janela aberta na dock virou uma bolinha, e os ícones ficaram todos do mesmo tamanho.

## 1.0.0.34 — 01/10/2026
- Relógio no padrão compacto, cartão com a hora em segundos e calendário compacto.

## 1.0.0.33 — 01/10/2026
- Um presente na barra quando sai versão nova do WinDock; o clique atualiza.
- O robô da cota fica verde, amarelo ou vermelho pelas faixas de 70% e 95%.

## 1.0.0.32 — 01/10/2026
- Ícones compactos também diminuem o desenho, não só o espaço.

## 1.0.0.31 — 01/10/2026
- Ícones compactos: a barra com metade do espaço entre os ícones.
- A dock não fica mais presa quando o Explorer trava.
- O ícone de atualizações acompanha o que acabou de ser instalado.

## 1.0.0.30 — 29/09/2026
- O cartão de cota de IA ganhou um botão para perguntar de novo, na hora.

## 1.0.0.29 — 29/09/2026
- O robô da cota de IA muda de cor conforme o uso.

## 1.0.0.28 — 29/09/2026
- CPU e memória na barra, com a rede no cartão.
- O nome da faixa rola quando não cabe.

## 1.0.0.27 — 29/09/2026
- O pacote que nunca atualiza pode ser silenciado.
- Atualizar pelo winget não abre mais janela de terminal.

## 1.0.0.26 — 28/09/2026
- Depois de instalar, o ícone de atualizações apaga em segundos.

## 1.0.0.25 — 28/09/2026
- O cartão de atualizações passou a perguntar ao Windows Update de verdade — antes dizia "nada" com atualização esperando.
- O log ficou muito mais barato de escrever.

## 1.0.0.24 — 28/09/2026
- Lembretes com hora no calendário.
- O que há para atualizar (Windows Update e winget), na barra.

## 1.0.0.23 — 25/09/2026
- Remover pen-drive e HD externo com segurança, pela barra.

## 1.0.0.22 — 24/09/2026
- Cota de IA com várias contas e modo compacto; aguenta quando o serviço recusa a consulta.

## 1.0.0.21 — 24/09/2026
- Cota de IA na barra: quanto do Claude já foi usado, e de qual conta.
- A bandeja é lida logo depois do logon, e o clique num ícone sem nome voltou a funcionar.

## 1.0.0.20 — 23/09/2026
- Fechar uma janela pela miniatura, sem precisar abri-la.

## 1.0.0.19 — 23/09/2026
- Registrar cada passo no log virou opção nas Configurações, desligada por padrão.

## 1.0.0.18 — 23/09/2026
- A dock aparece no logon em cerca de 1,3 s, e não mais em 3 s.

## 1.0.0.17 — 22/09/2026
- Janelas novas podem nascer flutuando, e Alt+Shift+C devolve todas ao mosaico.

## 1.0.0.16 — 14/09/2026
- Tarefas concluídas do calendário se apagam sozinhas depois de um prazo (padrão: 2 dias).

## 1.0.0.15 — 14/09/2026
- A caixa de cópia do Explorer fica fora do mosaico, centralizada.
- Ctrl+Alt+S abre as Configurações.

## 1.0.0.14 — 14/09/2026
- Fechar o calendário descarta a tarefa digitada; só Enter ou o "+" gravam.

## 1.0.0.13 — 14/09/2026
- Tarefas do dia no próprio cartão do calendário: ver, anotar, mover, marcar como feita e remover.

## 1.0.0.12 — 14/09/2026
- O clique na dock minimiza também os programas Delphi no estilo antigo.

## 1.0.0.11 — 14/09/2026
- O WinDock abre uma vez só; abrir de novo mostra as Configurações da que já está rodando.

## 1.0.0.10 — 14/09/2026
- "Abrir local do arquivo" no menu do ícone da dock.

## 1.0.0.9 — 11/09/2026
- Janela solta largada em cima da dock volta para a área útil.

## 1.0.0.8 — 11/09/2026
- Atalhos de música (Alt+Shift+← → ↑) para voltar, passar e pausar a faixa.

## 1.0.0.7 — 11/09/2026
- Atalhos do mosaico configuráveis nas Configurações.

## 1.0.0.6 — 11/09/2026
- Ctrl+Alt+seta não passa mais pelas janelas minimizadas antes de chegar ao destino.

## 1.0.0.5 — 11/09/2026
- O botão de um app instalado pelo Squirrel (Discord) volta a achar a pasta depois que ele se atualiza.

## 1.0.0.4 — 10/09/2026
- Mover uma anotação do calendário para outro dia.
- Ler a bandeja não manda mais Esc para a janela em foco.
- Downloads pelas Releases do GitHub.

## 1.0.0.3 — 09/09/2026
- Alt+T prende a janela em foco acima das outras.
- Exceções do mosaico por tipo de janela.
- Botão de app da Store abre certo.
- A dock sobe com o Windows em 150 ms, e não mais em 4 s.

## 1.0.0.2 — 08/09/2026
- Calendário com feriados e anotações.
- Cartão do que está tocando também para vídeo e outros serviços de streaming.

## 1.0.0.1 — 05/09/2026
- Música na barra: controle das faixas e cartão com o que está tocando, com escolha dos programas.
- Ordem dos itens da barra à escolha.
- Brilho do monitor na barra, com opção para desligar.

## 1.0.0.0 — 04/09/2026
- Primeira versão: dock, barra superior, busca no Alt+Espaço e mosaico.
