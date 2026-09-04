using System.Windows.Media.Imaging;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Uma linha da lista do launcher: o que mostrar e o que fazer no Enter.</summary>
public sealed class LauncherEntry
{
    public required string Name { get; init; }

    /// <summary>Linha de baixo: de onde veio o resultado.</summary>
    public required string Detail { get; init; }

    public BitmapSource? Icon { get; init; }

    /// <summary>Letra mostrada num circulo quando o item nao tem icone (energia, comando).</summary>
    public string Glyph { get; init; } = string.Empty;

    public bool HasIcon => Icon is not null;

    public required Action Run { get; init; }

    internal int Score { get; set; }
}

/// <summary>
/// A busca do launcher: apps fixados na dock primeiro, depois os instalados, depois os
/// comandos de energia, e por ultimo "executar o que foi digitado" (o Win+R de sempre).
///
/// A ordem nao e so estetica: o Enter dispara o primeiro item, entao o que voce usa todo
/// dia — o que ja esta fixado — tem que ganhar de um app instalado de nome parecido.
/// </summary>
public sealed class Launcher
{
    private readonly DockModel _model;

    public Launcher(DockModel model) => _model = model;

    /// <summary>
    /// Comandos de energia so aparecem com tres letras ou mais e so por prefixo do nome:
    /// desligar a maquina nao pode ser o resultado de um Enter apressado.
    /// </summary>
    private const int PowerMinimumQuery = 3;

    public IReadOnlyList<LauncherEntry> Search(string query, int limit = 9)
    {
        query = query.Trim();

        // com o campo vazio a janela e so a caixa de busca: listar os fixados ali seria
        // repetir a dock, que esta na tela do lado
        if (query.Length == 0) return Array.Empty<LauncherEntry>();

        // "run <algo>": o texto inteiro vira comando, e a lista mostra so ele. Nada de
        // misturar com aplicativos — quem escreveu o prefixo ja disse o que quer
        if (CommandOf(query) is { } comando) return new[] { CommandEntry(comando) };

        var found = new List<LauncherEntry>();
        var needle = Text.Normalize(query);

        // 1. o que ja esta na dock (fixado ou aberto)
        foreach (var item in _model.Items)
        {
            var score = Match(Text.Normalize(item.Label), needle);
            if (score <= 0) continue;
            var entry = Pinned(item);
            entry.Score = score + 400;     // vale mais que um app so instalado
            found.Add(entry);
        }

        // 2. apps instalados
        foreach (var app in AppCatalog.All())
        {
            var score = Match(app.Search, needle);
            if (score <= 0) continue;
            if (found.Any(f => f.Name.Equals(app.Name, StringComparison.CurrentCultureIgnoreCase))) continue;

            found.Add(new LauncherEntry
            {
                Name = app.Name,
                Detail = "Aplicativo",
                Icon = IconService.Shell(app.IconSource),
                Run = () => AppCatalog.Launch(app),
                Score = score
            });
        }

        // 3. energia
        if (query.Length >= PowerMinimumQuery)
        {
            foreach (var power in PowerService.All)
            {
                if (!Text.Normalize(power.Name).StartsWith(needle, StringComparison.Ordinal)) continue;
                found.Add(new LauncherEntry
                {
                    Name = power.Name,
                    Detail = "Energia",
                    Glyph = power.Name[..1],   // a lista usa glifo de icone; aqui vai a letra
                    Run = power.Run,
                    Score = 200
                });
            }
        }

        return found.OrderByDescending(f => f.Score)
                    .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(limit)
                    .ToList();
    }

    /// <summary>
    /// O prefixo que transforma o resto do texto num comando: <c>run notepad</c>, e o traço
    /// é opcional (<c>run - notepad</c>).
    ///
    /// Antes, "executar comando" era uma linha acrescentada ao fim de <b>toda</b> busca, com
    /// o texto digitado. Funcionava, mas ocupava um lugar na lista mesmo quando ninguém
    /// queria comando nenhum — e com a lista limitada a poucos resultados, esse lugar passou
    /// a ser caro. Como prefixo, ele só aparece quando é pedido.
    /// </summary>
    private const string RunPrefix = "run";

    /// <summary>O texto é um pedido de comando? Devolve o comando, ou <c>null</c>.</summary>
    public static string? CommandOf(string query)
    {
        var texto = query.TrimStart();

        if (!texto.StartsWith(RunPrefix, StringComparison.CurrentCultureIgnoreCase)) return null;

        var resto = texto[RunPrefix.Length..];

        // "run" sozinho ainda não é um comando; é preciso o separador — assim quem procura
        // um app chamado "Runtime..." continua achando o app, e não cai no modo comando
        if (resto.Length == 0 || (resto[0] != ' ' && resto[0] != '-')) return null;

        var comando = resto.TrimStart(' ', '-').Trim();
        return comando.Length == 0 ? null : comando;
    }

    /// <summary>A única entrada quando o texto começa com o prefixo de comando.</summary>
    public static LauncherEntry CommandEntry(string command) => new()
    {
        Name = command,
        Detail = "Executar comando, caminho ou endereço",
        Glyph = ">",
        Run = () => AppCatalog.Run(command)
    };

    private LauncherEntry Pinned(Models.DockItem item) => new()
    {
        Name = item.Label,
        Detail = item.HasWindows ? "Na dock — aberto" : "Na dock",
        Icon = item.Icon,
        Run = () => _model.Activate(item)
    };

    // ── correspondencia ─────────────────────────────────────

    /// <summary>
    /// Quanto o texto combina com o que foi digitado, ou zero se nao combina:
    /// comeco do nome vale mais que comeco de palavra, que vale mais que letras
    /// espalhadas na ordem ("bmt" achando "BM Testes"). O texto ja chega normalizado.
    /// </summary>
    private static int Match(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay)) return 0;

        if (hay.StartsWith(needle, StringComparison.Ordinal)) return 1000 - hay.Length;

        // inicio de qualquer palavra: "note" achando "Bloco de notas" nao, mas
        // "notas" sim; e "vs code" achando "Visual Studio Code" pela subsequencia
        var at = hay.IndexOf(needle, StringComparison.Ordinal);
        if (at > 0 && hay[at - 1] is ' ' or '-' or '_' or '.') return 800 - hay.Length;
        if (at > 0) return 600 - hay.Length;

        return Subsequence(hay, needle) ? 400 - hay.Length : 0;
    }

    /// <summary>As letras aparecem na ordem, mesmo que separadas.</summary>
    private static bool Subsequence(string hay, string needle)
    {
        var i = 0;
        foreach (var c in hay)
        {
            if (c != needle[i]) continue;
            if (++i == needle.Length) return true;
        }
        return false;
    }
}
