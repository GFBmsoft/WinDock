using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WinDock.Ui;

/// <summary>
/// Desenha um glifo da fonte de icones com um contorno da mesma cor, o que engrossa o
/// traco sem borrar.
///
/// A primeira tentativa foi empilhar o mesmo texto tres vezes, deslocado por meio pixel.
/// Funciona de longe, mas cada copia cai numa fracao de pixel diferente e o antialias das
/// tres se soma: o desenho fica serrilhado. Aqui o glifo vira geometria uma vez so e e
/// pintado com preenchimento e caneta juntos — um unico contorno, liso em qualquer tamanho.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender |
                                                    FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsRender |
                                            FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Quanto o contorno engrossa o traco. Zero deixa o glifo como a fonte desenhou.</summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(
        nameof(Weight), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(0.7, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Weight
    {
        get => (double)GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private FormattedText? Text()
    {
        if (string.IsNullOrEmpty(Glyph)) return null;

        return new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                 new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal,
                                              FontStretches.Normal),
                                 Size, Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override Size MeasureOverride(Size available)
    {
        var text = Text();
        return text is null
            ? new Size(Size, Size)
            : new Size(text.Width + Weight, text.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text();
        if (text is null) return;

        var geometry = text.BuildGeometry(new Point(Weight / 2, 0));
        var pen = Weight > 0 ? new Pen(Fill, Weight) { LineJoin = PenLineJoin.Round } : null;

        dc.DrawGeometry(Fill, pen, geometry);
    }
}
