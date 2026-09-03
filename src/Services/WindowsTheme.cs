using System.Windows.Media;
using Microsoft.Win32;

namespace WinDock.Services;

/// <summary>
/// O tema que o Windows está usando agora: claro ou escuro, e a cor de destaque escolhida
/// pela pessoa.
///
/// A dock e o painel de configurações têm paleta própria (escura, em <c>src/Ui/Fluent.xaml</c>)
/// porque são a nossa barra — mas uma **caixa de diálogo** não é: ela aparece no meio da tela,
/// com a barra de título do sistema, e uma janela escura num Windows claro (ou o contrário)
/// denuncia na hora que não é do sistema. Por isso quem segue o tema daqui é a
/// <see cref="ConfirmWindow"/>.
///
/// Tudo é lido do registro, e não do WinRT (<c>UISettings</c>): a referência ao WinRT arrastaria
/// um alvo de plataforma novo no .csproj por dois valores que estão a uma chave de distância.
/// </summary>
public static class WindowsTheme
{
    private const string Personalize =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string AccentKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    /// <summary>
    /// O Windows está no tema escuro para aplicativos?
    ///
    /// <c>AppsUseLightTheme</c> vale para as janelas dos apps; existe uma chave irmã
    /// (<c>SystemUsesLightTheme</c>) que é só da barra de tarefas e do menu iniciar, e usá-la
    /// aqui daria a resposta errada para quem escolhe "escuro no sistema, claro nos apps".
    ///
    /// Sem a chave (instalação nova, política de empresa), o padrão do Windows 11 é o claro.
    /// </summary>
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Personalize);
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// A cor de destaque, no tom que o Windows usa em botão sobre fundo do tema atual.
    ///
    /// Não é a cor crua que a pessoa escolheu: o Windows guarda uma paleta de oito tons dela e
    /// usa um tom **claro** no tema escuro e um **escuro** no tema claro, para o texto do botão
    /// continuar legível.
    ///
    /// A paleta está em <c>AccentPalette</c>: 8 entradas de 4 bytes (RGB + um byte que é sempre
    /// zero), **do mais claro para o mais escuro** — Light3, Light2, Light1, a cor base, Dark1,
    /// Dark2, Dark3 e, na última, a cor pura escolhida. Conferido nesta máquina: a entrada 3
    /// bate com o valor de <c>AccentColorMenu</c>, que é a cor base de agora.
    ///
    /// Daí a escolha: Light2 no tema escuro e Dark1 no claro, que é o par que o próprio Windows
    /// usa nos botões de ação principal.
    /// </summary>
    public static Color Accent(bool dark)
    {
        // os tons padrão do Windows 11 quando a leitura falha — o azul de fábrica
        var fallback = dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AccentKey);
            if (key?.GetValue("AccentPalette") is not byte[] palette || palette.Length < 32)
                return fallback;

            var offset = dark ? 1 * 4 : 4 * 4;
            return Color.FromRgb(palette[offset], palette[offset + 1], palette[offset + 2]);
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Preto ou branco por cima da cor de destaque, pelo brilho dela.
    ///
    /// A conta é a luminância aproximada da recomendação do W3C. Um tom claro (o do tema
    /// escuro) pede texto preto; um tom escuro pede branco — que é exatamente o que o Windows
    /// faz nos botões dele.
    /// </summary>
    public static Color OnAccent(Color accent) =>
        (accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114) > 150
            ? Colors.Black
            : Colors.White;
}
