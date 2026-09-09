using System.ComponentModel;
using System.Globalization;

namespace WinDock.Services;

/// <summary>
/// Um quadradinho do calendário: o número e o que ele tem de especial.
///
/// A <see cref="Date"/> anda junto porque a grade é o único lugar que sabe que casa é que dia —
/// o texto "7" aparece em três meses diferentes na mesma tela.
/// </summary>
public sealed record CalendarDay(
    string Text,
    DateTime Date,
    bool IsToday,
    bool InMonth,
    bool IsWeekend,
    string? Holiday,
    IReadOnlyList<CalendarNote> Notes)
{
    public bool IsHoliday => Holiday is not null;
    public bool HasNote => Notes.Count > 0;

    /// <summary>Tudo o que havia para fazer no dia já está marcado como feito — o pontinho muda
    /// de cor, que é o "dia resolvido" sem precisar abrir nada.</summary>
    public bool AllDone => Notes.Count > 0 && Notes.All(n => n.Done);

    /// <summary>
    /// O que a dica de mouse mostra: o feriado e as anotações, uma por linha.
    ///
    /// O marcador de cada linha conta o estado — "✓" para o que está resolvido, "•" para o que
    /// não está —, e é o que permite ler o dia inteiro sem abrir a caixa. Com uma anotação só e
    /// nada feito, o marcador some: um item sozinho não precisa de lista.
    /// </summary>
    public string Tooltip
    {
        get
        {
            var linhas = new List<string>();
            if (Holiday is not null) linhas.Add(Holiday);

            if (Notes.Count == 1 && !Notes[0].Done) linhas.Add(Notes[0].Text);
            else linhas.AddRange(Notes.Select(n => $"{(n.Done ? "✓" : "•")} {n.Text}"));

            return string.Join(Environment.NewLine, linhas);
        }
    }

    public bool HasTooltip => Tooltip.Length > 0;
}

/// <summary>
/// O mês que o calendário da barra mostra.
///
/// É desenhado à mão, e não com o <c>Calendar</c> do WPF, pelo mesmo motivo da pilha da
/// bateria: o controle pronto vem com o tema claro e reestilizá-lo custa um template
/// inteiro — mais XAML do que a grade que ele desenharia. Aqui saem seis semanas de sete
/// dias, sempre, para a altura do painel não mudar de mês para mês.
/// </summary>
public sealed class MonthCalendar : INotifyPropertyChanged
{
    private readonly DockConfig _config;

    public MonthCalendar(DockConfig config) => _config = config;

    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>Quantas linhas a grade tem. Seis cobrem qualquer mês, inclusive fevereiro
    /// que começa no sábado.</summary>
    private const int Weeks = 6;

    /// <summary>"Setembro de 2026", com a inicial maiúscula (em pt-BR o mês vem minúsculo).</summary>
    public string Title
    {
        get
        {
            var text = _month.ToString("MMMM 'de' yyyy", CultureInfo.CurrentCulture);
            return text.Length > 0 ? char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..] : text;
        }
    }

    /// <summary>Verdadeiro quando o mês na tela é o de hoje — o botão "hoje" some.</summary>
    public bool IsCurrentMonth => _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month;

    /// <summary>As iniciais dos dias da semana, na ordem em que a cultura começa a semana.</summary>
    public IReadOnlyList<string> WeekHeaders
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var first = (int)culture.DateTimeFormat.FirstDayOfWeek;

            return Enumerable.Range(0, 7)
                .Select(i =>
                {
                    var name = culture.DateTimeFormat.GetShortestDayName((DayOfWeek)((first + i) % 7));
                    return char.ToUpper(name[0], culture) + (name.Length > 1 ? name[1..] : string.Empty);
                })
                .ToList();
        }
    }

    /// <summary>
    /// As 42 casas da grade, na ordem de leitura. As do mês anterior e do seguinte entram
    /// apagadas em vez de vazias: sem elas a primeira semana ficaria torta, e com elas dá
    /// para ver que dia da semana cai a virada.
    /// </summary>
    public IReadOnlyList<CalendarDay> Days
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var firstDayOfWeek = (int)culture.DateTimeFormat.FirstDayOfWeek;

            // recua até o começo da semana em que cai o dia 1º
            var offset = ((int)_month.DayOfWeek - firstDayOfWeek + 7) % 7;
            var start = _month.AddDays(-offset);

            var today = DateTime.Today;
            var days = new List<CalendarDay>(Weeks * 7);

            for (var i = 0; i < Weeks * 7; i++)
            {
                var date = start.AddDays(i);
                days.Add(new CalendarDay(
                    date.Day.ToString(CultureInfo.CurrentCulture),
                    date,
                    date == today,
                    date.Month == _month.Month && date.Year == _month.Year,
                    date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                    Holidays.Of(date),
                    NotesOf(date)));
            }

            return days;
        }
    }

    // ── anotações ───────────────────────────────────────────

    /// <summary>A chave de um dia no <see cref="DockConfig.CalendarNotes"/>.</summary>
    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static readonly CalendarNote[] Nenhuma = Array.Empty<CalendarNote>();

    public IReadOnlyList<CalendarNote> NotesOf(DateTime date) =>
        _config.CalendarNotes.TryGetValue(Key(date), out var itens) ? itens : Nenhuma;

    /// <summary>
    /// Guarda as anotações de um dia e redesenha a grade. Lista vazia apaga o dia do arquivo —
    /// uma chave sem nada dentro só encheria o config de linhas mortas.
    /// </summary>
    /// <summary>
    /// Acrescenta uma anotação a um dia, preservando as que ele já tinha.
    ///
    /// É a metade "chegada" de mover uma anotação de dia: quem move grava primeiro no destino e
    /// só então tira da origem, porque se algo falhar no meio é melhor a anotação existir duas
    /// vezes do que não existir em lugar nenhum.
    /// </summary>
    public void AddNote(DateTime date, CalendarNote nota)
    {
        var atuais = NotesOf(date).ToList();
        atuais.Add(nota);
        SetNotes(date, atuais);
    }

    public void SetNotes(DateTime date, IEnumerable<CalendarNote> itens)
    {
        var chave = Key(date);
        var limpos = itens.Where(n => !string.IsNullOrWhiteSpace(n.Text))
                          .Select(n => new CalendarNote { Text = n.Text.Trim(), Done = n.Done })
                          .ToList();

        if (limpos.Count == 0)
        {
            if (!_config.CalendarNotes.Remove(chave)) return;
        }
        else
        {
            _config.CalendarNotes[chave] = limpos;
        }

        _config.Save();
        Raise(nameof(Days));
    }

    public void PreviousMonth() => Go(_month.AddMonths(-1));
    public void NextMonth() => Go(_month.AddMonths(1));

    /// <summary>Volta para o mês de hoje — e é o que o painel faz toda vez que abre.</summary>
    public void GoToToday() => Go(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));

    private void Go(DateTime month)
    {
        _month = month;
        Raise(nameof(Title));
        Raise(nameof(Days));
        Raise(nameof(IsCurrentMonth));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
