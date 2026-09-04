# WinDock — instalar, usar e remover

Guia prático. Para o funcionamento por dentro (por que a dock não rouba foco, como a barra do
Windows é escondida, como a busca acha os apps), veja o [README](../README.md).

## Requisitos

| | |
|---|---|
| Sistema | Windows 10 ou 11 |
| Para **rodar** | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Para **compilar** | .NET 8 SDK (já traz o runtime) |

Para saber o que existe na máquina:

```powershell
dotnet --list-runtimes
```

Se aparecer uma linha `Microsoft.WindowsDesktop.App 8.x`, está pronto. Não existindo, ou você
instala o runtime, ou gera um executável que não depende de nada (veja
[Sem instalar o .NET](#sem-instalar-o-net)).

## Instalar

### 1. Gerar o executável

```powershell
cd D:\Projetos\WinDock
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Sai um `dist\WinDock.exe` de ~240 KB, arquivo único, sem instalador e sem DLL solta ao lado.

### 2. Escolher onde ele vai morar

O executável roda de qualquer pasta, mas **não o deixe em `dist`**: a próxima publicação
sobrescreve o arquivo, e a opção "Iniciar com o Windows" guarda o caminho onde ele estava.

```powershell
$destino = "$env:LOCALAPPDATA\Programs\WinDock"
New-Item -ItemType Directory -Force $destino | Out-Null
Copy-Item dist\WinDock.exe $destino -Force
& "$destino\WinDock.exe"
```

### 3. Ligar o início automático

Com a dock na tela: botão direito em qualquer ícone > **Configurações...** > **Iniciar com o
Windows**. Isso cria a tarefa agendada `WinDock`, com gatilho ao fazer logon.

> Se você mover o arquivo depois, desmarque e marque de novo — a tarefa guarda o caminho antigo.

A tarefa é usada no lugar da chave `Run` porque o Windows atrasa de propósito tudo que sobe pela
chave: os aplicativos de inicialização entram numa fila e o primeiro deles só arranca uns dez
segundos depois da área de trabalho aparecer. A tarefa não passa por essa fila. Quem já tinha o
início automático ligado pela chave é migrado sozinho, na primeira vez que abrir esta versão.
Se a política da máquina não deixar criar tarefas, a chave `Run` volta a ser usada.

### Sem instalar o .NET

Trocando um parâmetro, o executável passa a carregar o runtime dentro dele:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist-standalone
```

Fica com ~150 MB em vez de 240 KB, e roda em máquina sem .NET nenhum.

## Rodar

```powershell
WinDock.exe              # a dock
WinDock.exe --settings   # a dock, já abrindo o painel de configurações
```

O dia a dia:

| ação | como |
|---|---|
| Ativar / minimizar um app | clique no ícone |
| Alternar entre janelas do mesmo app | clique de novo |
| Ver as janelas de um app | pare o mouse no ícone (a partir de duas janelas) |
| Ir para uma janela específica | clique na miniatura dela |
| Menu (janelas, nova instância, fixar, fechar) | botão direito no ícone |
| Fixar um atalho específico (`.lnk`) | botão direito > **Adicionar atalho...** |
| Reordenar | arraste um ícone sobre o outro |
| Buscar aplicativos | `Alt+Espaço` |
| Configurações | botão direito > **Configurações...** |
| Sair | botão direito > **Sair do WinDock** |

Na barra de cima:

| ação | como |
|---|---|
| Calendário do mês | clique na data |
| Desligar, reiniciar, hibernar, bloquear, sair | botão de energia, à direita |
| Volume de um programa só | clique no alto-falante > seção **Aplicativos** |
| Abrir um programa da bandeja | clique no ícone dele, à esquerda da bateria |
| Ver os ícones da bandeja | seta `⌄`, à esquerda da bateria |

> O cartão da bandeja abre na hora e é relido do painel do próprio Windows a cada abertura, então
> mostra exatamente o que ele mostra — inclusive quando um programa fechou. Se algo mudou, a
> lista se corrige um instante depois de o cartão aparecer. Ele **some sozinho depois de três
> segundos** parado, e a contagem reinicia enquanto o mouse estiver dentro.

**Sempre saia pelo menu.** Ao sair, a dock devolve a faixa de tela que reservou e traz de volta
a barra do Windows, se você a tinha escondido. Matar o processo à força pula essa parte — veja
[Se algo ficou fora do lugar](#se-algo-ficou-fora-do-lugar).

## Atualizar

1. Saia pela dock (botão direito > **Sair do WinDock**) — com o app rodando, o arquivo fica
   travado e a publicação falha com `MSB3021`;
2. `dotnet publish ...` de novo (o comando da instalação);
3. copie o `dist\WinDock.exe` por cima do que está em uso;
4. abra outra vez.

Suas preferências e os apps fixados ficam em `%APPDATA%\WinDock\config.json` e sobrevivem à
troca do executável.

## Remover

```powershell
# 1. feche a dock pelo menu (botão direito num ícone > Sair do WinDock)

# 2. tire do início automático (a tarefa, e a chave Run se ela tiver sido usada)
schtasks /delete /tn WinDock /f 2>$null
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
                    -Name WinDock -ErrorAction SilentlyContinue

# 3. apague o executável
Remove-Item "$env:LOCALAPPDATA\Programs\WinDock" -Recurse -Force -ErrorAction SilentlyContinue

# 4. apague as preferências (opcional: aqui estão os apps fixados)
Remove-Item "$env:APPDATA\WinDock" -Recurse -Force -ErrorAction SilentlyContinue
```

Não há nada além disso: sem serviço, sem entrada em "Aplicativos instalados", sem arquivo em
`Program Files`.

## Onde ficam os arquivos

| o quê | onde |
|---|---|
| Executável | onde você copiou (sugestão: `%LOCALAPPDATA%\Programs\WinDock`) |
| Preferências e apps fixados | `%APPDATA%\WinDock\config.json` |
| Início automático | tarefa agendada `WinDock` (plano B: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, valor `WinDock`) |

O `config.json` pode ser editado à mão com a dock fechada; ela grava o arquivo ao sair, então
uma edição feita com o app aberto seria sobrescrita.

## Se algo ficou fora do lugar

### A área de trabalho ficou encolhida, ou a barra do Windows sumiu

Acontece se o processo for morto à força (Gerenciador de Tarefas, `Stop-Process -Force`,
queda de energia) enquanto a barra estava escondida: a dock não chega a desfazer o que fez.
Abrir e sair pelo menu resolve. Para consertar sem abrir a dock:

```powershell
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Fix {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cb; public RECT mon; public RECT work; public uint f; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint f);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, ref RECT r, uint c);
  public static void Run() {
    ShowWindow(FindWindow("Shell_TrayWnd", null), 5);          // barra de volta
    var p = new POINT();
    var mi = new MONITORINFO(); mi.cb = Marshal.SizeOf(typeof(MONITORINFO));
    GetMonitorInfo(MonitorFromPoint(p, 1), ref mi);
    var work = mi.work;
    SystemParametersInfo(0x002F, 0, ref work, 0x0002);         // shell recalcula a área útil
  }
}
'@
[Fix]::Run()
```

Reiniciar o Explorer (`Stop-Process -Name explorer -Force`) também resolve, ao custo de fechar
as janelas do Explorer abertas.

### `Alt+Espaço` não abre a busca

Outro programa já registrou o atalho — o **PowerToys Run** usa o mesmo por padrão. A dock avisa
na abertura quando não consegue registrar. Saídas: desligar o atalho no outro programa, ou usar
o menu de contexto da dock > **Buscar aplicativos**.

### A busca demora na primeira vez

Nos primeiros segundos depois do logon a dock ainda está montando a lista de aplicativos em
segundo plano. Passado isso, cada busca leva menos de 2 ms.

### Um ícone não abre nada ao ser clicado

A dock registra o que falhou em `%APPDATA%\WinDock\windock.log`:

```powershell
Get-Content "$env:APPDATA\WinDock\windock.log" -Tail 20
```

Duas causas comuns aparecem ali. **"falha ao abrir"** é alvo que mudou de lugar — app
reinstalado noutra pasta, ou versão que mudou de número no nome: apague o botão e fixe de novo.
**"a janela não veio para a frente"** é app rodando como administrador: uma dock comum não tem
permissão para mexer numa janela de integridade mais alta, e o clique não tem efeito.

Num ícone da **bandeja**, **"a bandeja mudou desde a última leitura"** quer dizer que um ícone
apareceu ou sumiu depois de a lista ter sido lida — a dock prefere não acionar nada a acionar o
ícone errado. Clique na seta duas vezes para reler e tente de novo.

Arquivo vazio ou inexistente significa que nada falhou — se ainda assim o clique não faz o que
você espera, é comportamento e não erro (por exemplo: clicar no app que já está em foco minimiza).

### A publicação falha com `MSB3021` / arquivo em uso

A dock está rodando e segurando o `.exe`. Saia por **Sair do WinDock** e publique de novo.

### A dock não aparece

Confira se o processo subiu:

```powershell
Get-Process WinDock -ErrorAction SilentlyContinue | Select-Object Id, Path
```

Se estiver rodando e mesmo assim invisível, provavelmente ela está numa borda diferente da que
você espera (ou fora da tela, depois de trocar de monitor). Apague o `config.json` e abra de
novo para voltar aos padrões — os apps fixados se perdem, o resto volta ao normal:

```powershell
Remove-Item "$env:APPDATA\WinDock\config.json"
```
