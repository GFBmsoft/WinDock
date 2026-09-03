using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// Verdadeiro esconde, falso mostra — o contrário do <c>BooleanToVisibilityConverter</c>.
///
/// Existe para o "Voltar para hoje" do calendário, que só faz sentido quando o mês na tela
/// <em>não</em> é o de hoje. A alternativa seria uma segunda propriedade no modelo só para
/// carregar a negação, como o par ClockCentered/ClockAtRight já faz.
/// </summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
