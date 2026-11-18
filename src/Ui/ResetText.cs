using System.Globalization;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// Quando a janela de cota zera, dito como se diz em voz alta: "zera em 2h14", "zera em
/// 3d 4h", e "zerou às 12:50" quando a leitura é de antes disso.
///
/// O tempo que falta vale mais que a hora exata — a pergunta de quem olha é "dá para
/// continuar hoje?", não "que horas são lá". A hora vai junto quando é ainda hoje, porque aí
/// ela é acionável; de amanhã em diante só o prazo, que é o que se consegue guardar.
/// </summary>
public sealed class ResetText : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime quando) return string.Empty;

        // Já passou: a leitura é de antes de a janela zerar, e o serviço já mostra 0% nela (veja
        // AiUsageService.Vigente). "Zera agora" ficaria na tela até a próxima leitura — de manhã,
        // a noite inteira.
        var falta = quando - DateTime.Now;
        if (falta <= TimeSpan.Zero)
            return quando.Date == DateTime.Today ? $"zerou às {quando:HH:mm}"
                 : quando.Date == DateTime.Today.AddDays(-1) ? $"zerou ontem às {quando:HH:mm}"
                 : $"zerou em {quando:dd/MM}";

        if (falta.TotalDays >= 1)
            return $"zera em {(int)falta.TotalDays}d {falta.Hours}h";

        var prazo = falta.TotalHours >= 1
            ? $"{(int)falta.TotalHours}h{falta.Minutes:00}"
            : $"{falta.Minutes} min";

        return quando.Date == DateTime.Today
            ? $"zera em {prazo}, às {quando:HH:mm}"
            : $"zera em {prazo}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
