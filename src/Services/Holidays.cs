namespace WinDock.Services;

/// <summary>
/// Os feriados nacionais brasileiros, calculados — não uma lista para atualizar todo ano.
///
/// Metade deles é fixa no calendário (Natal, Tiradentes) e a outra metade anda com a Páscoa:
/// Carnaval, Sexta-feira Santa e Corpus Christi caem a uma distância fixa dela. Como a Páscoa
/// sai de uma conta fechada, o calendário responde por qualquer ano que a pessoa folheie —
/// para trás ou para a frente — sem depender de internet nem de manutenção anual.
///
/// Ficam de fora os estaduais e municipais, que não têm regra nacional; e os pontos
/// facultativos (o Carnaval é feriado só em parte do país, mas na prática de quem usa isto
/// aqui ele vale mais marcado do que ignorado).
/// </summary>
public static class Holidays
{
    /// <summary>O nome do feriado nesta data, ou <c>null</c> se for um dia comum.</summary>
    public static string? Of(DateTime date)
    {
        var dia = date.Date;
        return Do(dia.Year).TryGetValue(dia, out var nome) ? nome : null;
    }

    /// <summary>
    /// Os feriados de um ano, calculados uma vez.
    ///
    /// O cache existe porque isto é consultado 42 vezes a cada vez que o calendário se
    /// desenha — uma por casa da grade — e folhear os meses redesenha a grade inteira.
    /// </summary>
    private static readonly Dictionary<int, Dictionary<DateTime, string>> Cache = new();

    private static Dictionary<DateTime, string> Do(int ano)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(ano, out var pronto)) return pronto;

            var pascoa = Easter(ano);
            var feriados = new Dictionary<DateTime, string>
            {
                [new DateTime(ano, 1, 1)]   = "Confraternização Universal",
                [new DateTime(ano, 4, 21)]  = "Tiradentes",
                [new DateTime(ano, 5, 1)]   = "Dia do Trabalho",
                [new DateTime(ano, 9, 7)]   = "Independência do Brasil",
                [new DateTime(ano, 10, 12)] = "Nossa Senhora Aparecida",
                [new DateTime(ano, 11, 2)]  = "Finados",
                [new DateTime(ano, 11, 15)] = "Proclamação da República",
                [new DateTime(ano, 11, 20)] = "Consciência Negra",
                [new DateTime(ano, 12, 25)] = "Natal",
            };

            // os móveis, todos contados a partir da Páscoa. A quarta-feira de cinzas fica de
            // fora de propósito: é dia útil (o expediente só começa mais tarde), e pintá-la de
            // vermelho junto com os outros faria o calendário mentir sobre um dia de trabalho.
            Add(feriados, pascoa.AddDays(-48), "Carnaval");
            Add(feriados, pascoa.AddDays(-47), "Carnaval");
            Add(feriados, pascoa.AddDays(-2),  "Sexta-feira Santa");
            Add(feriados, pascoa.AddDays(60),  "Corpus Christi");

            Cache[ano] = feriados;
            return feriados;
        }
    }

    /// <summary>Um móvel pode cair no ano vizinho (o Carnaval de um ano que começa em janeiro);
    /// guardar só o que é deste ano mantém o cache coerente com a chave dele.</summary>
    private static void Add(Dictionary<DateTime, string> feriados, DateTime data, string nome)
    {
        if (!feriados.ContainsKey(data)) feriados[data] = nome;
    }

    /// <summary>
    /// O domingo de Páscoa, pelo algoritmo de Meeus/Jones/Butcher (calendário gregoriano).
    ///
    /// São contas inteiras sem exceção nenhuma — vale de 1583 a 4099, muito além de qualquer
    /// mês que alguém vá folhear aqui. As variáveis levam os nomes de uma letra do algoritmo
    /// original de propósito: renomeá-las para "a distância do ciclo metônico" não explicaria
    /// mais e tornaria impossível conferir contra a referência.
    /// </summary>
    private static DateTime Easter(int ano)
    {
        var a = ano % 19;
        var b = ano / 100;
        var c = ano % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;

        var mes = (h + l - 7 * m + 114) / 31;
        var dia = (h + l - 7 * m + 114) % 31 + 1;

        return new DateTime(ano, mes, dia);
    }
}
