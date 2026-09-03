using System.Runtime.InteropServices;

namespace WinDock.Services;

/// <summary>
/// Volume do alto-falante padrao, pelo Core Audio do Windows — o mesmo que o painel de
/// volume mexe. Sao tres interfaces COM sem biblioteca de apoio: o enumerador de
/// dispositivos, o dispositivo e o controle de volume dele.
///
/// A ordem dos metodos nas interfaces e o que importa aqui: COM chama por posicao na
/// tabela, nao por nome. Por isso os metodos que nao usamos continuam declarados, so com
/// nome generico — tirar um deslocaria todos os seguintes.
/// </summary>
public static class VolumeService
{
    public static bool Available => Endpoint() is not null;

    /// <summary>Volume atual, de 0 a 100. Zero quando nao ha dispositivo.</summary>
    public static int Level
    {
        get
        {
            var endpoint = Endpoint();
            if (endpoint is null) return 0;
            return endpoint.GetMasterVolumeLevelScalar(out var scalar) == 0
                ? (int)Math.Round(scalar * 100) : 0;
        }
        set
        {
            var endpoint = Endpoint();
            if (endpoint is null) return;

            var scalar = Math.Clamp(value, 0, 100) / 100f;
            var context = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar(scalar, ref context);
        }
    }

    public static bool Muted
    {
        get
        {
            var endpoint = Endpoint();
            return endpoint is not null && endpoint.GetMute(out var muted) == 0 && muted;
        }
        set
        {
            var endpoint = Endpoint();
            if (endpoint is null) return;

            var context = Guid.Empty;
            endpoint.SetMute(value, ref context);
        }
    }

    /// <summary>Uma saida de audio: o nome que aparece no Windows e se ela e a atual.</summary>
    public sealed record AudioDevice(string Id, string Name, bool IsDefault);

    /// <summary>Saidas de audio ligadas — alto-falantes, fones, o audio do monitor.</summary>
    public static IReadOnlyList<AudioDevice> Devices()
    {
        var found = new List<AudioDevice>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out var collection) != 0)
                return found;

            var currentId = string.Empty;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var current) == 0)
                current.GetId(out currentId);

            collection.GetCount(out var count);
            for (var i = 0u; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                if (device.GetId(out var id) != 0) continue;

                found.Add(new AudioDevice(id, FriendlyName(device), id == currentId));
            }
        }
        catch { /* sem dispositivo de audio: a lista fica vazia */ }

        return found;
    }

    /// <summary>
    /// Troca a saida padrao. A interface que faz isso (<c>IPolicyConfig</c>) nao esta
    /// documentada — e a mesma que o painel de som usa por dentro, e o unico caminho sem
    /// pedir para a pessoa abrir as configuracoes do Windows.
    /// </summary>
    public static void SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { 0, 1, 2 })   // console, multimidia e comunicacao
                policy.SetDefaultEndpoint(id, role);
        }
        catch { /* versao do Windows sem essa interface: nada muda */ }
    }

    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(StorageRead, out var store) != 0) return "Saída de áudio";

            var key = PKEY_Device_FriendlyName;
            store.GetValue(ref key, out var value);
            try
            {
                return value.vt == VT_LPWSTR
                    ? Marshal.PtrToStringUni(value.p) ?? "Saída de áudio"
                    : "Saída de áudio";
            }
            finally { PropVariantClear(ref value); }
        }
        catch { return "Saída de áudio"; }
    }

    // ── volume por aplicativo ───────────────────────────────

    /// <summary>
    /// Um programa tocando som agora, com o volume dele — o que o mixer do Windows mostra.
    ///
    /// Guarda a interface COM da sessao em vez de reabri-la a cada ajuste: arrastar o
    /// controle dispara dezenas de escritas por segundo, e cada uma custaria enumerar as
    /// sessoes do dispositivo de novo.
    /// </summary>
    public sealed class AppSession : System.ComponentModel.INotifyPropertyChanged, IDisposable
    {
        private readonly ISimpleAudioVolume _volume;

        internal AppSession(ISimpleAudioVolume volume, uint processId, string name,
                            System.Windows.Media.Imaging.BitmapSource? icon)
        {
            _volume = volume;
            ProcessId = processId;
            Name = name;
            Icon = icon;
        }

        public uint ProcessId { get; }
        public string Name { get; }
        public System.Windows.Media.Imaging.BitmapSource? Icon { get; }
        public bool HasIcon => Icon is not null;

        /// <summary>Primeira letra, para o circulo que aparece quando nao ha icone.</summary>
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

        public int Level
        {
            get => _volume.GetMasterVolume(out var level) == 0 ? (int)Math.Round(level * 100) : 0;
            set
            {
                var context = Guid.Empty;
                _volume.SetMasterVolume(Math.Clamp(value, 0, 100) / 100f, ref context);
                Changed(nameof(Level));
                Changed(nameof(Glyph));
            }
        }

        public bool Muted
        {
            get => _volume.GetMute(out var muted) == 0 && muted;
            set
            {
                var context = Guid.Empty;
                _volume.SetMute(value, ref context);
                Changed(nameof(Muted));
                Changed(nameof(Glyph));
            }
        }

        /// <summary>O mesmo desenho de alto-falante da barra, aplicado a este programa.</summary>
        public string Glyph => Muted || Level == 0 ? ""
                             : Level < 33          ? ""
                             : Level < 66          ? ""
                                                   : "";

        public void Dispose()
        {
            try { Marshal.ReleaseComObject(_volume); } catch { }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Os programas com som na saida atual.
    ///
    /// Sessoes "expiradas" ficam de fora — sao programas que ja fecharam e cuja sessao o
    /// Windows ainda nao recolheu; elas apareceriam no mixer como linhas mortas. As
    /// inativas ficam: e o programa que existe e esta em silencio no momento, e poder
    /// ajusta-lo antes de ele tocar e metade da utilidade disto.
    /// </summary>
    public static IReadOnlyList<AppSession> Sessions()
    {
        var found = new List<AppSession>();

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0)
                return found;

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, 0, out var raw) != 0 ||
                raw is not IAudioSessionManager2 manager) return found;

            if (manager.GetSessionEnumerator(out var sessions) != 0) return found;
            if (sessions.GetCount(out var count) != 0) return found;

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0) continue;
                if (control is not IAudioSessionControl2 control2) continue;

                if (control.GetState(out var state) == 0 && state == SessionExpired) continue;
                if (control2.GetProcessId(out var pid) != 0) continue;

                // a sessao de "sons do sistema" nao tem processo proprio e nao e um app
                if (control2.IsSystemSoundsSession() == 0) continue;

                if (control is not ISimpleAudioVolume volume) continue;

                var (name, icon) = Describe(pid, control);
                found.Add(new AppSession(volume, pid, name, icon));
            }
        }
        catch { /* sem audio ou sem sessoes: a lista fica vazia */ }

        return found.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Nome e icone do programa dono da sessao. O caminho do executavel da os dois de uma
    /// vez e e o que o mixer do Windows mostra; o <c>GetDisplayName</c> da sessao so vem
    /// preenchido em alguns programas, entao serve de reserva.
    /// </summary>
    private static (string Name, System.Windows.Media.Imaging.BitmapSource? Icon) Describe(
        uint pid, IAudioSessionControl control)
    {
        var path = string.Empty;
        var handle = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle != 0)
        {
            try
            {
                var size = 1024;
                var text = new System.Text.StringBuilder(size);
                if (QueryFullProcessImageName(handle, 0, text, ref size)) path = text.ToString();
            }
            finally { CloseHandle(handle); }
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0 && control.GetDisplayName(out var display) == 0 && !string.IsNullOrWhiteSpace(display))
            name = display;

        return (name.Length > 0 ? name : "Aplicativo", path.Length > 0 ? IconService.For(path) : null);
    }

    private const int SessionExpired = 2;

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags,
                                                         System.Text.StringBuilder text, ref int size);

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // os dois primeiros vem do IAudioSessionManager: a tabela COM e herdada
        [PreserveSig] int GetAudioSessionControl(ref Guid session, int flags, out IAudioSessionControl control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, int flags, out ISimpleAudioVolume volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(nint notification);
        [PreserveSig] int UnregisterSessionNotification(nint notification);
        [PreserveSig] int RegisterDuckNotification(string sessionId, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // repete o IAudioSessionControl inteiro: a ordem da tabela e o que vale
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>
    /// O dispositivo padrao pode trocar (fone que entra, monitor que sai), entao ele e
    /// buscado a cada uso em vez de guardado. A chamada e barata.
    /// </summary>
    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device) != 0)
                return null;

            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, ClsCtxAll, 0, out var control) == 0
                ? control as IAudioEndpointVolume : null;
        }
        catch { return null; }
    }

    private const int DataFlowRender = 0;    // saida de audio
    private const int RoleMultimedia = 1;
    private const int ClsCtxAll = 23;
    private const int DeviceStateActive = 1;
    private const int StorageRead = 0;
    private const ushort VT_LPWSTR = 31;

    /// <summary>System.Devices.FriendlyName — o nome que aparece no painel de som.</summary>
    private static PropertyKey PKEY_Device_FriendlyName => new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public nint p;
        public nint p2;
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // so o ultimo metodo interessa, mas a ordem da tabela COM precisa ser respeitada
        [PreserveSig] int GetMixFormat(string device, out nint format);
        [PreserveSig] int GetDeviceFormat(string device, bool isDefault, out nint format);
        [PreserveSig] int ResetDeviceFormat(string device);
        [PreserveSig] int SetDeviceFormat(string device, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string device, bool isDefault, out long defaultPeriod,
                                              out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string device, ref long period);
        [PreserveSig] int GetShareMode(string device, out nint mode);
        [PreserveSig] int SetShareMode(string device, ref nint mode);
        [PreserveSig] int GetPropertyValue(string device, bool store, ref PropertyKey key,
                                           out PropVariant value);
        [PreserveSig] int SetPropertyValue(string device, bool store, ref PropertyKey key,
                                           ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
        [PreserveSig] int SetEndpointVisibility(string device, bool visible);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int contexto, nint activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint client);
        [PreserveSig] int UnregisterControlChangeNotify(nint client);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float decibels, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float decibels);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
    }
}
