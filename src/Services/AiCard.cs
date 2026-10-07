using System.Globalization;
using System.Windows.Media;

namespace WinDock.Services;

/// <summary>
/// Uma conta no cartão, pronta para o XAML: já com o texto montado e a decisão de o que
/// mostrar tomada.
///
/// Existe para o XAML não precisar decidir nada. Sem isto, o `DataTemplate` teria de escolher
/// entre nome e e-mail, juntar organização com papel e traduzir o estado em mensagem — três
/// coisas que em XAML viram três conversores e ficam difíceis de ler, e que aqui são um
/// `switch` e um `string.Join`.
/// </summary>
public sealed class AiCard
{
    public AiCard(AiAccountUsage leitura, bool primeira, bool padrao = false)
    {
        var perfil = leitura.Perfil;

        Id = perfil.Id;
        Titulo = perfil.Titulo;
        Gauges = leitura.Gauges;
        Primeira = primeira;
        Padrao = padrao;
        Cor = CorDoUso(Gauges.Select(g => g.Percent).ToList());

        // O e-mail sai quando ele já é o título — repetir a mesma linha duas vezes gasta a
        // altura do cartão sem dizer nada.
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(perfil.Email) && perfil.Email != Titulo) partes.Add(perfil.Email!);

        // A organização de uma conta pessoal chega como "<e-mail>'s Organization" — o mesmo
        // e-mail que já está na linha, com uma palavra em inglês pendurada. Repetir isso
        // consome a largura toda e trunca a informação que interessa, então some. A de uma
        // conta de empresa tem nome próprio e fica.
        var organizacao = perfil.Organizacao;
        if (!string.IsNullOrWhiteSpace(perfil.Email) &&
            organizacao?.Contains(perfil.Email!, StringComparison.OrdinalIgnoreCase) == true)
            organizacao = null;

        if (!string.IsNullOrWhiteSpace(organizacao))
            partes.Add(string.IsNullOrWhiteSpace(perfil.Papel)
                       ? organizacao!
                       : $"{organizacao} · {perfil.Papel}");

        Linha = string.Join("  ·  ", partes);

        // O do perfil primeiro, que já vem pronto ("Max 5x") e acompanha a troca de plano — veja
        // `AiUsageService.NomeDoPlano`. O da credencial ("max", que o cartão mostra "Max") fica
        // para quando o perfil não diz nada.
        Plano = !string.IsNullOrWhiteSpace(perfil.Plano) ? perfil.Plano!
              : string.IsNullOrWhiteSpace(leitura.Plano) ? string.Empty
              : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(leitura.Plano.Replace('_', ' '));

        Mensagem = leitura.State switch
        {
            AiUsageState.NoCredentials => "Sem credencial nesta pasta.",
            AiUsageState.Expired => "Sessão expirada — use o Claude Code uma vez para renovar.",
            AiUsageState.Failed => leitura.Erro ?? "Não deu para ler a cota agora.",
            AiUsageState.Unknown => "Consultando…",
            _ => string.Empty
        };

        // Números que sobreviveram a uma leitura que falhou: eles continuam valendo mais que
        // nada, mas quem olha precisa saber que não são de agora. Sem a hora, o cartão
        // mentiria com cara de atualizado.
        Nota = leitura.Desde is { } quando && !string.IsNullOrWhiteSpace(leitura.Erro)
               ? $"sem atualizar desde {quando:HH:mm}"
               : string.Empty;
    }

    /// <summary>Aviso discreto embaixo das barras quando elas são de uma leitura anterior.</summary>
    public string Nota { get; } = string.Empty;

    /// <summary>A pasta da conta (".claude"), que é como a configuração a conhece.</summary>
    public string Id { get; }

    /// <summary>
    /// É a conta padrão — a do número e da cor do robô na barra? O cartão a marca com uma
    /// estrela, e só quando há outra ao lado: com uma conta só não há o que distinguir.
    /// </summary>
    public bool Padrao { get; }

    /// <summary>
    /// A cor do robô desta conta no cartão, pela regra do robô da barra: o medidor mais cheio
    /// dela. Na conta padrão dá a mesma cor que está na barra (pedido em 07/10/2026; antes o
    /// robô do cartão era sempre branco, e a cor da barra não tinha par lá dentro).
    /// </summary>
    public Brush Cor { get; }

    /// <summary>
    /// A cor de um conjunto de medidores: verde até 70%, amarelo de 70 a 95%, vermelho a partir
    /// de 95%, pelo mais cheio. Sem medidor nenhum, branco.
    /// </summary>
    public static Brush CorDoUso(IReadOnlyCollection<double> medidores)
    {
        if (medidores.Count == 0) return SemLeitura;

        var cheio = medidores.Max();
        return cheio >= 95 ? Fim : cheio >= 70 ? Atencao : Calmo;
    }

    /// <summary>
    /// As cores do robô. Amarelo e vermelho são os do <c>UsageBrush</c>, que pinta as barras do
    /// cartão, nas mesmas faixas. O verde é o de sucesso do Windows 11 sobre fundo escuro — e
    /// substitui o branco de antes, que deixava o robô igual aos outros ícones: a pessoa pediu
    /// que ele dissesse "está tranquilo" também, e não só quando aperta.
    /// </summary>
    private static readonly Brush SemLeitura = Congelado(0xF2, 0xF2, 0xF2);
    private static readonly Brush Calmo = Congelado(0x6C, 0xCB, 0x5F);
    private static readonly Brush Atencao = Congelado(0xFF, 0xB9, 0x00);
    private static readonly Brush Fim = Congelado(0xFF, 0x60, 0x5C);

    private static Brush Congelado(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string Titulo { get; }
    public string Linha { get; }
    public string Plano { get; }
    public IReadOnlyList<AiGauge> Gauges { get; }
    public string Mensagem { get; }

    /// <summary>Sem medidor não há barra para desenhar: fica a mensagem, em uma linha.</summary>
    public bool TemMensagem => Gauges.Count == 0;

    /// <summary>
    /// É a primeira conta do cartão? O separador entre contas fica no topo de cada uma, e a
    /// primeira não pode ter um — ele ficaria colado na borda de cima.
    /// </summary>
    public bool Primeira { get; }

    public bool TemSeparador => !Primeira;
}
