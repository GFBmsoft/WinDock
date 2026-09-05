// O mínimo que o BrightnessService precisa do resto do projeto, para ele poder ser
// exercitado sem subir a dock: o log vai para o console e o interop é o mesmo.
//
// POINT fica DENTRO de Native, como no projeto real — o serviço faz
// `using static WinDock.Interop.Native`, e é isso que traz o tipo para o escopo.
using System.Runtime.InteropServices;

namespace WinDock.Services
{
    /// <summary>O serviço grava o diagnóstico ao lado da configuração; aqui vai para o temp.</summary>
    internal static class DockConfig
    {
        public static string Dir => System.IO.Path.GetTempPath();
    }

    internal static class Log
    {
        public static void Trace(string m) => Console.WriteLine($"   [rastro] {m}");
        public static void Write(string m) => Console.WriteLine($"   [log] {m}");
        public static void Write(string m, Exception e) => Console.WriteLine($"   [log] {m} — {e.Message}");
    }
}

namespace WinDock.Interop
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        public static extern nint MonitorFromPoint(POINT point, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint lParam);
    }
}
