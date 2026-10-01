using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Uma opção do menu de energia.
///
/// <paramref name="StartsGroup"/> marca onde entra uma linha separadora <b>antes</b> do item:
/// as opções se dividem entre o que mexe só na sua sessão (bloquear, sair) e o que mexe na
/// máquina inteira (suspender, hibernar, reiniciar, desligar). São consequências de tamanhos
/// bem diferentes, e a linha existe para o olho notar a fronteira antes de o dedo passar por
/// ela — é o que o menu do GNOME faz entre "Lock" e "Power Off".
///
/// <paramref name="Color"/> é a cor do desenho no cartão da barra, no idioma das ferramentas e
/// do processador: fria no que só mexe na sessão, quente no que desliga — o vermelho fica só
/// com o Desligar, que é o mesmo vermelho do botão na barra.
/// </summary>
public sealed record PowerCommand(string Name, string Glyph, string Description, Action Run,
                                  bool StartsGroup = false, string Color = "#FFF2F2F2")
{
    /// <summary>A mesma cor, bem transparente: o fundo redondo do ladrilho no cartão.</summary>
    public string Tint => Color.Length == 9 ? "#30" + Color[3..] : Color;
}

/// <summary>
/// Desligar, reiniciar, hibernar, suspender, bloquear e encerrar a sessão.
///
/// A lista mora aqui, e não no menu que a mostra, porque ela é usada em dois lugares: o
/// botão da barra de cima e a busca do <c>Alt+Espaço</c>. Duas cópias divergiriam com o
/// tempo, e o pior dos casos é o comando errado atrás do rótulo certo.
/// </summary>
public static class PowerService
{
    /// <summary>
    /// Os glifos da Segoe Fluent Icons, escritos pelo código e não pelo caractere: no
    /// editor eles são um quadradinho vazio, e um deles já se perdeu numa edição — o botão
    /// de energia ficou invisível na barra sem nada quebrar nem avisar.
    /// </summary>
    public const string Power = "";      // botão de energia
    private const string Lock = "";      // cadeado
    private const string SignOut = "";   // porta com seta para fora
    private const string Restart = "";   // seta circular

    /// <summary>
    /// Suspender e hibernar ficam com duas luas parecidas de propósito: o Windows não dá
    /// ícone nenhum a elas, e quem separa as duas é o nome ao lado — não o desenho.
    /// </summary>
    private const string Moon = "";
    private const string MoonThin = "";

    /// <summary>
    /// Na ordem em que aparecem no menu: as que interrompem o trabalho por último. Quem
    /// erra o clique em "Bloquear" volta com a senha; quem erra em "Desligar" perde o que
    /// não salvou, então esse fica longe da borda de cima, onde o cursor chega primeiro.
    /// </summary>
    public static IReadOnlyList<PowerCommand> All { get; } = new[]
    {
        new PowerCommand("Bloquear", Lock, "Tranca a tela sem fechar nada",
                         () => LockWorkStation(), Color: "#FF6CB6FF"),

        new PowerCommand("Encerrar sessão", SignOut, "Fecha seus programas e volta ao logon",
                         () => Shutdown("/l"), Color: "#FFB08AE8"),

        // daqui para baixo o efeito é na máquina, não só na sua sessão: a linha separadora
        // entra aqui
        new PowerCommand("Suspender", Moon, "Desliga a tela e dorme; volta na hora",
                         () => SetSuspendState(false, false, false), StartsGroup: true, Color: "#FF4FD1C5"),

        new PowerCommand("Hibernar", MoonThin, "Salva a sessão em disco e desliga",
                         () => SetSuspendState(true, false, false), Color: "#FF7FB2F0"),

        new PowerCommand("Reiniciar", Restart, "Fecha tudo e liga de novo",
                         () => Shutdown("/r /t 0"), Color: "#FFF4A261"),

        new PowerCommand("Desligar", Power, "Fecha tudo e desliga a máquina",
                         () => Shutdown("/s /t 0"), Color: "#FFFF6B6B")
    };

    /// <summary>
    /// O <c>shutdown.exe</c> em vez da API <c>ExitWindowsEx</c>: ele já pede o privilégio
    /// de desligamento sozinho, já negocia com os programas que têm coisa por salvar, e o
    /// que aparece na tela é o diálogo do próprio Windows.
    /// </summary>
    private static void Shutdown(string args)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex) { Log.Write($"falha no comando de energia 'shutdown {args}'", ex); }
    }

    // ── o que o cartão mostra além das ações ─────────────────
    //
    // Pedido de 01/10/2026: o cartão só tinha a conta e as ações, e o do Windows diz em que
    // estado a máquina está. Daqui saem os planos de energia, há quanto tempo ela está ligada
    // e quanto a bateria ainda dura — tudo lido na hora em que o cartão abre, sem relógio nenhum
    // por trás.

    /// <summary>
    /// Os planos de energia da máquina (Equilibrado, Alto desempenho...), o ativo marcado.
    ///
    /// Pela <c>powrprof</c>, que é o que o <c>powercfg</c> usa, e não pela saída dele: o texto é
    /// traduzido e muda de formato entre versões. O mesmo plano pode vir repetido na
    /// enumeração (o <c>powercfg /list</c> desta máquina lista o Alto desempenho duas vezes), por
    /// isso a lista sai sem repetidos.
    /// </summary>
    public static IReadOnlyList<PowerPlan> Plans()
    {
        var ativo = ActivePlan();
        var planos = new List<PowerPlan>();

        for (uint i = 0; ; i++)
        {
            var tamanho = 16u;
            var buffer = new byte[16];
            var erro = PowerEnumerate(0, 0, 0, ACCESS_SCHEME, i, buffer, ref tamanho);
            if (erro == ERROR_NO_MORE_ITEMS || erro != 0) break;

            var id = new Guid(buffer);
            if (planos.Any(p => p.Id == id)) continue;
            planos.Add(new PowerPlan(id, FriendlyName(id), id == ativo));
        }

        return planos;
    }

    private static string FriendlyName(Guid id)
    {
        uint tamanho = 0;
        PowerReadFriendlyName(0, ref id, 0, 0, null, ref tamanho);
        if (tamanho == 0) return id.ToString();

        var buffer = new byte[tamanho];
        if (PowerReadFriendlyName(0, ref id, 0, 0, buffer, ref tamanho) != 0) return id.ToString();
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static Guid ActivePlan()
    {
        if (PowerGetActiveScheme(0, out var ptr) != 0 || ptr == 0) return Guid.Empty;
        try { return System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    /// <summary>Troca o plano ativo — o mesmo efeito de escolhê-lo nas Opções de Energia.</summary>
    public static bool SetPlan(Guid id)
    {
        var erro = PowerSetActiveScheme(0, ref id);
        if (erro != 0) Log.Write($"plano de energia {id}: PowerSetActiveScheme devolveu {erro}");
        return erro == 0;
    }

    /// <summary>"Ligado há 6 h 22 min" — desde o último arranque de verdade (a hibernação não zera).</summary>
    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (t.TotalDays >= 1) return $"Ligado há {(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"Ligado há {(int)t.TotalHours} h {t.Minutes} min";
        return $"Ligado há {Math.Max(1, t.Minutes)} min";
    }

    /// <summary>
    /// A linha da bateria embaixo da porcentagem: na tomada, carregando ou quanto ainda dura.
    /// O Windows só sabe estimar o tempo depois de alguns minutos fora da tomada — antes disso
    /// diz "calculando", como a tela dele.
    /// </summary>
    public static string BatteryDetail()
    {
        if (!GetSystemPowerStatus(out var s)) return string.Empty;
        if (s.ACLineStatus == 1)
            return s.BatteryLifePercent >= 100 ? "Na tomada, carga completa" : "Na tomada, carregando";

        if (s.BatteryLifeTime == uint.MaxValue) return "Na bateria — calculando o tempo restante";
        var t = TimeSpan.FromSeconds(s.BatteryLifeTime);
        return t.TotalHours >= 1
            ? $"Na bateria — {(int)t.TotalHours} h {t.Minutes} min restantes"
            : $"Na bateria — {t.Minutes} min restantes";
    }
}

/// <summary>Um plano de energia do Windows, como aparece nas Opções de Energia.</summary>
public sealed record PowerPlan(Guid Id, string Name, bool Active);
