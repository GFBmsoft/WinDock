using System.Runtime.InteropServices;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Brilho do monitor onde a barra está, por DDC/CI.
///
/// <para><b>Por que DDC/CI e não WMI.</b> O caminho "óbvio" seria
/// <c>WmiMonitorBrightnessMethods</c>, mas ele exige o pacote NuGet <c>System.Management</c> —
/// seria a primeira dependência do projeto. O <c>dxva2.dll</c> é nativo do Windows e não custa
/// nada. Medido nesta máquina, e o resultado foi o inverso do esperado: o DDC/CI <b>falha</b> no
/// painel do notebook e <b>funciona</b> no monitor externo (0 a 100), enquanto o WMI faz o
/// oposto. Como a barra vive no monitor principal, o nativo cobre o caso que importa.</para>
///
/// <para><b>A escrita é cara: 61 ms medidos.</b> DDC/CI conversa com o monitor por I²C, e isso
/// não é instantâneo. Arrastar um controle dispararia dezenas dessas chamadas em sequência e
/// travaria a interface — por isso a escrita sai da thread da interface e só o <b>último</b>
/// valor pedido é aplicado (veja <see cref="Set"/>).</para>
/// </summary>
public sealed class BrightnessService : IDisposable
{
    /// <summary>O monitor passou a responder depois do arranque: a barra precisa reavaliar.</summary>
    public event EventHandler? AvailabilityChanged;

    private nint _monitor;          // o monitor físico, mantido aberto entre os ajustes
    private bool _procurou;
    private long _ultimaTentativa;
    private bool _insistindo;
    private int _pendente = -1;
    private Task? _escrevendo;
    private readonly object _trava = new();

    /// <summary>Este monitor aceita ajuste de brilho? Falso num que não fala DDC/CI.</summary>
    public bool Available
    {
        get
        {
            Ensure();

            // primeira negativa: quase sempre é o tumulto do arranque, não o monitor.
            // Passa a insistir em segundo plano e avisa quando conseguir.
            if (_monitor == 0 && !_insistindo) { _insistindo = true; Retentar(); }

            return _monitor != 0;
        }
    }

    /// <summary>Brilho atual, de 0 a 100. Zero quando não há suporte.</summary>
    public int Level
    {
        get
        {
            Ensure();
            if (_monitor == 0) return 0;

            return GetMonitorBrightness(_monitor, out _, out var atual, out var max) && max > 0
                ? (int)Math.Round(atual * 100.0 / max)
                : 0;
        }
    }

    /// <summary>
    /// Pede um brilho novo. Volta na hora: quem escreve é uma tarefa de fundo.
    ///
    /// Enquanto ela aplica um valor, os que chegarem no meio do caminho substituem o
    /// anterior em vez de entrarem numa fila — arrastar um controle gera dezenas de pedidos
    /// por segundo, e aplicar todos deixaria o brilho "correndo atrás" do cursor por
    /// segundos depois de a pessoa ter soltado.
    /// </summary>
    public void Set(int percent)
    {
        Ensure();
        if (_monitor == 0) return;

        Interlocked.Exchange(ref _pendente, Math.Clamp(percent, 0, 100));

        lock (_trava)
        {
            if (_escrevendo is { IsCompleted: false }) return;
            _escrevendo = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            var valor = Interlocked.Exchange(ref _pendente, -1);
            if (valor < 0) return;

            try
            {
                if (!GetMonitorBrightness(_monitor, out var min, out _, out var max)) { Reset(); return; }

                // a escala do monitor nem sempre é 0..100
                var alvo = (uint)Math.Round(min + (max - min) * valor / 100.0);
                if (!SetMonitorBrightness(_monitor, alvo)) Reset();
            }
            catch { Reset(); return; }
        }
    }

    /// <summary>
    /// Insiste em achar o monitor, em segundo plano, enquanto ele não responde.
    ///
    /// <para><b>Por que insistir.</b> A conversa DDC/CI acontece num barramento I²C, que é
    /// serial, lento e sem acesso concorrente. Durante o arranque da barra — que sobe janelas,
    /// lê a bandeja por UIAutomation e conversa com o WinRT ao mesmo tempo — essa conversa
    /// falha: medido nesta máquina, um monitor devolve handle zero e o outro devolve handle
    /// válido e recusa a consulta com <c>ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND</c>
    /// (0xC0262589). O mesmo código, no mesmo monitor, com o mesmo handle, funciona sem falha
    /// num processo de console — e também funciona elevado, com PerMonitorV2, publicado em
    /// arquivo único e em thread STA. Nenhuma condição estática reproduz o problema: a
    /// variável é o instante, não o ambiente.</para>
    ///
    /// <para><b>Por que um laço próprio e não retentativa preguiçosa.</b> A barra consulta
    /// <see cref="Available"/> uma única vez, no arranque — exatamente o pior instante. Ao
    /// receber falso ela esconde o item e nunca mais pergunta, então uma retentativa que só
    /// acontece "na próxima consulta" nunca acontece. Daí o laço ativo, que avisa por
    /// <see cref="AvailabilityChanged"/> quando finalmente consegue.</para>
    /// </summary>
    private void Retentar()
    {
        Task.Run(async () =>
        {
            // meio minuto de tentativas espaçadas: passado o tumulto do arranque, o monitor
            // responde na primeira. Se em 30s não respondeu, é monitor que não fala DDC/CI
            // mesmo (o painel deste notebook é um), e insistir só gastaria I²C à toa.
            for (var i = 0; i < 15 && _monitor == 0; i++)
            {
                await Task.Delay(2000).ConfigureAwait(false);

                Ensure();
                if (_monitor == 0) continue;

                Log.Trace($"brilho — disponível na tentativa {i + 2}");
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        });
    }

    /// <summary>
    /// Acha o monitor físico onde a barra está, uma vez só.
    ///
    /// O handle é guardado porque abri-lo também custa: refazer isso a cada passo de um
    /// arraste somaria ao custo que já é alto da escrita.
    /// </summary>
    private void Ensure()
    {
        if (_monitor != 0) return;

        // Antes, uma falha aqui era definitiva: `_procurou = true` na entrada e pronto, o
        // brilho sumia da barra pelo resto da sessão. E a falha ERA transitória — o mesmo
        // código, no mesmo monitor, devolve o handle sem problema quando roda fora da dock.
        // Como isto custa poucos milissegundos e só repete enquanto não deu certo, uma nova
        // tentativa a cada dois segundos sai de graça e cobre o caso de o monitor ainda não
        // estar pronto para conversar quando a barra sobe.
        var agora = Environment.TickCount64;
        if (_procurou && agora - _ultimaTentativa < 2000) return;

        _procurou = true;
        _ultimaTentativa = agora;

        try
        {
            // Todos os monitores, não só o principal.
            //
            // O DDC/CI é atendido pelo monitor, não pelo Windows: nesta máquina o painel do
            // notebook recusa e o externo aceita, e nada garante que o que aceita seja o
            // principal. Ficar preso ao principal era desistir do ajuste sempre que a barra
            // estivesse no monitor "errado".
            var monitores = new List<nint>();
            EnumDisplayMonitors(0, 0, (nint h, nint _, ref RECT _, nint _) => { monitores.Add(h); return true; }, 0);

            // o principal primeiro: é onde a barra vive, e é o que a pessoa espera ajustar
            var principal = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
            if (principal != 0) { monitores.Remove(principal); monitores.Insert(0, principal); }

            foreach (var h in monitores)
                if (TentarAbrir(h)) return;

            Log.Trace($"brilho — nenhum dos {monitores.Count} monitores aceitou DDC/CI");
        }
        catch (Exception ex) { Log.Trace($"brilho — EXCEÇÃO: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Abre um monitor e fica com ele se responder de verdade à consulta de brilho.</summary>
    private bool TentarAbrir(nint h)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, out var quantos) || quantos == 0)
        { Log.Trace($"brilho —   {h:X}: sem monitor físico (erro {Marshal.GetLastWin32Error()})"); return false; }

        // Buffer nativo, e não um PHYSICAL_MONITOR[] marshalado.
        //
        // A struct tem um campo `string` com `ByValTStr`, o que a torna não-blittable: o
        // marshaller monta uma cópia nativa, chama a função e devolve os dados por conta
        // própria. Aqui o handle voltava ZERO — e o pior, sem erro nenhum: `GetMonitorBrightness(0, …)`
        // respondia sucesso e o serviço se dava por disponível com um handle inútil.
        //
        // Lendo o primeiro campo direto da memória (o handle é o primeiro membro da struct),
        // não há marshalling de struct nenhum para dar errado.
        var tamanho = Marshal.SizeOf<PHYSICAL_MONITOR>();
        var buffer = Marshal.AllocHGlobal(tamanho * (int)quantos);

        try
        {
            if (!GetPhysicalMonitorsFromHMONITOR(h, quantos, buffer))
            { Log.Trace($"brilho —   {h:X}: GetPhysicalMonitors falhou (erro {Marshal.GetLastWin32Error()})"); return false; }

            // varre todos os físicos do adaptador, não só o primeiro
            for (var i = 0; i < quantos; i++)
            {
                var handle = Marshal.ReadIntPtr(buffer + i * tamanho);

                // handle zero nunca serve — era exatamente o que passava antes
                if (handle == 0) { Log.Trace($"brilho —   {h:X}[{i}]: handle zero"); continue; }
                // e só serve se responder: há monitor que devolve handle e recusa a consulta
                if (GetMonitorBrightness(handle, out _, out _, out _))
                {
                    _monitor = handle;
                    Log.Trace($"brilho — OK — monitor {h:X}, handle {handle:X}");
                    return true;
                }

                Log.Trace($"brilho —   {h:X}[{i}]: handle {handle:X} recusou a consulta (erro {Marshal.GetLastWin32Error()})");
                DestroyPhysicalMonitor(handle);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return false;
    }

    /// <summary>O monitor sumiu ou parou de responder (troca de cabo, desligou): esquece.</summary>
    private void Reset()
    {
        _monitor = 0;
        _procurou = false;
    }

    public void Dispose()
    {
        if (_monitor == 0) return;

        try { DestroyPhysicalMonitor(_monitor); } catch { }
        _monitor = 0;
    }

    // ── interop ─────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public nint hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint quantos);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint quantos, nint buffer);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(nint fisico);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(nint fisico, out uint min, out uint atual, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(nint fisico, uint valor);

}
