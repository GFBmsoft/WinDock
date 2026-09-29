using System.Windows;
using System.Windows.Media;

namespace WinDock.Ui;

/// <summary>
/// O gráfico miúdo do cartão de sistema: os últimos 60 segundos de uma medida, como uma
/// linha com a área preenchida por baixo.
///
/// Desenhado à mão, como o resto dos controles do projeto — trazer uma biblioteca de
/// gráficos para quatro linhas de 60 pontos seria uma dependência inteira por causa de
/// nada. O caminho é geometria pura: um <c>StreamGeometry</c> por quadro, que o WPF
/// congela e joga fora; não há elemento visual por ponto.
///
/// <para><b>Vetor novo a cada amostra.</b> O binding só redesenha quando a referência muda:
/// mexer no conteúdo do mesmo vetor não avisa ninguém, e o gráfico ficaria parado com os
/// números certos por baixo. Quem fornece os dados manda uma cópia (veja
/// <c>SystemStatsService.Snapshot</c>).</para>
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>
    /// O teto da escala. Zero quer dizer "o maior valor da série manda".
    ///
    /// CPU e memória fixam em 100, senão uma máquina parada desenharia o ruído de 2% como
    /// uma montanha. A rede fica no automático: não existe teto conhecido, e fixar um faria
    /// toda navegação normal virar uma linha rente ao chão.
    /// </summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Quantos pontos a linha guarda, mesmo que a série ainda esteja enchendo.</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var valores = Values;
        var largura = ActualWidth;
        var altura = ActualHeight;

        // o trilho: a caixa do gráfico existe mesmo antes da primeira amostra, senão o
        // cartão cresce um pouquinho quando os dados chegam
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                                null, new Rect(0, 0, largura, altura), 3, 3);

        if (valores is null || valores.Count < 2 || largura <= 0 || altura <= 0) return;

        var teto = Maximum > 0 ? Maximum : Math.Max(1, valores.Max());
        var passo = largura / Math.Max(1, Capacity - 1);

        // a série entra pela direita: com menos de 60 amostras o gráfico enche da direita
        // para a esquerda, em vez de esticar meia dúzia de pontos por toda a largura
        double X(int i) => largura - (valores.Count - 1 - i) * passo;
        double Y(double v) => altura - Math.Clamp(v / teto, 0, 1) * (altura - 1) - 0.5;

        var linha = new StreamGeometry();
        using (var ctx = linha.Open())
        {
            ctx.BeginFigure(new Point(X(0), altura), isFilled: true, isClosed: false);
            ctx.LineTo(new Point(X(0), Y(valores[0])), isStroked: false, isSmoothJoin: false);

            for (var i = 1; i < valores.Count; i++)
                ctx.LineTo(new Point(X(i), Y(valores[i])), isStroked: true, isSmoothJoin: false);

            ctx.LineTo(new Point(X(valores.Count - 1), altura), isStroked: false, isSmoothJoin: false);
        }
        linha.Freeze();

        // a área por baixo é a mesma cor da linha, quase apagada: dá volume sem competir
        // com o texto que fica ao lado
        var area = Stroke.Clone();
        area.Opacity = 0.18;
        area.Freeze();

        dc.DrawGeometry(area, new Pen(Stroke, 1.2) { LineJoin = PenLineJoin.Round }, linha);
    }
}
