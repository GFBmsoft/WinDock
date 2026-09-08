using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// Onde se escreve a anotação de um dia do calendário.
///
/// Segue o desenho e o tema do <see cref="ConfirmWindow"/> — é a mesma família de caixa do
/// sistema, e as duas nascem da barra, que é escura por conta própria mas abre janelas no tema
/// do Windows.
/// </summary>
public partial class NoteWindow : Window
{
    /// <summary>A lista do dia, do jeito que está na tela.</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<CalendarNote> _notes = new();

    /// <summary>Quem guarda o que mudou. Chamado a cada alteração, não no fim.</summary>
    private readonly Action<IReadOnlyList<CalendarNote>> _save;

    private NoteWindow(DateTime date, string? holiday, IEnumerable<CalendarNote> notes,
                       Action<IReadOnlyList<CalendarNote>> save)
    {
        InitializeComponent();
        _save = save;

        var texto = date.ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.CurrentCulture);
        Heading.Text = char.ToUpper(texto[0], CultureInfo.CurrentCulture) + texto[1..];

        if (holiday is not null)
        {
            HolidayLine.Text = $"Feriado — {holiday}";
            HolidayLine.Visibility = Visibility.Visible;
        }

        foreach (var n in notes) Track(new CalendarNote { Text = n.Text, Done = n.Done });
        NotesList.ItemsSource = _notes;

        _notes.CollectionChanged += (_, _) => { UpdateEmptyLine(); Save(); };
        UpdateEmptyLine();

        ApplyTheme();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Põe o item na lista já ouvindo o "feito" dele.
    ///
    /// A caixinha marcada não mexe na coleção — muda uma propriedade de dentro do item —, então o
    /// <c>CollectionChanged</c> não vê nada. Sem ouvir cada item, marcar como feito seria a única
    /// alteração da tela que não chegava ao arquivo.
    /// </summary>
    private void Track(CalendarNote nota)
    {
        nota.PropertyChanged += (_, _) => Save();
        _notes.Add(nota);
    }

    /// <summary>
    /// Grava o dia inteiro, a cada mudança.
    ///
    /// Não existe "Gravar" nesta caixa: o gesto de anotar termina no Enter, e um botão a mais
    /// depois disso só criava a chance de fechar a janela e perder o que já estava escrito na
    /// tela. Gravar o dia inteiro (e não o item que mudou) é o que mantém o arquivo igual ao que
    /// se vê, inclusive depois de remover.
    /// </summary>
    private void Save() => _save(_notes.ToList());

    /// <summary>A legenda do vazio some assim que existe um item: ela explica uma lista em
    /// branco, e sobrando embaixo de três anotações vira ruído.</summary>
    private void UpdateEmptyLine() =>
        EmptyLine.Visibility = _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Passa para a lista o que estiver no campo de texto — e, com ela, para o arquivo.
    ///
    /// É chamado também ao fechar a janela: quem digitou e fechou sem apertar Enter perderia o
    /// texto sem aviso nenhum, e "escrevi e sumiu" é o pior desfecho possível numa anotação.
    /// </summary>
    private void CommitBox()
    {
        var texto = NoteBox.Text.Trim();
        if (texto.Length == 0) return;

        Track(new CalendarNote { Text = texto });
        NoteBox.Clear();
    }

    private void OnAddNote(object sender, RoutedEventArgs e)
    {
        CommitBox();
        NoteBox.Focus();
    }

    private void OnRemoveNote(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CalendarNote nota) _notes.Remove(nota);
    }

    private bool _dark;

    /// <summary>A mesma paleta do <see cref="ConfirmWindow"/>: são a mesma caixa para quem usa.</summary>
    private void ApplyTheme()
    {
        _dark = WindowsTheme.IsDark;
        var accent = WindowsTheme.Accent(_dark);

        void Set(string key, Color color) => Resources[key] = new SolidColorBrush(color);

        Set("DialogSurface",       _dark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3));
        Set("DialogFooter",        _dark ? Rgb(0x1C, 0x1C, 0x1C) : Rgb(0xEE, 0xEE, 0xEE));
        Set("DialogDivider",       _dark ? Rgb(0x30, 0x30, 0x30) : Rgb(0xE0, 0xE0, 0xE0));
        Set("DialogText",          _dark ? Rgb(0xFF, 0xFF, 0xFF) : Rgb(0x1B, 0x1B, 0x1B));
        Set("DialogTextDim",       _dark ? Rgb(0xC5, 0xC5, 0xC5) : Rgb(0x5D, 0x5D, 0x5D));
        Set("DialogControl",       _dark ? Rgb(0x2D, 0x2D, 0x2D) : Rgb(0xFD, 0xFD, 0xFD));
        Set("DialogControlHover",  _dark ? Rgb(0x33, 0x33, 0x33) : Rgb(0xF6, 0xF6, 0xF6));
        Set("DialogControlBorder", _dark ? Rgb(0x3D, 0x3D, 0x3D) : Rgb(0xE0, 0xE0, 0xE0));
        Set("DialogAccent",        accent);
        Set("DialogOnAccent",      WindowsTheme.OnAccent(accent));

        // O risco do item concluído, montado aqui e não no XAML: um `Pen` é um `Freezable`, e
        // pendurar `DynamicResource` dentro dele é justamente o caso em que o WPF costuma
        // reclamar em tempo de execução. Pronto e guardado como recurso, o XAML só o referencia.
        var caneta = new Pen(new SolidColorBrush(accent), 1.6);
        caneta.Freeze();

        var risco = new TextDecoration
        {
            Location = TextDecorationLocation.Strikethrough,
            Pen = caneta,
            PenThicknessUnit = TextDecorationUnit.Pixel
        };

        var decoracao = new TextDecorationCollection { risco };
        decoracao.Freeze();

        Resources["DoneStrike"] = decoracao;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                              DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Enter grava mais uma linha e deixa a janela aberta: um dia costuma ter mais de um
        // compromisso, e fechar a cada item obrigaria a reabrir a caixa para o próximo
        if (e.Key == Key.Enter) { CommitBox(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    /// <summary>Fechar por qualquer caminho — botão, Esc ou o X da janela — aproveita o que
    /// ficou escrito no campo. Digitar e fechar é um gesto comum demais para custar o texto.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CommitBox();
        base.OnClosing(e);
    }

    /// <summary>
    /// Abre a caixa das anotações de um dia. Cada mudança é gravada na hora pelo
    /// <paramref name="save"/> — não há botão de confirmar.
    /// </summary>
    /// <param name="owner">
    /// A barra. Importa por dois motivos: ela é topmost, e sem dono a caixa nasceria por baixo
    /// dela; e é <c>WS_EX_NOACTIVATE</c>, então nada que ela abre vem para a frente com o
    /// teclado junto — daí o empurrão no <c>Loaded</c>, o mesmo do <see cref="ConfirmWindow"/>.
    /// </param>
    public static void Edit(Window owner, DateTime date, string? holiday,
                            IReadOnlyList<CalendarNote> notes,
                            Action<IReadOnlyList<CalendarNote>> save)
    {
        var dialog = new NoteWindow(date, holiday, notes, save) { Owner = owner };

        dialog.Loaded += (_, _) =>
        {
            WindowService.Focus(new WindowInteropHelper(dialog).Handle);

            // o cursor já no campo: o gesto que trouxe a pessoa aqui é escrever
            dialog.NoteBox.Focus();
        };

        dialog.ShowDialog();
    }
}
