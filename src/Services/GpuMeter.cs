using System.Runtime.InteropServices;

namespace WinDock.Services;

/// <summary>
/// Quanto a placa de vídeo está trabalhando, na conta do Gerenciador de Tarefas.
///
/// O Windows não tem uma chamada barata como o <c>GetSystemTimes</c> da CPU: o número vem dos
/// contadores <c>GPU Engine</c>, um por motor de cada processo ("pid_1234_luid_…_engtype_3D").
/// A conta do Gerenciador soma os processos de cada motor e fica com o motor mais ocupado — é
/// por isso que um vídeo tocando aparece em "Video Decode" e não some na média do 3D.
///
/// <para>Pelo PDH direto, e não pelo <c>PerformanceCounter</c>, que abriria um contador por
/// instância (são ~200, e mudam a cada processo que nasce). Medido em 02/10/2026: abrir a
/// consulta custa 1,2 s — por isso ela abre numa thread à parte, e até lá não há número —, e
/// cada amostra depois disso, menos de 1 ms.</para>
/// </summary>
public sealed class GpuMeter : IDisposable
{
    private nint _query;
    private nint _counter;
    private volatile bool _ready;
    private bool _opening;

    /// <summary>Já há o que ler. Falso enquanto a consulta abre, e para sempre numa máquina sem o contador.</summary>
    public bool Ready => _ready;

    /// <summary>
    /// Uma leitura, de 0 a 100, ou nulo enquanto não há consulta. A primeira chamada só manda
    /// abrir — quem chama de segundo em segundo recebe o número a partir da volta seguinte.
    /// </summary>
    public double? Sample()
    {
        if (!_ready)
        {
            if (!_opening)
            {
                _opening = true;
                Task.Run(Open);
            }
            return null;
        }

        if (PdhCollectQueryData(_query) != 0) return null;

        uint tamanho = 0;
        if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref tamanho, out _, nint.Zero) != PDH_MORE_DATA)
            return null;

        var buffer = Marshal.AllocHGlobal((int)tamanho);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref tamanho, out var quantos, buffer) != 0)
                return null;

            // soma por motor (placa + tipo), e o resultado é o motor mais ocupado
            var porMotor = new Dictionary<string, double>(StringComparer.Ordinal);
            var passo = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < quantos; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * passo);
                if (item.CStatus != 0) continue;

                var nome = Marshal.PtrToStringUni(item.Name) ?? "";
                var luid = nome.IndexOf("luid_", StringComparison.Ordinal);
                var motor = luid >= 0 ? nome[luid..] : nome;   // tira o pid da frente
                var eng = motor.IndexOf("_eng_", StringComparison.Ordinal);
                var tipo = motor.IndexOf("engtype_", StringComparison.Ordinal);
                if (eng >= 0 && tipo > eng) motor = motor[..eng] + "|" + motor[tipo..];

                porMotor[motor] = porMotor.GetValueOrDefault(motor) + item.Value;
            }

            return porMotor.Count == 0 ? 0 : Math.Clamp(porMotor.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Open()
    {
        try
        {
            if (PdhOpenQuery(null, nint.Zero, out var query) != 0) return;
            if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", nint.Zero, out var counter) != 0)
            {
                PdhCloseQuery(query);
                Log.Write("o contador de uso da GPU não existe nesta máquina");
                return;
            }

            // a porcentagem é a diferença entre duas coletas: esta é a primeira
            PdhCollectQueryData(query);
            _query = query;
            _counter = counter;
            _ready = true;
        }
        catch (Exception ex)
        {
            Log.Write("não foi possível abrir o contador da GPU", ex);
        }
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
        _ready = false;
    }

    private const uint PDH_FMT_DOUBLE = 0x200;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint Name;
        public uint CStatus;
        public double Value;   // o double alinha em 8: o PDH_FMT_COUNTERVALUE tem 4 bytes de folga antes dele
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize,
                                                          out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(nint query);
}
