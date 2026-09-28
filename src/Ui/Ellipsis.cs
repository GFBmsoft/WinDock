using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinDock.Ui;

/// <summary>
/// Põe a dica de mouse com o texto inteiro num <see cref="TextBlock"/> que está cortado — e só
/// nele.
///
/// O WPF corta o texto com <c>TextTrimming</c>, mas não conta a ninguém que cortou: não existe
/// nada como o <c>IsTextTrimmed</c> do WinUI para pendurar um gatilho. Sem isto, restariam duas
/// saídas ruins — dica em todo mundo, e aí o mouse parado sobre "Pagar luz" abre um balão
/// repetindo "Pagar luz"; ou dica em ninguém, e o texto longo vira reticências sem jeito de ler.
///
/// A largura do texto é medida com <see cref="FormattedText"/> em vez de um <c>Measure</c>
/// avulso: medir o próprio elemento fora da passagem de layout do WPF suja o arranjo que ele
/// acabou de calcular.
/// </summary>
public static class Ellipsis
{
    public static readonly DependencyProperty ShowTooltipProperty =
        DependencyProperty.RegisterAttached("ShowTooltip", typeof(bool), typeof(Ellipsis),
            new PropertyMetadata(false, OnChanged));

    public static void SetShowTooltip(DependencyObject o, bool value) => o.SetValue(ShowTooltipProperty, value);
    public static bool GetShowTooltip(DependencyObject o) => (bool)o.GetValue(ShowTooltipProperty);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock texto) return;

        texto.SizeChanged -= OnSizeChanged;
        texto.Loaded -= OnLoaded;

        if (!(bool)e.NewValue) { texto.ToolTip = null; return; }

        texto.SizeChanged += OnSizeChanged;
        texto.Loaded += OnLoaded;
        Update(texto);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Update((TextBlock)sender);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((TextBlock)sender);

    private static void Update(TextBlock texto)
    {
        if (texto.ActualWidth <= 0 || texto.Text.Length == 0) { texto.ToolTip = null; return; }

        var medida = new FormattedText(
            texto.Text,
            CultureInfo.CurrentCulture,
            texto.FlowDirection,
            new Typeface(texto.FontFamily, texto.FontStyle, texto.FontWeight, texto.FontStretch),
            texto.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(texto).PixelsPerDip);

        // a folga de um pixel evita a dica que aparece e some com o texto que cabe raspando
        var cortado = medida.Width > texto.ActualWidth + 1;
        texto.ToolTip = cortado ? texto.Text : null;
    }
}
