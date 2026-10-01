using System.Diagnostics;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace WinDock.Services;

/// <summary>Um ponto do histórico da bateria: a hora, a carga e como a máquina estava a partir dali.</summary>
/// <param name="Awake">Ligada e em uso; falso em suspensão (a linha fica apagada nesse trecho).</param>
public sealed record BatterySample(DateTime Time, double Percent, bool Ac, bool Awake);

/// <summary>O que o cartão da bateria mostra além da carga de agora.</summary>
/// <param name="Health">Capacidade cheia de hoje sobre a de fábrica, em porcentagem.</param>
/// <param name="FullRuntime">Quanto a bateria cheia dura em uso, pela estimativa do Windows.</param>
public sealed record BatteryHistory(IReadOnlyList<BatterySample> Samples, double? Health, TimeSpan? FullRuntime);

/// <summary>
/// O histórico de carga da bateria — o gráfico de "Uso da bateria" das Configurações do Windows.
///
/// Vem do <c>powercfg /batteryreport /xml</c>, que é de onde o Windows tira o dele: a seção
/// <c>RecentUsage</c> traz a última semana em trechos (cada um com a hora, a carga em mWh e se a
/// máquina estava na tomada, ativa ou suspensa), e a <c>RuntimeEstimates</c> traz a capacidade
/// de fábrica e a de hoje. O relatório leva meio segundo para sair, então roda fora da
/// interface, só quando o cartão abre, e o resultado vale por dois minutos.
///
/// <para>Pedido de 01/10/2026: com a bateria indo para o cartão de energia, o ícone dela na
/// barra tinha ficado sem função.</para>
/// </summary>
public static class BatteryHistoryService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/battery/2012";

    private static BatteryHistory? _cache;
    private static DateTime _cacheAt;

    public static async Task<BatteryHistory?> ReadAsync()
    {
        if (_cache is not null && DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(2)) return _cache;

        var historico = await Task.Run(Read);
        if (historico is not null) { _cache = historico; _cacheAt = DateTime.UtcNow; }
        return historico;
    }

    private static BatteryHistory? Read()
    {
        var arquivo = Path.Combine(Path.GetTempPath(), $"windock-bateria-{Environment.ProcessId}.xml");
        try
        {
            using (var p = Process.Start(new ProcessStartInfo
                   {
                       FileName = "powercfg.exe",
                       Arguments = $"/batteryreport /xml /output \"{arquivo}\"",
                       UseShellExecute = false,
                       CreateNoWindow = true,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true
                   }))
            {
                if (p is null) return null;
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } return null; }
            }

            return File.Exists(arquivo) ? Parse(XDocument.Load(arquivo)) : null;
        }
        catch (Exception ex)
        {
            Log.Write("histórico da bateria: o powercfg não respondeu", ex);
            return null;
        }
        finally
        {
            try { File.Delete(arquivo); } catch { }
        }
    }

    /// <summary>
    /// Os trechos viram pontos: a carga no começo de cada um, sobre a capacidade cheia daquele
    /// momento — que é o que a porcentagem do Windows mede, e não a de fábrica. O último ponto
    /// é o do próprio relatório, que é "agora".
    /// </summary>
    internal static BatteryHistory Parse(XDocument xml)
    {
        var amostras = new List<BatterySample>();
        foreach (var e in xml.Descendants(Ns + "UsageEntry"))
        {
            if (!DateTime.TryParse((string?)e.Attribute("LocalTimestamp"), out var hora)) continue;
            var carga = (double?)e.Attribute("ChargeCapacity") ?? 0;
            var cheia = (double?)e.Attribute("FullChargeCapacity") ?? 0;
            if (cheia <= 0) continue;

            var tipo = (string?)e.Attribute("EntryType") ?? "";
            amostras.Add(new BatterySample(
                hora,
                Math.Clamp(carga / cheia * 100, 0, 100),
                (string?)e.Attribute("Ac") == "1",
                tipo is "Active" or "ReportGenerated"));
        }

        double? saude = null;
        TimeSpan? autonomia = null;
        var estimativas = xml.Descendants(Ns + "RuntimeEstimates").FirstOrDefault();
        if (estimativas is not null)
        {
            var fabrica = (double?)estimativas.Element(Ns + "DesignCapacity")?.Element(Ns + "Capacity");
            var hoje = estimativas.Element(Ns + "FullChargeCapacity");
            var capacidade = (double?)hoje?.Element(Ns + "Capacity");
            if (fabrica > 0 && capacidade > 0) saude = Math.Min(100, capacidade.Value / fabrica.Value * 100);

            var texto = (string?)hoje?.Element(Ns + "ActiveRuntime");
            if (!string.IsNullOrEmpty(texto))
            {
                try
                {
                    var t = XmlConvert.ToTimeSpan(texto);
                    if (t > TimeSpan.Zero) autonomia = t;
                }
                catch { /* duração em formato que não é ISO: fica sem a autonomia */ }
            }
        }

        return new BatteryHistory(amostras.OrderBy(a => a.Time).ToList(), saude, autonomia);
    }
}
