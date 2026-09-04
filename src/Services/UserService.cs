using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinDock.Services;

/// <summary>
/// Quem está usando a máquina: o nome e a foto da conta.
///
/// Lido uma vez e guardado — nada disso muda enquanto a sessão está aberta, e a foto custa
/// uma leitura de disco que não faz sentido repetir toda vez que o menu de energia abre.
/// </summary>
public static class UserService
{
    /// <summary>
    /// O nome que o Windows mostra para a conta.
    ///
    /// <c>GetUserNameEx(NameDisplay)</c> é o caminho certo, mas em conta local ele
    /// simplesmente falha — medido nesta máquina: devolve zero e string vazia, e o
    /// <c>FullName</c> da conta também está em branco. Nesses casos o próprio Windows exibe
    /// o nome de logon, e é o que se faz aqui.
    /// </summary>
    public static string Name => _name ??= ReadName();
    private static string? _name;

    private static string ReadName()
    {
        try
        {
            var buffer = new StringBuilder(256);
            var size = buffer.Capacity;

            if (GetUserNameEx(NameDisplay, buffer, ref size) != 0)
            {
                var display = buffer.ToString().Trim();
                if (display.Length > 0) return display;
            }
        }
        catch { /* sem secur32 ou conta sem nome de exibição: cai no de logon */ }

        return Environment.UserName;
    }

    /// <summary>
    /// A foto da conta, ou <c>null</c> quando não há uma.
    ///
    /// O caminho vem do registro, e não de uma varredura de pasta: o Windows guarda em
    /// <c>AccountPicture\Users\&lt;SID&gt;</c> um valor por tamanho (<c>Image32</c> até
    /// <c>Image1080</c>) apontando para o arquivo real. Pedimos do maior para o menor a
    /// partir de 192 px — a foto aparece pequena na tela, mas escolher uma versão maior
    /// que o necessário deixa a redução por conta do WPF, que faz melhor que o recorte
    /// pronto de 32 px.
    ///
    /// Sem foto configurada não há fallback para o boneco cinza do Windows de propósito: o
    /// card mostra as iniciais, que dizem mais.
    /// </summary>
    public static BitmapSource? Picture => _picture ??= ReadPicture();
    private static BitmapSource? _picture;

    private static BitmapSource? ReadPicture()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid)) return null;

            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            if (key is null) return null;

            foreach (var tamanho in new[] { "Image448", "Image240", "Image192", "Image96", "Image64" })
            {
                if (key.GetValue(tamanho) is not string caminho || !File.Exists(caminho)) continue;

                var imagem = new BitmapImage();
                imagem.BeginInit();
                imagem.CacheOption = BitmapCacheOption.OnLoad;   // não segura o arquivo aberto
                imagem.UriSource = new Uri(caminho);
                imagem.EndInit();
                imagem.Freeze();
                return imagem;
            }
        }
        catch (Exception ex) { Log.Write("não deu para ler a foto da conta", ex); }

        return null;
    }

    public static bool HasPicture => Picture is not null;

    /// <summary>As iniciais, para o círculo que aparece quando não há foto.</summary>
    public static string Initials
    {
        get
        {
            var partes = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return partes.Length switch
            {
                0 => "?",
                1 => partes[0][..Math.Min(2, partes[0].Length)].ToUpperInvariant(),
                _ => $"{partes[0][0]}{partes[^1][0]}".ToUpperInvariant()
            };
        }
    }

    private const int NameDisplay = 3;

    [System.Runtime.InteropServices.DllImport("secur32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserNameEx(int format, StringBuilder buffer, ref int size);
}
