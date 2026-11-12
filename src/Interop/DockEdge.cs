namespace WinDock.Interop;

/// <summary>
/// A borda da tela em que uma barra fica. Os valores são os do <c>ABE_*</c> do shell.
///
/// Num arquivo só dele para o <c>tests/BarCheck</c> poder compilá-lo junto com o
/// <c>BarGeometry</c>, sem trazer o <see cref="AppBar"/> (que é WPF e Win32).
/// </summary>
public enum DockEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }
