using System.IO;
using System.Windows.Media;

namespace WinDock.Services;

/// <summary>
/// Os comandos do botão de ferramentas da barra: o desenho de cada um e o disparo.
///
/// Rodar é o mesmo caminho do Win+R da busca (<see cref="AppCatalog.Run"/>), pelo shell — é ele
/// que sabe abrir um <c>.msc</c> no console certo e um <c>.cpl</c> no painel de controle. A dock
/// roda elevada, então o que sai daqui também sai elevado: é o que se quer de um gerenciador de
/// dispositivos ou de uma limpeza de disco, que de outro jeito pediriam o UAC de novo.
/// </summary>
public static class ToolsService
{
    public static void Run(ToolCommand tool)
    {
        Log.Trace($"ferramenta: {tool.Name} → {tool.Command}");
        AppCatalog.Run(tool.Command);
    }

    /// <summary>
    /// O desenho de uma linha do cartão: um glifo da Segoe numa cor, no mesmo idioma do chip e do
    /// pente de memória da barra.
    ///
    /// Começou com o ícone do executável, e o usuário pediu para trocar (01/10/2026): os ícones
    /// do Windows para <c>cleanmgr</c>, <c>mmc</c> e <c>mstsc</c> são desenhos coloridos de outra
    /// época, e lado a lado num cartão escuro pareciam colados de outro programa. O glifo é
    /// escolhido pelo que o comando abre — o programa ou, num <c>control inetcpl.cpl</c>, o
    /// painel —, e o que não está na tabela fica com o prompt de comando, em cinza.
    /// </summary>
    public static (string Glyph, Brush Fill) Look(string command)
    {
        var palavras = (command ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.GetFileNameWithoutExtension(p.Trim('"')))
            .ToList();

        // o argumento vem antes do programa: em "control.exe inetcpl.cpl" quem diz o que abre é o .cpl
        foreach (var nome in Enumerable.Reverse(palavras))
            if (Known.TryGetValue(nome, out var visual)) return (visual.Glyph, Frozen(visual.Color));

        return ("\uE756", Frozen("#FFC8C8CC"));
    }

    private static readonly Dictionary<string, (string Glyph, string Color)> Known = new()
    {
        ["cleanmgr"]   = ("\uE74D", "#FF7FD18B"),   // lixeira, verde
        ["inetcpl"]    = ("\uE774", "#FF6CB6FF"),   // globo, azul
        ["devmgmt"]    = ("\uE772", "#FFB08AE8"),   // dispositivos, lilás
        ["mstsc"]      = ("\uE703", "#FF4FD1C5"),   // dois aparelhos ligados, verde-água
        ["services"]   = ("\uE9F5", "#FFF4A261"),   // engrenagens, laranja
        ["taskmgr"]    = ("\uE9D9", "#FFF4A261"),   // diagnóstico, laranja
        ["eventvwr"]   = ("\uE81C", "#FFFFB86C"),   // histórico
        ["regedit"]    = ("\uE943", "#FF6CB6FF"),   // código
        ["compmgmt"]   = ("\uE977", "#FFB08AE8"),   // computador
        ["diskmgmt"]   = ("\uEDA2", "#FF7FD18B"),   // disco
        ["sysdm"]      = ("\uE977", "#FFB08AE8"),
        ["appwiz"]     = ("\uE71D", "#FF6CB6FF"),   // todos os apps
        ["ncpa"]       = ("\uE839", "#FF6CB6FF"),   // rede
        ["firewall"]   = ("\uE83D", "#FFFF6B6B"),   // escudo
        ["wf"]         = ("\uE83D", "#FFFF6B6B"),
        ["gpedit"]     = ("\uE8D7", "#FFF4A261"),   // permissões
        ["control"]    = ("\uE713", "#FFC8C8CC"),   // configurações
        ["explorer"]   = ("\uEC50", "#FFFFC857"),   // pasta
        ["cmd"]        = ("\uE756", "#FFC8C8CC"),
        ["powershell"] = ("\uE756", "#FF6CB6FF"),
        ["pwsh"]       = ("\uE756", "#FF6CB6FF"),
        ["wt"]         = ("\uE756", "#FFC8C8CC"),
        ["notepad"]    = ("\uE70B", "#FFC8C8CC"),   // nota
        ["calc"]       = ("\uE8EF", "#FFC8C8CC"),   // calculadora
    };

    private static readonly Dictionary<string, Brush> Brushes = new();

    private static Brush Frozen(string hex)
    {
        if (Brushes.TryGetValue(hex, out var pronto)) return pronto;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return Brushes[hex] = brush;
    }
}
