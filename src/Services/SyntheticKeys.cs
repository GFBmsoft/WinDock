using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// As teclas que o próprio WinDock aperta — e o carimbo que permite reconhecê-las depois.
///
/// O Esc é a única forma de fechar o painel de ícones ocultos do Windows (ele não é uma janela
/// que se possa mandar fechar), então a leitura da bandeja aperta Esc várias vezes por minuto.
/// Ao mesmo tempo, a barra **escuta** o Esc por <c>GetAsyncKeyState</c> para fechar os cartões —
/// ela é <c>WS_EX_NOACTIVATE</c> e nunca recebe evento de tecla, então escutar é o único caminho.
///
/// As duas coisas juntas se mordiam: o Esc que a leitura mandava para o shell era lido pela barra
/// como "a pessoa apertou Esc", e o cartão da bandeja se fechava sozinho 70 ms depois de abrir —
/// que era o tempo entre a leitura terminar e o vigia olhar o teclado. Do lado de fora parecia que
/// o clique é que tinha fechado.
///
/// O sistema não distingue tecla sintética de tecla física (o <c>GetAsyncKeyState</c> responde
/// igual para as duas), então quem tem de distinguir é quem manda: aqui fica o instante do último
/// Esc nosso, e quem escuta pergunta antes de reagir.
/// </summary>
public static class SyntheticKeys
{
    private static long _lastEscape;

    /// <summary>
    /// Quanto tempo um Esc nosso continua "nosso".
    ///
    /// Cobre com folga o pior caso do caminho: o vigia dos cartões olha o teclado a cada 100 ms,
    /// e o bit que ele lê acumula desde a consulta anterior. Meio segundo é curto o bastante para
    /// não engolir um Esc de verdade dado logo em seguida — e se engolir um, a pessoa aperta de
    /// novo; o contrário (o cartão fechando sozinho) não tem como ser desfeito.
    /// </summary>
    private static readonly TimeSpan Janela = TimeSpan.FromMilliseconds(500);

    /// <summary>Aperta Esc, deixando o carimbo antes — a ordem importa: quem escuta pode
    /// perguntar no mesmo instante em que a tecla chega.</summary>
    public static void SendEscape()
    {
        Volatile.Write(ref _lastEscape, DateTime.UtcNow.Ticks);

        keybd_event(VK_ESCAPE, 0, 0, 0);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>O Esc que acabou de ser detectado foi apertado por nós?</summary>
    public static bool EscapeWasOurs()
    {
        var quando = new DateTime(Volatile.Read(ref _lastEscape), DateTimeKind.Utc);
        return DateTime.UtcNow - quando < Janela;
    }
}
