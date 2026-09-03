using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// Verdadeiro esconde, falso mostra — o par do <see cref="InverseBoolToVisibility"/>, mas
/// para quem já tem a propriedade no sentido positivo.
///
/// É do "Lendo a bandeja…" do cartão: ele aparece enquanto <c>HasTrayIcons</c> é falso e sai
/// quando a lista chega. Sem isso o cartão nasceria vazio e pularia para a grade cheia.
/// </summary>
public sealed class BoolToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
