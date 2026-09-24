using System.Windows;
using System.Windows.Controls;

namespace WinDock.Views;

/// <summary>
/// O cartão da cota de IA: quem está logado, o plano, e uma cápsula por janela de uso.
///
/// O <c>DataContext</c> é o <c>PanelModel</c>, como no resto da barra — mas os bindings são
/// por nome, então um harness pode montá-lo com qualquer objeto que tenha as mesmas
/// propriedades. É o que permite conferir o desenho sem abrir o cartão na tela de ninguém.
/// </summary>
public partial class AiUsageCard : UserControl
{
    public AiUsageCard() => InitializeComponent();

    /// <summary>
    /// Dá ao preenchimento da cápsula a largura da porcentagem.
    ///
    /// Em XAML isto seria uma conta entre a largura do trilho e um valor do item, e não há
    /// como multiplicar dois números num binding sem um conversor de vários valores. O evento
    /// de mudança de tamanho resolve com o que o layout já calculou — e é chamado de novo
    /// sozinho quando o cartão muda de largura.
    /// </summary>
    /// <summary>
    /// A seta do rodapé: alterna entre o cartão compacto e o detalhado.
    ///
    /// Chama o modelo por reflection pelo mesmo motivo que o medidor: o harness que confere o
    /// desenho monta este controle com um objeto de mentira, e um `cast` para `PanelModel`
    /// derrubaria o harness em vez de alternar.
    /// </summary>
    private void OnToggleExpand(object sender, RoutedEventArgs e) =>
        DataContext?.GetType().GetMethod("ToggleAiExpanded")?.Invoke(DataContext, null);

    private void OnGaugeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border trilho || trilho.Child is not Border preenchimento) return;

        // o item pode ser o AiGauge de verdade ou o de um harness: o que importa é ter Percent
        var percent = trilho.DataContext?.GetType().GetProperty("Percent")?.GetValue(trilho.DataContext);
        if (percent is not double valor) return;

        preenchimento.Width = Math.Max(0, trilho.ActualWidth * Math.Clamp(valor, 0, 100) / 100);
    }
}
