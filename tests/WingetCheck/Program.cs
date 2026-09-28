using WinDock.Services;

var falhas = 0;
void Check(string nome, bool ok, string detalhe = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FALHA")} {nome}{(ok || detalhe == "" ? "" : "  -> " + detalhe)}");
    if (!ok) falhas++;
}

// ── a tabela em português, que é a desta máquina ────────────
//
// Copiada da forma como o winget imprime: cabeçalho, régua de hifens, as linhas, uma linha em
// branco e o resumo. As colunas são alinhadas com espaços, e o número deles varia com o
// conteúdo — é por isso que a separação é por "dois ou mais brancos", e não por posição fixa.

var ptBr = string.Join("\r\n",
    "Nome                 ID                        Versão      Disponível   Origem",
    "----------------------------------------------------------------------------------",
    "Git                  Git.Git                   2.45.1      2.47.0       winget",
    "Notepad++            Notepad++.Notepad++       8.6.9       8.7.1        winget",
    "Microsoft PowerShell Microsoft.PowerShell      7.4.5.0     7.5.0.0      winget",
    "",
    "3 atualizações disponíveis.");

var pt = UpdatesService.Parse(ptBr);

Check("tabela em português: três pacotes", pt.Count == 3, $"vieram {pt.Count}");
Check("o nome sai inteiro, com o espaço de dentro",
      pt.Count == 3 && pt[2].Name == "Microsoft PowerShell",
      pt.Count == 3 ? $"veio '{pt[2].Name}'" : "");
Check("a versão de agora e a que vem", pt.Count > 0 && pt[0].Current == "2.45.1" && pt[0].Available == "2.47.0",
      pt.Count > 0 ? $"veio '{pt[0].Current}' → '{pt[0].Available}'" : "");
Check("o id vem junto", pt.Count > 0 && pt[0].Id == "Git.Git", pt.Count > 0 ? $"veio '{pt[0].Id}'" : "");
Check("a linha de resumo não vira pacote", pt.All(p => !p.Name.Contains("atualizações")));

// ── a mesma tabela em inglês ────────────────────────────────
//
// Um Windows em inglês imprime outros títulos e outro resumo. Nada disso é lido — o que orienta
// a leitura é a régua e o alinhamento —, e é justamente isso que esta conferência prova.

var enUs = string.Join("\r\n",
    "Name                 Id                        Version     Available    Source",
    "----------------------------------------------------------------------------------",
    "Git                  Git.Git                   2.45.1      2.47.0       winget",
    "",
    "1 upgrades available.");

var en = UpdatesService.Parse(enUs);
Check("tabela em inglês: o idioma não muda a leitura",
      en.Count == 1 && en[0].Id == "Git.Git", $"vieram {en.Count}");

// ── máquina em dia ──────────────────────────────────────────
//
// Sem tabela nenhuma: é o que o winget responde quando não há o que atualizar, e foi o que esta
// máquina respondeu em 28/09/2026. Tem de sair lista vazia, e não erro.

var nada = UpdatesService.Parse(
    "Nenhum pacote instalado foi encontrado que corresponda aos critérios de entrada.\r\n");
Check("sem atualização nenhuma: lista vazia, sem erro", nada.Count == 0, $"vieram {nada.Count}");

// ── o pacote sem origem ─────────────────────────────────────
//
// Um pacote instalado fora do winget (um .exe baixado à mão) aparece com a coluna Origem vazia.
// Ele tem versão e disponível, então continua valendo — a linha só fica com quatro campos.

var semOrigem = UpdatesService.Parse(string.Join("\r\n",
    "Nome                 ID                        Versão      Disponível   Origem",
    "----------------------------------------------------------------------------------",
    "AULA F99             ARP\\Machine\\X86\\{1CA0}    2.0         2.1",
    ""));
Check("pacote sem origem continua na lista", semOrigem.Count == 1 && semOrigem[0].Available == "2.1",
      $"vieram {semOrigem.Count}");

// ── a linha curta demais ────────────────────────────────────
//
// Um pacote instalado que o winget lista sem versão disponível não tem o que atualizar; a linha
// não chega a quatro campos e é descartada em vez de virar um pacote com campos trocados.

var curta = UpdatesService.Parse(string.Join("\r\n",
    "Nome                 ID                        Versão",
    "----------------------------------------------------------------------------------",
    "Algo                 Algo.Algo                 1.0",
    ""));
Check("linha sem a coluna 'disponível' é descartada", curta.Count == 0, $"vieram {curta.Count}");

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} conferência(s) falharam");
return falhas == 0 ? 0 : 1;
