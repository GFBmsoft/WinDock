using System.Windows.Media;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// O contorno ao redor da janela em foco no mosaico — a própria borda nativa que o Windows 11
/// já desenha em volta de qualquer janela comum, só recolorida (<c>DWMWA_BORDER_COLOR</c>).
///
/// Não é mais uma janela nossa flutuando por cima de tudo. Essa foi a causa raiz de uma saga
/// inteira de sumiços intermitentes: nenhuma técnica de overlay (WPF, GDI puro, janela crua,
/// color-key) conseguia vencer de forma confiável a disputa de z-order contra QUALQUER outra
/// janela real que estivesse por baixo — perdia especificamente contra elas (nunca contra área
/// vazia da tela), o que só fazia sentido depois de perceber que não era timing nem técnica de
/// desenho: era o conceito de "janela separada por cima" que não tinha como ganhar sempre.
/// Pedindo pro próprio DWM recolorir a borda que ele mesmo já desenha em volta da janela
/// rastreada, não sobra overlay nenhum pra perder essa disputa — a "borda" é parte da janela
/// em si, no mesmo lugar dela no z-order, sempre.
///
/// O preço: sem controle de espessura nem raio de canto — o Windows decide isso (uma linha
/// fina, uns 1-2px, cantos arredondados como o resto do Windows 11) — só a cor é configurável.
/// Só existe a partir do Windows 11 22H2.
/// </summary>
internal sealed class TilingBorderWindow
{
    private readonly DockConfig _config;
    private nint _current;

    public TilingBorderWindow(DockConfig config)
    {
        _config = config;
    }

    /// <summary>Cor — chamado de novo sempre que a pessoa mexe nesse campo no painel de
    /// configurações. Reaplica na janela que já está contornada, se houver uma.</summary>
    internal void ApplyStyle()
    {
        if (_current != 0) Paint(_current);
    }

    /// <summary>Contorna <paramref name="hwnd"/> — se era outra antes, devolve a borda dela
    /// pro padrão do sistema primeiro. Repinta mesmo quando já era esta mesma janela: o
    /// "batimento" (rodando o tempo todo em <c>TilingService</c>) chama isto várias vezes por
    /// segundo de propósito, porque o Windows pode "esquecer" a cor pedida sozinho (num
    /// redimensionamento, por exemplo) — pular a repintura por achar que já estava aplicada
    /// deixava o contorno sumido sem chance nenhuma de se corrigir sozinho depois.</summary>
    internal void ShowAround(nint hwnd)
    {
        if (hwnd != _current && _current != 0) Reset(_current);
        _current = hwnd;
        Paint(_current);
    }

    internal void Hide()
    {
        if (_current == 0) return;
        Reset(_current);
        _current = 0;
    }

    internal void Close() => Hide();

    private void Paint(nint hwnd)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_config.TilingBorderColor);
            // COLORREF é 0x00BBGGRR — ordem trocada em relação ao ARGB do WPF
            var colorref = unchecked((int)(uint)((color.B << 16) | (color.G << 8) | color.R));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
        }
        catch { /* hex inválido digitado a meio: não muda nada */ }
    }

    private static void Reset(nint hwnd)
    {
        var value = unchecked((int)DWMWA_COLOR_DEFAULT);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
    }
}
