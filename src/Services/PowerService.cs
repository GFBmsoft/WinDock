using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Uma opção do menu de energia: o nome, o glifo e o que ela faz.</summary>
/// <summary>
/// Uma opção do menu de energia.
///
/// <paramref name="StartsGroup"/> marca onde entra uma linha separadora <b>antes</b> do item:
/// as opções se dividem entre o que mexe só na sua sessão (bloquear, sair) e o que mexe na
/// máquina inteira (suspender, hibernar, reiniciar, desligar). São consequências de tamanhos
/// bem diferentes, e a linha existe para o olho notar a fronteira antes de o dedo passar por
/// ela — é o que o menu do GNOME faz entre "Lock" e "Power Off".
/// </summary>
public sealed record PowerCommand(string Name, string Glyph, string Description, Action Run,
                                  bool StartsGroup = false);

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
                         () => LockWorkStation()),

        new PowerCommand("Encerrar sessão", SignOut, "Fecha seus programas e volta ao logon",
                         () => Shutdown("/l")),

        // daqui para baixo o efeito é na máquina, não só na sua sessão: a linha separadora
        // entra aqui
        new PowerCommand("Suspender", Moon, "Desliga a tela e dorme; volta na hora",
                         () => SetSuspendState(false, false, false), StartsGroup: true),

        new PowerCommand("Hibernar", MoonThin, "Salva a sessão em disco e desliga",
                         () => SetSuspendState(true, false, false)),

        new PowerCommand("Reiniciar", Restart, "Fecha tudo e liga de novo",
                         () => Shutdown("/r /t 0")),

        new PowerCommand("Desligar", Power, "Fecha tudo e desliga a máquina",
                         () => Shutdown("/s /t 0"))
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
}
