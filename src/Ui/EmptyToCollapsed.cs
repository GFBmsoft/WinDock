using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDock.Ui;

/// <summary>
/// Texto vazio some, texto com conteúdo aparece.
///
/// É do cartão da cota de IA, onde metade do cabeçalho é opcional: quem usa conta pessoal
/// não tem organização, e o plano só existe quando a credencial o traz. Sem isto, os
/// espaços reservados apareceriam como faixas vazias entre o nome e as barras.
/// </summary>
public sealed class EmptyToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
