using System.Globalization;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// Zero vira desligado, qualquer outro valor vira ligado — pro toggle de "Contorno" nas
/// configurações, que por baixo ainda é a espessura (zero desliga sem desligar o mosaico
/// inteiro; o Windows decide a espessura de verdade agora, então só sobrou o liga/desliga).
/// </summary>
public sealed class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1 : 0;
}
