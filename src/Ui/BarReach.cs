using System.Windows;

namespace WinDock.Ui;

/// <summary>
/// Liga o alcance do clique na coluna inteira do botão da barra de cima, da borda de cima da
/// tela até a de baixo da barra, como nos ícones da barra do Windows.
///
/// <para><b>Por que existe.</b> O botão é mais baixo que a barra (16 px numa barra de 24, nos
/// ícones compactos) e fica no meio dela; acima e abaixo dele o clique não era de ninguém. E a
/// barra é janela em camada: com a opacidade em 0, o pixel sem desenho tem alfa zero e o
/// Windows manda o clique para a janela de baixo — só o traço do ícone respondia, e era preciso
/// mirar em cima dele (relato de 27/10/2026, o mesmo da pílula da dock na
/// <see cref="Services.DockTheme.PillBrush"/>). A borda de cima é justamente onde o cursor para
/// quando é jogado para o alto.</para>
///
/// <para><b>Como.</b> Os templates dos botões da barra (<c>PanelButton</c>, <c>ClockButton</c>,
/// <c>TrayButton</c>) têm um retângulo de alfa 1, que não se vê, esticado para cima e para baixo
/// com margem negativa. O botão continua do tamanho de antes — os cartões se ancoram nele, e
/// esticá-lo os faria descer — e só o alcance cresce. Os mesmos estilos servem aos botões de
/// dentro dos cartões, onde esticar seria cobrir os vizinhos: por isso o retângulo só aparece
/// com esta propriedade, posta nas filas da barra e herdada por quem está nelas.</para>
/// </summary>
public static class BarReach
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(BarReach),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
