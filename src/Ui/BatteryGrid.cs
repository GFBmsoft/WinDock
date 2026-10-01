using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinDock.Services;

namespace WinDock.Ui;

/// <summary>
/// A semana da bateria num quadriculado, no desenho das contribuições do GitHub — o mesmo do
/// painel do GRepos (<c>GradeContribuicoes</c>), pedido pelo usuário em 01/10/2026 no lugar de
/// um gráfico de linha.
///
/// Uma linha por dia (os últimos sete, hoje embaixo) e uma coluna por hora. A cor diz como a
/// máquina estava naquela hora: verde na tomada, âmbar na bateria, mais forte quanto mais carga
/// havia; a casa vazia é a hora em que ela estava desligada ou dormindo. Desenhado à mão pelo
/// mesmo motivo de lá: são 168 casas, e 168 bordas no XAML pesariam num cartão que abre a cada
/// clique.
/// </summary>
public sealed class BatteryGrid : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<BatterySample>), typeof(BatteryGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BatteryGrid)d)._cells = null));

    public IReadOnlyList<BatterySample>? Samples
    {
        get => (IReadOnlyList<BatterySample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private const double Cell = 9, Gap = 2, Step = Cell + Gap;
    private const double Labels = 28;   // coluna dos dias
    private const double Hours = 13;    // faixa das horas, em cima
    private const int Days = 7;

    /// <summary>Uma hora do quadriculado: a carga, e se estava na tomada. Nula: desligada ou dormindo.</summary>
    private sealed record Slot(DateTime Hour, double Percent, bool Ac);

    private Slot?[,]? _cells;

    private static readonly Brush Empty = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Label = Frozen(Color.FromRgb(0x9D, 0x9D, 0x9D));
    private static readonly Color Green = Color.FromRgb(0x7F, 0xD1, 0x8B);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xC8, 0x57);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public BatteryGrid()
    {
        ToolTipService.SetInitialShowDelay(this, 100);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTip = "";   // sem isto o ToolTipService nem tenta abrir; o texto vem no OnMouseMove
    }

    protected override Size MeasureOverride(Size available) =>
        new(Labels + 24 * Step - Gap, Hours + Days * Step - Gap);

    /// <summary>
    /// A carga no meio de cada hora: interpolada entre o registro de antes e o de depois, como
    /// a linha do gráfico do Windows faz. A hora só fica vazia se a máquina dormiu nela inteira.
    /// </summary>
    private Slot?[,] Build()
    {
        var cells = new Slot?[Days, 24];
        var amostras = (Samples ?? []).OrderBy(s => s.Time).ToList();
        if (amostras.Count == 0) return cells;

        var agora = DateTime.Now;
        var primeiroDia = agora.Date.AddDays(-(Days - 1));

        for (var d = 0; d < Days; d++)
            for (var h = 0; h < 24; h++)
            {
                var hora = primeiroDia.AddDays(d).AddHours(h);
                if (hora > agora) continue;   // as horas de hoje que ainda não chegaram

                // os trechos que tocam esta hora: basta um acordado para a casa ter cor, e basta um
                // na bateria para ela ser âmbar — olhar só o meio da hora perdia os quarenta minutos
                // fora da tomada de 29/09, que começaram às 9h46
                var fimDaHora = hora.AddHours(1);
                var acordado = false;
                var naBateria = false;
                for (var j = 0; j < amostras.Count; j++)
                {
                    var de = amostras[j].Time;
                    var ate = j + 1 < amostras.Count ? amostras[j + 1].Time : agora;
                    if (de >= fimDaHora || ate <= hora || !amostras[j].Awake) continue;
                    acordado = true;
                    if (!amostras[j].Ac) naBateria = true;
                }
                if (!acordado) continue;

                // a carga, no meio da hora (ou agora, na hora corrente), entre o registro de antes e o de depois
                var meio = hora.AddMinutes(30) > agora ? agora : hora.AddMinutes(30);
                var i = amostras.FindLastIndex(s => s.Time <= meio);
                if (i < 0) i = 0;
                var a = amostras[i];
                var b = i + 1 < amostras.Count ? amostras[i + 1] : null;
                var carga = a.Percent;
                if (b is not null && b.Time > a.Time && meio > a.Time)
                    carga += (b.Percent - a.Percent) * (meio - a.Time).TotalMilliseconds / (b.Time - a.Time).TotalMilliseconds;

                cells[d, h] = new Slot(hora, Math.Clamp(carga, 0, 100), !naBateria);
            }

        return cells;
    }

    /// <summary>Quatro tons, como os níveis do GitHub: a carga em faixas de 25%.</summary>
    private static Brush Paint(Slot s)
    {
        var nivel = s.Percent switch { < 25 => 0.35, < 50 => 0.55, < 75 => 0.78, _ => 1.0 };
        var cor = s.Ac ? Green : Amber;
        return Frozen(Color.FromArgb((byte)(nivel * 255), cor.R, cor.G, cor.B));
    }

    protected override void OnRender(DrawingContext dc)
    {
        _cells ??= Build();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var primeiroDia = DateTime.Now.Date.AddDays(-(Days - 1));

        foreach (var h in new[] { 0, 6, 12, 18 })
            Text(dc, $"{h}h", Labels + h * Step, -1, dpi);

        for (var d = 0; d < Days; d++)
        {
            var dia = primeiroDia.AddDays(d);
            var nome = d == Days - 1 ? "hoje" : dia.ToString("ddd", PtBr).TrimEnd('.');
            Text(dc, nome, 0, Hours + d * Step - 2, dpi);

            for (var h = 0; h < 24; h++)
            {
                var hora = dia.AddHours(h);
                if (hora > DateTime.Now) continue;

                var slot = _cells[d, h];
                dc.DrawRoundedRectangle(slot is null ? Empty : Paint(slot), null,
                    new Rect(Labels + h * Step, Hours + d * Step, Cell, Cell), 2, 2);
            }
        }
    }

    /// <summary>A hora sob o ponteiro, para a dica: "qui 01/10, 14h — 100%, na tomada".</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _cells ??= Build();

        var p = e.GetPosition(this);
        var h = (int)((p.X - Labels) / Step);
        var d = (int)((p.Y - Hours) / Step);
        string dica;

        if (p.X < Labels || p.Y < Hours || h is < 0 or > 23 || d is < 0 or >= Days)
            dica = "Cada casa é uma hora: verde na tomada, âmbar na bateria, mais forte com mais carga";
        else
        {
            var hora = DateTime.Now.Date.AddDays(-(Days - 1) + d).AddHours(h);
            var quando = hora.ToString("ddd dd/MM, H'h'", PtBr).Replace(".", "");
            dica = _cells[d, h] is { } s
                ? $"{quando} — {s.Percent:0}%, {(s.Ac ? "na tomada" : "na bateria")}"
                : hora > DateTime.Now ? quando : $"{quando} — desligado ou dormindo";
        }

        if (!Equals(ToolTip, dica)) ToolTip = dica;
    }

    private static void Text(DrawingContext dc, string s, double x, double y, double dpi) =>
        dc.DrawText(new FormattedText(s, PtBr, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                                      9.5, Label, dpi), new Point(x, y));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
