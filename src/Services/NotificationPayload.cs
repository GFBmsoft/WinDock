using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace WinDock.Services;

/// <summary>
/// O XML inteiro de uma notificação, lido do banco onde o Windows guarda a Central
/// (<c>%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db</c>).
///
/// <para><b>Existe porque a API corta o que importa.</b> O <c>GetTextElements</c> do
/// <c>UserNotificationListener</c> devolve só título e corpo: o texto
/// <c>placement="attribution"</c>, onde o Chrome escreve o site ("web.whatsapp.com"), fica de fora
/// — visto em 02/10/2026 (EV26): a mensagem do WhatsApp Web chegou como "Google Chrome · Astrid
/// Rossa · 1 nova mensagem", sem nada dizendo de onde. O banco tem o XML como o app mandou, com a
/// atribuição e o <c>launch</c>, que traz o endereço da página.</para>
///
/// <para>Pelo <c>winsqlite3.dll</c>, o SQLite que vem no próprio Windows — a dock continua sem
/// dependência nenhuma. Aberto só para leitura, uma consulta por notificação nova (o resultado
/// fica guardado pelo Id), e qualquer falha devolve nulo: é um enfeite, não pode derrubar a
/// lista.</para>
/// </summary>
public static class NotificationPayload
{
    private static readonly string DbPath = Environment.ExpandEnvironmentVariables(
        @"%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db");

    private static readonly Dictionary<uint, string?> Sites = new();

    /// <summary>
    /// O site de uma notificação do navegador, pela atribuição ou pelo endereço do <c>launch</c>;
    /// vazio quando o XML não diz, ou não pôde ser lido.
    /// </summary>
    public static string SiteOf(uint id, Func<string, string> host)
    {
        if (Sites.TryGetValue(id, out var guardado)) return guardado ?? "";

        string? site = null;
        try
        {
            if (Read(id) is { } xml) site = SiteFromXml(xml, host);
        }
        catch (Exception ex)
        {
            Log.Write($"não deu para ler o XML da notificação {id}", ex);
        }

        Sites[id] = site;
        return site ?? "";
    }

    /// <summary>O site escrito no XML da notificação: a atribuição, senão o endereço no <c>launch</c>.</summary>
    public static string SiteFromXml(string xml, Func<string, string> host)
    {
        var toast = XElement.Parse(xml);

        var site = toast.Descendants("text")
            .Where(t => (string?)t.Attribute("placement") == "attribution")
            .Select(t => host(t.Value.Trim()))
            .FirstOrDefault(s => s.Length > 0);
        if (!string.IsNullOrEmpty(site)) return site;

        // sem atribuição, o endereço da página vem no launch, entre outros campos do Chrome
        // separados por "|"
        return ((string?)toast.Attribute("launch") ?? "")
            .Split('|', '#', ' ')
            .Select(p => p.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? host(p) : "")
            .FirstOrDefault(s => s.Length > 0) ?? "";
    }

    /// <summary>O XML da notificação, ou nulo se ela já saiu do banco.</summary>
    private static string? Read(uint id)
    {
        if (!File.Exists(DbPath)) return null;

        // somente leitura, pela URI: o serviço de notificações mantém o banco aberto
        var uri = "file:///" + DbPath.Replace('\\', '/') + "?mode=ro";
        if (sqlite3_open_v2(Utf8(uri), out var db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, 0) != 0)
        {
            sqlite3_close(db);
            return null;
        }

        try
        {
            if (sqlite3_prepare_v2(db, Utf8("select Payload from Notification where Id = ?"), -1, out var st, 0) != 0)
                return null;
            try
            {
                sqlite3_bind_int64(st, 1, id);
                if (sqlite3_step(st) != SQLITE_ROW) return null;

                var tamanho = sqlite3_column_bytes(st, 0);
                var dados = sqlite3_column_blob(st, 0);
                if (dados == 0 || tamanho <= 0) return null;

                var bytes = new byte[tamanho];
                Marshal.Copy(dados, bytes, 0, tamanho);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { sqlite3_finalize(st); }
        }
        finally { sqlite3_close(db); }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private const int SQLITE_OPEN_READONLY = 0x01;
    private const int SQLITE_OPEN_URI = 0x40;
    private const int SQLITE_ROW = 100;

    [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] file, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint stmt, nint tail);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_bind_int64(nint stmt, int index, long value);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_bytes(nint stmt, int col);
    [DllImport("winsqlite3.dll")] private static extern nint sqlite3_column_blob(nint stmt, int col);
    [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(nint stmt);
}
