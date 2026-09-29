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

// ── os que exigem alvo explícito ────────────────────────────
//
// O winget imprime uma segunda tabela, sob uma frase terminada em dois-pontos, com os pacotes que
// o "upgrade --all" não atualiza. Esta é a saída de verdade desta máquina em 29/09/2026: sem
// tabela normal nenhuma, só a segunda — o Discord, que se atualiza sozinho por fora e por isso
// nunca sai dali. Lido como tabela normal, ele virava uma oferta que o botão "Atualizar tudo"
// prometia resolver e nunca resolvia.

var soTeimoso = UpdatesService.Parse(string.Join("\r\n",
    "Nenhum pacote instalado foi encontrado que corresponda aos critérios de entrada.",
    "",
    "Os pacotes a seguir têm uma atualização disponível, mas exigem uma segmentação explícita para atualização:",
    "Nome    ID              Versão   Disponível Origem",
    "--------------------------------------------------",
    "Discord Discord.Discord 1.0.9259 1.0.9260   winget",
    ""));
Check("a tabela dos que exigem alvo explícito é lida", soTeimoso.Count == 1, $"vieram {soTeimoso.Count}");
Check("e vem marcada como tal", soTeimoso.Count == 1 && soTeimoso[0].Explicit,
      soTeimoso.Count == 1 ? "veio como pacote comum" : "");

// ── as duas tabelas juntas ──────────────────────────────────
//
// O caso completo: a lista normal primeiro, o resumo, e depois a dos teimosos. Antes a leitura
// parava na primeira linha em branco, e os de baixo nem apareciam no cartão.

var duas = UpdatesService.Parse(string.Join("\r\n",
    "Nome                 ID                        Versão      Disponível   Origem",
    "----------------------------------------------------------------------------------",
    "Git                  Git.Git                   2.45.1      2.47.0       winget",
    "",
    "1 atualizações disponíveis.",
    "Os pacotes a seguir têm uma atualização disponível, mas exigem uma segmentação explícita para atualização:",
    "Nome    ID              Versão   Disponível Origem",
    "--------------------------------------------------",
    "Discord Discord.Discord 1.0.9259 1.0.9260   winget",
    ""));
Check("as duas tabelas entram na lista", duas.Count == 2, $"vieram {duas.Count}");
Check("a de cima não é marcada e a de baixo é",
      duas.Count == 2 && !duas[0].Explicit && duas[1].Explicit);

// ── o silêncio ──────────────────────────────────────────────
//
// Calar é pelo pacote, e não pela versão: o que incomoda é a oferta que nunca se resolve, e ela
// volta com número novo toda semana. O que fica calado sai da conta do ícone sem sair da vista.

var status = new UpdateStatus(Array.Empty<string>(), duas, DateTime.Now)
                 .WithSilenced(new[] { "discord.discord" });

Check("o calado sai da conta", status.Total == 1 && !status.Winget.Any(p => p.Id == "Discord.Discord"),
      $"total {status.Total}");
Check("mas continua à vista, para poder voltar", status.Silenced.Count == 1);
Check("e volta inteiro quando se desfaz",
      status.WithSilenced(Array.Empty<string>()).Total == 2);

// ── a atualização silenciosa, de verdade ────────────────────
//
// Só com `dotnet run -- vivo`, porque chama o winget desta máquina. Usa um id que não existe: o
// winget responde com código diferente de zero e uma frase explicando, que é exatamente o caminho
// que o cartão precisa mostrar quando uma atualização falha — e nada é instalado nem removido.
//
// Este é o caminho que **não** dá para exercitar numa máquina em dia: sem pacote pendente, o botão
// "Atualizar tudo" nem aparece. Aqui pelo menos o motor é percorrido inteiro: processo iniciado,
// as duas saídas lidas por evento, código conferido e a última linha virando a frase do cartão.

if (args.Contains("vivo"))
{
    Console.WriteLine();
    Console.WriteLine("── winget de verdade ──");

    var erro = await UpdatesService.UpgradeAsync("Pacote.Que.Nao.Existe");
    Check("um pacote inexistente volta como erro, e não como sucesso", erro is not null,
          "voltou nulo, como se tivesse dado certo");
    Console.WriteLine("    frase do cartão: " + erro);
}

Console.WriteLine();
Console.WriteLine(falhas == 0 ? "tudo certo" : $"{falhas} conferência(s) falharam");
return falhas == 0 ? 0 : 1;
