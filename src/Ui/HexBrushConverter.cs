using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinDock.Ui;

/// <summary>
/// "#202124" -> Brush, para a amostra de cor do painel. Cor invalida enquanto o usuario
/// ainda esta digitando nao pode quebrar o binding, entao vira transparente.
/// </summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
                brush.Freeze();
                return brush;
            }
        }
        catch { }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
