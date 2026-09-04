namespace WinDock.Interop;

/// <summary>
/// O <c>RECT</c> que o <c>LayoutTree</c> espera, e só ele.
///
/// O <c>Native.cs</c> de verdade tem centenas de P/Invoke e arrasta WPF junto — trazê-lo para cá
/// obrigaria este projeto a virar <c>net8.0-windows</c> e perderia o ponto de conferir o cálculo
/// isoladamente. Como o <c>RECT</c> é uma struct de quatro inteiros que não muda, repeti-la aqui
/// custa menos do que a dependência inteira.
///
/// Tem de continuar idêntica à do <c>src/Interop/Native.cs</c>: se aquela mudar, esta muda junto,
/// senão a conferência passa a medir outra coisa.
/// </summary>
internal static class Native
{
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right - Left;
        public int Height => Bottom - Top;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }
}
