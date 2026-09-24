using System.Globalization;

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
    public AiCard(AiAccountUsage leitura, bool primeira)
    {
        var perfil = leitura.Perfil;

        Titulo = perfil.Titulo;
        Gauges = leitura.Gauges;
        Primeira = primeira;

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

        // a API manda "max"; o cartão mostra "Max"
        Plano = string.IsNullOrWhiteSpace(leitura.Plano)
                ? string.Empty
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
