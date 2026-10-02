using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinDock.Services;

/// <summary>
/// As pastas do botão de locais da barra: as de fábrica, o abrir no Explorador, o aviso de
/// quando a pasta não existe e o desenho de cada uma.
///
/// O desenho e a cor seguem o idioma das ferramentas (<see cref="ToolsService"/>): glifo da
/// Segoe numa cor, com o mesmo seletor nas Configurações.
/// </summary>
public static class PlacesService
{
    /// <summary>
    /// As de fábrica: as pastas do usuário, na ordem do menu de lugares do GNOME (EV27) — a
    /// pessoal primeiro, depois as que o Windows cria para cada um.
    ///
    /// O caminho é o de verdade, resolvido pelo Windows, e não "%USERPROFILE%\Documents": com o
    /// OneDrive a pasta Documentos mora dentro dele, e em Windows em português a pasta pode ter
    /// sido movida para outro disco pelas propriedades.
    /// </summary>
    public static List<PlaceEntry> Defaults()
    {
        var lista = new List<PlaceEntry>();

        void Add(string nome, string? caminho)
        {
            if (!string.IsNullOrEmpty(caminho)) lista.Add(new PlaceEntry { Name = nome, Path = caminho });
        }

        Add("Pasta pessoal", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add("Área de trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", Downloads());
        Add("Músicas", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        return lista;
    }

    /// <summary>
    /// Abre a pasta no Explorador e devolve o motivo, quando não abre — o mesmo contrato do
    /// <see cref="ToolsService.Run"/>: o cartão fica aberto e diz por quê.
    /// </summary>
    public static string? Open(PlaceEntry place)
    {
        Log.Trace($"local: {place.Name} → {place.Path}");

        var caminho = Expand(place.Path);
        if (caminho.Length == 0) return "o caminho está vazio.";
        if (Problem(caminho) is { } antes) return antes;

        try
        {
            // Pelo explorer.exe, e não pelo shell direto na pasta: uma pasta com o nome de um
            // programa ("D:\Ferramentas\setup") seria lida como o programa. Aspas só com espaço,
            // e sem a barra do fim — em "C:\" ela escaparia a aspa de fechamento.
            var arg = caminho.Contains(' ') ? $"\"{caminho.TrimEnd('\\')}\"" : caminho;
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = arg, UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"local {place.Name} não abriu", ex);
            return ex.Message;
        }
    }

    /// <summary>Por que a pasta não vai abrir, quando dá para saber antes — nulo se ela existe.</summary>
    public static string? Problem(string? path)
    {
        var caminho = Expand(path);
        if (caminho.Length == 0) return null;

        // endereço do shell ("shell:Downloads", "::{GUID}"): quem resolve é o Explorador
        if (caminho.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || caminho.StartsWith("::")) return null;

        // Pasta de rede desconectada leva segundos para responder "não existe" — e esta conta
        // roda na tela das Configurações a cada letra digitada. Para a rede, quem avisa é o clique.
        if (caminho.StartsWith(@"\\")) return null;

        try
        {
            if (Directory.Exists(caminho)) return null;
            if (File.Exists(caminho)) return "isto é um arquivo, não uma pasta.";
        }
        catch { /* caminho torto, digitado à mão: cai no aviso abaixo */ }

        return $"a pasta {caminho} não existe.";
    }

    private static string Expand(string? path) =>
        Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));

    /// <summary>
    /// O desenho de uma linha: o escolhido nas Configurações, ou o da pasta conhecida — a casa
    /// para a pessoal, a seta para os downloads —, ou a pasta amarela.
    /// </summary>
    public static (string Glyph, Brush Fill) Look(PlaceEntry place)
    {
        var (glifo, cor) = Auto(place.Path);

        if (ToolsService.ParseGlyph(place.Glyph) is { } escolhido) glifo = escolhido;
        if (!string.IsNullOrWhiteSpace(place.Color)) cor = place.Color;

        return (glifo, Frozen(cor));
    }

    private static (string Glyph, string Color) Auto(string? path)
    {
        var caminho = Expand(path).TrimEnd('\\');
        if (caminho.Length == 0) return (Folder, Yellow);

        foreach (var (pasta, visual) in Known())
            if (string.Equals(pasta, caminho, StringComparison.OrdinalIgnoreCase)) return visual;

        if (caminho.StartsWith(@"\\")) return ("\uE8CE", "#FF4FD1C5");   // pasta de rede, verde-água
        if (caminho.Length <= 2 && caminho.EndsWith(':')) return ("\uEDA2", "#FF7FD18B");   // disco inteiro

        return (Folder, Yellow);
    }

    private const string Folder = "\uE8B7";
    private const string Yellow = "#FFFFC857";

    /// <summary>As pastas do usuário e o desenho de cada uma, resolvidas uma vez por consulta.</summary>
    private static IEnumerable<(string Path, (string Glyph, string Color) Look)> Known()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ("\uE80F", "#FF6CB6FF"));       // casa, azul
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ("\uE7F4", "#FFC8C8CC")); // tela
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ("\uE8A5", "#FF6CB6FF"));      // documento
        yield return (Downloads() ?? "", ("\uE896", "#FF7FD18B"));                                                     // seta para baixo, verde
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ("\uE8D6", "#FFB08AE8"));          // nota, lilás
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ("\uE91B", "#FF4FD1C5"));       // foto
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), ("\uE714", "#FFF4A261"));         // filme, laranja
    }

    /// <summary>
    /// A pasta de downloads, que o <see cref="Environment.SpecialFolder"/> não tem: pelo
    /// identificador dela no shell, e não "perfil\Downloads" — ela também pode ter sido movida.
    /// </summary>
    private static string? Downloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                var caminho = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return caminho;
            }
        }
        catch { }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static readonly Dictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    private static Brush Frozen(string hex)
    {
        if (Brushes.TryGetValue(hex, out var pronto)) return pronto;

        Brush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)); }
        brush.Freeze();
        return Brushes[hex] = brush;
    }
}
