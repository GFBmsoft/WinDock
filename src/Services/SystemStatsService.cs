using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Uma unidade e o espaço dela: "C:  Sistema", o tamanho e quanto sobra.</summary>
public sealed record DriveSpace(string Name, long Total, long Free)
{
    public double Used => Total > 0 ? 100.0 * (Total - Free) / Total : 0;
}

/// <summary>
/// Quanto a máquina está trabalhando: CPU, memória, placa de vídeo, disco e o que passa pela rede.
///
/// Uma amostra por segundo, pedida pelo relógio que a barra já tem — este serviço não sobe
/// relógio nenhum. Guarda os últimos 60 segundos de cada medida, que é o que o cartão
/// desenha como gráfico; a barra mostra só a última.
///
/// <para>Tudo pelo caminho barato. CPU e memória vêm de duas chamadas do kernel
/// (<c>GetSystemTimes</c> e <c>GlobalMemoryStatusEx</c>), e não de <c>PerformanceCounter</c>:
/// o contador custa centenas de milissegundos para abrir, sobe thread de coleta e, num
/// item que fica na barra o dia inteiro, isso apareceria no arranque. A rede vem dos
/// contadores que o próprio Windows mantém por adaptador.</para>
/// </summary>
public sealed class SystemStatsService
{
    /// <summary>Quantos segundos de história o cartão desenha.</summary>
    public const int Window = 60;

    private readonly double[] _cpu = new double[Window];
    private readonly double[] _ram = new double[Window];
    private readonly double[] _down = new double[Window];
    private readonly double[] _up = new double[Window];
    private readonly double[] _gpu = new double[Window];
    private readonly double[] _disk = new double[Window];
    private readonly GpuMeter _gpuMeter = new();

    /// <summary>Quantas amostras já entraram — abaixo de <see cref="Window"/> o gráfico ainda está enchendo.</summary>
    private int _count;

    private long _idleAnterior, _kernelAnterior, _userAnterior;
    private long _recebidoAnterior, _enviadoAnterior;
    private DateTime _quandoAnterior;

    /// <summary>Uso de CPU da última amostra, de 0 a 100.</summary>
    public double Cpu { get; private set; }

    /// <summary>Memória física em uso, de 0 a 100.</summary>
    public double Ram { get; private set; }

    /// <summary>Uso da placa de vídeo, de 0 a 100 — o motor mais ocupado, como no Gerenciador de Tarefas.</summary>
    public double Gpu { get; private set; }

    /// <summary>Já há leitura da placa de vídeo. Nos primeiros segundos da dock, e numa máquina sem o contador, não há.</summary>
    public bool HasGpu => _gpuMeter.Ready;

    /// <summary>
    /// Tempo ativo do disco mais ocupado, de 0 a 100 — o "Disco" do Gerenciador de Tarefas. O
    /// mais ocupado, e não a média: com dois discos, um copiando a 100% e outro parado não é
    /// uma máquina a 50%.
    /// </summary>
    public double Disk { get; private set; }

    /// <summary>Bytes por segundo lidos e gravados, somados em todos os discos.</summary>
    public double DiskRead { get; private set; }
    public double DiskWrite { get; private set; }

    /// <summary>Bytes por segundo que entraram e que saíram, na última amostra.</summary>
    public double Down { get; private set; }
    public double Up { get; private set; }

    /// <summary>
    /// A história de cada medida, do mais antigo para o mais novo.
    ///
    /// Sai como cópia, e de propósito: quem desenha é um binding, e binding não redesenha
    /// quando o conteúdo do mesmo vetor muda por baixo dele. Sessenta doubles por segundo
    /// não são custo nenhum perto de redesenhar um gráfico que não mudou.
    /// </summary>
    public double[] CpuHistory => Snapshot(_cpu);
    public double[] RamHistory => Snapshot(_ram);
    public double[] DownHistory => Snapshot(_down);
    public double[] UpHistory => Snapshot(_up);
    public double[] GpuHistory => Snapshot(_gpu);
    public double[] DiskHistory => Snapshot(_disk);

    private double[] Snapshot(double[] origem)
    {
        var quanto = Math.Min(_count, Window);
        var saida = new double[quanto];
        Array.Copy(origem, Window - quanto, saida, 0, quanto);
        return saida;
    }

    /// <summary>
    /// Mede tudo uma vez. Chamada de segundo em segundo pela barra.
    ///
    /// A primeira chamada só acerta os ponteiros: toda medida aqui é a diferença entre duas
    /// leituras, e sem a anterior não há o que dividir.
    /// </summary>
    public void Sample()
    {
        var agora = DateTime.UtcNow;
        var segundos = (agora - _quandoAnterior).TotalSeconds;
        var primeira = _quandoAnterior == default;
        _quandoAnterior = agora;

        MeasureCpu(primeira);
        MeasureRam();
        MeasureNetwork(primeira, segundos);
        MeasureDisks();
        Gpu = _gpuMeter.Sample() ?? Gpu;

        if (primeira) return;

        Push(_cpu, Cpu);
        Push(_ram, Ram);
        Push(_down, Down);
        Push(_up, Up);
        Push(_gpu, Gpu);
        Push(_disk, Disk);
        _count++;
    }

    /// <summary>A série anda uma casa para a esquerda e o novo valor entra no fim.</summary>
    private static void Push(double[] serie, double valor)
    {
        Array.Copy(serie, 1, serie, 0, Window - 1);
        serie[Window - 1] = valor;
    }

    private void MeasureCpu(bool primeira)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        var dIdle = idle - _idleAnterior;
        var dKernel = kernel - _kernelAnterior;
        var dUser = user - _userAnterior;

        _idleAnterior = idle;
        _kernelAnterior = kernel;
        _userAnterior = user;

        if (primeira) return;

        // o tempo do kernel JÁ inclui o tempo parado; o total é kernel + user, e o ocupado é
        // o total menos o parado. Somar os três daria uma máquina sempre ociosa
        var total = dKernel + dUser;
        Cpu = total > 0 ? Math.Clamp(100.0 * (total - dIdle) / total, 0, 100) : 0;
    }

    private void MeasureRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status)) Ram = status.dwMemoryLoad;
    }

    /// <summary>
    /// O que passou pelos adaptadores que estão de pé, somado.
    ///
    /// Somado, e não "o adaptador principal": numa máquina com cabo e wi-fi ligados ao mesmo
    /// tempo, escolher um dá zero justamente quando o outro está trabalhando. Loopback fica
    /// de fora — falar consigo mesmo não é tráfego de rede, e um download local encheria o
    /// gráfico com um número que não saiu da máquina.
    /// </summary>
    private void MeasureNetwork(bool primeira, double segundos)
    {
        RefreshAdapters();

        // a lista trocou: os contadores somados são de outro conjunto, e a diferença para a
        // soma anterior seria um pico (ou um buraco) que ninguém baixou
        var adaptadores = _adaptadores;
        var trocou = !ReferenceEquals(adaptadores, _medidos);
        _medidos = adaptadores;

        long recebido = 0, enviado = 0;

        try
        {
            foreach (var nic in adaptadores)
            {
                var s = nic.GetIPStatistics();
                recebido += s.BytesReceived;
                enviado += s.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            // adaptador que sumiu desde a última enumeração (VPN caindo, cabo saindo): a amostra
            // desta volta se perde, e a lista é refeita já
            _adaptadoresEm = default;
            return;
        }

        var dRecebido = recebido - _recebidoAnterior;
        var dEnviado = enviado - _enviadoAnterior;

        _recebidoAnterior = recebido;
        _enviadoAnterior = enviado;

        if (primeira || trocou || segundos <= 0) return;

        // negativo acontece: um adaptador que some leva o contador dele junto, e a soma
        // desta volta fica menor que a anterior sem ninguém ter recebido menos que nada
        Down = Math.Max(0, dRecebido / segundos);
        Up = Math.Max(0, dEnviado / segundos);
    }

    /// <summary>Os adaptadores de pé, fora loopback e túneis — a lista que a medição soma.</summary>
    private volatile NetworkInterface[] _adaptadores = [];
    private NetworkInterface[]? _medidos;
    private DateTime _adaptadoresEm;
    private int _enumerando;

    /// <summary>De quanto em quanto tempo a lista de adaptadores é refeita.</summary>
    private static readonly TimeSpan AdapterRefresh = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Refaz a lista de adaptadores de tempos em tempos, e fora da thread de quem mede.
    ///
    /// <para><b>Era o item mais caro da barra inteira.</b> O <c>GetAllNetworkInterfaces</c> monta,
    /// para cada adaptador, endereços, DNS e gateway — 16 ms nesta máquina, que tem 36 (medido em
    /// 06/10/2026) —, e isto rodava a cada segundo na thread da interface: um quadro perdido por
    /// segundo, o dia inteiro, para ler dois contadores. Os contadores em si
    /// (<c>GetIPStatistics</c>) custam centésimos de milissegundo por adaptador.</para>
    ///
    /// <para>Trinta segundos é o atraso máximo para um adaptador novo entrar na soma — uma VPN
    /// que sobe, um cabo que entra. O que sai é percebido na hora: a leitura dele falha, e a
    /// lista é refeita em seguida.</para>
    /// </summary>
    private void RefreshAdapters()
    {
        if (DateTime.UtcNow - _adaptadoresEm < AdapterRefresh) return;
        if (Interlocked.Exchange(ref _enumerando, 1) == 1) return;

        _adaptadoresEm = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            try
            {
                _adaptadores = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .ToArray();
            }
            catch (NetworkInformationException) { /* fica a lista anterior; a próxima volta tenta de novo */ }
            finally { Volatile.Write(ref _enumerando, 0); }
        });
    }

    // ── disco ───────────────────────────────────────────────

    /// <summary>Os discos físicos encontrados — o N de cada \\.\PhysicalDriveN.</summary>
    private volatile int[] _discos = [];
    private DateTime _discosEm;
    private int _procurandoDiscos;

    /// <summary>A leitura anterior de cada disco, pelo número dele.</summary>
    private readonly Dictionary<int, DISK_PERFORMANCE> _discoAnterior = new();

    /// <summary>
    /// Lê os contadores de cada disco e faz a conta contra a leitura anterior.
    ///
    /// <para>O disco é aberto e fechado a cada leitura, e não guardado aberto: segurar o
    /// \\.\PhysicalDriveN de um pendrive faria o "Ejetar com segurança" dizer que ele está em
    /// uso. Abrir sem direito nenhum e pedir os contadores custa microssegundos.</para>
    /// </summary>
    private void MeasureDisks()
    {
        RefreshDisks();

        double ativo = 0, lido = 0, gravado = 0;
        var tamanho = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISK_PERFORMANCE>();

        foreach (var n in _discos)
        {
            using var disco = CreateFile($@"\\.\PhysicalDrive{n}", 0, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
            if (disco.IsInvalid ||
                !DeviceIoControl(disco, IOCTL_DISK_PERFORMANCE, 0, 0, out var agora, tamanho, out _, 0))
            {
                // o disco saiu (um pendrive puxado): fica fora da conta, e a lista é refeita já
                _discoAnterior.Remove(n);
                _discosEm = default;
                continue;
            }

            if (_discoAnterior.TryGetValue(n, out var antes) && agora.QueryTime > antes.QueryTime)
            {
                var relogio = (double)(agora.QueryTime - antes.QueryTime);
                var segundos = relogio / 1e7;
                ativo = Math.Max(ativo, Math.Clamp(100.0 * (1 - (agora.IdleTime - antes.IdleTime) / relogio), 0, 100));
                lido += Math.Max(0, agora.BytesRead - antes.BytesRead) / segundos;
                gravado += Math.Max(0, agora.BytesWritten - antes.BytesWritten) / segundos;
            }
            _discoAnterior[n] = agora;
        }

        Disk = ativo;
        DiskRead = lido;
        DiskWrite = gravado;
    }

    /// <summary>
    /// Procura os discos físicos de tempos em tempos, fora da thread de quem mede — no mesmo
    /// ritmo da lista de adaptadores. Os números não vêm sempre seguidos (o pendrive que sai
    /// deixa um buraco), então a procura passa por todos até um teto, e não para no primeiro
    /// que falta.
    /// </summary>
    private void RefreshDisks()
    {
        if (DateTime.UtcNow - _discosEm < AdapterRefresh) return;
        if (Interlocked.Exchange(ref _procurandoDiscos, 1) == 1) return;

        _discosEm = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            try
            {
                var achados = new List<int>();
                for (var n = 0; n < 16; n++)
                {
                    using var disco = CreateFile($@"\\.\PhysicalDrive{n}", 0, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
                    if (!disco.IsInvalid) achados.Add(n);
                }
                _discos = achados.ToArray();
            }
            finally { Volatile.Write(ref _procurandoDiscos, 0); }
        });
    }

    /// <summary>
    /// O espaço de cada unidade: as do próprio computador e as removíveis com mídia. As de rede
    /// ficam de fora — perguntar o tamanho de um compartilhamento que dormiu segura a resposta
    /// por segundos. Chamar fora da thread da interface, pelo mesmo motivo com um disco externo.
    /// </summary>
    public static IReadOnlyList<DriveSpace> Drives()
    {
        var lista = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady || d.TotalSize <= 0) continue;

                var nome = d.Name.TrimEnd('\\');
                if (!string.IsNullOrWhiteSpace(d.VolumeLabel)) nome += "  " + d.VolumeLabel;
                lista.Add(new DriveSpace(nome, d.TotalSize, d.TotalFreeSpace));
            }
            catch (IOException) { /* a mídia saiu entre a lista e a pergunta */ }
            catch (UnauthorizedAccessException) { }
        }
        return lista;
    }

    /// <summary>"476 GB", "1,8 TB" — o tamanho de uma unidade, sem casas que ninguém lê.</summary>
    public static string Size(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        const double gb = 1024d * 1024 * 1024;

        if (bytes >= 1024 * gb) return (bytes / (1024 * gb)).ToString("0.0", c) + " TB";
        if (bytes >= 10 * gb) return (bytes / gb).ToString("0", c) + " GB";
        return (bytes / gb).ToString("0.0", c) + " GB";
    }

    /// <summary>
    /// "1,2 MB/s", "840 kB/s", "0 B/s" — curto o bastante para caber na barra.
    ///
    /// Em bytes, e não em bits: é o número que o Explorer e o gerenciador de tarefas mostram,
    /// e ver "8 Mb/s" aqui e "1 MB/s" ali para a mesma transferência confunde mais do que a
    /// precisão do bit resolve.
    /// </summary>
    public static string Rate(double bytesPorSegundo)
    {
        var c = CultureInfo.CurrentCulture;

        if (bytesPorSegundo >= 1024 * 1024 * 1024) return (bytesPorSegundo / (1024 * 1024 * 1024)).ToString("0.0", c) + " GB/s";
        if (bytesPorSegundo >= 1024 * 1024) return (bytesPorSegundo / (1024 * 1024)).ToString("0.0", c) + " MB/s";
        if (bytesPorSegundo >= 1024) return (bytesPorSegundo / 1024).ToString("0", c) + " kB/s";
        return bytesPorSegundo.ToString("0", c) + " B/s";
    }
}
