using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDock.Interop;

namespace WinDock.Services;

/// <summary>
/// Traduz as preferencias em valores que o XAML consome direto (tamanhos, brushes,
/// orientacao). Recalcula sozinho quando a config muda, para o painel aplicar ao vivo.
/// </summary>
public sealed class DockTheme : INotifyPropertyChanged
{
    private readonly DockConfig _config;

    public DockTheme(DockConfig config)
    {
        _config = config;
        _config.PropertyChanged += (_, _) => RaiseAll();
    }

    private bool Vertical => _config.Edge is DockEdge.Left or DockEdge.Right;

    /// <summary>
    /// Lado do botao: a espessura menos a folga da pilula e as margens do eixo curto —
    /// as do eixo longo nao apertam o botao, so afastam a pilula das pontas.
    /// </summary>
    public double ButtonSize => Math.Max(16, _config.Size - _config.PillPadding * 2 - ShortAxisMargin);

    private int ShortAxisMargin => Vertical
        ? _config.MarginLeft + _config.MarginRight
        : _config.MarginTop + _config.MarginBottom;

    /// <summary>
    /// O icone perde o espaco da faixa das bolinhas, mas so o que a folga dele nao cobre:
    /// a bolinha mora na margem que ja existia embaixo do icone, e o icone so encolhe
    /// quando essa margem nao da conta.
    /// </summary>
    public double IconSize => _config.IconSize > 0
        ? _config.IconSize
        : Math.Max(8, ButtonSize - _config.IconPadding * 2 - Math.Max(0, IndicatorLane - _config.IconPadding));
    public Thickness IconMargin => new(_config.IconPadding);
    public CornerRadius PillCorner => new(_config.CornerRadius);
    public Thickness PillPadding => new(_config.PillPadding);

    /// <summary>Folga em volta da pilula, um lado de cada vez.</summary>
    public Thickness PillMargin =>
        new(_config.MarginLeft, _config.MarginTop, _config.MarginRight, _config.MarginBottom);

    public Orientation Orientation => Vertical ? Orientation.Vertical : Orientation.Horizontal;

    // ── indicador de app aberto ─────────────────────────────
    // Um traco fino, como o da barra do Windows: curto enquanto o app esta so aberto e
    // comprido quando ele esta em primeiro plano. Fica sempre do lado da borda onde a
    // dock esta encostada, em faixa propria — por cima do icone ele disputava espaco com
    // o desenho do app.

    /// <summary>Tracos em fila: perpendicular a fila dos icones.</summary>
    public Orientation IndicatorOrientation => Vertical ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>Lado do botao onde a faixa dos tracos fica encostada.</summary>
    public Dock IndicatorDock => _config.Edge switch
    {
        DockEdge.Top   => Dock.Top,
        DockEdge.Left  => Dock.Left,
        DockEdge.Right => Dock.Right,
        _              => Dock.Bottom
    };

    /// <summary>Espessura do traco.</summary>
    public double IndicatorThickness => Math.Max(2, Math.Round(ButtonSize * 0.075));

    /// <summary>Comprimento com o app apenas aberto.</summary>
    private double ShortLength => Math.Max(5, Math.Round(ButtonSize * 0.17));

    /// <summary>Comprimento com o app em primeiro plano — so quando ele tem uma janela.</summary>
    private double LongLength => Math.Max(12, Math.Round(ButtonSize * 0.42));

    public double IndicatorShortWidth  => Vertical ? IndicatorThickness : ShortLength;
    public double IndicatorShortHeight => Vertical ? ShortLength : IndicatorThickness;
    public double IndicatorLongWidth   => Vertical ? IndicatorThickness : LongLength;
    public double IndicatorLongHeight  => Vertical ? LongLength : IndicatorThickness;

    /// <summary>Ponta arredondada, como a do Windows.</summary>
    public CornerRadius IndicatorCorner => new(IndicatorThickness / 2);

    /// <summary>Espessura da faixa: o traco mais um fio de respiro.</summary>
    public double IndicatorLane => IndicatorThickness + 4;

    public HorizontalAlignment IndicatorHorizontal => _config.Edge switch
    {
        DockEdge.Left  => HorizontalAlignment.Left,
        DockEdge.Right => HorizontalAlignment.Right,
        _              => HorizontalAlignment.Center
    };

    public VerticalAlignment IndicatorVertical => _config.Edge switch
    {
        DockEdge.Top    => VerticalAlignment.Top,
        DockEdge.Bottom => VerticalAlignment.Bottom,
        _               => VerticalAlignment.Center
    };

    /// <summary>Cor da bolinha do app em primeiro plano.</summary>
    public Brush IndicatorBrush => Frozen(ParseColor(_config.IndicatorColor, Color.FromRgb(0x6C, 0xB6, 0xFF)));

    /// <summary>A mesma cor, apagada: o app esta aberto, mas nao em primeiro plano.</summary>
    public Brush IndicatorDimBrush
    {
        get
        {
            var color = ParseColor(_config.IndicatorColor, Color.FromRgb(0x6C, 0xB6, 0xFF));
            color.A = 0x80;
            return Frozen(color);
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ── divisoria entre fixados e abertos ───────────────────

    /// <summary>
    /// O traco que separa os apps fixados dos que so estao abertos. Fino, discreto e no
    /// sentido contrario ao da dock: barra em pe numa dock deitada, e vice-versa.
    /// </summary>
    public double DividerWidth  => Vertical ? Math.Round(ButtonSize * 0.5) : 1;
    public double DividerHeight => Vertical ? 1 : Math.Round(ButtonSize * 0.5);

    /// <summary>Respiro dos dois lados do traco, no sentido em que a dock cresce.</summary>
    public Thickness DividerMargin => Vertical ? new Thickness(0, 5, 0, 5) : new Thickness(5, 0, 5, 0);

    /// <summary>A cor do indicador, bem apagada: e uma divisoria, nao um aviso.</summary>
    public Brush DividerBrush
    {
        get
        {
            var color = ParseColor(_config.IndicatorColor, Color.FromRgb(0x6C, 0xB6, 0xFF));
            color.A = 0x40;
            return Frozen(color);
        }
    }

    /// <summary>Respiro entre um traco e o seguinte, quando o app tem varias janelas.</summary>
    public Thickness IndicatorSpacing => Vertical ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);

    public HorizontalAlignment PillHorizontal => Vertical
        ? HorizontalAlignment.Center
        : (_config.Centered ? HorizontalAlignment.Center : HorizontalAlignment.Left);

    public VerticalAlignment PillVertical => Vertical
        ? (_config.Centered ? VerticalAlignment.Center : VerticalAlignment.Top)
        : VerticalAlignment.Center;

    /// <summary>Cor da pilula com a opacidade escolhida aplicada no alfa.</summary>
    public Brush PillBrush
    {
        get
        {
            var color = ParseColor(_config.Background);
            color.A = (byte)Math.Round(_config.Opacity * 2.55);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Hex do usuario, ou o padrao enquanto ele ainda esta digitando o valor.</summary>
    private static Color ParseColor(string hex, Color? fallback = null)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback ?? Color.FromRgb(0x20, 0x21, 0x24); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(ButtonSize), nameof(IconSize), nameof(IconMargin), nameof(PillCorner),
                     nameof(PillPadding), nameof(PillMargin), nameof(Orientation),
                     nameof(PillHorizontal), nameof(PillVertical), nameof(PillBrush),
                     nameof(IndicatorOrientation), nameof(IndicatorHorizontal),
                     nameof(IndicatorVertical), nameof(IndicatorThickness),
                     nameof(IndicatorShortWidth), nameof(IndicatorShortHeight),
                     nameof(IndicatorLongWidth), nameof(IndicatorLongHeight),
                     nameof(IndicatorCorner), nameof(IndicatorSpacing),
                     nameof(IndicatorDock), nameof(IndicatorLane),
                     nameof(IndicatorBrush), nameof(IndicatorDimBrush),
                     nameof(DividerWidth), nameof(DividerHeight),
                     nameof(DividerMargin), nameof(DividerBrush)
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
