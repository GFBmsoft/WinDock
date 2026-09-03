using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinDock.Ui;

/// <summary>
/// Faz os icones deslizarem ate a posicao nova em vez de pularem para ela.
///
/// A ideia e a de sempre para animar layout: anota onde cada um esta, deixa a mudanca
/// acontecer, e entao empurra cada icone de volta para onde estava e solta — a animacao
/// que leva de volta ao lugar certo e o que a pessoa ve. As posicoes vem do slot de
/// layout, e nao da tela, para nao somar a translacao de uma animacao ainda em curso.
/// </summary>
public static class Reorder
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

    public static void Animate(ItemsControl host, Action change)
    {
        var before = Slots(host);

        change();
        host.UpdateLayout();      // precisa do layout novo ja calculado para medir o depois

        foreach (var (element, now) in Slots(host))
        {
            if (!before.TryGetValue(element, out var old)) continue;

            var dx = old.X - now.X;
            var dy = old.Y - now.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            Slide(element, dx, dy);
        }
    }

    private static Dictionary<FrameworkElement, Point> Slots(ItemsControl host)
    {
        var slots = new Dictionary<FrameworkElement, Point>();

        for (var i = 0; i < host.Items.Count; i++)
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                slots[fe] = LayoutInformation.GetLayoutSlot(fe).TopLeft;

        return slots;
    }

    private static void Slide(FrameworkElement element, double dx, double dy)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            element.RenderTransform = transform;
        }

        // desacelerando no fim: o movimento parece acompanhar o mouse, e nao um relogio
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        transform.BeginAnimation(TranslateTransform.XProperty,
                                 new DoubleAnimation(dx, 0, Duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty,
                                 new DoubleAnimation(dy, 0, Duration) { EasingFunction = ease });
    }
}
