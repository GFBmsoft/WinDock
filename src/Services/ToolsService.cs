using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinDock.Services;

/// <summary>
/// Os comandos do botão de ferramentas da barra: o desenho de cada um, o disparo e o aviso de
/// quando não vai dar certo.
///
/// Rodar é pelo shell, como o Win+R — é ele que sabe abrir um <c>.msc</c> no console certo e um
/// <c>.cpl</c> no painel de controle. A dock roda elevada, então o que sai daqui também sai
/// elevado: é o que se quer de um gerenciador de dispositivos ou de uma limpeza de disco, que de
/// outro jeito pediriam o UAC de novo.
/// </summary>
public static class ToolsService
{
    /// <summary>
    /// Roda o comando e devolve o motivo, quando não roda.
    ///
    /// <para>Já foi o <see cref="AppCatalog.Run"/>, que engole o erro — certo para a busca, que
    /// fecha de qualquer jeito, e errado aqui: em 01/10/2026 o usuário apontou para
    /// <c>Revo\revo.exe</c>, que não existe (o da pasta é <c>RevoUPort.exe</c>), e o clique não
    /// fez nada nem disse nada.</para>
    ///
    /// <para>Um executável com caminho abre <b>na própria pasta</b>, e não no perfil do usuário:
    /// programa portátil procura configuração e idioma ao lado de si, pela pasta de trabalho.</para>
    /// </summary>
    public static string? Run(ToolCommand tool)
    {
        Log.Trace($"ferramenta: {tool.Name} → {tool.Command}");

        var comando = Environment.ExpandEnvironmentVariables(tool.Command.Trim());
        if (comando.Length == 0) return "o comando está vazio.";

        if (Problem(comando) is { } antes) return antes;

        var (file, args) = AppCatalog.Split(comando);
        var pasta = Path.IsPathRooted(file) && File.Exists(file)
            ? Path.GetDirectoryName(file)!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = pasta
            });
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"ferramenta {tool.Name} não abriu", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// Por que este comando não vai abrir, quando dá para saber sem rodá-lo: o arquivo do caminho
    /// não existe, ou o nome solto não está em lugar nenhum onde o Executar procuraria.
    ///
    /// Quando o caminho erra só o nome do arquivo, a pasta existe — e aí a mensagem diz quais
    /// executáveis há nela, que é a pergunta seguinte de quem errou o nome.
    /// </summary>
    public static string? Problem(string command)
    {
        command = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
        if (command.Length == 0) return null;

        var (file, _) = AppCatalog.Split(command);

        // endereço ("ms-settings:", "https://"): quem resolve é o shell, e não há o que conferir
        if (file.Contains(':') && !Path.IsPathRooted(file)) return null;
        if (file.Contains("://")) return null;

        if (Path.IsPathRooted(file))
        {
            if (File.Exists(file) || Directory.Exists(file)) return null;

            var pasta = Path.GetDirectoryName(file);
            if (pasta is not null && Directory.Exists(pasta))
            {
                var exes = Directory.EnumerateFiles(pasta, "*.exe").Select(Path.GetFileName).Take(4).ToList();
                return exes.Count == 0
                    ? $"não achei {Path.GetFileName(file)}, e a pasta não tem nenhum .exe."
                    : $"não achei {Path.GetFileName(file)}. Na pasta há: {string.Join(", ", exes)}.";
            }

            return $"não achei {file}.";
        }

        return Resolve(file) is null && !InAppPaths(file)
            ? $"o Windows não acha \"{file}\" — nem na pasta do sistema, nem no PATH."
            : null;
    }

    /// <summary>
    /// O caminho completo de um nome solto ("devmgmt.msc", "mstsc"), procurado como o Executar
    /// procura: na pasta do sistema, na do Windows e no PATH, completando a extensão quando não
    /// vem nenhuma.
    /// </summary>
    private static string? Resolve(string name)
    {
        var pastas = new List<string>
        {
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        pastas.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries));

        string[] nomes = Path.HasExtension(name)
            ? [name]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => name + ext.ToLowerInvariant())
                .ToArray();

        foreach (var pasta in pastas)
            foreach (var nome in nomes)
            {
                try
                {
                    var caminho = Path.Combine(pasta.Trim(), nome);
                    if (File.Exists(caminho)) return caminho;
                }
                catch { /* entrada torta no PATH */ }
            }

        return null;
    }

    /// <summary>"chrome", "excel": o Executar também acha quem se registrou em App Paths.</summary>
    private static bool InAppPaths(string name)
    {
        var exe = Path.HasExtension(name) ? name : name + ".exe";
        const string chave = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(chave + exe);
            using var hklm = Registry.LocalMachine.OpenSubKey(chave + exe);
            return hkcu is not null || hklm is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// O desenho de uma linha do cartão: um glifo da Segoe numa cor, no mesmo idioma do chip e do
    /// pente de memória da barra.
    ///
    /// Começou com o ícone do executável, e o usuário pediu para trocar (01/10/2026): os ícones
    /// do Windows para <c>cleanmgr</c>, <c>mmc</c> e <c>mstsc</c> são desenhos coloridos de outra
    /// época, e lado a lado num cartão escuro pareciam colados de outro programa.
    ///
    /// Vale o que a pessoa escolheu nas Configurações (<see cref="ToolCommand.Glyph"/> e
    /// <see cref="ToolCommand.Color"/>); o que ela deixou em automático sai da tabela, pelo que o
    /// comando abre — o programa ou, num <c>control inetcpl.cpl</c>, o painel. Fora da tabela, um
    /// executável com caminho ganha o desenho de aplicativo, e o resto o do prompt, em cinza.
    /// </summary>
    public static (string Glyph, Brush Fill) Look(ToolCommand tool)
    {
        var (glifo, cor) = Auto(tool.Command);

        if (ParseGlyph(tool.Glyph) is { } escolhido) glifo = escolhido;
        if (!string.IsNullOrWhiteSpace(tool.Color)) cor = tool.Color;

        return (glifo, Frozen(cor));
    }

    /// <summary>"EA99" → o caractere do glifo; qualquer outra coisa → nenhum.</summary>
    public static string? ParseGlyph(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) &&
        int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var codigo) &&
        codigo is >= 0xE000 and <= 0xF8FF
            ? char.ConvertFromUtf32(codigo)
            : null;

    private static (string Glyph, string Color) Auto(string command)
    {
        var palavras = (command ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.GetFileNameWithoutExtension(p.Trim('"')))
            .ToList();

        // o argumento vem antes do programa: em "control.exe inetcpl.cpl" quem diz o que abre é o .cpl
        foreach (var nome in Enumerable.Reverse(palavras))
            if (Known.TryGetValue(nome, out var visual)) return visual;

        var (file, _) = AppCatalog.Split((command ?? "").Trim());
        return Path.IsPathRooted(file) ? ("", "#FFC8C8CC") : ("", "#FFC8C8CC");
    }

    private static readonly Dictionary<string, (string Glyph, string Color)> Known = new()
    {
        ["cleanmgr"]   = ("", "#FF7FD18B"),   // vassoura, verde
        ["inetcpl"]    = ("", "#FF6CB6FF"),   // globo, azul
        ["devmgmt"]    = ("", "#FFB08AE8"),   // dispositivos, lilás
        ["mstsc"]      = ("", "#FF4FD1C5"),   // dois aparelhos ligados, verde-água
        ["services"]   = ("", "#FFF4A261"),   // engrenagens, laranja
        ["taskmgr"]    = ("", "#FFF4A261"),   // diagnóstico, laranja
        ["eventvwr"]   = ("", "#FFFFC857"),   // histórico
        ["regedit"]    = ("", "#FF6CB6FF"),   // código
        ["compmgmt"]   = ("", "#FFB08AE8"),   // computador
        ["diskmgmt"]   = ("", "#FF7FD18B"),   // disco
        ["sysdm"]      = ("", "#FFB08AE8"),
        ["appwiz"]     = ("", "#FF6CB6FF"),   // todos os apps
        ["ncpa"]       = ("", "#FF6CB6FF"),   // rede
        ["firewall"]   = ("", "#FFFF6B6B"),   // escudo
        ["wf"]         = ("", "#FFFF6B6B"),
        ["gpedit"]     = ("", "#FFF4A261"),   // chave
        ["control"]    = ("", "#FFC8C8CC"),   // configurações
        ["explorer"]   = ("", "#FFFFC857"),   // pasta
        ["cmd"]        = ("", "#FFC8C8CC"),
        ["powershell"] = ("", "#FF6CB6FF"),
        ["pwsh"]       = ("", "#FF6CB6FF"),
        ["wt"]         = ("", "#FFC8C8CC"),
        ["notepad"]    = ("", "#FFC8C8CC"),   // nota
        ["calc"]       = ("", "#FFC8C8CC"),   // calculadora
        ["revouport"]  = ("", "#FFFF6B6B"),   // desinstalador: lixeira, vermelho
    };

    /// <summary>
    /// Os desenhos do seletor das Configurações, em fileiras por assunto: limpeza e disco,
    /// máquina e rede, sistema, segurança e código, arquivos, e o resto.
    /// </summary>
    public static readonly string[] Glyphs =
    [
        "EA99", "E74D", "EDA2", "E977", "E772", "E703", "E774", "E839",
        "E9F5", "E713", "E90F", "E835", "E9D9", "E950", "E964", "E83D",
        "E8D7", "E72E", "E943", "E756", "EBE8", "E81C", "E896", "E7E8",
        "EC50", "ED25", "E8A5", "E70B", "E8EF", "E721", "E715", "E77B",
        "ECAA", "E768", "E8D6", "E8B9", "E790", "EA80", "E701", "EB51",
        // conversa, telefone, pessoas, sino, estrela, envelope aberto, arroba: os das notificações
        "E8BD", "E8F2", "E717", "E716", "EA8F", "E734", "E8C3", "E910",
        // casa, tela, foto, filme, biblioteca, pasta aberta, disco de rede, nuvem: os dos locais
        "E80F", "E7F4", "E91B", "E714", "E8F1", "E838", "E8CE", "E753",
    ];

    /// <summary>As cores do seletor — as mesmas da barra e dos gráficos.</summary>
    public static readonly string[] Colors =
    [
        "#FFC8C8CC", "#FF7FD18B", "#FF4FD1C5", "#FF6CB6FF",
        "#FFB08AE8", "#FFF4A261", "#FFFFC857", "#FFFF6B6B",
    ];

    private static readonly Dictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    private static Brush Frozen(string hex)
    {
        if (Brushes.TryGetValue(hex, out var pronto)) return pronto;

        Brush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch { brush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xCC)); }   // hex torto, digitado à mão
        brush.Freeze();
        return Brushes[hex] = brush;
    }
}
