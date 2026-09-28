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

    /// <summary>
    /// O arquivo, aberto uma vez e mantido aberto.
    ///
    /// Antes cada linha reabria tudo: <c>CreateDirectory</c>, <c>File.Exists</c>,
    /// <c>FileInfo.Length</c> e um <c>AppendAllText</c> que abre, escreve e fecha — quatro visitas
    /// ao disco para escrever uma frase. Medido em 28/09/2026: **0,83 ms por linha contra 0,006 ms**
    /// com o arquivo aberto, 130 vezes. Com o rastro ligado, a 150 linhas por segundo, era um oitavo
    /// de cada segundo gasto escrevendo log na thread da interface — e as medições de arranque
    /// saíam infladas pelo próprio instrumento que as media.
    ///
    /// Continua **síncrono**, e isso é escolha, não preguiça: uma fila com thread de fundo perderia
    /// as últimas linhas justamente quando a dock morre, que é quando elas são a única coisa que
    /// importa. Com <c>AutoFlush</c>, cada linha está no disco no instante em que é escrita — a
    /// mesma garantia de antes, 130 vezes mais barata.
    /// </summary>
    private static StreamWriter? _writer;

    /// <summary>
    /// Quantos bytes o arquivo já tem, contados aqui.
    ///
    /// É o que substitui o <c>FileInfo.Length</c> por linha: perguntar o tamanho ao sistema a cada
    /// escrita seria reintroduzir, em menor escala, o custo que se acabou de tirar. A conta começa
    /// no tamanho real na abertura e cresce com o que se escreve — e não precisa ser exata, porque
    /// só decide quando o arquivo passa de meio mega.
    /// </summary>
    private static long _bytes;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var linha = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";

                Open();
                if (_writer is null) return;

                _writer.Write(linha);
                _bytes += linha.Length;

                Rotate();
            }
        }
        catch
        {
            // nem o log pode atrapalhar a dock. O arquivo é solto para que a próxima linha
            // tente abri-lo de novo: o erro pode ter sido do momento (o antivírus segurando o
            // arquivo, a pasta recriada por fora), e desistir para sempre deixaria a dock muda.
            Close();
        }
    }

    /// <summary>
    /// Abre o arquivo, se ainda não estiver aberto.
    ///
    /// <c>FileShare.ReadWrite</c> não é detalhe: sem ele, o arquivo aberto pela dock não poderia
    /// ser lido enquanto ela roda — e ler o log com a dock de pé é exatamente o que se faz para
    /// investigar qualquer coisa nela.
    ///
    /// Sem BOM, porque o arquivo pode já existir de uma versão anterior: o <c>AppendAllText</c>
    /// não escrevia BOM nenhum, e acrescentar um no meio do texto deixaria três bytes estranhos
    /// no começo de uma linha.
    /// </summary>
    private static void Open()
    {
        if (_writer is not null) return;

        Directory.CreateDirectory(DockConfig.Dir);

        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _bytes = stream.Length;

        _writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// Fecha o arquivo. Chamado no encerramento da dock e sempre que uma escrita falha.
    ///
    /// Com <c>AutoFlush</c> não há nada pendente para salvar — isto solta o handle, e não os
    /// dados.
    /// </summary>
    public static void Close()
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { /* já estava perdido */ }
            _writer = null;
        }
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
    ///
    /// Agora que o arquivo fica aberto, mover exige fechá-lo antes — o Windows não renomeia um
    /// arquivo com handle de escrita aberto. A próxima linha reabre, e nasce o arquivo novo. Roda
    /// **depois** de escrever, e não antes: assim a decisão usa o tamanho que o arquivo tem de
    /// fato, sem precisar perguntá-lo ao sistema.
    /// </summary>
    private static void Rotate()
    {
        if (_bytes <= MaxBytes) return;

        Close();
        File.Move(FilePath, PreviousPath, overwrite: true);
        _bytes = 0;
    }

    public static void Write(string message, Exception ex) =>
        Write($"{message} — {ex.GetType().Name}: {ex.Message}");

    // ── rastro de uso ───────────────────────────────────────

    /// <summary>
    /// Existindo este arquivo, a dock passa a registrar o que acontece a cada gesto — não só
    /// os erros.
    ///
    /// Continua existindo depois de o rastro virar opção no painel, e para um caso que o painel
    /// não cobre: quando a dock não chega a abrir, não há onde clicar. Criar o arquivo liga o
    /// rastro já no arranque seguinte, que é justamente o que se precisa ver.
    /// </summary>
    private static readonly string TraceFlag = Path.Combine(DockConfig.Dir, "rastrear");

    /// <summary>
    /// Registrar cada passo, e não só o que deu errado. Quem manda é a configuração
    /// (<see cref="DockConfig.Trace"/>), que escreve aqui ao carregar e a cada mudança — o
    /// arquivo <c>rastrear</c> serve de partida, para o arranque que acontece antes de existir
    /// configuração lida.
    ///
    /// Um campo, e não uma consulta ao disco por linha: isto é perguntado dentro de coisas que
    /// acontecem dezenas de vezes por segundo.
    /// </summary>
    public static bool Tracing { get; set; } = File.Exists(TraceFlag);

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
