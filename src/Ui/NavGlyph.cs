using System.Windows;

namespace WinDock.Ui;

/// <summary>O glifo de um item da navegação das Configurações, posto direto no XAML do item.</summary>
public static class NavGlyph
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(NavGlyph), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string value) => d.SetValue(GlyphProperty, value);
}
