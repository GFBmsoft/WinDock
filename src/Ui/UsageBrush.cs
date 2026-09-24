using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinDock.Ui;

/// <summary>
/// A cor de um medidor de cota, pela porcentagem: azul enquanto sobra, âmbar quando está
/// apertando, vermelho no fim.
///
/// As faixas são as mesmas que a bateria usa para "acabando" — e o azul é o
/// <c>#FF4CC2FF</c> de realce do resto do painel, para o cartão não estrear uma paleta só
/// dele.
/// </summary>
public sealed class UsageBrush : IValueConverter
{
    private static readonly SolidColorBrush Calmo = Frozen(0x4C, 0xC2, 0xFF);
    private static readonly SolidColorBrush Atencao = Frozen(0xFF, 0xB9, 0x00);
    private static readonly SolidColorBrush Fim = Frozen(0xFF, 0x60, 0x5C);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };

        return percent >= 90 ? Fim : percent >= 70 ? Atencao : Calmo;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
