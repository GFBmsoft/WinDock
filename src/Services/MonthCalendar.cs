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
    IReadOnlyList<CalendarNote> Notes,
    bool IsSelected = false)
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
                    NotesOf(date),
                    date == _selected));
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
        if (_selected == date.Date) RaiseSelection();
    }

    // ── o dia escolhido ─────────────────────────────────────

    /// <summary>
    /// O dia cujas tarefas aparecem embaixo do mês, no próprio cartão — ou nenhum, que é como o
    /// cartão abre.
    ///
    /// Continua escolhido ao folhear os meses: a pessoa pode ir ver outro mês sem perder a lista
    /// que tinha aberto.
    /// </summary>
    private DateTime? _selected;

    public DateTime? SelectedDate => _selected;
    public bool HasSelection => _selected is not null;

    /// <summary>"Sábado, 12 de setembro", com a inicial maiúscula.</summary>
    public string SelectedTitle
    {
        get
        {
            if (_selected is not { } dia) return string.Empty;
            var texto = dia.ToString("dddd, d 'de' MMMM", CultureInfo.CurrentCulture);
            return char.ToUpper(texto[0], CultureInfo.CurrentCulture) + texto[1..];
        }
    }

    public string? SelectedHoliday => _selected is { } dia ? Holidays.Of(dia) : null;
    public bool HasSelectedHoliday => SelectedHoliday is not null;
    public IReadOnlyList<CalendarNote> SelectedNotes => _selected is { } dia ? NotesOf(dia) : Nenhuma;
    public bool SelectedEmpty => HasSelection && SelectedNotes.Count == 0;

    public void Select(DateTime date)
    {
        _selected = date.Date;
        RaiseSelection();
        Raise(nameof(Days));   // o anel em volta do dia
    }

    public void ClearSelection()
    {
        if (_selected is null) return;
        _selected = null;
        RaiseSelection();
        Raise(nameof(Days));
    }

    /// <summary>
    /// Marca ou desmarca uma tarefa do dia escolhido. Passa pelo <see cref="SetNotes"/> — e não
    /// muda o item no lugar — para gravar o arquivo e redesenhar o pontinho do dia na mesma hora,
    /// como a caixa de anotação faz.
    /// </summary>
    public void ToggleDone(CalendarNote nota)
    {
        if (_selected is not { } dia) return;
        SetNotes(dia, NotesOf(dia).Select(n => ReferenceEquals(n, nota)
            ? new CalendarNote { Text = n.Text, Done = !n.Done }
            : n).ToList());
    }

    public void RemoveNote(CalendarNote nota)
    {
        if (_selected is not { } dia) return;
        SetNotes(dia, NotesOf(dia).Where(n => !ReferenceEquals(n, nota)).ToList());
    }

    /// <summary>Acrescenta uma tarefa ao dia escolhido. Texto em branco não vira linha.</summary>
    public void AddToSelected(string texto)
    {
        if (_selected is not { } dia || string.IsNullOrWhiteSpace(texto)) return;
        AddNote(dia, new CalendarNote { Text = texto.Trim() });
    }

    /// <summary>
    /// Leva uma tarefa do dia escolhido para outro dia. Devolve falso quando não havia o que mover
    /// (o destino é o próprio dia).
    ///
    /// Grava no destino **antes** de tirar da origem: se algo falhar no meio do caminho, uma
    /// anotação repetida é um aborrecimento, e uma anotação perdida é o que ninguém perdoa.
    /// </summary>
    public bool MoveNote(CalendarNote nota, DateTime destino)
    {
        if (_selected is not { } dia || destino.Date == dia) return false;

        AddNote(destino.Date, new CalendarNote { Text = nota.Text, Done = nota.Done });
        RemoveNote(nota);
        return true;
    }

    /// <summary>
    /// Entende a data digitada para mover uma tarefa, com o ano opcional.
    ///
    /// Sem ano, parte do ano do dia aberto — e não do de hoje. Só que isso sozinho erra na virada:
    /// com o dia 28/12 aberto, "05/01" viraria janeiro **do mesmo ano**, onze meses atrás, quando a
    /// intenção óbvia é o janeiro que vem. Caindo muito para trás, o ano seguinte é a leitura certa.
    ///
    /// O corte é de seis meses, e não "qualquer data passada", porque mover para trás é legítimo:
    /// quem digita "27/12" com o dia 28/12 aberto quer ontem mesmo, e empurrar isso para o ano que
    /// vem seria pior que o problema que se está resolvendo.
    /// </summary>
    public static bool TryParseDay(DateTime aberto, string texto, out DateTime dia)
    {
        dia = default;

        var limpo = texto.Trim();
        if (limpo.Length == 0) return false;

        var cultura = CultureInfo.CurrentCulture;
        string[] formatos = ["d/M/yyyy", "d/M/yy", "d/M", "d-M-yyyy", "d-M", "d.M.yyyy", "d.M"];

        if (!DateTime.TryParseExact(limpo, formatos, cultura, DateTimeStyles.None, out var lida) &&
            !DateTime.TryParse(limpo, cultura, DateTimeStyles.None, out lida)) return false;

        // com ano digitado não há o que adivinhar
        if (limpo.Count(c => c is '/' or '-' or '.') >= 2)
        {
            dia = lida.Date;
            return true;
        }

        dia = new DateTime(aberto.Year, lida.Month, lida.Day);
        if (dia < aberto.Date.AddMonths(-6)) dia = dia.AddYears(1);

        return true;
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedDate));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedTitle));
        Raise(nameof(SelectedHoliday));
        Raise(nameof(HasSelectedHoliday));
        Raise(nameof(SelectedNotes));
        Raise(nameof(SelectedEmpty));
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
