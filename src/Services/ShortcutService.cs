using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Um .lnk ja lido: alvo, argumentos e o AppUserModelID que ele carrega.</summary>
public sealed record Shortcut(string LnkPath, string Target, string Arguments, string Aumid,
                              string IconPath, int IconIndex)
{
    public string Name => Path.GetFileNameWithoutExtension(LnkPath);
}

/// <summary>
/// Le os atalhos que o Windows ja conhece (os fixados na barra de tarefas e os do menu
/// iniciar). Sao eles que guardam o AppUserModelID, os argumentos e o icone certo — e o
/// caminho para fixar "Chrome perfil 2" em vez de so "chrome.exe".
/// </summary>
public static class ShortcutService
{
    private static List<Shortcut>? _cache;
    private static readonly object Gate = new();

    /// <summary>Atalhos fixados na barra de tarefas, primeiro; depois o menu iniciar.</summary>
    public static IReadOnlyList<Shortcut> All()
    {
        // o lock existe porque o aquecimento roda numa thread propria: sem ele, a primeira
        // atualizacao da dock e o Warm varreriam o menu iniciar inteiro duas vezes, em
        // paralelo, no minuto mais disputado da maquina
        lock (Gate) return _cache ??= Scan();
    }

    public static void Invalidate() { lock (Gate) _cache = null; }

    /// <summary>
    /// Os atalhos **se já estiverem lidos** — nunca esperando por eles.
    ///
    /// O <see cref="All"/> segura quem chama enquanto a varredura acontece, e era isso que
    /// atrasava a dock a subir: o aquecimento já estava no meio do menu iniciar quando a
    /// primeira atualização pedia o atalho de cada app aberto, e a interface ficava parada
    /// no <c>lock</c> até a última chamada COM voltar. Os apps abertos apareciam só depois.
    ///
    /// Quem só quer enfeitar o botão (nome e ícone certos) usa isto e aceita um "ainda não":
    /// a dock desenha com o que dá para saber do executável e se corrige sozinha quando o
    /// <see cref="Ready"/> avisa.
    /// </summary>
    private static List<Shortcut>? Loaded => Volatile.Read(ref _cache);

    /// <summary>Avisa que a varredura terminou e os atalhos já podem ser consultados.</summary>
    public static event Action? Ready;

    /// <summary>
    /// Le os atalhos numa thread propria, antes que alguem precise deles.
    ///
    /// Sao umas duzentas chamadas COM ao shell — meio segundo com o disco quente, bem mais
    /// no logon — e elas ficavam no caminho da primeira atualizacao da dock, ou seja, entre
    /// o logon e a barra aparecer. Aqui saem da frente: a dock pinta com os fixados e a
    /// varredura termina em paralelo.
    /// </summary>
    public static void Warm()
    {
        var thread = new Thread(() =>
        {
            try { All(); } catch { /* so adiantar trabalho */ }
            try { Ready?.Invoke(); } catch { /* quem ouve que se vire */ }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);   // o COM do shell pede STA
        thread.Start();
    }

    /// <summary>Acha o atalho que corresponde a um AppUserModelID de janela.</summary>
    public static Shortcut? ByAumid(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        return Loaded?.FirstOrDefault(s => string.Equals(s.Aumid, aumid, StringComparison.OrdinalIgnoreCase));
    }

    private static List<Shortcut> Scan()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };

        var found = new List<Shortcut>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var s = Read(lnk);
                    if (s is not null) found.Add(s);
                }
            }
            catch { /* pasta sem permissao: segue para a proxima */ }
        }
        return found;
    }

    public static Shortcut? Read(string lnkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);

            var target = new StringBuilder(260);
            link.GetPath(target, target.Capacity, nint.Zero, 0);

            var args = new StringBuilder(1024);
            link.GetArguments(args, args.Capacity);

            var icon = new StringBuilder(260);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);

            var aumid = string.Empty;
            if (link is IPropertyStore store)
            {
                var key = PKEY_AppUserModel_ID;
                store.GetValue(ref key, out var v);
                try { if (v.vt == VT_LPWSTR) aumid = Marshal.PtrToStringUni(v.p) ?? string.Empty; }
                finally { PropVariantClear(ref v); }
            }

            Marshal.ReleaseComObject(link);

            var path = target.ToString();
            if (string.IsNullOrWhiteSpace(path)) return null;

            return new Shortcut(lnkPath, path, args.ToString(), aumid, icon.ToString(), iconIndex);
        }
        catch { return null; }
    }

    // ── COM do shell ────────────────────────────────────────
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, nint fd, uint flags);
        void GetIDList(out nint pidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
