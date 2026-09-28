namespace WinDock.Services;

/// <summary>
/// O <c>Log</c> que o <c>UpdatesService</c> chama, reduzido ao que ele usa.
///
/// O <c>Log</c> de verdade escreve em <c>%APPDATA%\WinDock</c> e arrasta o <c>DockConfig</c>
/// junto, que por sua vez arrasta o WPF — trazê-lo para cá transformaria uma conferência de
/// texto numa montagem de interface. Aqui as linhas vão para a saída padrão, onde de todo jeito
/// é útil lê-las quando algo falha.
/// </summary>
internal static class Log
{
    internal static void Trace(string message) => Console.WriteLine("    . " + message);

    internal static void Write(string message) => Console.WriteLine("    ! " + message);

    internal static void Write(string message, Exception ex) =>
        Console.WriteLine("    ! " + message + " — " + ex.Message);
}
