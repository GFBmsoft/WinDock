using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>O que a pessoa fez com o balão.</summary>
public enum ReminderChoice
{
    /// <summary>Fechou: o aviso já cumpriu o papel e a tarefa continua como estava.</summary>
    Dismiss,

    /// <summary>Pediu para ser lembrada de novo daqui a pouco.</summary>
    Snooze,

    /// <summary>Marcou a tarefa como feita ali mesmo.</summary>
    Done
}

/// <summary>
/// O balão que avisa que a hora de uma tarefa chegou.
///
/// Quem decide quando abrir, onde empilhar e o que fazer com a resposta é o
/// <see cref="ReminderService"/>; aqui só mora o desenho e o gesto.
/// </summary>
public partial class ReminderWindow : Window
{
    public ReminderWindow(DateTime dia, CalendarNote nota)
    {
        InitializeComponent();

        Note = nota;

        var quando = nota.HasTime ? $"às {nota.TimeText}" : "hoje";
        var data = dia.Date == DateTime.Today
            ? quando
            : $"{dia:dd/MM} {quando}";

        Heading.Text = $"Lembrete · {data}".ToUpper(CultureInfo.CurrentCulture);
        Body.Text = nota.Text;

        SourceInitialized += OnSourceInitialized;
    }

    public CalendarNote Note { get; }

    public event Action<ReminderWindow, ReminderChoice>? Chosen;

    /// <summary>
    /// A janela não é ativável nem aparece no Alt+Tab.
    ///
    /// <c>ShowActivated="False"</c> só impede a ativação do primeiro instante; sem o
    /// <c>WS_EX_NOACTIVATE</c>, o clique num botão do balão traria a janela para a frente e
    /// tiraria o teclado de quem estava escrevendo — que é exatamente o que um aviso não pode
    /// fazer. É a mesma escolha da barra de cima.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Choose(ReminderChoice.Dismiss);
    private void OnSnooze(object sender, RoutedEventArgs e) => Choose(ReminderChoice.Snooze);
    private void OnDone(object sender, RoutedEventArgs e) => Choose(ReminderChoice.Done);

    private void Choose(ReminderChoice escolha)
    {
        Chosen?.Invoke(this, escolha);
        Close();
    }
}
