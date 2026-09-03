using System.ComponentModel;
using System.Globalization;

namespace WinDock.Services;

/// <summary>Um quadradinho do calendário: o número e o que ele tem de especial.</summary>
public sealed record CalendarDay(string Text, bool IsToday, bool InMonth, bool IsWeekend);

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
                    date == today,
                    date.Month == _month.Month && date.Year == _month.Year,
                    date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
            }

            return days;
        }
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
