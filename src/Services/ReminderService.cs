using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// O relógio dos lembretes: acorda de tempos em tempos, procura tarefas marcadas para avisar cuja
/// hora já chegou e põe um balão na tela para cada uma.
///
/// Vive na dock, e não na barra de cima, porque a barra é opcional: quem desligou a barra continua
/// anotando tarefas pelo calendário — e continua querendo ser avisado delas.
///
/// Lê as tarefas direto do <see cref="DockConfig.CalendarNotes"/>, e não por um
/// <see cref="MonthCalendar"/> próprio: o calendário é o mês na tela, com mês aberto e dia
/// escolhido, e nada disso tem a ver com um aviso. Os objetos são os mesmos que o cartão mostra —
/// eles moram no config —, então marcar "feito" aqui acende o visto lá, sem ninguém avisar ninguém.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private readonly DockConfig _config;
    private readonly DispatcherTimer _timer;
    private readonly List<ReminderWindow> _abertos = new();

    /// <summary>
    /// As tarefas adiadas e a hora de voltar. Fica na memória de propósito: adiar é "me avise de
    /// novo daqui a pouco", não uma mudança na tarefa — e se a máquina for desligada no meio, o
    /// que vale é o que está escrito no calendário.
    /// </summary>
    private readonly Dictionary<CalendarNote, DateTime> _adiados = new();

    /// <summary>Quanto tempo o "Adiar" empurra o aviso.</summary>
    private static readonly TimeSpan Snooze = TimeSpan.FromMinutes(10);

    /// <summary>
    /// De quanto em quanto tempo a lista é varrida.
    ///
    /// Meio minuto, e não um segundo: um lembrete que chega meio minuto depois continua sendo o
    /// lembrete, e a varredura passa por todas as tarefas do dia a cada vez.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    public ReminderService(DockConfig config)
    {
        _config = config;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
        _timer.Tick += (_, _) => Check(DateTime.Now);
        _timer.Start();

        // A primeira passagem espera alguns segundos, e não é para poupar o arranque: é a barra de
        // cima que precisa ter registrado a AppBar dela antes. Sem isso, o balão do primeiro aviso
        // do dia é posicionado numa área de trabalho que ainda vai do topo da tela, e nasce **por
        // cima da barra** — só o primeiro, o que é o pior tipo de defeito, porque some ao
        // investigar. Medido em 28/09/2026.
        var primeira = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        primeira.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); Check(DateTime.Now); };
        primeira.Start();
    }

    /// <summary>
    /// Procura o que está na hora e mostra o balão de cada um.
    ///
    /// Uma tarefa avisada é marcada no arquivo (<c>NotifiedAt</c>) — é o que faz o aviso sair uma
    /// vez só, mesmo depois de fechar e abrir a dock.
    /// </summary>
    private void Check(DateTime agora)
    {
        foreach (var (nota, quando) in _adiados.ToList())
        {
            if (quando > agora) continue;
            _adiados.Remove(nota);
            Show(agora.Date, nota);
        }

        foreach (var (dia, nota) in Due(agora))
        {
            nota.NotifiedAt = agora;
            _config.Save();
            Log.Trace($"lembrete: “{nota.Text}” de {dia:yyyy-MM-dd} {(nota.HasTime ? nota.TimeText : "sem hora")}");
            Show(dia, nota);
        }
    }

    /// <summary>
    /// As tarefas de hoje que estão marcadas para avisar, cuja hora já passou e que ainda não
    /// foram avisadas.
    ///
    /// A tarefa sem hora conta a partir da meia-noite: ela é do dia, e o aviso dela é "hoje tem
    /// isto". O atraso entra — quem estava com a máquina desligada às 14h quer saber às 15h que
    /// havia algo às 14h —, mas só dentro do próprio dia: a máquina ligada depois de uma semana
    /// de férias despejaria a semana inteira de uma vez. O que passou do dia fica no calendário,
    /// que é onde ele ainda faz sentido.
    /// </summary>
    private IEnumerable<(DateTime Day, CalendarNote Note)> Due(DateTime agora)
    {
        foreach (var (chave, itens) in _config.CalendarNotes.ToList())
        {
            if (!DateTime.TryParseExact(chave, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var dia)) continue;
            if (dia.Date != agora.Date) continue;

            foreach (var nota in itens.ToList())
            {
                if (!nota.Notify || nota.Done || nota.NotifiedAt is not null) continue;
                if (dia.Date + (nota.At ?? TimeSpan.Zero) > agora) continue;
                yield return (dia.Date, nota);
            }
        }
    }

    /// <summary>
    /// Põe um balão na tela, empilhado abaixo dos que já estão abertos.
    ///
    /// A mesma tarefa não ganha dois balões: adiar e a varredura seguinte podem se cruzar, e dois
    /// avisos do mesmo compromisso só ocupariam a tela duas vezes.
    /// </summary>
    private void Show(DateTime dia, CalendarNote nota)
    {
        if (_abertos.Any(b => ReferenceEquals(b.Note, nota))) return;

        var balao = new ReminderWindow(dia, nota);
        balao.Chosen += OnChosen;
        balao.Closed += (_, _) => { _abertos.Remove(balao); Restack(); };

        _abertos.Add(balao);
        balao.Show();
        Restack();
    }

    /// <summary>
    /// Empilha os balões embaixo da data, no mesmo lugar em que o cartão do calendário abre.
    ///
    /// O aviso vem de uma tarefa do calendário, e sai debaixo do relógio pelo mesmo motivo que o
    /// calendário sai: é ali que a pessoa olha quando o assunto é dia e hora. Daí ele seguir a
    /// opção de relógio centralizado — com ela ligada os dois nascem no meio da barra, e com ela
    /// desligada, os dois no canto direito.
    ///
    /// A conta é sobre a área de trabalho, e não sobre a tela: a barra de cima e a dock já
    /// reservaram as faixas delas pelo AppBar, então o balão nasce abaixo da barra sozinho, sem
    /// precisar saber que ela existe nem qual é a altura dela.
    /// </summary>
    private void Restack()
    {
        if (_abertos.Count == 0) return;

        var area = WorkArea(_abertos[0]);

        // Os 5 px são a mesma folga do cartão do calendário, e não um número escolhido aqui: lá o
        // popup fica a `target.Height + 6` do topo do botão da data, que começa 1 px dentro da
        // barra — a conta dá o mesmo topo. Os dois cartões têm a mesma margem de 10 px por dentro,
        // então a distância que se vê até a barra fica idêntica.
        var y = area.Top + 5;

        foreach (var balao in _abertos)
        {
            balao.Left = _config.PanelCenterClock
                ? area.Left + (area.Width - balao.Width) / 2
                : area.Right - balao.Width - 6;
            balao.Top = y;
            y += balao.ActualHeight > 0 ? balao.ActualHeight : 110;
        }
    }

    /// <summary>
    /// A área de trabalho perguntada ao Windows, e não ao <c>SystemParameters.WorkArea</c>.
    ///
    /// O WPF guarda esse valor em cache e o atualiza por <c>WM_SETTINGCHANGE</c> — e no arranque
    /// o cache é anterior ao registro da AppBar da barra de cima. Medido em 28/09/2026: o Windows
    /// respondia topo 24 e o WPF ainda dizia 0, e o primeiro balão do dia nascia **por cima da
    /// barra**. Só o primeiro, o que é o pior tipo de defeito — some ao investigar.
    ///
    /// O retângulo vem em pixels da tela; a conversão para as unidades do WPF sai da própria
    /// janela, que já está na tela quando isto roda.
    /// </summary>
    private static Rect WorkArea(Window referencia)
    {
        var rect = new RECT();
        if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0)) return SystemParameters.WorkArea;

        var origem = PresentationSource.FromVisual(referencia);
        var para = origem?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        return new Rect(para.Transform(new Point(rect.Left, rect.Top)),
                        para.Transform(new Point(rect.Right, rect.Bottom)));
    }

    private void OnChosen(ReminderWindow balao, ReminderChoice escolha)
    {
        var nota = balao.Note;

        switch (escolha)
        {
            case ReminderChoice.Snooze:
                _adiados[nota] = DateTime.Now + Snooze;
                Log.Trace($"lembrete adiado: “{nota.Text}” volta em {Snooze.TotalMinutes:0} min");
                break;

            case ReminderChoice.Done:
                // mexe no objeto no lugar em vez de reconstruir a lista do dia: o cartão do
                // calendário pode estar aberto, e o CalendarNote avisa a tela sozinho — o visto
                // acende sem tirar a linha de baixo do mouse de quem está lendo
                nota.Done = true;
                nota.DoneAt = DateTime.Now;
                _config.Save();
                Log.Trace($"lembrete concluído pelo balão: “{nota.Text}”");
                break;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var balao in _abertos.ToList()) balao.Close();
        _abertos.Clear();
    }
}
