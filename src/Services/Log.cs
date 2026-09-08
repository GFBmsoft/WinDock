using System.IO;

namespace WinDock.Services;

/// <summary>
/// Um arquivo de texto ao lado do config, so para o que falha em silencio.
///
/// A dock engole os erros de proposito — um atalho quebrado nao pode derrubar a barra —
/// mas engolir sem registrar deixa o usuario sem nada para olhar quando um icone nao abre.
/// Aqui fica o rastro: o que se tentou abrir, por qual caminho, e o erro que veio.
/// </summary>
public static class Log
{
    public static readonly string FilePath = Path.Combine(DockConfig.Dir, "windock.log");

    /// <summary>
    /// A volta anterior, guardada inteira quando o arquivo atual enche.
    ///
    /// Existe porque investigar é sempre olhar para trás: o registro que interessa é o do
    /// minuto em que a coisa aconteceu, e ele é justamente o que já passou quando alguém vai
    /// procurar. Em 08/09/2026 o rastro de um teste sumiu entre o gesto e a leitura.
    /// </summary>
    public static readonly string PreviousPath = Path.Combine(DockConfig.Dir, "windock.1.log");

    /// <summary>Acima disto o arquivo dá lugar a um novo — e vira o <see cref="PreviousPath"/>.
    /// São duas voltas guardadas, meio mega no total: um diário de bordo, não um histórico.</summary>
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DockConfig.Dir);
                Rotate();

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* nem o log pode atrapalhar a dock */ }
    }

    /// <summary>
    /// Cheio, o arquivo vira "a volta anterior" e um novo começa — em vez de apagar tudo.
    ///
    /// Apagar era barato e custou caro: com o rastro ligado o limite chega em minutos, e o que
    /// se perdia era sempre o começo da história (o arranque da dock, o instante do gesto que
    /// se queria entender). Guardar uma volta atrás significa que a janela de tempo do registro
    /// nunca é menor que <see cref="MaxBytes"/> — antes, logo depois de encher, ela era zero.
    ///
    /// O <c>Move</c> com sobrescrita é uma operação só do sistema de arquivos: não há instante
    /// em que as duas voltas estejam perdidas.
    /// </summary>
    private static void Rotate()
    {
        if (!File.Exists(FilePath)) return;
        if (new FileInfo(FilePath).Length <= MaxBytes) return;

        File.Move(FilePath, PreviousPath, overwrite: true);
    }

    public static void Write(string message, Exception ex) =>
        Write($"{message} — {ex.GetType().Name}: {ex.Message}");

    // ── rastro de uso ───────────────────────────────────────

    /// <summary>
    /// Existindo este arquivo, a dock passa a registrar o que acontece a cada gesto — não só
    /// os erros.
    ///
    /// É um arquivo, e não uma opção no painel, porque quem liga isto está investigando: dá
    /// para criar e apagar sem mexer na configuração e sem reiniciar nada além da dock. E
    /// fica desligado por padrão: o rastro é detalhado demais para o dia a dia.
    /// </summary>
    private static readonly string TraceFlag = Path.Combine(DockConfig.Dir, "rastrear");

    /// <summary>
    /// Lido uma vez, ao subir. Conferir o arquivo a cada linha custaria um acesso a disco
    /// dentro de coisas que acontecem dezenas de vezes por segundo.
    /// </summary>
    private static readonly bool Tracing = File.Exists(TraceFlag);

    /// <summary>
    /// Registra um passo do uso, com o relógio em milissegundos — é a diferença entre dois
    /// carimbos que diz onde o tempo foi.
    /// </summary>
    public static void Trace(string message)
    {
        if (!Tracing) return;
        Write($"[rastro {DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}
