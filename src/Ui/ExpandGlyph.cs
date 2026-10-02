using System.Globalization;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// A seta do rodapé dos cartões compactos: para baixo quando há o que abrir, para cima quando já
/// está aberto — aponta para onde o clique leva, como a do cartão da cota.
/// </summary>
public sealed class ExpandGlyph : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
