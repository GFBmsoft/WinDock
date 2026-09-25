using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Services;

/// <summary>
/// Um aparelho externo que pode ser removido com segurança, já com as letras que ele montou.
///
/// A unidade da lista é o <b>aparelho</b>, e não a letra: um HD externo particionado em duas
/// aparece como um pen-drive só, com "E:, F:" ao lado — é assim que a remoção funciona de
/// verdade (quem sai da máquina é o aparelho inteiro) e é o que o painel do Windows mostra.
/// </summary>
/// <param name="Id">A identidade do nó que será ejetado, do Gerenciador de Dispositivos.</param>
/// <param name="Name">O nome de catálogo do disco — "SanDisk Cruzer Blade USB Device".</param>
/// <param name="Roots">As raízes montadas — <c>E:\</c> —, na ordem alfabética.</param>
/// <param name="Size">Tamanho somado das partições montadas, já escrito para gente ler.</param>
public sealed record RemovableDevice(string Id, string Name, IReadOnlyList<string> Roots, string Size)
{
    /// <summary>"E:" ou "E:, F:" — as raízes sem a barra, que é ruído numa linha de lista.</summary>
    public string Letters => string.Join(", ", Roots.Select(r => r.TrimEnd('\\')));

    /// <summary>A linha do cartão: "E: · 58 GB".</summary>
    public string Detail => string.IsNullOrEmpty(Size) ? Letters : $"{Letters} · {Size}";
}

/// <summary>
/// Os pen-drives e HDs externos ligados agora, e a remoção segura deles.
///
/// <para><b>O caminho é o do próprio Windows, e não um atalho.</b> Ejetar aqui é
/// <c>CM_Request_Device_Eject</c> no nó do aparelho — a mesma chamada por trás do ícone
/// "Remover hardware com segurança" da bandeja. Ela pede a saída ao sistema, que consulta
/// quem está usando o disco e pode <b>vetar</b>; o veto volta com o nome de quem vetou, e é
/// isso que vira a mensagem no cartão. O contrário — desmontar o volume na marra com
/// <c>FSCTL_DISMOUNT_VOLUME</c> — funcionaria mais vezes e é exatamente por isso que não
/// serve: o que ele "resolve" é justamente o aviso de que há escrita pendente.</para>
///
/// <para><b>Por que não basta olhar <c>DriveType</c>.</b> Pen-drive vem como
/// <c>Removable</c>, mas HD externo por USB vem como <c>Fixed</c>, igualzinho ao disco de
/// sistema — a diferença está no barramento, não no tipo de mídia. Então cada letra montada
/// é perguntada ao driver (<c>IOCTL_STORAGE_QUERY_PROPERTY</c>) e só passa quem estiver em
/// USB, FireWire, SD ou MMC. É o que deixa o disco interno de fora sem depender de
/// heurística de nome.</para>
///
/// <para><b>Quem é ejetado não é o disco, é o ancestral removível.</b> O nó do disco quase
/// nunca é o que sai da máquina: acima dele está o aparelho USB, e é esse que carrega a
/// capacidade <c>CM_DEVCAP_REMOVABLE</c>. Ejetar o disco direto devolve sucesso sem tirar
/// nada — o aparelho continua lá e as letras voltam. Por isso se sobe a árvore até achar o
/// primeiro nó removível, e o nome bonito continua vindo do disco, que é quem tem o nome do
/// produto ("Cruzer Blade"); o pai costuma se chamar só "Dispositivo de Armazenamento em
/// Massa USB".</para>
///
/// <para>Nada aqui precisa de administrador: os handles de volume são abertos com acesso
/// <b>zero</b>, que basta para as perguntas de identidade e não dá direito de ler o
/// conteúdo.</para>
/// </summary>
public static class RemovableService
{
    /// <summary>
    /// Os aparelhos externos montados agora.
    ///
    /// Chamada de uma tarefa de fundo: são alguns handles e uns IOCTLs por letra, rápido mas
    /// não instantâneo, e uma letra de rede fora do ar pode demorar a responder.
    /// </summary>
    public static IReadOnlyList<RemovableDevice> Devices()
    {
        var porAparelho = new Dictionary<string, (string Nome, List<string> Letras, long Bytes)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var drive in Drives())
        {
            var letra = drive.Name[..1];

            var numero = DeviceNumber($@"\\.\{letra}:");
            if (numero is not { } disco) continue;

            if (!Externo($@"\\.\{letra}:")) continue;

            var devInst = DiskDevInst(disco);
            if (devInst is not { } disk) continue;

            var alvo = RemovableAncestor(disk);
            if (alvo is not { } aparelho) continue;

            var id = DeviceId(aparelho);
            if (id.Length == 0) continue;

            var nome = Name(disk);
            if (string.IsNullOrWhiteSpace(nome)) nome = Rotulo(drive) ?? "Dispositivo externo";

            if (!porAparelho.TryGetValue(id, out var atual))
                atual = (nome, new List<string>(), 0);

            atual.Letras.Add(drive.Name);
            atual.Bytes += Tamanho(drive);
            porAparelho[id] = atual;
        }

        return porAparelho
            .Select(p => new RemovableDevice(p.Key, p.Value.Nome,
                                             p.Value.Letras.Order(StringComparer.Ordinal).ToList(),
                                             Humano(p.Value.Bytes)))
            .OrderBy(d => d.Letters, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Tenta remover o aparelho com segurança.
    ///
    /// <para>Insiste três vezes com meio segundo de intervalo, porque o veto mais comum é
    /// passageiro: o Windows mesmo costuma estar terminando de gravar o que ficou em cache
    /// quando o pedido chega, e a segunda tentativa passa. Mais que isso seria teimosia — se
    /// um programa está de fato com um arquivo aberto, ele vai vetar a décima igual.</para>
    /// </summary>
    /// <returns>Vazio se saiu; senão, o motivo já escrito para aparecer no cartão.</returns>
    public static async Task<string> Eject(string id)
    {
        var devInst = FindDevNode(id);
        if (devInst is not { } no) return "esse aparelho não está mais aqui";

        string? ultimo = null;

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            var veto = new StringBuilder(260);
            var r = CM_Request_Device_Eject(no, out var tipo, veto, veto.Capacity, 0);

            if (r == CR_SUCCESS && tipo == PNP_VetoTypeUnknown)
            {
                Log.Write($"dispositivo removido com segurança: {id}");
                return string.Empty;
            }

            ultimo = Motivo(r, tipo, veto.ToString());
            Log.Trace($"remoção recusada (tentativa {tentativa}/3): {ultimo}");

            if (tentativa < 3) await Task.Delay(500);
        }

        return ultimo ?? "o Windows não deixou remover agora";
    }

    /// <summary>
    /// O veto em português, e com o culpado quando o Windows o nomeia.
    ///
    /// O nome vem cru — pode ser o caminho de um executável, o nome de um serviço ou o de uma
    /// janela —, então entra entre parênteses no fim em vez de virar a frase: dizer "o
    /// Explorer está usando" quando o texto era um caminho de DLL enganaria mais do que
    /// ajudaria.
    /// </summary>
    private static string Motivo(int codigo, int tipo, string quem)
    {
        var frase = tipo switch
        {
            PNP_VetoLegacyDevice or PNP_VetoPendingClose => "o aparelho ainda está terminando de gravar",
            PNP_VetoWindowsApp or PNP_VetoWindowsService => "um programa ainda está usando o aparelho",
            PNP_VetoOutstandingOpen => "ainda há arquivo aberto no aparelho",
            PNP_VetoDevice or PNP_VetoDriver => "o driver do aparelho não deixou removê-lo agora",
            PNP_VetoIllegalDeviceRequest or PNP_VetoNonDisableable => "este aparelho não pode ser removido por aqui",
            _ when codigo != CR_SUCCESS => "o Windows não deixou remover agora",
            _ => "algo ainda está usando o aparelho"
        };

        return string.IsNullOrWhiteSpace(quem) ? frase : $"{frase} ({quem.Trim()})";
    }

    // ── saber quando algo entra ou sai ──────────────────────

    /// <summary>A mensagem que o Windows manda quando o parque de dispositivos muda.</summary>
    public const int WM_DEVICECHANGE = 0x0219;

    /// <summary>
    /// Pede ao Windows que avise esta janela quando um volume entrar ou sair.
    ///
    /// <para><b>A assinatura explícita existe porque a difusão não basta.</b> O
    /// <c>WM_DEVICECHANGE</c> de volume é difundido para as janelas de primeiro nível, mas a
    /// barra é <c>WS_EX_NOACTIVATE</c> e <c>WS_EX_TOOLWINDOW</c> — e depender de o shell
    /// resolver incluí-la na difusão é apostar a única fonte de atualização da lista numa
    /// coisa que não está escrito em lugar nenhum que vale para janelas assim. Registrada,
    /// ela recebe por direito próprio.</para>
    ///
    /// <para>Não há relógio nenhum por trás desta lista, e é de propósito: perguntar de
    /// tempos em tempos custaria acordar um HD externo adormecido só para saber que ele
    /// continua lá.</para>
    /// </summary>
    /// <returns>O registro, para ser desfeito no fechamento; zero se não deu.</returns>
    public static nint Watch(nint hWnd)
    {
        var filtro = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GUID_DEVINTERFACE_VOLUME,
            dbcc_name = string.Empty
        };

        var registro = RegisterDeviceNotification(hWnd, ref filtro, DEVICE_NOTIFY_WINDOW_HANDLE);

        if (registro == nint.Zero) Log.Write("não deu para assinar os avisos de dispositivo; a lista de remoção só será lida ao abrir o cartão");
        return registro;
    }

    public static void Unwatch(nint registro)
    {
        if (registro != nint.Zero) UnregisterDeviceNotification(registro);
    }

    /// <summary>
    /// Este <c>WM_DEVICECHANGE</c> é dos que mudam a lista?
    ///
    /// Só chegada e saída completa. As outras — o "posso remover?" e o "removi na marra" —
    /// chegam antes de o volume sair de fato, e reler naquele instante devolveria o aparelho
    /// que está de saída, para tirá-lo da lista um aviso depois.
    /// </summary>
    public static bool IsArrivalOrRemoval(nint wParam) =>
        wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE;

    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const uint DBT_DEVTYP_DEVICEINTERFACE = 0x05;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00;

    private static readonly Guid GUID_DEVINTERFACE_VOLUME = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1)]
        public string dbcc_name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterDeviceNotification(nint recipient,
                                                          ref DEV_BROADCAST_DEVICEINTERFACE filter,
                                                          uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(nint handle);

    // ── as perguntas por letra ──────────────────────────────

    /// <summary>
    /// As letras candidatas: as que existem agora, fora a do sistema.
    ///
    /// <para>Tirar a do sistema é economia, não segurança — ele não está em USB e cairia na
    /// peneira do barramento de qualquer jeito —, mas é a letra que sempre existe e
    /// perguntar por ela nunca traria resposta diferente.</para>
    ///
    /// <para><b>Nada aqui encosta no sistema de arquivos.</b> A tentação era filtrar por
    /// <c>DriveInfo.IsReady</c>, que descartaria o leitor de cartão vazio numa linha — mas
    /// ele pergunta o espaço livre ao volume, e perguntar isso a um HD externo adormecido o
    /// acorda. Quem faz o mesmo serviço sem tocar na mídia é o próprio
    /// <see cref="DeviceNumber"/> logo adiante: slot vazio não abre, e a letra cai fora
    /// sozinha. O rótulo e o tamanho, que também acordam o disco, ficam para o fim — só as
    /// letras que já passaram por toda a peneira os pagam.</para>
    /// </summary>
    private static IEnumerable<DriveInfo> Drives()
    {
        var sistema = Path.GetPathRoot(Environment.SystemDirectory);

        // O `yield` não pode morar dentro de um `catch`, então a lista é obtida antes e a
        // falha vira lista vazia — que é o que a barra deve mostrar quando o sistema não
        // responde: nenhum aparelho, e não um erro na cara de quem passava por ali.
        string[] letras = [];
        try { letras = Environment.GetLogicalDrives(); }
        catch (Exception ex) { Log.Write("não deu para listar as unidades", ex); }

        foreach (var nome in letras)
        {
            if (nome.Length < 2 || !char.IsLetter(nome[0])) continue;
            if (string.Equals(nome, sistema, StringComparison.OrdinalIgnoreCase)) continue;

            DriveInfo? d = null;
            try { d = new DriveInfo(nome); } catch { }

            if (d is not null) yield return d;
        }
    }

    /// <summary>O número do disco físico por trás da letra, ou <c>null</c> se ela não tem um.</summary>
    private static uint? DeviceNumber(string caminho)
    {
        using var h = Open(caminho);
        if (h is null) return null;

        var buffer = new byte[12];   // STORAGE_DEVICE_NUMBER: tipo, número, partição
        return DeviceIoControl(h.Handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, nint.Zero, 0,
                               buffer, buffer.Length, out var lidos, nint.Zero) && lidos >= 8
            ? BitConverter.ToUInt32(buffer, 4)
            : null;
    }

    /// <summary>
    /// O disco por trás desta letra está num barramento de aparelho externo?
    ///
    /// A resposta que importa é o <c>BusType</c> do descritor, e não o <c>RemovableMedia</c>
    /// ao lado dele: mídia removível é o disquete e o cartão SD — o HD externo tem mídia
    /// fixa dentro de uma caixa que se desliga da máquina, e é ele o caso principal do botão.
    /// </summary>
    private static bool Externo(string caminho)
    {
        using var h = Open(caminho);
        if (h is null) return false;

        // STORAGE_PROPERTY_QUERY: StorageDeviceProperty (0) + PropertyStandardQuery (0)
        var pergunta = new byte[12];

        var resposta = new byte[512];
        if (!DeviceIoControl(h.Handle, IOCTL_STORAGE_QUERY_PROPERTY, pergunta, pergunta.Length,
                             resposta, resposta.Length, out var lidos, nint.Zero) || lidos < 32)
            return false;

        var bus = BitConverter.ToUInt32(resposta, 28);
        return bus is BusTypeUsb or BusType1394 or BusTypeSd or BusTypeMmc;
    }

    /// <summary>O handle do volume, fechado ao sair do escopo.</summary>
    private sealed class Volume(nint h) : IDisposable
    {
        public nint Handle { get; } = h;
        public void Dispose() => CloseHandle(Handle);
    }

    /// <summary>
    /// Abre o volume só para perguntar quem ele é.
    ///
    /// Acesso <b>zero</b> de propósito: identidade, número do disco e barramento são
    /// perguntas que o driver responde sem dar direito nenhum sobre o conteúdo — pedir
    /// leitura aqui exigiria administrador e daria à dock um poder que ela não usa para nada.
    /// </summary>
    private static Volume? Open(string caminho)
    {
        var h = CreateFile(caminho, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, nint.Zero,
                           OPEN_EXISTING, 0, nint.Zero);

        return h == INVALID_HANDLE_VALUE ? null : new Volume(h);
    }

    // ── da letra até o nó que se ejeta ──────────────────────

    /// <summary>
    /// O nó do Gerenciador de Dispositivos que corresponde ao disco físico de número
    /// <paramref name="numero"/>.
    ///
    /// O caminho é indireto porque não existe um direto: percorre-se a lista de interfaces de
    /// disco, abre-se cada uma e pergunta-se o número dela — o par "interface, nó" vem junto
    /// da enumeração, e é esse nó que se queria desde o começo.
    /// </summary>
    private static uint? DiskDevInst(uint numero)
    {
        var guid = GUID_DEVINTERFACE_DISK;
        var set = SetupDiGetClassDevs(ref guid, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == nint.Zero || set == -1) return null;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };

            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, nint.Zero, ref guid, i, ref iface); i++)
            {
                var detalhe = new SP_DEVICE_INTERFACE_DETAIL_DATA
                {
                    // O tamanho declarado é o do cabeçalho, não o do buffer: 8 em 64 bits
                    // (int + alinhamento), 6 em 32. Com o tamanho do buffer aqui, a API
                    // recusa com ERROR_INVALID_USER_BUFFER e nada funciona.
                    cbSize = nint.Size == 8 ? 8 : 6
                };

                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, ref detalhe,
                                                     Marshal.SizeOf<SP_DEVICE_INTERFACE_DETAIL_DATA>(),
                                                     out _, ref info))
                    continue;

                if (DeviceNumber(detalhe.DevicePath) == numero) return info.DevInst;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return null;
    }

    /// <summary>
    /// Sobe do disco até o primeiro nó que o Windows considera removível — o aparelho que
    /// sai da máquina.
    ///
    /// Quatro níveis bastam com folga (disco → armazenamento em massa → aparelho USB) e
    /// existem como limite, não como expectativa: uma árvore estranha não pode virar laço.
    /// </summary>
    private static uint? RemovableAncestor(uint devInst)
    {
        var atual = devInst;

        for (var nivel = 0; nivel < 4; nivel++)
        {
            if (IsRemovable(atual)) return atual;
            if (CM_Get_Parent(out var pai, atual, 0) != CR_SUCCESS || pai == 0) break;
            atual = pai;
        }

        return null;
    }

    private static bool IsRemovable(uint devInst)
    {
        var buffer = new byte[4];
        var tamanho = (uint)buffer.Length;

        return CM_Get_DevNode_Registry_Property(devInst, CM_DRP_CAPABILITIES, out _, buffer, ref tamanho, 0) == CR_SUCCESS
               && (BitConverter.ToUInt32(buffer, 0) & CM_DEVCAP_REMOVABLE) != 0;
    }

    /// <summary>O nome de catálogo do nó: o do fabricante quando existe, senão a descrição.</summary>
    private static string Name(uint devInst) =>
        Texto(devInst, CM_DRP_FRIENDLYNAME) ?? Texto(devInst, CM_DRP_DEVICEDESC) ?? string.Empty;

    private static string? Texto(uint devInst, uint propriedade)
    {
        var buffer = new byte[512];
        var tamanho = (uint)buffer.Length;

        if (CM_Get_DevNode_Registry_Property(devInst, propriedade, out _, buffer, ref tamanho, 0) != CR_SUCCESS)
            return null;

        var texto = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(tamanho, buffer.Length)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
    }

    private static string DeviceId(uint devInst)
    {
        var buffer = new StringBuilder(512);
        return CM_Get_Device_ID(devInst, buffer, buffer.Capacity, 0) == CR_SUCCESS ? buffer.ToString() : string.Empty;
    }

    /// <summary>
    /// O nó daquela identidade, agora.
    ///
    /// A ejeção não pode guardar o número do nó de quando a lista foi montada: o
    /// <c>DEVINST</c> é um índice vivo da árvore de dispositivos, e entre abrir o cartão e
    /// clicar em "Remover" pode ter entrado ou saído outro aparelho. A identidade em texto é
    /// que é estável — daí ela ser o que a lista carrega, e este ser o primeiro passo da
    /// remoção.
    /// </summary>
    private static uint? FindDevNode(string id) =>
        CM_Locate_DevNode(out var devInst, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS ? devInst : null;

    // ── enfeites ────────────────────────────────────────────

    /// <summary>
    /// O rótulo e o tamanho são perguntas ao sistema de arquivos, e um pen-drive pode ser
    /// arrancado entre uma e outra — aí qualquer coisa do <c>DriveInfo</c> estoura. Numa
    /// lista que se remonta sozinha, o certo é a letra sumir na volta seguinte, e não a barra
    /// cair por causa de um enfeite.
    /// </summary>
    private static string? Rotulo(DriveInfo d)
    {
        try { return string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel; }
        catch { return null; }
    }

    private static long Tamanho(DriveInfo d)
    {
        try { return d.TotalSize; }
        catch { return 0; }
    }

    private static string Humano(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        _ => $"{bytes / (1024.0 * 1024 * 1024 * 1024):0.#} TB"
    };

    // ── interop ─────────────────────────────────────────────

    private static readonly Guid GUID_DEVINTERFACE_DISK = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private const uint BusType1394 = 0x04;
    private const uint BusTypeUsb = 0x07;
    private const uint BusTypeSd = 0x0C;
    private const uint BusTypeMmc = 0x0D;

    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private static readonly nint INVALID_HANDLE_VALUE = -1;

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    private const uint CM_DRP_DEVICEDESC = 0x01;
    private const uint CM_DRP_FRIENDLYNAME = 0x0D;
    private const uint CM_DRP_CAPABILITIES = 0x10;
    private const uint CM_DEVCAP_REMOVABLE = 0x04;

    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00;
    private const int CR_SUCCESS = 0;

    private const int PNP_VetoTypeUnknown = 0;
    private const int PNP_VetoLegacyDevice = 1;
    private const int PNP_VetoPendingClose = 2;
    private const int PNP_VetoWindowsApp = 3;
    private const int PNP_VetoWindowsService = 4;
    private const int PNP_VetoOutstandingOpen = 5;
    private const int PNP_VetoDevice = 6;
    private const int PNP_VetoDriver = 7;
    private const int PNP_VetoIllegalDeviceRequest = 8;
    private const int PNP_VetoNonDisableable = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string DevicePath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFile(string name, uint access, uint share, nint security,
                                          uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint device, uint code, nint inBuffer, int inSize,
                                               byte[] outBuffer, int outSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint device, uint code, byte[] inBuffer, int inSize,
                                               byte[] outBuffer, int outSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid interfaceClass,
                                                           uint index, ref SP_DEVICE_INTERFACE_DATA iface);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
               EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA iface,
                                                               ref SP_DEVICE_INTERFACE_DETAIL_DATA detail,
                                                               int detailSize, out int required,
                                                               ref SP_DEVINFO_DATA info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern int CM_Get_Device_ID(uint devInst, StringBuilder buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static extern int CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint type,
                                                               byte[] buffer, ref uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Request_Device_EjectW")]
    private static extern int CM_Request_Device_Eject(uint devInst, out int vetoType, StringBuilder vetoName,
                                                      int nameLength, uint flags);
}
