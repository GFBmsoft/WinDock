using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDock.Services;

/// <summary>
/// Os ícones desenhados dos fixados (<see cref="DockConfig.DrawnIcons"/>): um glifo colorido no
/// lugar do ícone do programa, no estilo dos programas do próprio usuário — GRepos, GTest, GTerm,
/// GConv, GRemote, GSQL, que os <c>tools\gerar-icone.ps1</c> deles desenham assim.
///
/// <para>Pedido de 03/11/2026, e escolhido numa prévia com os fixados de verdade: o desenho sem a
/// moldura escura dos irmãos, que aparece só com o mouse em cima (é o realce do botão, no
/// <c>MainWindow.xaml</c>). A moldura em volta do ícone do próprio app encolhia o desenho para uns
/// 13 px no tamanho da dock e o tirava da leitura.</para>
///
/// <para>Duas fontes, e não uma. Os glifos do Windows (Segoe Fluent Icons) são de traço, como os
/// desenhos dos irmãos, mas não têm marca nenhuma; os logos (Chrome, VS Code, Discord, Spotify)
/// vêm de uma Nerd Font, e são cheios. Ela está instalada só no usuário, e o WPF a acha pelo nome.
/// App sem desenho conhecido, ou numa máquina sem a fonte, fica com o ícone do programa.</para>
/// </summary>
public static class IconDrawing
{
    /// <summary>O desenho que vale para um botão: a fonte, o caractere e a cor.</summary>
    public sealed record Look(FontFamily Font, string Text, Brush Fill);

    /// <summary>
    /// O que este botão desenha: o glifo escolhido nas Configurações, senão o da tabela. Nulo
    /// quando não há desenho, e o botão fica com o ícone do programa.
    /// </summary>
    public static Look? For(string id, DrawnIconChoice? escolha)
    {
        var conhecido = Known.TryGetValue(KeyOf(id), out var k) ? k : ((bool Nerd, int Code, string Color)?)null;

        string? texto = null;
        FontFamily? fonte = null;

        // o seletor só oferece glifos do Windows
        if (ToolsService.ParseGlyph(escolha?.Glyph) is { } escolhido)
            (texto, fonte) = (escolhido, Segoe(char.ConvertToUtf32(escolhido, 0)));
        else if (conhecido is { } c)
            (texto, fonte) = (char.ConvertFromUtf32(c.Code), c.Nerd ? Nerd(c.Code) : Segoe(c.Code));

        if (texto is null || fonte is null) return null;

        var cor = !string.IsNullOrWhiteSpace(escolha?.Color) ? escolha!.Color : conhecido?.Color ?? Cinza;
        return new Look(fonte, texto, Brush(cor));
    }

    /// <summary>
    /// O ícone desenhado do botão, ou nulo quando ele não tem desenho. Os perfis do Chrome têm o
    /// mesmo logo, então levam a inicial do perfil num selo, no canto.
    /// </summary>
    public static BitmapSource? Draw(string id, DrawnIconChoice? escolha)
    {
        if (For(id, escolha) is not { } desenho) return null;

        var inicial = ChromeProfiles.ByAumid(id) is { Name.Length: > 0 } perfil
            ? perfil.Name[..1].ToUpper(CultureInfo.CurrentCulture)
            : string.Empty;

        var chave = $"{desenho.Font.Source}|{desenho.Text}|{desenho.Fill}|{inicial}";
        if (Feitos.TryGetValue(chave, out var pronto)) return pronto;

        var imagem = Render(desenho, inicial);
        if (Feitos.Count > 200) Feitos.Clear();
        Feitos[chave] = imagem;
        return imagem;
    }

    /// <summary>Um por desenho: o mesmo app fixado e aberto, ou dois perfis com a mesma inicial.</summary>
    private static readonly Dictionary<string, BitmapSource> Feitos = new();

    /// <summary>
    /// Em 64 px, o maior tamanho que a dock costuma pedir (ícone de até 64 nas Configurações), e
    /// o desenho ocupa quase o quadrado inteiro, como os ícones depois do <c>IconService.Fill</c>.
    /// </summary>
    private const int Lado = 64;
    private const double Ocupa = 0.86;

    private static BitmapSource Render(Look desenho, string inicial)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var texto = new FormattedText(desenho.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          new Typeface(desenho.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                          Lado, desenho.Fill, 1.0);

            // centrado pelo desenho, e não pela caixa da letra: cada glifo tem a sua folga
            var forma = texto.BuildGeometry(new Point(0, 0));
            var caixa = forma.Bounds;
            if (!caixa.IsEmpty && caixa.Width > 0 && caixa.Height > 0)
            {
                var escala = Lado * Ocupa / Math.Max(caixa.Width, caixa.Height);
                dc.PushTransform(new TranslateTransform(Lado / 2.0, Lado / 2.0));
                dc.PushTransform(new ScaleTransform(escala, escala));
                dc.PushTransform(new TranslateTransform(-caixa.X - caixa.Width / 2, -caixa.Y - caixa.Height / 2));
                dc.DrawGeometry(desenho.Fill, null, forma);
                dc.Pop(); dc.Pop(); dc.Pop();
            }

            if (inicial.Length > 0)
            {
                var raio = Lado * 0.28;
                var centro = new Point(Lado - raio, Lado - raio);
                dc.DrawEllipse(Brush("#FFFFC857"), new Pen(Brush("#FF1B1F2A"), Lado * 0.05), centro, raio, raio);

                var letra = new FormattedText(inicial, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                              new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                                              raio * 1.35, Brush("#FF1B1F2A"), 1.0);
                dc.DrawText(letra, new Point(centro.X - letra.Width / 2, centro.Y - letra.Height / 2));
            }
        }

        var imagem = new RenderTargetBitmap(Lado, Lado, 96, 96, PixelFormats.Pbgra32);
        imagem.Render(visual);
        imagem.Freeze();
        return imagem;
    }

    /// <summary>
    /// A chave da tabela: o nome do executável, ou o AppUserModelID de um app da Store. Os perfis
    /// do Chrome (<c>Chrome</c>, <c>Chrome.UserData.Profile1</c>) são todos o Chrome.
    /// </summary>
    private static string KeyOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        if (ChromeProfiles.ByAumid(id) is not null) return "chrome";
        return id.Contains('\\') ? Path.GetFileNameWithoutExtension(id) : id;
    }

    /// <summary>
    /// Os desenhos de fábrica, os mesmos da prévia que o usuário aprovou. As cores são as das
    /// ferramentas da barra (<c>ToolsService.Colors</c>) e as dos irmãos; Nerd marca os logos.
    /// </summary>
    private static readonly Dictionary<string, (bool Nerd, int Code, string Color)> Known =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"]          = (true, 0xF268, "#FF6CB6FF"),
        ["code"]            = (true, 0xF0A1E, "#FF6CB6FF"),
        ["discord"]         = (true, 0xF066F, "#FF8C9EFF"),
        ["spotify"]         = (true, 0xF1BC, "#FF7FD18B"),
        ["SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"] = (true, 0xF1BC, "#FF7FD18B"),
        ["obsidian"]        = (true, 0xF01C8, "#FFB08AE8"),
        ["mdbsys"]          = (true, 0xF01BC, "#FFF0883E"),   // banco, como os cilindros do GConv
        ["windowsterminal"] = (false, 0xE756, "#FF7FD18B"),
        ["wt"]              = (false, 0xE756, "#FF7FD18B"),
        ["cmd"]             = (false, 0xE756, "#FFC8C8CC"),
        ["powershell"]      = (false, 0xE756, "#FF6CB6FF"),
        ["pwsh"]            = (false, 0xE756, "#FF6CB6FF"),
        ["explorer"]        = (false, 0xE8B7, "#FFFFC857"),
        ["bds"]             = (false, 0xE943, "#FFFF6B6B"),   // Delphi
        ["notepad++"]       = (false, 0xE70F, "#FF7FD18B"),
        ["notepad"]         = (false, 0xE70F, "#FFC8C8CC"),
        ["gridcraft"]       = (false, 0xE80A, "#FFB08AE8"),
        ["taskmgr"]         = (false, 0xE9D9, "#FFF4A261"),
        ["Microsoft.WindowsStore_8wekyb3d8bbwe!App"] = (false, 0xE719, "#FF6CB6FF"),
        ["windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"] = (false, 0xE713, "#FFC8C8CC"),
        ["systemsettings"]  = (false, 0xE713, "#FFC8C8CC"),
    };

    /// <summary>A cor de um glifo escolhido para um app que a tabela não conhece.</summary>
    private const string Cinza = "#FFC8C8CC";

    private static readonly string[] SegoeNames = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>A Nerd Font desta máquina é a CaskaydiaCove; as outras são as que costumam vir com terminais.</summary>
    private static readonly string[] NerdNames =
        ["CaskaydiaCove Nerd Font", "CaskaydiaMono Nerd Font", "JetBrainsMono Nerd Font", "Hack Nerd Font",
         "FiraCode Nerd Font", "Symbols Nerd Font"];

    private static FontFamily? Segoe(int code) => WithGlyph(SegoeNames, code);
    private static FontFamily? Nerd(int code) => WithGlyph(NerdNames, code);

    /// <summary>
    /// A primeira fonte que tem o glifo. Pergunta ao mapa de caracteres porque pedir uma fonte que
    /// não existe não falha: o WPF entrega a Segoe UI no lugar, e ela não tem nada nesses códigos.
    /// </summary>
    private static FontFamily? WithGlyph(string[] nomes, int code)
    {
        foreach (var nome in nomes)
        {
            var familia = Families.TryGetValue(nome, out var f) ? f : Families[nome] = new FontFamily(nome);
            var tipo = new Typeface(familia, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tipo.TryGetGlyphTypeface(out var glifos) && glifos.CharacterToGlyphMap.ContainsKey(code)) return familia;
        }
        return null;
    }

    private static readonly Dictionary<string, FontFamily> Families = new();

    private static Brush Brush(string hex)
    {
        Color cor;
        try { cor = (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { cor = (Color)ColorConverter.ConvertFromString(Cinza)!; }

        var pincel = new SolidColorBrush(cor);
        pincel.Freeze();
        return pincel;
    }
}
