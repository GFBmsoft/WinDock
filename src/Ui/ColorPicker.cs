using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WinDock.Ui;

/// <summary>
/// A amostra de cor das Configurações, que abre uma paleta ao ser clicada.
///
/// Antes a amostra era só um quadrado pintado ao lado do campo de texto, e escolher cor era
/// digitar hex de cabeça. O campo continua ao lado — é por ele que se cola um tom exato —, e a
/// paleta é o caminho de quem quer só escolher. As cores são as de destaque do Windows 11
/// (Configurações › Personalização › Cores), mais uma fileira de fundos escuros e as que a
/// própria dock usa de fábrica.
///
/// Feito em código, sem XAML próprio, para ser um arquivo só e servir a qualquer janela que
/// tenha os recursos do <c>Fluent.xaml</c>.
/// </summary>
public sealed class ColorPicker : Button
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                                      (d, _) => ((ColorPicker)d).Repaint()));

    /// <summary>A cor em hex ("#202124"), a mesma string que o campo de texto ao lado edita.</summary>
    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>
    /// Fundos escuros primeiro (é o que se escolhe para a dock e a barra), depois as cores da
    /// dock de fábrica, e então as 48 de destaque do Windows 11 na ordem da tela dele.
    /// </summary>
    private static readonly string[] Palette =
    [
        "#000000", "#141414", "#1C1C1C", "#202124", "#2B2B2B", "#3A3A3A", "#5A5A5A", "#FFFFFF",
        "#6CB6FF", "#4CC2FF", "#FFB56C", "#F4A261", "#B08AE8", "#7FD18B", "#FF6B6B", "#E8E8EA",
        "#FFB900", "#FF8C00", "#F7630C", "#CA5010", "#DA3B01", "#EF6950", "#D13438", "#FF4343",
        "#E74856", "#E81123", "#EA005E", "#C30052", "#E3008C", "#BF0077", "#C239B3", "#9A0089",
        "#0078D7", "#0063B1", "#8E8CD8", "#6B69D6", "#8764B8", "#744DA9", "#B146C2", "#881798",
        "#0099BC", "#2D7D9A", "#00B7C3", "#038387", "#00B294", "#018574", "#00CC6A", "#10893E",
        "#7A7574", "#5D5A58", "#68768A", "#515C6B", "#567C73", "#486860", "#498205", "#107C10",
        "#767676", "#4C4A48", "#69797E", "#4A5459", "#647C64", "#525E54", "#847545", "#7E735F",
    ];

    private readonly Border _swatch;
    private readonly Popup _popup;
    private readonly UniformGrid _grid;

    public ColorPicker()
    {
        Width = 32;
        Height = 32;
        Cursor = Cursors.Hand;
        Focusable = true;
        ToolTip = "Escolher numa paleta";

        _swatch = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "FluentControlBorder");

        // sem o modelo padrão do botão: ele pintaria o cinza do Windows clássico em volta da
        // amostra, e o que se quer aqui é só o quadrado da cor
        Template = new ControlTemplate(typeof(ColorPicker))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
        };
        Content = _swatch;

        _grid = new UniformGrid { Columns = 8 };
        foreach (var hex in Palette) _grid.Children.Add(SwatchFor(hex));

        var corpo = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    Caption("Clique numa cor. Para um tom exato, digite o hex no campo ao lado."),
                    _grid
                }
            },
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black }
        };
        corpo.SetResourceReference(Border.BackgroundProperty, "FluentCard");
        corpo.SetResourceReference(Border.BorderBrushProperty, "FluentCardBorder");

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -230,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = corpo
        };

        Click += (_, _) => { MarkSelected(); _popup.IsOpen = !_popup.IsOpen; };
        Repaint();
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Width = 232, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FluentTextDim");
        return t;
    }

    private Button SwatchFor(string hex)
    {
        var amostra = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            Background = Brush(hex),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        // a moldura de fora acende na cor escolhida agora e no realce do mouse
        var moldura = new Border
        {
            Padding = new Thickness(2), Margin = new Thickness(1), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Child = amostra
        };

        var botao = new Button
        {
            Tag = hex, ToolTip = hex, Cursor = Cursors.Hand, Content = moldura,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        botao.MouseEnter += (_, _) => { if (!IsCurrent(hex)) moldura.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)); };
        botao.MouseLeave += (_, _) => { if (!IsCurrent(hex)) moldura.BorderBrush = Brushes.Transparent; };
        botao.Click += (_, _) =>
        {
            Color = hex;
            _popup.IsOpen = false;
        };
        return botao;
    }

    private bool IsCurrent(string hex) => string.Equals(Normalize(Color), hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>"#ff202124" e "#202124" são a mesma cor para quem marca a amostra escolhida.</summary>
    private static string Normalize(string? hex)
    {
        hex = (hex ?? "").Trim();
        if (hex.Length == 9 && hex.StartsWith('#') && hex[1..3].Equals("FF", StringComparison.OrdinalIgnoreCase))
            return "#" + hex[3..];
        return hex;
    }

    private void MarkSelected()
    {
        foreach (var filho in _grid.Children.OfType<Button>())
        {
            if (filho.Content is Border moldura)
                moldura.BorderBrush = IsCurrent((string)filho.Tag)
                    ? (Brush)FindResource("FluentAccent")
                    : Brushes.Transparent;
        }
    }

    private void Repaint() => _swatch.Background = Brush(Color);

    private static Brush Brush(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)!);
                b.Freeze();
                return b;
            }
        }
        catch { /* ainda digitando: fica transparente, como a amostra antiga */ }
        return Brushes.Transparent;
    }
}
