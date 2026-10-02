using System.Globalization;
using System.Net.NetworkInformation;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Quanto a máquina está trabalhando: CPU, memória, placa de vídeo e o que passa pela rede.
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
        Gpu = _gpuMeter.Sample() ?? Gpu;

        if (primeira) return;

        Push(_cpu, Cpu);
        Push(_ram, Ram);
        Push(_down, Down);
        Push(_up, Up);
        Push(_gpu, Gpu);
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
        long recebido = 0, enviado = 0;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var s = nic.GetIPStatistics();
                recebido += s.BytesReceived;
                enviado += s.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            // adaptador sumindo no meio da enumeração (VPN subindo, cabo saindo): a amostra
            // desta volta se perde, a próxima vem inteira
            return;
        }

        var dRecebido = recebido - _recebidoAnterior;
        var dEnviado = enviado - _enviadoAnterior;

        _recebidoAnterior = recebido;
        _enviadoAnterior = enviado;

        if (primeira || segundos <= 0) return;

        // negativo acontece: um adaptador que some leva o contador dele junto, e a soma
        // desta volta fica menor que a anterior sem ninguém ter recebido menos que nada
        Down = Math.Max(0, dRecebido / segundos);
        Up = Math.Max(0, dEnviado / segundos);
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
