using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Radios;

namespace WinDock.Services;

/// <summary>
/// Um aparelho lembrado pelo Windows: se esta conectado agora e, quando o Windows sabe,
/// quanto resta de bateria (0 a 100). <c>null</c> em <see cref="Battery"/> quer dizer
/// "este aparelho nao informa", que e diferente de "esta com zero".
/// </summary>
public sealed record BluetoothDevice(string Name, bool Connected, int? Battery)
{
    public bool HasBattery => Battery is not null;

    /// <summary>Texto curto para a linha da lista: "93%".</summary>
    public string BatteryText => Battery is { } b ? $"{b}%" : string.Empty;
}

/// <summary>
/// Estado do bluetooth e os aparelhos pareados.
///
/// O radio vem da API classica (<c>bthprops</c>): <c>BluetoothFindFirstRadio</c> so devolve
/// handle com o radio <b>ligado</b>, o que ja e a resposta para o icone. Para <b>ligar e
/// desligar</b> nao existe API classica — o unico caminho e o WinRT
/// (<c>Windows.Devices.Radios.Radio</c>), e e por isso que o projeto tem TFM
/// <c>net8.0-windows10.0.19041.0</c>.
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

    /// <summary>
    /// Liga ou desliga o radio. Nao ha equivalente na API classica — <c>bthprops</c> sabe
    /// dizer se o radio existe e mexer em visibilidade, mas nao alimenta-lo —, entao aqui
    /// se usa o WinRT, que e o mesmo caminho do botao do painel do Windows.
    ///
    /// O acesso precisa ser pedido antes (<c>RequestAccessAsync</c>): numa maquina onde a
    /// pessoa bloqueou o controle de radios em Privacidade, a resposta vem negada e nada
    /// acontece — devolvemos falso em vez de estourar.
    /// </summary>
    /// <returns>Verdadeiro se o radio ficou no estado pedido.</returns>
    public static async Task<bool> SetOn(bool on)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                Log.Write("o Windows negou o acesso aos rádios; o bluetooth não foi alterado");
                return false;
            }

            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null)
            {
                Log.Write("nenhum rádio bluetooth nesta máquina");
                return false;
            }

            var alvo = on ? RadioState.On : RadioState.Off;
            var resposta = await radio.SetStateAsync(alvo);

            Log.Trace($"bluetooth {(on ? "ligado" : "desligado")}: {resposta}");
            return resposta == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Write($"não deu para {(on ? "ligar" : "desligar")} o bluetooth", ex);
            return false;
        }
    }

    /// <summary>Aparelhos pareados — fones, teclados, mouses —, os conectados primeiro.</summary>
    public static IReadOnlyList<BluetoothDevice> Devices()
    {
        var found = new List<BluetoothDevice>();

        var guid = GUID_DEVCLASS_BLUETOOTH;
        var set = SetupDiGetClassDevs(ref guid, null, 0, 0);
        if (set == nint.Zero || set == -1) return found;

        Dictionary<string, int>? services = null;

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

                if (string.IsNullOrWhiteSpace(name)) continue;

                var connected = Connected(set, ref info);
                var battery = Battery(set, ref info);

                // classico conectado sem bateria no aparelho: ela pode estar no servico de
                // maos-livres dele. A lista dos servicos so e montada quando alguem precisa.
                if (battery is null && connected &&
                    id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) &&
                    Text(set, ref info, DEVPKEY_Bluetooth_DeviceAddress) is { } address)
                {
                    services ??= ServiceBatteries();
                    if (services.TryGetValue(address, out var b)) battery = b;
                }

                found.Add(new BluetoothDevice(name!.Trim(), connected, battery));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return found
            .OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Se o aparelho esta conectado <b>agora</b>.
    ///
    /// A pergunta parece a mesma que enumerar so os "presentes" (<c>DIGCF_PRESENT</c>), que
    /// era como isto funcionava antes, mas nao e: presente quer dizer que o Windows tem o
    /// aparelho montado, e o fone continua montado por um tempo depois de desligar.
    /// <c>DEVPKEY_Device_IsConnected</c> e a resposta que o proprio painel do Windows usa.
    ///
    /// Conferido nesta maquina: o teclado (LE) ligado devolve verdadeiro, o fone JBL
    /// desligado devolve falso — enquanto os dois aparecem na lista de pareados.
    /// </summary>
    private static bool Connected(nint set, ref SP_DEVINFO_DATA info) =>
        Byte(set, ref info, DEVPKEY_Device_IsConnected) is { } b && b != 0;

    /// <summary>
    /// Bateria de 0 a 100, ou <c>null</c> quando o aparelho nao informa.
    ///
    /// No proprio aparelho a propriedade so existe para quem publica o servico de bateria —
    /// na pratica, os Bluetooth LE; fone classico guarda a dele num servico, ver
    /// <see cref="ServiceBatteries"/>. Quando ela existe mas vale mais que 100 (o 255 que
    /// aparece em aparelho classico) e um "nao sei", nao um valor.
    /// </summary>
    private static int? Battery(nint set, ref SP_DEVINFO_DATA info) =>
        Byte(set, ref info, DEVPKEY_Bluetooth_Battery) is { } b && b <= 100 ? b : null;

    /// <summary>
    /// Bateria dos aparelhos classicos, pelo endereco de cada um.
    ///
    /// Fone classico informa a bateria pelo perfil de maos-livres, e o Windows guarda o
    /// numero no servico <b>Hands-Free AG</b> daquele aparelho
    /// (<c>BTHENUM\{0000111E-...}_..._&lt;endereco&gt;_...</c>), nao no aparelho em si. Esse
    /// servico nem e da classe Bluetooth (vem como "Sistema"), por isso a busca aqui e por
    /// tudo que o enumerador <c>BTHENUM</c> criou. Conferido nesta maquina: o JBL Tune 720BT
    /// conectado devolve 70% no Hands-Free AG e nada no <c>BTHENUM\DEV_</c>.
    ///
    /// So vale para aparelho conectado: o servico continua montado um tempo depois que o
    /// fone desliga, e o numero que ficou nele e o da ultima conversa.
    /// </summary>
    private static Dictionary<string, int> ServiceBatteries()
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var set = SetupDiGetClassDevs(0, "BTHENUM", 0, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == nint.Zero || set == -1) return found;

        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                if (Battery(set, ref info) is not { } battery) continue;
                if (Text(set, ref info, DEVPKEY_Bluetooth_DeviceAddress) is not { } address) continue;

                found.TryAdd(address, battery);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return found;
    }

    private static string? Text(nint set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        var buffer = new byte[256];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out var type, buffer, buffer.Length, out var size, 0) ||
            type != DEVPROP_TYPE_STRING)
            return null;

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static byte? Byte(nint set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
    {
        var buffer = new byte[8];
        return SetupDiGetDeviceProperty(set, ref info, ref key, out _, buffer, buffer.Length, out var size, 0)
               && size >= 1
            ? buffer[0] : null;
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

    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_ALLCLASSES = 0x04;

    private const uint DEVPROP_TYPE_STRING = 0x12;

    /// <summary>Endereco do aparelho, o mesmo no aparelho e nos servicos dele (texto, "84D352AB7ED7").</summary>
    private static readonly DEVPROPKEY DEVPKEY_Bluetooth_DeviceAddress =
        new() { fmtid = new Guid("2bd67d8b-8beb-48d5-87e0-6cda3428040a"), pid = 1 };

    /// <summary>Bateria do aparelho, 0 a 100 (<c>DEVPROP_TYPE_BYTE</c>).</summary>
    private static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery =
        new() { fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), pid = 2 };

    /// <summary>Conectado agora (<c>DEVPROP_TYPE_BOOLEAN</c>: 0xFF sim, 0 nao).</summary>
    private static readonly DEVPROPKEY DEVPKEY_Device_IsConnected =
        new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 15 };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    /// <summary>Sem classe: junto com <c>DIGCF_ALLCLASSES</c>, tudo de um enumerador.</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref SP_DEVINFO_DATA info,
                                                          StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref SP_DEVINFO_DATA info,
                                                                uint property, out uint type,
                                                                byte[] buffer, int size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key,
                                                        out uint type, byte[] buffer, int size,
                                                        out uint required, uint flags);

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
