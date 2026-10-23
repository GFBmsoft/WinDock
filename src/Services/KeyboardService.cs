using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Um teclado da lista do Windows: o HKL que o identifica, a sigla do país do desenho ("US",
/// "BR") e o nome por extenso, o mesmo de Configurações › Hora e idioma.
/// </summary>
public sealed record KeyboardLayout(nint Hkl, string Short, string Name);

/// <summary>
/// Qual teclado está valendo e a troca para o próximo — o "POR / PTB2" da barra do Windows, que
/// some junto com ela.
///
/// <para><b>O teclado é de cada janela, e o que vale é o da que está em primeiro plano</b>: é
/// nela que a pessoa digita. A barra é <c>WS_EX_NOACTIVATE</c>, então o clique no botão não tira
/// a janela de primeiro plano do lugar — e a troca vai para quem tem que ir.</para>
///
/// <para><b>A troca é o <c>WM_INPUTLANGCHANGEREQUEST</c> postado na janela em primeiro plano.</b>
/// O TSF tem uma via "para a sessão inteira" (<c>ITfInputProcessorProfileMgr.ActivateProfile</c>
/// com <c>TF_IPPMF_FORSESSION</c>), e foi a primeira tentada: medido nesta máquina em 23/10/2026,
/// ela troca o teclado só da thread de quem chama — a janela em primeiro plano continuou no
/// teclado de antes. A mensagem trocou na hora.</para>
///
/// <para><b>A lista é a de Hora e idioma, e não a do <c>GetKeyboardLayoutList</c></b>. Esta
/// traz também os teclados que algum programa carregou por conta própria: nesta máquina, o US
/// puro (04090409), que não está entre os escolhidos e que o Win+Espaço não oferece. Entrar na
/// roda faria a troca parar num terceiro teclado que a pessoa nem sabe que existe.</para>
///
/// <para><b>A sigla é do país do desenho, e não do idioma.</b> Os dois teclados desta máquina
/// são do mesmo idioma, português do Brasil — um ABNT, outro Estados Unidos (internacional).
/// Pelo idioma, os dois seriam "BR".</para>
/// </summary>
public static class KeyboardService
{
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    /// <summary>
    /// O teclado da janela em primeiro plano. Zero quando não há janela nenhuma — tela de
    /// bloqueio, troca de área de trabalho —, e aí quem pergunta fica com o que já sabia.
    /// </summary>
    public static nint Current()
    {
        var janela = GetForegroundWindow();
        if (janela == 0) return 0;

        var thread = GetWindowThreadProcessId(janela, out _);
        return thread == 0 ? 0 : GetKeyboardLayout(thread);
    }

    /// <summary>
    /// Os teclados escolhidos em Hora e idioma, na ordem de lá — a mesma do Win+Espaço. Sem
    /// essa lista no registro, valem os carregados.
    /// </summary>
    public static IReadOnlyList<KeyboardLayout> List()
    {
        var carregados = Loaded();
        var lista = new List<KeyboardLayout>();

        foreach (var (idioma, klid) in Chosen())
        {
            var hkl = carregados.FirstOrDefault(h => Language(h) == idioma &&
                                                     Klid(h).Equals(klid, StringComparison.OrdinalIgnoreCase));
            if (hkl != 0 && !lista.Any(k => Same(k.Hkl, hkl))) lista.Add(Describe(hkl));
        }

        if (lista.Count == 0) lista.AddRange(carregados.Select(Describe));
        return lista;
    }

    /// <summary>O que vem depois do teclado de agora, dando a volta no fim; o primeiro, se o de agora não está na lista.</summary>
    public static KeyboardLayout? After(nint atual, IReadOnlyList<KeyboardLayout> lista)
    {
        if (lista.Count == 0) return null;

        var i = lista.ToList().FindIndex(k => Same(k.Hkl, atual));
        return lista[(i + 1) % lista.Count];
    }

    /// <summary>Pede à janela em primeiro plano que passe para este teclado.</summary>
    public static bool Activate(nint hkl)
    {
        var janela = GetForegroundWindow();
        return janela != 0 && PostMessage(janela, WM_INPUTLANGCHANGEREQUEST, 0, hkl);
    }

    /// <summary>
    /// O mesmo teclado, pelos 32 bits de baixo. O HKL vem com sinal estendido em 64 bits
    /// (<c>FFFFFFFFF0010416</c>), e não há garantia de que toda origem estenda igual.
    /// </summary>
    public static bool Same(nint a, nint b) => unchecked((uint)a == (uint)b);

    private static readonly Dictionary<uint, KeyboardLayout> _described = new();

    /// <summary>A sigla e o nome de um HKL, guardados: o nome sai de uma DLL de recursos do Windows.</summary>
    public static KeyboardLayout Describe(nint hkl)
    {
        var chave = unchecked((uint)hkl);
        if (_described.TryGetValue(chave, out var pronto)) return pronto;

        var klid = Klid(hkl);
        return _described[chave] = new KeyboardLayout(hkl, Short(klid, Language(hkl)), Name(klid));
    }

    private static List<nint> Loaded()
    {
        var n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return new();

        var hkls = new nint[n];
        n = GetKeyboardLayoutList(n, hkls);
        return hkls.Take(n).ToList();
    }

    /// <summary>
    /// Os pares idioma e desenho de <c>HKCU\Control Panel\International\User Profile</c>: um
    /// valor <c>0416:00020409 = 2</c> por teclado, debaixo da chave do idioma, com a posição
    /// na lista. Os métodos de entrada de japonês, chinês e afins vêm com GUIDs no lugar do
    /// desenho e ficam de fora — não são um desenho de teclado que um HKL ative.
    /// </summary>
    private static List<(ushort Language, string Klid)> Chosen()
    {
        var lista = new List<(ushort, string)>();
        try
        {
            using var perfil = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (perfil?.GetValue("Languages") is not string[] idiomas) return lista;

            foreach (var idioma in idiomas)
            {
                using var chave = perfil.OpenSubKey(idioma);
                if (chave is null) continue;

                var teclados = chave.GetValueNames()
                    .Where(n => n.Length == 13 && n[4] == ':')
                    .OrderBy(n => chave.GetValue(n) as int? ?? int.MaxValue);

                foreach (var nome in teclados)
                    if (ushort.TryParse(nome[..4], NumberStyles.HexNumber, null, out var lang))
                        lista.Add((lang, nome[5..]));
            }
        }
        catch (Exception ex) { Log.Write("não deu para ler a lista de teclados de Hora e idioma", ex); }
        return lista;
    }

    private static ushort Language(nint hkl) => unchecked((ushort)((uint)hkl & 0xFFFF));

    /// <summary>
    /// O nome do desenho do teclado (o KLID, <c>00020409</c>) a partir do HKL.
    ///
    /// A palavra de cima do HKL é o desenho: um código de idioma quando é o desenho padrão de
    /// um idioma (<c>0416</c> → <c>00000416</c>); <c>Fxxx</c> quando é uma variante, e o
    /// <c>xxx</c> é o "Layout Id" de alguma chave de <c>Keyboard Layouts</c>
    /// (<c>F001</c> → <c>00020409</c>); <c>Exxx</c> nos métodos de entrada, em que o HKL
    /// inteiro já é o nome.
    /// </summary>
    private static string Klid(nint hkl)
    {
        var baixo = unchecked((uint)hkl);
        var desenho = (ushort)(baixo >> 16);

        return (desenho & 0xF000) switch
        {
            0xF000 => LayoutIds().GetValueOrDefault(desenho & 0x0FFF) ?? baixo.ToString("X8"),
            0xE000 => baixo.ToString("X8"),
            _ => desenho.ToString("X8"),
        };
    }

    private static Dictionary<int, string>? _layoutIds;

    /// <summary>"Layout Id" → KLID, lido uma vez: são umas duzentas chaves, e não mudam.</summary>
    private static Dictionary<int, string> LayoutIds()
    {
        if (_layoutIds is not null) return _layoutIds;

        var mapa = new Dictionary<int, string>();
        try
        {
            using var raiz = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            foreach (var klid in raiz?.GetSubKeyNames() ?? [])
            {
                using var chave = raiz!.OpenSubKey(klid);
                if (chave?.GetValue("Layout Id") is string id &&
                    int.TryParse(id, NumberStyles.HexNumber, null, out var n))
                    mapa.TryAdd(n, klid);
            }
        }
        catch (Exception ex) { Log.Write("não deu para ler os desenhos de teclado do Windows", ex); }

        return _layoutIds = mapa;
    }

    /// <summary>
    /// A sigla do país do desenho: os quatro últimos dígitos do KLID são o idioma para o qual ele
    /// foi desenhado — <c>00020409</c> é americano, seja qual for o idioma em que foi posto.
    /// </summary>
    private static string Short(string klid, ushort idioma)
    {
        if (uint.TryParse(klid, NumberStyles.HexNumber, null, out var n) && Region((int)(n & 0xFFFF)) is { } pais)
            return pais;

        return Region(idioma) ?? klid[^4..];
    }

    private static string? Region(int lcid)
    {
        try
        {
            var cultura = CultureInfo.GetCultureInfo(lcid);
            return cultura.IsNeutralCulture ? null : new RegionInfo(cultura.Name).TwoLetterISORegionName;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// O nome que o Windows mostra, no idioma dele ("Estados Unidos (internacional)"). O
    /// "Layout Display Name" aponta para um texto dentro de uma DLL; o "Layout Text", em inglês,
    /// é a reserva.
    /// </summary>
    private static string Name(string klid)
    {
        try
        {
            using var chave = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");

            if (chave?.GetValue("Layout Display Name") is string indireto)
            {
                var texto = new StringBuilder(260);
                if (SHLoadIndirectString(indireto, texto, texto.Capacity, 0) == 0 && texto.Length > 0)
                    return texto.ToString();
            }

            if (chave?.GetValue("Layout Text") is string ingles && ingles.Length > 0) return ingles;
        }
        catch (Exception ex) { Log.Write($"não deu para ler o nome do teclado {klid}", ex); }

        return klid;
    }

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint thread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? list);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder buffer, int size, nint reserved);
}
