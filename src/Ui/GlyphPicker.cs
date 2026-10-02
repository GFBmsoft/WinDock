using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinDock.Services;

namespace WinDock.Ui;

/// <summary>
/// O botão com o desenho de uma ferramenta, nas Configurações, que abre o seletor de desenho e
/// cor. Pedido de 01/10/2026: o automático acerta os comandos do Windows que a dock conhece, mas
/// um programa portátil qualquer caía no desenho genérico de aplicativo, sem jeito de trocar.
///
/// Escreve direto no <see cref="IGlyphChoice"/> da linha (<see cref="Tool"/>) — uma ferramenta ou,
/// desde 02/10/2026, uma origem de notificação —, e se redesenha
/// quando ele muda — inclusive quando é o comando que muda e o automático escolhe outro desenho.
/// Feito em código pelo mesmo motivo do <see cref="ColorPicker"/>: um arquivo só.
/// </summary>
public sealed class GlyphPicker : Button
{
    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(IGlyphChoice), typeof(GlyphPicker),
        new PropertyMetadata(null, (d, e) => ((GlyphPicker)d).OnToolChanged(e.OldValue as IGlyphChoice, e.NewValue as IGlyphChoice)));

    public IGlyphChoice? Tool
    {
        get => (IGlyphChoice?)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly TextBlock _preview;
    private readonly Popup _popup;
    private readonly UniformGrid _glyphs = new() { Columns = 8 };
    private readonly UniformGrid _colors = new() { Columns = 8, Margin = new Thickness(0, 0, 0, 8) };

    public GlyphPicker()
    {
        Width = 32;
        Height = 28;
        Cursor = Cursors.Hand;
        ToolTip = "Escolher o desenho e a cor";

        _preview = new TextBlock
        {
            FontFamily = IconFont, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var caixa = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = _preview };
        caixa.SetResourceReference(Border.BackgroundProperty, "FluentControl");
        caixa.SetResourceReference(Border.BorderBrushProperty, "FluentControlBorder");

        Template = new ControlTemplate(typeof(GlyphPicker)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Content = caixa;

        foreach (var cor in ToolsService.Colors) _colors.Children.Add(ColorSwatch(cor));
        foreach (var hex in ToolsService.Glyphs) _glyphs.Children.Add(GlyphButton(hex));

        var auto = new Button { Content = "Automático", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        auto.SetResourceReference(StyleProperty, "FluentButton");
        auto.Height = 26;
        auto.ToolTip = "O desenho e a cor que a dock escolhe sozinha";
        auto.Click += (_, _) =>
        {
            if (Tool is null) return;
            Tool.Glyph = string.Empty;
            Tool.Color = string.Empty;
            _popup!.IsOpen = false;
        };

        var corpo = new Border
        {
            Padding = new Thickness(10), Margin = new Thickness(8), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Width = 264, Children = { _colors, _glyphs, auto } },
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black }
        };
        corpo.SetResourceReference(Border.BackgroundProperty, "FluentCard");
        corpo.SetResourceReference(Border.BorderBrushProperty, "FluentCardBorder");

        _popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = corpo
        };

        Click += (_, _) => { Repaint(); _popup.IsOpen = !_popup.IsOpen; };
    }

    private void OnToolChanged(IGlyphChoice? antes, IGlyphChoice? agora)
    {
        if (antes is not null) antes.PropertyChanged -= OnToolPropertyChanged;
        if (agora is not null) agora.PropertyChanged += OnToolPropertyChanged;
        Repaint();
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e) => Repaint();

    /// <summary>O botão e a grade mostram o desenho na cor que vale agora, e marcam o escolhido.</summary>
    private void Repaint()
    {
        if (Tool is null) return;
        _preview.Text = Tool.LookGlyph;
        _preview.Foreground = Tool.LookFill;

        var atual = Tool.LookGlyph;
        foreach (var filho in _glyphs.Children.OfType<Button>())
        {
            if (filho.Content is not Border moldura || moldura.Child is not TextBlock desenho) continue;
            desenho.Foreground = Tool.LookFill;
            moldura.BorderBrush = desenho.Text == atual ? (Brush)FindResource("FluentAccent") : Brushes.Transparent;
        }

        foreach (var filho in _colors.Children.OfType<Button>())
            if (filho.Content is Border moldura)
                moldura.BorderBrush = string.Equals((string)filho.Tag, Tool.Color, StringComparison.OrdinalIgnoreCase)
                    ? (Brush)FindResource("FluentAccent")
                    : Brushes.Transparent;
    }

    private Button GlyphButton(string hex)
    {
        var desenho = new TextBlock
        {
            Text = ToolsService.ParseGlyph(hex) ?? "", FontFamily = IconFont, FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var moldura = new Border
        {
            Width = 30, Height = 30, Margin = new Thickness(1), CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Child = desenho
        };
        var botao = Flat(moldura, hex);
        botao.MouseEnter += (_, _) => moldura.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        botao.MouseLeave += (_, _) => moldura.Background = Brushes.Transparent;
        botao.Click += (_, _) =>
        {
            if (Tool is null) return;
            Tool.Glyph = hex;
            _popup.IsOpen = false;
        };
        return botao;
    }

    /// <summary>A cor muda o desenho já e deixa a grade aberta: o próximo clique costuma ser o desenho.</summary>
    private Button ColorSwatch(string hex)
    {
        var amostra = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!)
        };
        var moldura = new Border
        {
            Padding = new Thickness(2), Margin = new Thickness(3), CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Child = amostra,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var botao = Flat(moldura, hex);
        botao.Click += (_, _) => { if (Tool is not null) Tool.Color = hex; };
        return botao;
    }

    private static Button Flat(UIElement content, string tag) => new()
    {
        Content = content, Tag = tag, Cursor = Cursors.Hand, ToolTip = tag,
        Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
    };
}
