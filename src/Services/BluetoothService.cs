using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Services;

/// <summary>Um aparelho lembrado pelo Windows, e se ele esta conectado agora.</summary>
public sealed record BluetoothDevice(string Name, bool Connected);

/// <summary>
/// Estado do bluetooth e os aparelhos pareados.
///
/// O radio vem da API classica (<c>bthprops</c>): <c>BluetoothFindFirstRadio</c> so devolve
/// handle com o radio <b>ligado</b>, o que ja e a resposta para o icone.
///
/// Os aparelhos vem da lista de dispositivos do Windows, e nao da API classica de bluetooth.
/// O motivo apareceu no teste: a API classica so enxerga bluetooth "clássico" — o fone JBL
/// aparecia, mas o teclado (que e Bluetooth LE) nao. Na lista de dispositivos os dois estao
/// la, distinguidos pelo identificador: <c>BTHENUM\DEV_</c> para os classicos e
/// <c>BTHLE\DEV_</c> para os LE. O resto (servicos, perfis, enumeradores) e ruido do
/// sistema e fica de fora.
/// </summary>
public static class BluetoothService
{
    /// <summary>O radio esta ligado?</summary>
    public static bool IsOn
    {
        get
        {
            var parms = new FindRadioParams { dwSize = Marshal.SizeOf<FindRadioParams>() };
            var find = BluetoothFindFirstRadio(ref parms, out var radio);

            if (radio != 0) CloseHandle(radio);
            if (find == 0) return false;

            BluetoothFindRadioClose(find);
            return true;
        }
    }

    /// <summary>Aparelhos pareados — fones, teclados, mouses —, os conectados primeiro.</summary>
    public static IReadOnlyList<BluetoothDevice> Devices()
    {
        var paired = Enumerate(presentOnly: false);
        var connected = Enumerate(presentOnly: true).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return paired
            .Select(d => new BluetoothDevice(d.Name, connected.Contains(d.Id)))
            .OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<(string Id, string Name)> Enumerate(bool presentOnly)
    {
        var found = new List<(string, string)>();

        var guid = GUID_DEVCLASS_BLUETOOTH;
        var set = SetupDiGetClassDevs(ref guid, null, 0, presentOnly ? DIGCF_PRESENT : 0);
        if (set == nint.Zero || set == -1) return found;

        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var id = InstanceId(set, ref info);

                // so os aparelhos em si: BTHENUM\DEV_ (classico) e BTHLE\DEV_ (baixa energia).
                // O que vem com um GUID no lugar do DEV_ e um servico daquele aparelho.
                if (!id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) &&
                    !id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Property(set, ref info, SPDRP_FRIENDLYNAME)
                           ?? Property(set, ref info, SPDRP_DEVICEDESC);

                if (!string.IsNullOrWhiteSpace(name)) found.Add((id, name!.Trim()));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return found;
    }

    private static string InstanceId(nint set, ref SP_DEVINFO_DATA info)
    {
        var buffer = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out _)
            ? buffer.ToString() : string.Empty;
    }

    private static string? Property(nint set, ref SP_DEVINFO_DATA info, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, buffer.Length, out var size))
            return null;

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ── interop ─────────────────────────────────────────────
    private static readonly Guid GUID_DEVCLASS_BLUETOOTH = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    private const uint DIGCF_PRESENT = 0x02;
    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref SP_DEVINFO_DATA info,
                                                          StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA info,
                                                                uint property, out uint type,
                                                                byte[] buffer, int size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [StructLayout(LayoutKind.Sequential)]
    private struct FindRadioParams { public int dwSize; }

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern nint BluetoothFindFirstRadio(ref FindRadioParams parms, out nint radio);

    [DllImport("bthprops.cpl")]
    private static extern bool BluetoothFindRadioClose(nint find);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
