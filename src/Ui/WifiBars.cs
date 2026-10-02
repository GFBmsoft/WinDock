using System.Globalization;
using System.Windows.Data;
using WinDock.Services;

namespace WinDock.Ui;

/// <summary>As barras de sinal de uma rede (0 a 4) no desenho das ondas — veja <c>WifiService.GlyphForBars</c>.</summary>
public sealed class WifiBars : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        WifiService.GlyphForBars(value is byte b ? b : value is int i ? i : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
