using Microsoft.Win32;

namespace WinDock.Services;

/// <summary>
/// A faixa do Windows: o balão que o próprio Windows mostra no canto da tela quando chega uma
/// notificação. Com o balão da dock ligado, cada notificação aparecia duas vezes (pedido de
/// 06/11/2026); a dock desliga a do Windows app por app, como o "Mostrar faixas de notificação" de
/// Configurações › Sistema › Notificações, e a notificação continua indo para a Central — que é de
/// onde a dock lê.
///
/// <para>É o valor <c>ShowBanner</c> (DWORD 0) na chave de cada app em
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\&lt;AppUserModelID&gt;</c>,
/// o mesmo que as Configurações gravam. Conferido em 06/11/2026: gravado direto no registro, vale já
/// na notificação seguinte, sem reiniciar nada — a faixa some e a notificação chega à Central e ao
/// sino. O AppUserModelID com barra (<c>{1AC14E77-…}\WindowsPowerShell\v1.0\powershell.exe</c>)
/// mora em chaves aninhadas, como o Windows também faz.</para>
///
/// <para>Não há interruptor para todos os apps de uma vez. Por isso a dock passa nos que o Windows
/// já conhece ao abrir e em cada app novo que manda notificação; a primeira notificação de um app
/// que nunca apareceu ainda sai com faixa, porque quando a dock a vê ela já foi mostrada.</para>
/// </summary>
public static class WindowsBanner
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string Value = "ShowBanner";

    /// <summary>
    /// Desliga a faixa do app. Verdadeiro só quando foi a dock que desligou agora: a que já estava
    /// desligada (pela pessoa, nas Configurações) não conta, e não volta quando a opção sai.
    /// </summary>
    public static bool Hide(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        try
        {
            using var chave = Registry.CurrentUser.CreateSubKey($@"{Root}\{aumid}", writable: true);
            if (chave.GetValue(Value) is int atual && atual == 0) return false;
            chave.SetValue(Value, 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"a faixa do Windows de {aumid} continua ligada", ex);
            return false;
        }
    }

    /// <summary>Devolve a faixa do app ao padrão do Windows (ligada), se ela ainda está desligada.</summary>
    public static void Restore(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return;
        try
        {
            using var chave = Registry.CurrentUser.OpenSubKey($@"{Root}\{aumid}", writable: true);
            if (chave?.GetValue(Value) is int atual && atual == 0) chave.DeleteValue(Value, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write($"a faixa do Windows de {aumid} não voltou", ex);
        }
    }

    /// <summary>
    /// Os apps que o Windows já conhece: cada chave com valores abaixo de <c>Settings</c>. A chave
    /// sem valores é só um pedaço de AppUserModelID com barra, e o app está mais para dentro.
    /// </summary>
    public static List<string> Known()
    {
        var apps = new List<string>();
        try
        {
            using var raiz = Registry.CurrentUser.OpenSubKey(Root);
            if (raiz is not null) Walk(raiz, "", apps);
        }
        catch (Exception ex)
        {
            Log.Write("a lista de apps das notificações do Windows não pôde ser lida", ex);
        }
        return apps;
    }

    private static void Walk(RegistryKey chave, string caminho, List<string> apps)
    {
        foreach (var nome in chave.GetSubKeyNames())
        {
            var app = caminho.Length == 0 ? nome : $@"{caminho}\{nome}";
            using var filha = chave.OpenSubKey(nome);
            if (filha is null) continue;
            if (filha.ValueCount > 0) apps.Add(app);
            else Walk(filha, app, apps);
        }
    }
}
