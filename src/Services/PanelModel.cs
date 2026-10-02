using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// O que a barra de cima mostra: hora, data, bateria e volume. Existe porque esconder a
/// barra do Windows tira o relogio junto — e o relogio e a coisa que mais falta.
/// </summary>
public sealed class PanelModel : INotifyPropertyChanged, IDisposable
{
    private readonly DockConfig _config;
    private readonly DispatcherTimer _timer;

    public PanelModel(DockConfig config)
    {
        _config = config;
        _config.PropertyChanged += OnConfigChanged;

        // O monitor costuma recusar a conversa DDC/CI no meio do arranque (veja
        // BrightnessService.Retentar): quando ele passa a responder, o item precisa aparecer
        // na barra, que já perguntou uma vez e recebeu "não". Volta para a thread da interface
        // porque o aviso chega de uma tarefa de fundo.
        _brightness.AvailabilityChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _brightnessLevel = _brightness.Level;
                OnChanged(nameof(Brightness));
                OnChanged(nameof(HasBrightness));
            });

        // de segundo em segundo, mas as propriedades so avisam a interface quando o valor
        // muda de verdade: o relogio nao mostra segundos
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Update();
        _timer.Start();

        Update();

        // a thread que le a bandeja sobe junto com a barra, e nao no primeiro clique: criar
        // uma thread STA com bomba de mensagens leva alguns milissegundos, e quem esperava
        // por ela era a thread da interface — a barra travava no primeiro clique da seta.
        if (_config.PanelTray)
        {
            TrayService.ClearLeftovers();

            // O que se aprendeu sobre os ícones sem nome volta do disco antes da primeira
            // leitura — é só assim que ele serve para alguma coisa: aprendido de novo a cada
            // sessão, a primeira leitura de todo dia pagava os 400 ms de espera pelos nomes.
            TrayService.AnonymousAt = _config.TrayAnonymousAt;
            // pelo dispatcher: o aviso vem da thread da bandeja, e gravar a configuração de
            // duas threads ao mesmo tempo é um arquivo pela metade
            TrayService.AnonymousLearned += (_, _) => _dispatcher.InvokeAsync(() =>
            {
                _config.TrayAnonymousAt = TrayService.AnonymousAt;
                _config.Save();
            });

            TrayReader.Start();
            WarmTray();
        }

        WatchAiUsage();
        WatchUpdates();
        WatchSelfUpdate();
        WatchNotifications();

        // A lista de dispositivos externos não tem relógio: quem avisa é o Windows, pelo
        // WM_DEVICECHANGE que a barra assina (veja PanelWindow.OnDeviceChange). Esta é só a
        // primeira leitura, para o pen-drive que já estava espetado antes de a dock subir —
        // uns segundos depois, porque o logon já é o momento mais disputado da máquina.
        var primeiroUsb = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        primeiroUsb.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); RefreshRemovable(); };
        primeiroUsb.Start();

        // A mídia avisa sozinha quando muda, mas o evento vem de thread do WinRT — a
        // interface só pode ser tocada pelo dispatcher.
        _media.Changed += () => _dispatcher.InvokeAsync(() => Media = _media.Current);
        MediaService.UseStore(_config);   // a lista de vistos vem do disco e volta pra ele
        _media.SetAllowed(_config.MediaApps);
        _ = _media.Start();

        // O brilho é lido uma vez: ninguém o muda por fora com frequência, e cada leitura
        // conversa com o monitor por I²C. Esta primeira leitura volta na hora, com zero, e
        // manda o serviço procurar o monitor em segundo plano — quem traz o valor de verdade
        // é o AvailabilityChanged ligado logo acima. Era aqui que a barra ficava quase um
        // segundo parada no arranque, esperando dois monitores dizerem que não falam DDC/CI.
        //
        // Só quando o item está ligado. Sem isto, a barra conversava por I²C com monitor
        // nenhum precisar: HasBrightness curto-circuita no PanelBrightness e nunca chega a
        // perguntar, então quem tem o brilho desligado pagava a procura inteira para jogar o
        // resultado fora. Ligar a opção depois não perde nada — HasBrightness passa a
        // consultar Available, que dispara a mesma procura em segundo plano.
        if (_config.PanelBrightness) _brightnessLevel = _brightness.Level;
    }

    /// <summary>
    /// Faz a primeira leitura da bandeja sem ninguem esperando por ela.
    ///
    /// A primeira leitura de cada sessao e a cara: a janela do painel do Windows ainda nao
    /// existe, nasce sem camada, a foto sai preta e e preciso ler de novo — dois segundos ao
    /// todo. Pagos no primeiro clique, era o clique que parecia travado; pagos aqui, ninguem
    /// vê.
    ///
    /// Isto só é possível agora que ler ficou invisível e não mexe mais na área de trabalho.
    /// Uma versão anterior fazia isto e foi removida justamente porque piscava a barra do
    /// Windows durante o logon.
    ///
    /// Uns segundos depois de a barra subir, e não junto: o logon já é o momento mais
    /// disputado da máquina.
    ///
    /// **E insiste, porque seis segundos não bastam.** No log de 24/09 o aquecimento falhou
    /// duas vezes seguidas às 07:50 com "o botão mostrar ícones ocultos não foi encontrado":
    /// logo depois do logon a árvore de automação da barra do Windows ainda não está de pé, e
    /// o <c>WaitForChevron</c> estoura. Uma tentativa só que falha deixa o cache vazio — ou
    /// seja, o primeiro clique do dia paga a leitura inteira, que é exatamente o que este
    /// aquecimento existe para evitar. As tentativas param assim que uma trouxer ícones.
    /// </summary>
    private void WarmTray()
    {
        // Espaçadas de propósito: se a barra do Windows ainda não montou aos 6 s, insistir
        // logo em seguida encontra a mesma coisa. Depois de dois minutos, desiste — a essa
        // altura ou não há bandeja nesta sessão, ou o primeiro clique resolve sozinho.
        var prazos = new Queue<int>(new[] { 6, 30, 120 });

        var warm = new DispatcherTimer(DispatcherPriority.Background);

        void Agendar()
        {
            if (prazos.Count == 0) { Log.Write("a bandeja não pôde ser lida no arranque; o primeiro clique vai relê-la"); return; }
            warm.Interval = TimeSpan.FromSeconds(prazos.Dequeue());
            warm.Start();
        }

        warm.Tick += (_, _) =>
        {
            warm.Stop();
            RefreshTray(() =>
            {
                if (TrayService.Cached.Count > 0)
                {
                    Log.Trace($"bandeja aquecida: {TrayService.Cached.Count} ícone(s) prontos antes do primeiro clique");
                    return;
                }

                Log.Trace("aquecimento da bandeja não trouxe ícones; tentando de novo mais tarde");
                Agendar();
            });
        };

        Agendar();
    }

    // ── relogio ─────────────────────────────────────────────
    private string _time = string.Empty;
    public string Time { get => _time; private set => Set(ref _time, value); }

    private string _date = string.Empty;
    /// <summary>Data por extenso, abreviada: "Seg, 31 de ago".</summary>
    public string Date { get => _date; private set => Set(ref _date, value); }

    // ── o cartão do relógio ─────────────────────────────────

    private string _clockSeconds = string.Empty;
    /// <summary>A hora com segundos, "14:07:42" — só do cartão, e só enquanto ele está aberto.</summary>
    public string ClockSeconds { get => _clockSeconds; private set => Set(ref _clockSeconds, value); }

    private string _clockLongDate = string.Empty;
    /// <summary>A data por extenso, "quinta-feira, 1 de outubro de 2026".</summary>
    public string ClockLongDate { get => _clockLongDate; private set => Set(ref _clockLongDate, value); }

    private DispatcherTimer? _secondsTimer;

    /// <summary>
    /// Liga o relógio de segundos do cartão.
    ///
    /// Não é o relógio da barra, de propósito. Aquele dispara a cada segundo contado a partir de
    /// quando a dock abriu, e não da virada do segundo — num tique às ,98 e no seguinte às ,01 de
    /// dois segundos depois, um número some. Para minutos isso não aparece; com os segundos à
    /// vista, aparece. Este reagenda a si mesmo para logo depois de cada virada, e só existe
    /// com o cartão aberto: fechado, ninguém está olhando.
    /// </summary>
    public void StartSeconds()
    {
        _secondsTimer ??= new DispatcherTimer(DispatcherPriority.Render);
        _secondsTimer.Tick -= OnSecond;
        _secondsTimer.Tick += OnSecond;
        OnSecond(null, EventArgs.Empty);
    }

    public void StopSeconds() => _secondsTimer?.Stop();

    private void OnSecond(object? sender, EventArgs e)
    {
        var agora = DateTime.Now;
        var cultura = CultureInfo.CurrentCulture;

        ClockSeconds = agora.ToString("HH:mm:ss", cultura);
        ClockLongDate = agora.ToString("dddd, d 'de' MMMM 'de' yyyy", cultura);

        // 15 ms depois da próxima virada: o bastante para não acordar ainda no segundo velho
        _secondsTimer!.Interval = TimeSpan.FromMilliseconds(1000 - agora.Millisecond + 15);
        _secondsTimer.Start();
    }

    // ── bateria ─────────────────────────────────────────────
    private bool _hasBattery;
    /// <summary>Falso num desktop: a seção da bateria simplesmente não aparece.</summary>
    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }

    private int _batteryPercent;
    public int BatteryPercent
    {
        get => _batteryPercent;
        private set
        {
            if (!Set(ref _batteryPercent, value)) return;
            OnChanged(nameof(BatteryFill));
            OnChanged(nameof(BatteryBrush));
            OnChanged(nameof(BatteryBarWidth));
            OnChanged(nameof(BatteryBarBrush));
        }
    }

    /// <summary>
    /// A pilha acompanha os outros icones no branco, e so muda de cor quando a cor diz
    /// alguma coisa: vermelho abaixo de 20%, verde enquanto carrega. Cheia e na tomada
    /// nao ha o que avisar — volta ao branco.
    /// </summary>
    public Brush BatteryBrush => BatteryColor(_batteryPercent, _charging);

    /// <summary>A regra da cor, separada para poder ser conferida sem depender da bateria real.</summary>
    public static Brush BatteryColor(int percent, bool charging)
    {
        if (percent < 20) return Frozen(Color.FromRgb(0xE8, 0x11, 0x23));                  // vermelho
        if (charging && percent < 100) return Frozen(Color.FromRgb(0x6C, 0xCB, 0x5F));     // verde
        return Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));                                    // branco
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>A barra do cartão da bateria: 290 px é a largura dele, a do quadriculado.</summary>
    public double BatteryBarWidth => Math.Round(290 * Math.Clamp(_batteryPercent, 0, 100) / 100.0);

    /// <summary>Verde com folga, âmbar abaixo de 40%, vermelha abaixo de 20% — as cores da barra.</summary>
    public Brush BatteryBarBrush => _batteryPercent < 20 ? Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B))
                                  : _batteryPercent < 40 ? Frozen(Color.FromRgb(0xFF, 0xC8, 0x57))
                                  : Frozen(Color.FromRgb(0x7F, 0xD1, 0x8B));

    /// <summary>Largura do miolo da pilha, em pixels — o desenho e o mesmo do Windows.</summary>
    public double BatteryFill => Math.Round(BatteryWidth * Math.Clamp(_batteryPercent, 0, 100) / 100.0);

    /// <summary>Vao interno da pilha desenhada na barra.</summary>
    public const double BatteryWidth = 17;

    private bool _charging;
    public bool Charging
    {
        get => _charging;
        private set { if (Set(ref _charging, value)) OnChanged(nameof(BatteryBrush)); }
    }

    /// <summary>
    /// O numero so aparece ao parar o mouse na pilha: o desenho ja diz o quanto resta, e
    /// a barra fica mais limpa sem a porcentagem escrita o tempo todo.
    /// </summary>
    public string BatteryTooltip => _charging
        ? $"{_batteryPercent}% — na tomada"
        : $"{_batteryPercent}% de carga";

    // ── o cartão da bateria: o gráfico da semana ────────────

    private IReadOnlyList<BatterySample> _batterySamples = [];
    public IReadOnlyList<BatterySample> BatterySamples { get => _batterySamples; private set => Set(ref _batterySamples, value); }

    private string _batteryHealth = string.Empty;
    /// <summary>"Saúde 93% · cheia dura cerca de 4 h 45 min" — vazio enquanto o relatório não chega.</summary>
    public string BatteryHealth { get => _batteryHealth; private set => Set(ref _batteryHealth, value); }

    private bool _batteryLoading;
    public bool BatteryLoading { get => _batteryLoading; private set => Set(ref _batteryLoading, value); }

    /// <summary>Relê a carga de agora na hora, e o histórico em segundo plano (meio segundo de powercfg).</summary>
    public async void LoadBatteryHistory()
    {
        UpdateBattery();
        BatteryDetail = PowerService.BatteryDetail();

        BatteryLoading = BatterySamples.Count == 0;
        var historico = await BatteryHistoryService.ReadAsync();
        BatteryLoading = false;
        if (historico is null) { BatteryHealth = "Não consegui ler o histórico da bateria."; return; }

        BatterySamples = historico.Samples;

        var partes = new List<string>();
        if (historico.Health is { } saude) partes.Add($"Saúde {saude:0}%");
        if (historico.FullRuntime is { } t)
            partes.Add(t.TotalHours >= 1 ? $"cheia dura cerca de {(int)t.TotalHours} h {t.Minutes} min"
                                         : $"cheia dura cerca de {t.Minutes} min");
        BatteryHealth = string.Join(" · ", partes);
    }

    // ── conta ───────────────────────────────────────────────

    /// <summary>Nome e foto de quem está usando a máquina, para o topo do menu de energia.</summary>
    public string UserName => UserService.Name;
    public System.Windows.Media.Imaging.BitmapSource? UserPicture => UserService.Picture;
    public bool HasUserPicture => UserService.HasPicture;
    public bool NoUserPicture => !UserService.HasPicture;
    public string UserInitials => UserService.Initials;

    // ── o estado da máquina, no topo do cartão de energia ────

    private IReadOnlyList<PowerPlan> _powerPlans = [];
    public IReadOnlyList<PowerPlan> PowerPlans { get => _powerPlans; private set => Set(ref _powerPlans, value); }

    private string _uptime = string.Empty;
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }

    private string _batteryDetail = string.Empty;
    public string BatteryDetail { get => _batteryDetail; private set => Set(ref _batteryDetail, value); }

    /// <summary>Relê o tempo ligado, que é o que o cartão do usuário mostra embaixo do nome.</summary>
    public void RefreshPowerCard() => Uptime = PowerService.Uptime();

    /// <summary>
    /// Relê o cartão da bateria quando ele abre. A carga é na hora; plano e histórico só com o
    /// cartão detalhado, que é onde aparecem — o histórico custa meio segundo de powercfg. Trocar
    /// de plano por fora (nas Opções de Energia) fica certo na próxima abertura.
    /// </summary>
    public void RefreshBatteryCard()
    {
        UpdateBattery();
        BatteryDetail = PowerService.BatteryDetail();
        if (!BatteryExpanded) return;

        PowerPlans = PowerService.Plans();
        LoadBatteryHistory();
    }

    public void SetPowerPlan(Guid id)
    {
        if (PowerService.SetPlan(id)) PowerPlans = PowerService.Plans();
    }

    // ── brilho ──────────────────────────────────────────────
    private readonly BrightnessService _brightness = new();

    private int _brightnessLevel;

    /// <summary>
    /// Brilho do monitor, de 0 a 100.
    ///
    /// O valor exibido muda na hora; a escrita no monitor é que sai por fora (veja
    /// <see cref="BrightnessService.Set"/>), porque custa uns 60 ms e travaria o arraste.
    /// </summary>
    public int Brightness
    {
        get => _brightnessLevel;
        set
        {
            var novo = Math.Clamp(value, 0, 100);
            if (!Set(ref _brightnessLevel, novo)) return;

            _brightness.Set(novo);
            OnChanged(nameof(BrightnessGlyph));
        }
    }

    /// <summary>Falso num monitor que não fala DDC/CI: o item some da barra.</summary>
    public bool HasBrightness => _config.PanelBrightness && _brightness.Available;

    /// <summary>
    /// O sol da Segoe Fluent Icons.
    ///
    /// Sem varia\u00E7\u00E3o por n\u00EDvel, ao contr\u00E1rio do alto-falante do volume: a fonte tem um \u00FAnico
    /// desenho de brilho, e inventar varia\u00E7\u00E3o com outros glifos daria um \u00EDcone que n\u00E3o
    /// combina com o resto da barra. Quem diz o n\u00EDvel \u00E9 a barrinha do card.
    /// </summary>
    public string BrightnessGlyph => "\uE706";

    /// <summary>Roda do mouse sobre o ícone: sobe ou desce de cinco em cinco.</summary>
    public void NudgeBrightness(int direction) =>
        Brightness = _brightnessLevel + (direction > 0 ? 5 : -5);

    // ── rede e sistema ──────────────────────────────────────

    private readonly SystemStatsService _stats = new();

    /// <summary>
    /// O item de sistema na barra — e, com ele, a medição.
    ///
    /// A rede não tem item próprio: ela é medida junto e aparece no cartão. Desligar este
    /// item desliga as três medidas, que é o que faz a opção não custar nada a quem não a quer.
    /// </summary>
    public bool HasSystemInfo => _config.PanelSystem;

    /// <summary>O botão de ferramentas — veja <c>DockConfig.PanelTools</c>.</summary>
    public bool HasTools => _config.PanelTools;

    /// <summary>Uma linha do cartão de ferramentas: o comando da config e o desenho do que ele abre.</summary>
    public sealed record ToolRow(ToolCommand Tool, string Glyph, System.Windows.Media.Brush Fill)
    {
        public string Name => Tool.Name.Length > 0 ? Tool.Name : Tool.Command;
        public string Command => Tool.Command;
    }

    private IReadOnlyList<ToolRow>? _tools;

    /// <summary>
    /// Os comandos do cartão, montados na primeira vez que o cartão abre e de novo quando a
    /// lista muda nas Configurações — e não a cada letra digitada lá.
    /// </summary>
    public IReadOnlyList<ToolRow> Tools => _tools ??= _config.Tools
        .Where(t => !string.IsNullOrWhiteSpace(t.Command))
        .Select(t => { var (glifo, cor) = ToolsService.Look(t); return new ToolRow(t, glifo, cor); })
        .ToList();

    public bool HasNoTools => Tools.Count == 0;

    private string _toolsError = string.Empty;
    /// <summary>
    /// Por que o último comando do cartão não abriu. Fica no cartão, aberto, em vez de ele se
    /// fechar como se tivesse dado certo — e some na próxima vez que o cartão abre.
    /// </summary>
    public string ToolsError { get => _toolsError; set => Set(ref _toolsError, value); }

    /// <summary>Ícones da barra com metade do respiro — veja <c>DockConfig.PanelCompact</c>.</summary>
    public bool PanelCompact => _config.PanelCompact;

    private string _netDown = "0 B/s";
    public string NetDown { get => _netDown; private set => Set(ref _netDown, value); }

    private string _netUp = "0 B/s";
    public string NetUp { get => _netUp; private set => Set(ref _netUp, value); }

    private string _cpuText = "0%";
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }

    private string _ramText = "0%";
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }

    private string _gpuText = "0%";
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    private bool _hasGpu;
    /// <summary>A placa de vídeo entra no cartão quando a leitura dela chega — veja o <c>GpuMeter</c>.</summary>
    public bool HasGpu { get => _hasGpu; private set => Set(ref _hasGpu, value); }

    private string _systemTooltip = "CPU e memória";
    public string SystemTooltip { get => _systemTooltip; private set => Set(ref _systemTooltip, value); }

    /// <summary>Os últimos 60 segundos de cada medida — é o que os gráficos do cartão desenham.</summary>
    public IReadOnlyList<double> CpuHistory => _stats.CpuHistory;
    public IReadOnlyList<double> RamHistory => _stats.RamHistory;
    public IReadOnlyList<double> DownHistory => _stats.DownHistory;
    public IReadOnlyList<double> UpHistory => _stats.UpHistory;
    public IReadOnlyList<double> GpuHistory => _stats.GpuHistory;

    /// <summary>
    /// Mede tudo uma vez por segundo, e só com o item ligado: somar os contadores de todos
    /// os adaptadores é barato, mas não é de graça, e quem deixou o item desligado não deve
    /// pagar por isso.
    /// </summary>
    private void UpdateStats()
    {
        if (!HasSystemInfo) return;

        _stats.Sample();

        NetDown = SystemStatsService.Rate(_stats.Down);
        NetUp = SystemStatsService.Rate(_stats.Up);
        CpuText = _stats.Cpu.ToString("0") + "%";
        RamText = _stats.Ram.ToString("0") + "%";
        GpuText = _stats.Gpu.ToString("0") + "%";
        HasGpu = _stats.HasGpu;

        // a rede entra na dica do item, e não na barra: quem quer o número de passagem lê
        // aqui, e quem quer acompanhar abre o cartão
        SystemTooltip = HasGpu
            ? $"CPU {CpuText} · memória {RamText} · GPU {GpuText} · rede {NetDown} ↓ {NetUp} ↑"
            : $"CPU {CpuText} · memória {RamText} · rede {NetDown} ↓ {NetUp} ↑";

        // os gráficos só existem enquanto o cartão está aberto; avisar sempre não custa
        // nada e evita o cartão abrir com o desenho de um minuto atrás
        OnChanged(nameof(CpuHistory));
        OnChanged(nameof(RamHistory));
        OnChanged(nameof(DownHistory));
        OnChanged(nameof(UpHistory));
        OnChanged(nameof(GpuHistory));
    }

    // ── mídia ───────────────────────────────────────────────
    private readonly MediaService _media = new();

    private MediaInfo _mediaInfo = MediaInfo.None;

    /// <summary>O que está tocando. Trocar isto redesenha o item da barra e o card.</summary>
    public MediaInfo Media
    {
        get => _mediaInfo;
        private set
        {
            _mediaInfo = value;
            OnChanged(nameof(Media));
            OnChanged(nameof(ShowMedia));
            OnChanged(nameof(MediaGlyph));
            OnChanged(nameof(MediaProgress));
            OnChanged(nameof(MediaElapsed));
            OnChanged(nameof(MediaRemaining));
        }
    }

    /// <summary>
    /// O item da barra só existe quando há mídia — e quando a pessoa quis o recurso.
    ///
    /// Sumir em vez de esmaecer foi escolha de quem usa: a barra já carrega relógio, bandeja,
    /// bateria, wi-fi, bluetooth, volume, sino e energia, e um item a mais parado ali o dia
    /// inteiro pesa mais do que o deslocamento dos vizinhos quando a música começa.
    /// </summary>
    public bool ShowMedia => _config.PanelMedia && _mediaInfo.HasMedia;

    /// <summary>
    /// Pausar quando está tocando, tocar quando está parado.
    ///
    /// Escrito como escape, e não com o caractere: glifos da Segoe Fluent Icons são um
    /// quadradinho vazio no editor e se perdem em qualquer edição de texto que passe por
    /// cima — o botão fica invisível na barra sem quebrar nem avisar nada. Aconteceu com o
    /// ícone de energia antes, e aconteceu de novo com estes três aqui.
    /// </summary>
    public string MediaGlyph => _mediaInfo.IsPlaying ? "\uE769" : "\uE768";

    /// <summary>Quanto da faixa já passou, de 0 a 1 — a largura da barrinha do card.</summary>
    public double MediaProgress => _mediaInfo.Duration > TimeSpan.Zero
        ? Math.Clamp(_mediaInfo.Position.TotalSeconds / _mediaInfo.Duration.TotalSeconds, 0, 1)
        : 0;

    public string MediaElapsed => Clock(_mediaInfo.Position);
    public string MediaRemaining => Clock(_mediaInfo.Duration);

    /// <summary>"3:07" — e "1:02:33" só quando a faixa passa da hora.</summary>
    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                          : $"{t.Minutes}:{t.Seconds:00}";

    public Task ToggleMedia() => _media.TogglePlay();
    public Task NextMedia() => _media.Next();
    public Task PreviousMedia() => _media.Previous();
    public Task SeekMedia(double fraction) => _media.Seek(fraction);

    /// <summary>
    /// Relê a posição da faixa. Só vale a pena com o card aberto: o resto da informação
    /// chega por evento, e a posição é a única coisa que anda sozinha.
    /// </summary>
    public async Task RefreshMediaPosition()
    {
        await _media.Refresh();
        Media = _media.Current;
    }

    // ── volume ──────────────────────────────────────────────
    private int _volume;
    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;
            VolumeService.Level = value;
            OnChanged(nameof(VolumeGlyph));
            OnChanged(nameof(VolumeBrush));
            OnChanged(nameof(VolumeFill));
        }
    }

    /// <summary>
    /// A cor da barra de volume, no mesmo espirito da pilha: so muda quando a cor diz alguma
    /// coisa. Aqui a escala e ao contrario da bateria — volume alto e que merece aviso.
    /// </summary>
    public Brush VolumeBrush => VolumeColor(_volume, _muted);

    /// <summary>
    /// Largura da parte preenchida da barra do mostrador, em pixels.
    ///
    /// E o mesmo caminho da pilha (<see cref="BatteryFill"/>), e nao um <c>ProgressBar</c>:
    /// tentou-se com um, e ele nao desenha nada. O WPF so calcula a largura do
    /// <c>PART_Indicator</c> quando o template tambem tem um <c>PART_Track</c> para medir —
    /// sem ele o indicador fica do tamanho todo e o que aparece e a barra de fundo, cheia.
    /// Uma conta aqui e previsivel e nao depende de peca nomeada nenhuma.
    /// </summary>
    public double VolumeFill => Math.Round(VolumeBarWidth * Math.Clamp(_volume, 0, 100) / 100.0);

    /// <summary>Largura da barra do mostrador de volume. Casa com a do cartao no XAML.</summary>
    public const double VolumeBarWidth = 150;

    /// <summary>A regra da cor, separada para poder ser conferida sem depender do audio real.</summary>
    public static Brush VolumeColor(int percent, bool muted)
    {
        if (muted || percent == 0) return Frozen(Color.FromRgb(0x9D, 0x9D, 0x9D));   // cinza
        if (percent < 30) return Frozen(Color.FromRgb(0x6C, 0xCB, 0x5F));            // verde
        if (percent <= 70) return Frozen(Color.FromRgb(0xE8, 0xBF, 0x2E));           // amarelo
        return Frozen(Color.FromRgb(0xE8, 0x11, 0x23));                              // vermelho
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            VolumeService.Muted = value;
            OnChanged(nameof(VolumeGlyph));
            OnChanged(nameof(VolumeBrush));
        }
    }

    /// <summary>
    /// Glifos da Segoe Fluent Icons, a fonte de icones do proprio Windows 11: assim o
    /// desenho do alto-falante e o das ondas de wi-fi sao os mesmos da barra nativa.
    /// </summary>
    public string VolumeGlyph => _muted || _volume == 0 ? ""   // alto-falante cortado
                               : _volume < 33          ? ""   // uma onda
                               : _volume < 66          ? ""   // duas
                                                       : "";  // cheio

    public string BellGlyph => "";
    public string BluetoothGlyph => "";

    // ── bluetooth ───────────────────────────────────────────
    private bool _bluetoothOn;
    /// <summary>O radio esta ligado. Desligado, o icone fica apagado.</summary>
    public bool BluetoothOn
    {
        get => _bluetoothOn;
        private set { if (Set(ref _bluetoothOn, value)) OnChanged(nameof(BluetoothOpacity)); }
    }

    /// <summary>Apagado quando o radio esta desligado — o mesmo que o Windows faz.</summary>
    public double BluetoothOpacity => _bluetoothOn ? 1.0 : 0.4;

    private string _bluetoothTooltip = "Bluetooth desligado";
    public string BluetoothTooltip { get => _bluetoothTooltip; private set => Set(ref _bluetoothTooltip, value); }

    /// <summary>Configuracoes de bluetooth e dispositivos.</summary>
    public const string BluetoothSettings = "ms-settings:bluetooth";

    private IReadOnlyList<BluetoothDevice> _bluetoothDevices = Array.Empty<BluetoothDevice>();

    /// <summary>Aparelhos lembrados pelo Windows, os conectados primeiro.</summary>
    public IReadOnlyList<BluetoothDevice> BluetoothDevices
    {
        get => _bluetoothDevices;
        private set
        {
            if (!Set(ref _bluetoothDevices, value)) return;
            OnChanged(nameof(HasBluetoothDevices));
            OnChanged(nameof(BluetoothConnected));
            OnChanged(nameof(HasNoBluetoothConnected));
        }
    }

    public bool HasBluetoothDevices => _bluetoothDevices.Count > 0;

    /// <summary>Texto do topo da lista: o estado do radio, em uma linha.</summary>
    public string BluetoothState => _bluetoothOn ? "Bluetooth ligado" : "Bluetooth desligado";

    /// <summary>Quantos aparelhos estao escondidos pela configuracao.</summary>
    public int HiddenBluetoothCount { get; private set; }

    public bool HasHiddenBluetooth => HiddenBluetoothCount > 0;

    /// <summary>
    /// Monta a lista quando o painel abre. Ela vem da lista de dispositivos do Windows, e
    /// nao da API de bluetooth, entao aparece mesmo com o radio desligado — util para saber
    /// o que esta pareado antes de ligar.
    /// </summary>
    public void RefreshBluetoothDevices()
    {
        BluetoothOn = BluetoothService.IsOn;
        OnChanged(nameof(BluetoothState));

        var all = BluetoothService.Devices();
        var hidden = _config.BluetoothHidden;

        HiddenBluetoothCount = all.Count(d => hidden.Contains(d.Name, StringComparer.CurrentCultureIgnoreCase));
        OnChanged(nameof(HiddenBluetoothCount));
        OnChanged(nameof(HasHiddenBluetooth));

        BluetoothDevices = all
            .Where(d => !hidden.Contains(d.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Liga ou desliga o radio, e remonta a lista com a resposta.
    ///
    /// O radio demora um instante para mudar de estado — pedir e perguntar na mesma linha
    /// devolveria o valor antigo. Por isso o <c>SetOn</c> e esperado e so depois a lista e
    /// remontada; enquanto isso <see cref="BluetoothBusy"/> desliga o botao, para dois
    /// cliques seguidos nao virarem dois pedidos concorrentes.
    /// </summary>
    public async Task ToggleBluetooth()
    {
        if (BluetoothBusy) return;

        BluetoothBusy = true;
        try { await BluetoothService.SetOn(!BluetoothOn); }
        finally { BluetoothBusy = false; }

        RefreshBluetoothDevices();
    }

    private bool _bluetoothBusy;
    /// <summary>O radio esta mudando de estado agora: o botao fica desabilitado.</summary>
    public bool BluetoothBusy
    {
        get => _bluetoothBusy;
        private set { if (Set(ref _bluetoothBusy, value)) OnChanged(nameof(BluetoothReady)); }
    }

    public bool BluetoothReady => !_bluetoothBusy;

    /// <summary>Tira um aparelho da lista da barra. Ele continua pareado no Windows.</summary>
    public void HideBluetoothDevice(string name)
    {
        if (!_config.BluetoothHidden.Contains(name, StringComparer.CurrentCultureIgnoreCase))
            _config.BluetoothHidden.Add(name);

        _config.Save();
        RefreshBluetoothDevices();
    }

    public void ShowAllBluetoothDevices()
    {
        _config.BluetoothHidden.Clear();
        _config.Save();
        RefreshBluetoothDevices();
    }

    /// <summary>Os aparelhos que estão conectados agora — o que o cartão mostra recolhido.</summary>
    public IReadOnlyList<BluetoothDevice> BluetoothConnected => _bluetoothDevices.Where(d => d.Connected).ToList();

    public bool HasNoBluetoothConnected => !_bluetoothDevices.Any(d => d.Connected);

    // ── wi-fi ───────────────────────────────────────────────

    private WifiState _wifi = WifiState.None;

    /// <summary>O rádio, a rede de agora e o sinal — relidos de cinco em cinco segundos e ao abrir o cartão.</summary>
    public WifiState Wifi
    {
        get => _wifi;
        private set
        {
            if (!Set(ref _wifi, value)) return;
            OnChanged(nameof(WifiGlyph));
            OnChanged(nameof(WifiOpacity));
            OnChanged(nameof(WifiTooltip));
            OnChanged(nameof(WifiStatus));
            OnChanged(nameof(WifiShowsBars));
        }
    }

    /// <summary>As ondas pelo sinal, como na barra do Windows — veja <c>WifiService.Glyph</c>.</summary>
    public string WifiGlyph => WifiService.Glyph(_wifi);

    /// <summary>
    /// As ondas apagadas por trás das acesas: com duas barras, sem elas o desenho seria um
    /// arquinho solto, e não "metade do sinal".
    /// </summary>
    public bool WifiShowsBars => _wifi.On && _wifi.Connected && _wifi.Bars < 4;

    public double WifiOpacity => _wifi.On ? 1.0 : 0.4;

    public string WifiTooltip =>
        !_wifi.HasRadio ? "Wi-Fi"
        : !_wifi.On ? "Wi-Fi desligado"
        : !_wifi.Connected ? "Wi-Fi: sem conexão"
        : $"{_wifi.Ssid}\n{(_wifi.Internet ? "Conectado, com internet" : "Conectado, sem internet")}";

    /// <summary>A linha de baixo da rede de agora, no cartão.</summary>
    public string WifiStatus =>
        !_wifi.On ? "O Wi-Fi está desligado"
        : !_wifi.Connected ? "Nenhuma rede conectada"
        : _wifi.Internet ? "Conectado, com internet" : "Conectado, sem internet";

    private bool _wifiBusy;
    /// <summary>O rádio está mudando de estado, ou uma conexão está em curso: o interruptor espera.</summary>
    public bool WifiBusy
    {
        get => _wifiBusy;
        private set { if (Set(ref _wifiBusy, value)) OnChanged(nameof(WifiReady)); }
    }

    public bool WifiReady => !_wifiBusy;

    private IReadOnlyList<WifiNetwork> _wifiNetworks = Array.Empty<WifiNetwork>();
    public IReadOnlyList<WifiNetwork> WifiNetworks
    {
        get => _wifiNetworks;
        private set => Set(ref _wifiNetworks, value);
    }

    private bool _wifiNeedsLocation;
    /// <summary>O Windows não deixou listar as redes — é a localização desligada (veja o <c>WifiService</c>).</summary>
    public bool WifiNeedsLocation { get => _wifiNeedsLocation; private set => Set(ref _wifiNeedsLocation, value); }

    private bool _wifiScanning;
    public bool WifiScanning { get => _wifiScanning; private set => Set(ref _wifiScanning, value); }

    public async Task RefreshWifi() => Wifi = await WifiService.ReadAsync();

    /// <summary>
    /// Procura as redes ao alcance — só com o cartão detalhado, que é onde a lista aparece: a
    /// varredura acorda o rádio e leva uns segundos.
    /// </summary>
    public async Task ScanWifi()
    {
        if (!_wifi.On || WifiScanning) { WifiNetworks = Array.Empty<WifiNetwork>(); return; }

        WifiScanning = true;
        try
        {
            var redes = await WifiService.ScanAsync(_wifi.Ssid);
            WifiNeedsLocation = redes is null;
            WifiNetworks = redes ?? Array.Empty<WifiNetwork>();
        }
        finally { WifiScanning = false; }

        CardResized?.Invoke(this, EventArgs.Empty);
    }

    public async Task ToggleWifi()
    {
        if (WifiBusy || !_wifi.HasRadio) return;

        WifiBusy = true;
        try { await WifiService.SetOn(!_wifi.On); }
        finally { WifiBusy = false; }

        // o rádio religado leva um instante até se conectar: a primeira leitura sai "sem rede", e a
        // de cinco segundos depois acerta
        await RefreshWifi();
        WifiNetworks = Array.Empty<WifiNetwork>();
        if (WifiExpanded && _wifi.On) await ScanWifi();
    }

    /// <summary>Conecta numa rede da lista. Falso quando ela pede senha — aí a do Windows assume.</summary>
    public async Task<bool> ConnectWifi(WifiNetwork rede)
    {
        if (WifiBusy) return true;

        WifiBusy = true;
        bool ok;
        try { ok = await WifiService.ConnectAsync(rede); }
        finally { WifiBusy = false; }

        await RefreshWifi();
        if (ok) await ScanWifi();
        return ok;
    }

    // ── cartões compactos ───────────────────────────────────

    /// <summary>
    /// Os cartões de volume, wi-fi, bluetooth e bateria nascem compactos — o essencial — e a seta
    /// do rodapé abre o resto, como o da cota de IA. A escolha de cada um mora na configuração
    /// (<c>DockConfig.CardsExpanded</c>), pelo mesmo motivo do da cota: é jeito de ler.
    /// </summary>
    public bool VolumeExpanded => IsExpanded("volume");
    public bool BluetoothExpanded => IsExpanded("bluetooth");
    public bool WifiExpanded => IsExpanded("wifi");
    public bool BatteryExpanded => IsExpanded("bateria");

    private bool IsExpanded(string card) => _config.CardsExpanded.Contains(card, StringComparer.OrdinalIgnoreCase);

    public void ToggleCard(string card)
    {
        // tirar devolve quantos saíram: nenhum quer dizer que estava recolhido, e então abre
        if (_config.CardsExpanded.RemoveAll(c => c.Equals(card, StringComparison.OrdinalIgnoreCase)) == 0)
            _config.CardsExpanded.Add(card);

        _config.Save();
        OnChanged(nameof(VolumeExpanded));
        OnChanged(nameof(BluetoothExpanded));
        OnChanged(nameof(WifiExpanded));
        OnChanged(nameof(BatteryExpanded));

        // recolher encolhe o cartão debaixo do cursor — veja o CardResized
        CardResized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sobe ou desce o volume na roda do mouse, em degraus de dois — o mesmo passo do
    /// controle do Windows.
    /// </summary>
    public void Nudge(int direction)
    {
        if (Muted && direction > 0) Muted = false;
        Volume = Math.Clamp(Volume + (direction > 0 ? 2 : -2), 0, 100);
    }

    // ── saidas de audio ─────────────────────────────────────
    private IReadOnlyList<VolumeService.AudioDevice> _audioDevices = Array.Empty<VolumeService.AudioDevice>();

    /// <summary>Fones, alto-falantes, o audio do monitor: o que estiver ligado.</summary>
    public IReadOnlyList<VolumeService.AudioDevice> AudioDevices
    {
        get => _audioDevices;
        private set { if (Set(ref _audioDevices, value)) OnChanged(nameof(AudioDefault)); }
    }

    /// <summary>Só a saída em uso — o que o cartão de volume mostra recolhido.</summary>
    public IReadOnlyList<VolumeService.AudioDevice> AudioDefault => _audioDevices.Where(d => d.IsDefault).ToList();

    /// <summary>
    /// A lista e montada quando o controle de volume abre, e nao de segundo em segundo:
    /// enumerar os dispositivos custa mais que ler um volume, e eles quase nunca mudam.
    /// </summary>
    public void RefreshAudioDevices() => AudioDevices = VolumeService.Devices();

    // ── volume por aplicativo ───────────────────────────────
    private IReadOnlyList<VolumeService.AppSession> _appSessions = Array.Empty<VolumeService.AppSession>();

    /// <summary>O mixer: um controle por programa com som, como no painel do Windows.</summary>
    public IReadOnlyList<VolumeService.AppSession> AppSessions
    {
        get => _appSessions;
        private set
        {
            // as sessoes seguram interfaces COM; as antigas saem de cena aqui, e nao no
            // coletor de lixo, para nao deixar o servico de audio com referencias penduradas
            foreach (var old in _appSessions) old.Dispose();

            Set(ref _appSessions, value);
            OnChanged(nameof(HasAppSessions));
        }
    }

    public bool HasAppSessions => _appSessions.Count > 0;

    /// <summary>Mostrar o volume por aplicativo — a opcao do painel de configuracoes.</summary>
    public bool ShowAppVolume => _config.PanelAppVolume;

    public void RefreshAppSessions() =>
        AppSessions = _config.PanelAppVolume
            ? VolumeService.Sessions()
            : Array.Empty<VolumeService.AppSession>();

    // ── bandeja ─────────────────────────────────────────────
    private IReadOnlyList<TrayIcon> _trayIcons = Array.Empty<TrayIcon>();

    /// <summary>Icones de bandeja dos programas rodando agora.</summary>
    public IReadOnlyList<TrayIcon> TrayIcons
    {
        get => _trayIcons;
        private set
        {
            if (!Set(ref _trayIcons, value)) return;
            OnChanged(nameof(HasTrayIcons));
            OnChanged(nameof(TrayColumns));
        }
    }

    public bool HasTrayIcons => _trayIcons.Count > 0;

    /// <summary>
    /// Colunas da grade do cartão.
    ///
    /// Fixar cinco reservava cinco células mesmo com três ícones, e o cartão nascia com um
    /// vão à direita. Aqui a grade tem a largura do que existe, até cinco por linha.
    /// </summary>
    public int TrayColumns => Math.Clamp(_trayIcons.Count, 1, 5);

    /// <summary>A seta da bandeja aparece na barra (opção ligada).</summary>
    public bool ShowTray => _config.PanelTray;

    /// <summary>
    /// A seta que abre o cartão da bandeja — a mesma ideia do "mostrar ícones ocultos" do
    /// Windows, e por isso aponta para baixo: é de lá que o cartão sai.
    ///
    /// Escrita pelo código, e não pelo caractere: os glifos da Segoe são um quadradinho
    /// vazio no editor, e um deles já se perdeu numa substituição de texto — a seta some da
    /// barra sem nada quebrar nem avisar.
    /// </summary>
    public string TrayGlyph => "\uE70D";

    public string TrayTooltip => "Ícones ocultos";

    // ── remover dispositivo externo ─────────────────────────

    /// <summary>
    /// O pen-drive da Segoe. Conferido em 15 px, que é o tamanho dos ícones da barra.
    ///
    /// Escrito pelo código, e não pelo caractere: veja o <see cref="TrayGlyph"/>.
    /// </summary>
    public string RemovableGlyph => "";

    private IReadOnlyList<RemovableDevice> _removable = Array.Empty<RemovableDevice>();

    public IReadOnlyList<RemovableDevice> Removable
    {
        get => _removable;
        private set
        {
            if (!Set(ref _removable, value)) return;
            OnChanged(nameof(HasRemovable));
            OnChanged(nameof(RemovableTooltip));
        }
    }

    /// <summary>
    /// O ícone só existe enquanto houver o que remover.
    ///
    /// Foi o pedido, e é o mesmo critério dos controles de mídia: um botão que passa o dia
    /// inteiro parado dizendo "nenhum dispositivo" ocupa lugar sem nunca responder nada —
    /// enquanto o vizinho se deslocar quando um pen-drive entra é justamente o aviso de que
    /// ele entrou.
    /// </summary>
    public bool HasRemovable => _config.PanelRemovable && _removable.Count > 0;

    public string RemovableTooltip =>
        _removable.Count switch
        {
            0 => "Remover dispositivo",
            1 => $"Remover com segurança — {_removable[0].Name}",
            var n => $"Remover com segurança — {n} dispositivos"
        };

    /// <summary>O que aconteceu na última tentativa; some quando o cartão fecha.</summary>
    private string _removableMessage = string.Empty;
    public string RemovableMessage
    {
        get => _removableMessage;
        private set { if (Set(ref _removableMessage, value)) OnChanged(nameof(HasRemovableMessage)); }
    }

    public bool HasRemovableMessage => _removableMessage.Length > 0;

    /// <summary>Enquanto o Windows decide, o cartão não aceita um segundo clique.</summary>
    private bool _removableBusy;
    public bool RemovableBusy
    {
        get => _removableBusy;
        private set { if (Set(ref _removableBusy, value)) OnChanged(nameof(RemovableReady)); }
    }

    public bool RemovableReady => !_removableBusy;

    /// <summary>Limpa o recado da última tentativa — chamado quando o cartão sai de cena.</summary>
    public void ClearRemovableMessage() => RemovableMessage = string.Empty;

    /// <summary>
    /// Abre o aparelho no Explorer.
    ///
    /// <b>Uma janela por raiz montada.</b> No caso normal — um pen-drive, uma letra — é uma
    /// janela só; num HD externo particionado em duas, são duas, porque "abrir o aparelho" ali
    /// quer dizer os dois volumes dele. Escolher uma das letras seria escolher no lugar de quem
    /// clicou, e não há critério: nem a primeira do alfabeto nem a maior é "a certa".
    /// </summary>
    public void OpenRemovable(string id)
    {
        var aparelho = _removable.FirstOrDefault(d => d.Id == id);
        if (aparelho is null) return;

        foreach (var raiz in aparelho.Roots) WindowService.OpenFolder(raiz);
    }

    /// <summary>
    /// Relê a lista em segundo plano.
    ///
    /// <para><b>Fora da thread da interface de propósito.</b> Cada letra montada custa dois
    /// IOCTLs e a varredura das interfaces de disco; numa máquina com muitos volumes isso é
    /// alguns milissegundos, mas uma letra de rede fora do ar pode segurar a resposta por
    /// bem mais que isso — e seria a barra inteira congelada no instante em que alguém
    /// espeta um pen-drive.</para>
    /// </summary>
    public void RefreshRemovable()
    {
        if (!_config.PanelRemovable) return;

        _ = Task.Run(() =>
        {
            var lista = RemovableService.Devices();

            Log.Trace(lista.Count == 0
                ? "externos: nenhum dispositivo removível montado"
                : $"externos: {string.Join("; ", lista.Select(d => $"{d.Name} ({d.Letters})"))}");

            _dispatcher.InvokeAsync(() => Removable = lista);
        });
    }

    /// <summary>
    /// Pede a remoção segura e conta o que deu.
    ///
    /// Em caso de sucesso a lista é relida na hora, e não pelo aviso do Windows: o
    /// <c>WM_DEVICECHANGE</c> da saída chega, mas chega depois — e entre uma coisa e outra o
    /// cartão continuaria oferecendo um aparelho que já saiu.
    /// </summary>
    public async Task EjectRemovable(string id)
    {
        if (RemovableBusy) return;

        var aparelho = _removable.FirstOrDefault(d => d.Id == id);
        RemovableBusy = true;
        RemovableMessage = "Removendo…";

        try
        {
            var erro = await RemovableService.Eject(id);

            RemovableMessage = erro.Length == 0
                ? $"{aparelho?.Name ?? "Dispositivo"} pode ser desconectado"
                : $"Não deu para remover: {erro}";
        }
        finally { RemovableBusy = false; }

        RefreshRemovable();
    }

    // ── atualizações esperando ──────────────────────────────

    private DispatcherTimer? _updatesTimer;

    /// <summary>
    /// A seta para baixo sobre a linha da Segoe Fluent Icons — o mesmo desenho que a Microsoft
    /// Store e o Windows Update usam para "há o que baixar".
    ///
    /// Escrito pelo código, e não pelo caractere: veja o <c>TrayGlyph</c>.
    ///
    /// Enquanto o winget instala, vira as duas setas em círculo: a barra também precisa dizer que
    /// o clique está andando — antes só o cartão sabia, e com ele fechado o ícone parecia ignorar
    /// a ordem até o número mudar de repente.
    /// </summary>
    public string UpdatesGlyph => _updatesUpgrading ? "" : "";

    private UpdateStatus _updates = UpdateStatus.Empty;

    public UpdateStatus Updates
    {
        get => _updates;
        private set
        {
            if (!Set(ref _updates, value)) return;
            OnChanged(nameof(HasUpdatesPending));
            OnChanged(nameof(UpdatesTooltip));
            OnChanged(nameof(UpdatesCount));
            OnChanged(nameof(UpdatesFill));
            OnChanged(nameof(UpdatesEmptyText));
            OnChanged(nameof(WindowsUpdates));
            OnChanged(nameof(UpdatesError));
            OnChanged(nameof(HasUpdatesError));
            OnChanged(nameof(HasWindowsUpdates));
            OnChanged(nameof(HasWingetUpdates));
            OnChanged(nameof(WindowsUpdatesText));
            OnChanged(nameof(UpdatesReadAt));
            OnChanged(nameof(WingetUpdates));
            OnChanged(nameof(WingetSilenced));
            OnChanged(nameof(HasWingetSilenced));
            OnChanged(nameof(WingetSilencedCount));
            OnChanged(nameof(WingetSilencedText));
            OnChanged(nameof(HasWingetUpgradeAll));
        }
    }

    /// <summary>
    /// O ícone fica na barra enquanto a opção estiver ligada — com ou sem atualização esperando.
    ///
    /// Era só com, no começo: "nada para atualizar" não parecia um estado que merecesse um ícone.
    /// O que isso tem de ruim só aparece em uso — sem o ícone não há diferença entre "está tudo em
    /// dia" e "a dock não está conferindo", e a resposta some junto com a pergunta. Apagado, ele
    /// responde as duas coisas de uma vez, e quem não quiser nenhum dos dois desliga a opção.
    /// </summary>
    public bool HasUpdates => _config.PanelUpdates;

    /// <summary>Há algo esperando — é o que acende o ícone e mostra o número.</summary>
    public bool HasUpdatesPending => _updates.Any;

    /// <summary>Azul quando há o que atualizar ou o winget está instalando; o cinza dos ícones apagados quando não há.</summary>
    public string UpdatesFill => _updates.Any || _updatesUpgrading ? "#FF4CC2FF" : "#FF8A8A8A";

    /// <summary>O número ao lado do ícone — vazio quando não há nada, e aí ele nem aparece.</summary>
    public string UpdatesCount => _updates.Any ? _updates.Total.ToString(CultureInfo.CurrentCulture) : string.Empty;

    public bool HasWindowsUpdates => _updates.Windows.Count > 0;
    public bool HasWingetUpdates => _updates.Winget.Count > 0;

    public string WindowsUpdatesText => _updates.Windows.Count == 1
        ? "1 atualização do Windows"
        : $"{_updates.Windows.Count} atualizações do Windows";

    /// <summary>Os títulos, como o Windows Update os escreve — é o que faz o cartão bater com a tela dele.</summary>
    public IReadOnlyList<string> WindowsUpdates => _updates.Windows;

    public IReadOnlyList<WingetPackage> WingetUpdates => _updates.Winget;

    /// <summary>
    /// Se o botão "Atualizar tudo pelo winget" tem o que fazer.
    ///
    /// Some quando tudo o que está na lista exige alvo explícito — caso real desta máquina, com o
    /// Discord sozinho ali. O botão continuaria clicável, o terminal abriria, o winget diria que
    /// não há nada a fazer, e a promessa do cartão teria sido falsa. Cada pacote desses tem o seu
    /// próprio "Atualizar" na linha.
    /// </summary>
    public bool HasWingetUpgradeAll => _updates.Winget.Any(p => !p.Explicit);

    /// <summary>Os que a pessoa mandou calar e continuam sendo oferecidos — fora da conta do ícone.</summary>
    public IReadOnlyList<WingetPackage> WingetSilenced => _updates.Silenced;
    public bool HasWingetSilenced => _updates.Silenced.Count > 0;
    public int WingetSilencedCount => _updates.Silenced.Count;

    /// <summary>
    /// Para de avisar sobre um pacote do winget.
    ///
    /// Vale para o pacote, não para a versão: o que incomoda é a oferta que nunca se resolve, e ela
    /// volta com número novo a cada semana. A lista some do cartão na hora, sem nova consulta — a
    /// resposta já está na mão, e o que mudou foi só de que lado da linha o pacote está.
    /// </summary>
    public void SilenceWinget(string key)
    {
        if (key.Length == 0) return;

        if (!_config.UpdatesSilenced.Contains(key, StringComparer.OrdinalIgnoreCase))
            _config.UpdatesSilenced.Add(key);

        _config.Save();
        Log.Write($"atualizações: {key} silenciado no winget");
        Updates = _updates.WithSilenced(_config.UpdatesSilenced);
        SchedulePace();
    }

    /// <summary>Volta a avisar sobre todos — o desfazer do "✕", no mesmo cartão.</summary>
    public void UnsilenceWingetAll()
    {
        if (_config.UpdatesSilenced.Count == 0) return;

        _config.UpdatesSilenced.Clear();
        _config.Save();
        Log.Write("atualizações: nenhum pacote do winget silenciado");
        Updates = _updates.WithSilenced(_config.UpdatesSilenced);
        SchedulePace();
    }

    /// <summary>
    /// Uma consulta está em curso.
    ///
    /// Existe por causa do "Conferir agora": a busca do Windows Update leva uns doze segundos, e um
    /// botão que não responde nesse tempo parece quebrado — foi assim que o defeito de 28/09
    /// apareceu para quem usava.
    /// </summary>
    private bool _updatesChecking;
    public bool UpdatesChecking
    {
        get => _updatesChecking;
        private set
        {
            if (!Set(ref _updatesChecking, value)) return;
            OnChanged(nameof(UpdatesReadAt));
            OnChanged(nameof(UpdatesEmptyText));
        }
    }

    /// <summary>
    /// O erro da última conferência, quando houve um.
    ///
    /// Mostrá-lo é o que separa "não há nada para atualizar" de "não consegui perguntar" — duas
    /// frases que o cartão dizia do mesmo jeito, e essa é metade do defeito de 28/09.
    /// </summary>
    public string UpdatesError => _updates.Error ?? string.Empty;
    public bool HasUpdatesError => _updates.Failed;

    public string UpdatesReadAt => _updatesUpgrading
        ? "atualizando…"
        : _updatesChecking
          ? "conferindo…"
          : _updates.When > DateTime.MinValue ? $"conferido às {_updates.When:HH:mm}" : string.Empty;

    public string UpdatesTooltip
    {
        get
        {
            if (_updatesUpgrading) return "Atualizando pelo winget…";

            if (!_updates.Any)
                return _updates.When > DateTime.MinValue
                       ? $"Nada para atualizar\n{UpdatesReadAt}"
                       : "Atualizações — ainda conferindo";

            var linhas = new List<string> { "Atualizações esperando" };
            if (HasWindowsUpdates) linhas.Add(WindowsUpdatesText);
            if (HasWingetUpdates) linhas.Add($"{_updates.Winget.Count} pelo winget");
            return string.Join("\n", linhas);
        }
    }

    /// <summary>
    /// A frase do cartão quando não há nada esperando — e ela diz de que fontes está falando.
    ///
    /// "Nada para atualizar" sozinho é ambíguo: pode ser que não haja mesmo, ou que a dock não
    /// tenha olhado. Nomear as duas fontes, com a hora da conferência embaixo, responde isso.
    /// </summary>
    public string UpdatesEmptyText => _updatesUpgrading
        ? "Atualizando pelo winget — isto pode levar alguns minutos."
        : _updatesChecking
        ? "Perguntando ao Windows Update e ao winget…"
        : UpdatesService.HasWinget
          ? "Nada esperando no Windows Update nem no winget."
          : "Nada esperando no Windows Update. (O winget não está instalado nesta máquina.)";

    /// <summary>
    /// O rodapé que conta quantos pacotes estão calados.
    ///
    /// Aparece mesmo quando não há mais nada esperando, e é de propósito: silêncio que não se vê
    /// vira esquecimento, e a pessoa fica sem saber que a dock está deixando de contar alguma coisa.
    /// </summary>
    public string WingetSilencedText => WingetSilencedCount == 1
        ? "1 pacote silenciado — voltar a avisar"
        : $"{WingetSilencedCount} pacotes silenciados — voltar a avisar";

    /// <summary>
    /// Começa a conferir o que há para atualizar.
    ///
    /// De três em três horas: atualização não chega em rajada, e as duas consultas custam caro — a
    /// do Windows Update vai à rede (uns doze segundos) e a do winget é um processo de console.
    /// </summary>
    private void WatchUpdates()
    {
        if (!_config.PanelUpdates) return;

        _updatesTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(3)
        };
        _updatesTimer.Tick += (_, _) => RefreshUpdates();
        _updatesTimer.Start();

        // dois minutos para a primeira: a busca do Windows Update vai à rede e leva uns doze
        // segundos, e o pior momento para isso é o logon, com o Windows ainda subindo o resto
        var primeira = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
        primeira.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); RefreshUpdates(); };
        primeira.Start();

        WatchWindowsInstalls();
    }

    private System.Diagnostics.Eventing.Reader.EventLogWatcher? _updatesWatcher;
    private DispatcherTimer? _installSettle;

    /// <summary>
    /// Reconfere quando o Windows Update termina de instalar alguma coisa — avisado pelo próprio
    /// Windows, e não adivinhado por relógio.
    ///
    /// O relógio errava dos dois lados. Em 01/10/2026 o ícone mostrou "2" por vinte minutos depois
    /// de o Defender se instalar sozinho, porque a próxima conferência só vinha na volta seguinte; e
    /// as reconferências de 2 e 10 min depois de abrir o Windows Update são um palpite de quanto
    /// a instalação demora. O evento 19 (instalou) e o 20 (falhou) do <c>WindowsUpdateClient</c>,
    /// no log do Sistema, são escritos no instante em que cada instalação acaba — inclusive as que
    /// ninguém pediu. Ler o log do Sistema não exige administrador.
    ///
    /// Os eventos vêm em rajada (quatro pacotes do mesmo app no mesmo segundo), então a conferência
    /// espera cinco segundos de silêncio antes de sair.
    /// </summary>
    private void WatchWindowsInstalls()
    {
        _installSettle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _installSettle.Tick += (_, _) => { _installSettle.Stop(); RefreshUpdates(); };

        try
        {
            var consulta = new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20)]]");

            _updatesWatcher = new System.Diagnostics.Eventing.Reader.EventLogWatcher(consulta);
            _updatesWatcher.EventRecordWritten += (_, e) =>
            {
                e.EventRecord?.Dispose();
                _dispatcher.InvokeAsync(() =>
                {
                    Log.Trace("atualizações: o Windows Update terminou uma instalação; reconferindo");
                    _installSettle.Stop();
                    _installSettle.Start();
                });
            };
            _updatesWatcher.Enabled = true;
        }
        catch (Exception ex)
        {
            // sem o aviso, sobram o relógio e as reconferências agendadas — o ícone só demora mais
            Log.Write("atualizações: não deu para ouvir as instalações do Windows Update", ex);
            _updatesWatcher?.Dispose();
            _updatesWatcher = null;
        }
    }

    /// <summary>
    /// Um pedido de conferência chegou enquanto não dava para atendê-lo — com outra em curso, ou
    /// com o winget instalando.
    /// </summary>
    private bool _updatesAgain;

    /// <summary>
    /// Relê em segundo plano; nada na barra espera pela resposta.
    ///
    /// Duas consultas ao mesmo tempo não acontecem: a do Windows Update leva uns doze segundos, e
    /// clicar duas vezes em "Conferir agora" poria duas buscas na rede para o mesmo resultado.
    ///
    /// Mas o pedido que chega no meio fica guardado, e não descartado — descartá-lo era o defeito
    /// do ícone que não apagava depois de "Atualizar tudo". A conferência em curso tinha lido o
    /// winget **antes** de ele instalar; a que o fim da instalação pedia era recusada por haver uma
    /// em curso; e a lista velha chegava por último e acendia o ícone de novo. Durante a instalação
    /// também não se lê: a lista sairia no meio do caminho, e o fim dela já pede a sua.
    /// </summary>
    public void RefreshUpdates()
    {
        if (!_config.PanelUpdates) return;

        if (_updatesChecking || _updatesUpgrading)
        {
            _updatesAgain = true;
            return;
        }

        _updatesAgain = false;

        UpdatesChecking = true;

        _ = Task.Run(async () =>
        {
            // Primeiro a pergunta rápida, do cache do agente: em ~3 s a tela já mostra algo. Ela é
            // a resposta **certa** no caso que mais incomoda — logo depois de instalar, quando o
            // agente já sabe o que sumiu e o ícone precisa apagar sem esperar doze segundos.
            var calados = _config.UpdatesSilenced.ToList();

            var rapido = await UpdatesService.ReadAsync(online: false, silenced: calados)
                                             .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => { if (!_updatesAgain) Updates = rapido; });

            // Depois a busca de verdade, que é a única capaz de descobrir o que ainda não se sabe.
            // O winget não é perguntado de novo: é um processo de console, e a lista dele não muda
            // nos doze segundos entre as duas fases.
            // a lista inteira, e não só a visível: quem está calado continua sendo contado do outro
            // lado da linha, e some do cartão se a pessoa mandar avisar de novo
            var doWinget = rapido.Winget.Concat(rapido.Silenced).ToList();

            var completo = await UpdatesService.ReadAsync(online: true, doWinget, calados)
                                               .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                UpdatesChecking = false;

                // o que chegou no meio pediu uma leitura mais nova que esta; ela sai agora e
                // esta não é mostrada, para o ícone não piscar um estado que já passou
                if (_updatesAgain && !_updatesUpgrading)
                {
                    RefreshUpdates();
                    return;
                }

                Updates = completo;
                SchedulePace();
            });
        });
    }

    /// <summary>
    /// Ajusta de quanto em quanto tempo se pergunta, conforme haja ou não o que atualizar.
    ///
    /// Três horas é a cadência de quem está em dia — atualização não chega em rajada. Mas "tem
    /// coisa esperando" é um estado que muda pelas mãos da pessoa, e logo depois: ela instala e
    /// quer ver o ícone apagar. Nesse estado a pergunta passa a ser de vinte em vinte minutos, o
    /// que custa uma ida à rede a mais por hora e só enquanto há assunto.
    /// </summary>
    private void SchedulePace()
    {
        if (_updatesTimer is null) return;

        var novo = _updates.Any ? TimeSpan.FromMinutes(20) : TimeSpan.FromHours(3);
        if (_updatesTimer.Interval == novo) return;

        _updatesTimer.Interval = novo;
        Log.Trace($"atualizações: conferindo a cada {novo.TotalMinutes:0} min");
    }

    /// <summary>
    /// A pessoa foi mexer nas atualizações — abriu o Windows Update ou o terminal do winget.
    ///
    /// Esse clique é a melhor pista que a dock tem de que o estado vai mudar, e mudar por fora
    /// dela. Em vez de esperar a próxima volta do relógio, ela reconfere duas vezes: dois minutos
    /// depois (o tempo de uma definição do Defender se instalar) e dez minutos depois (o tempo de
    /// um cumulativo baixar). Sem isto, instalar tudo e ver o ícone continuar aceso é o que
    /// acontece — foi o primeiro incômodo relatado depois que o cartão passou a funcionar.
    /// </summary>
    public void RecheckUpdatesSoon()
    {
        foreach (var minutos in new[] { 2, 10 })
        {
            var t = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(minutos)
            };
            t.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); RefreshUpdates(); };
            t.Start();
        }

        Log.Trace("atualizações: reconferência agendada para daqui a 2 e 10 min");
    }

    private bool _updatesUpgrading;
    /// <summary>
    /// O winget está instalando agora, chamado pela dock.
    /// </summary>
    public bool UpdatesUpgrading
    {
        get => _updatesUpgrading;
        private set
        {
            if (!Set(ref _updatesUpgrading, value)) return;
            OnChanged(nameof(UpdatesGlyph));
            OnChanged(nameof(UpdatesFill));
            OnChanged(nameof(UpdatesIdle));
            OnChanged(nameof(UpdatesTooltip));
            OnChanged(nameof(UpdatesEmptyText));
            OnChanged(nameof(UpdatesReadAt));
        }
    }

    /// <summary>Nada em curso: é o que habilita os botões que mexem na máquina.</summary>
    public bool UpdatesIdle => !_updatesUpgrading;

    private string _upgradeError = string.Empty;
    /// <summary>
    /// O que o winget disse quando a atualização não deu certo.
    ///
    /// Sem terminal, esta linha é **a única** notícia do fracasso — antes ela ficava na janela que
    /// a pessoa fechava. Vem com o botão de abrir o terminal ao lado, que é onde um instalador
    /// teimoso pode ser respondido.
    /// </summary>
    public string UpgradeError
    {
        get => _upgradeError;
        private set { if (Set(ref _upgradeError, value)) OnChanged(nameof(HasUpgradeError)); }
    }

    public bool HasUpgradeError => _upgradeError.Length > 0;

    /// <summary>
    /// Manda o winget atualizar — tudo, ou um pacote só — sem abrir janela, e confere ao terminar.
    ///
    /// O "ao terminar" é o motivo de existir: com o terminal, a dock não tinha como saber que a
    /// atualização acabou, e o ícone ficava aceso até a reconferência de dois minutos. Quem usava
    /// clicava em "Conferir agora" para limpá-lo — ou seja, fazia à mão o que a dock devia saber
    /// sozinha. Rodando aqui dentro, o fim do processo **é** o aviso.
    /// </summary>
    public void UpgradeWinget(string? id = null)
    {
        if (_updatesUpgrading) return;

        UpdatesUpgrading = true;
        UpgradeError = string.Empty;

        _ = Task.Run(async () =>
        {
            var erro = await UpdatesService.UpgradeAsync(id).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                UpdatesUpgrading = false;
                UpgradeError = erro ?? string.Empty;

                // sem erro, o que foi atualizado sai da conta já — o ícone muda junto com o fim do
                // winget, e não segundos depois, quando a releitura volta
                if (erro is null) Updates = _updates.WithoutUpgraded(id);

                // a conferência sai agora, e não daqui a dois minutos: o winget acabou de devolver
                // o controle, e o cache do agente já sabe o que sumiu
                RefreshUpdates();
            });
        });
    }

    // ── nova versão do WinDock ──────────────────────────────

    private DispatcherTimer? _selfUpdateTimer;
    private WinDockRelease? _release;

    /// <summary>A caixa de presente da Segoe: "tem novidade", sem se confundir com a seta ao lado.</summary>
    public string SelfUpdateGlyph => _selfUpdating ? "" : "";

    private string _selfUpdateTag = string.Empty;
    /// <summary>A tag da Release mais nova que esta dock, ou vazio quando ela está em dia.</summary>
    public string SelfUpdateTag
    {
        get => _selfUpdateTag;
        private set
        {
            if (!Set(ref _selfUpdateTag, value)) return;
            OnChanged(nameof(HasSelfUpdate));
            OnChanged(nameof(SelfUpdateTooltip));
        }
    }

    /// <summary>
    /// O ícone só existe enquanto há versão nova — ao contrário do das atualizações do Windows,
    /// que fica sempre. Lá, "nada esperando" é uma resposta que a pessoa quer ver; aqui, um
    /// presente apagado na barra o dia inteiro seria só um ícone a mais.
    /// </summary>
    public bool HasSelfUpdate => _config.PanelSelfUpdate && _selfUpdateTag.Length > 0;

    private bool _selfUpdating;
    public bool SelfUpdating
    {
        get => _selfUpdating;
        private set
        {
            if (!Set(ref _selfUpdating, value)) return;
            OnChanged(nameof(SelfUpdateGlyph));
            OnChanged(nameof(SelfUpdateTooltip));
        }
    }

    private string _selfUpdateProgress = string.Empty;

    public string SelfUpdateTooltip => _selfUpdating
        ? $"Atualizando o WinDock para a {_selfUpdateTag}… {_selfUpdateProgress}".TrimEnd()
        : $"WinDock {_selfUpdateTag} disponível\nVocê está na {SelfUpdateService.CurrentVersion} — clique para atualizar";

    /// <summary>
    /// Começa a vigiar as Releases. A primeira olhada é três minutos depois de abrir — o logon
    /// já é concorrido o bastante —, e depois de hora em hora; mas o GitHub só é perguntado
    /// quando a última consulta tem mais de um dia. Nas outras voltas vale a tag guardada, e é
    /// isso que faz o ícone aparecer logo depois de reiniciar, sem gastar consulta.
    /// </summary>
    private void WatchSelfUpdate()
    {
        _selfUpdateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(3) };
        _selfUpdateTimer.Tick += (_, _) =>
        {
            _selfUpdateTimer.Interval = TimeSpan.FromHours(1);
            CheckSelfUpdate();
        };
        _selfUpdateTimer.Start();
    }

    public void CheckSelfUpdate(bool force = false)
    {
        if (!_config.PanelSelfUpdate && !force) return;

        var atual = SelfUpdateService.CurrentVersion;
        var recente = _config.SelfUpdateCheckedAt is { } quando &&
                      DateTime.UtcNow - quando < SelfUpdateService.CheckInterval;

        if (recente && !force)
        {
            SelfUpdateTag = SelfUpdateService.IsNewer(atual, _config.SelfUpdateTag) ? _config.SelfUpdateTag : string.Empty;
            return;
        }

        _ = Task.Run(async () =>
        {
            var release = await SelfUpdateService.LatestAsync().ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                // sem resposta, a consulta fica para a próxima volta — e a tag guardada continua valendo
                if (release is null) return;

                _release = release;
                _config.SelfUpdateCheckedAt = DateTime.UtcNow;
                _config.SelfUpdateTag = release.Tag;
                _config.Save();

                var nova = SelfUpdateService.IsNewer(atual, release.Tag);
                Log.Trace($"nova versão: GitHub tem {release.Tag}, esta é {atual}{(nova ? " — avisando" : string.Empty)}");
                SelfUpdateTag = nova ? release.Tag : string.Empty;
            });
        });
    }

    /// <summary>
    /// Baixa a Release no mesmo sabor desta dock, troca o executável e reabre. Devolve o motivo
    /// quando não deu certo; quando dá, quem chamou fecha esta dock para a nova assumir.
    ///
    /// Fora de um executável de arquivo único numa pasta gravável não há troca possível, e o
    /// clique abre a página da Release.
    /// </summary>
    public async Task<(bool Swapped, string? Error)> InstallSelfUpdateAsync()
    {
        if (_selfUpdating) return (false, null);

        SelfUpdating = true;
        try
        {
            var release = _release?.Tag == _selfUpdateTag ? _release : await SelfUpdateService.LatestAsync();
            if (release is null) return (false, "o GitHub não respondeu; tente de novo em alguns minutos");

            var arquivo = SelfUpdateService.PickAsset(release);
            if (arquivo is null || !SelfUpdateService.CanSwap(out var exe))
            {
                SelfUpdateService.OpenReleasePage(release.Url);
                return (false, null);
            }

            Log.Write($"nova versão: baixando {arquivo.Name} ({arquivo.Size / 1024d / 1024d:N1} MB)");

            var progresso = new Progress<double>(p =>
            {
                _selfUpdateProgress = p.ToString("P0", CultureInfo.CurrentCulture);
                OnChanged(nameof(SelfUpdateTooltip));
            });

            var baixado = await SelfUpdateService.DownloadAsync(arquivo, System.IO.Path.GetDirectoryName(exe)!, progresso);

            SelfUpdateService.Swap(exe, baixado);
            Log.Write($"nova versão: {SelfUpdateService.CurrentVersion} → {release.Tag}, reabrindo");
            SelfUpdateService.Relaunch(exe);
            return (true, null);
        }
        catch (Exception ex)
        {
            Log.Write("nova versão: a atualização falhou", ex);
            return (false, ex.Message);
        }
        finally
        {
            _selfUpdateProgress = string.Empty;
            SelfUpdating = false;
        }
    }

    // ── cota de IA ──────────────────────────────────────────

    private readonly AiUsageService _ai = new();
    private DispatcherTimer? _aiTimer;

    /// <summary>
    /// O robô da Segoe, como no projeto que deu a ideia. Conferido de perto: continua
    /// legível em 15 px, que é o tamanho dos ícones da barra.
    ///
    /// Escrito pelo código, e não pelo caractere: veja o `TrayGlyph`.
    /// </summary>
    public string AiGlyph => "\uE99A";

    /// <summary>
    /// O ícone só aparece quando a opção está ligada **e** há alguma conta do Claude Code
    /// nesta máquina. Um botão que não tem o que mostrar é pior que botão nenhum: quem não
    /// usa o Claude Code veria um robô que só sabe dizer que não sabe.
    /// </summary>
    public bool HasAiUsage => _config.PanelAiUsage && AiUsageService.Installed;

    private AiUsage _aiUsage = new(Array.Empty<AiAccountUsage>(), DateTime.MinValue);

    public AiUsage AiUsage
    {
        get => _aiUsage;
        private set
        {
            if (!Set(ref _aiUsage, value)) return;
            OnChanged(nameof(AiCards));
            OnChanged(nameof(AiTooltip));
            OnChanged(nameof(HasAiReading));
            OnChanged(nameof(AiBrush));
            OnChanged(nameof(AiPercentText));
            OnChanged(nameof(AiReadAt));
        }
    }

    /// <summary>
    /// O número ao lado do robô, no jeito do processador e da memória: o medidor mais cheio de
    /// todas as contas — o mesmo que decide a cor, para o número e a cor nunca discordarem (um
    /// "12%" vermelho, com a semana em 96%, seria pior que número nenhum). Qual janela é, a dica
    /// do mouse e o cartão dizem. Vazio antes da primeira leitura.
    /// </summary>
    public string AiPercentText
    {
        get
        {
            var medidores = AiCards.SelectMany(c => c.Gauges).Select(g => g.Percent).ToList();
            return medidores.Count == 0 ? string.Empty : $"{medidores.Max():0}%";
        }
    }

    /// <summary>
    /// Uma entrada por conta, na ordem em que vieram — a padrão primeiro.
    ///
    /// Enquanto a primeira leitura não chega, sai uma entrada por conta encontrada, cada uma
    /// dizendo "Consultando…": o cartão nasce com o tamanho e os nomes certos, em vez de
    /// aparecer vazio e pular para o tamanho final quando a rede responde.
    /// </summary>
    public IReadOnlyList<AiCard> AiCards
    {
        get
        {
            var leituras = _aiUsage.Contas;

            if (leituras.Count == 0)
                leituras = Escolhidas()
                    .Select(p => new AiAccountUsage(p, AiUsageState.Unknown, Array.Empty<AiGauge>(), null))
                    .ToList();

            return leituras.Select((c, i) => new AiCard(c, i == 0)).ToList();
        }
    }

    /// <summary>
    /// A cor do robô na barra: verde até 70%, amarelo de 70 a 95%, vermelho a partir de 95%
    /// (pedido em 01/10/2026; antes era branco, âmbar a partir de 60% e vermelho de 90%).
    /// O verde diz "li, e sobra" — o branco ficou só para quando ainda não há leitura.
    ///
    /// Vale <b>o medidor mais cheio de todas as contas</b>, e não o de cinco horas que a dica
    /// do mouse mostra: qualquer janela que estourar interrompe o trabalho do mesmo jeito, e
    /// um ícone branco com a semana em 95% seria um aviso que chega quando não dá mais para
    /// fazer nada com ele.
    ///
    /// Sem leitura nenhuma o robô fica branco — "não sei" não é "está tudo bem", mas pintar
    /// de vermelho o que não se sabe é pior: o cartão, a um clique, é quem diz o que houve.
    /// </summary>
    public Brush AiBrush
    {
        get
        {
            var medidores = AiCards.SelectMany(c => c.Gauges).Select(g => g.Percent).ToList();
            if (medidores.Count == 0) return AiSemLeitura;

            var cheio = medidores.Max();
            return cheio >= 95 ? AiFim : cheio >= 70 ? AiAtencao : AiCalmo;
        }
    }

    /// <summary>
    /// As cores do robô. Amarelo e vermelho são os do <c>UsageBrush</c>, que pinta as barras do
    /// cartão, nas mesmas faixas. O verde é o de sucesso do Windows 11 sobre fundo escuro — e
    /// substitui o branco de antes, que deixava o robô igual aos outros ícones: a pessoa pediu
    /// que ele dissesse "está tranquilo" também, e não só quando aperta.
    /// </summary>
    private static readonly Brush AiSemLeitura = Congelado(0xF2, 0xF2, 0xF2);
    private static readonly Brush AiCalmo = Congelado(0x6C, 0xCB, 0x5F);
    private static readonly Brush AiAtencao = Congelado(0xFF, 0xB9, 0x00);
    private static readonly Brush AiFim = Congelado(0xFF, 0x60, 0x5C);

    private static Brush Congelado(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Já houve uma leitura nesta sessão? É o que decide o "atualizado às" do rodapé: antes
    /// da primeira resposta ele mostraria a hora em que o cartão foi aberto, como se aquele
    /// "Consultando…" acima já tivesse sido respondido.
    /// </summary>
    public bool HasAiReading => _aiUsage.Quando > DateTime.MinValue;

    /// <summary>
    /// Uma leitura está em curso.
    ///
    /// Existe pelo mesmo motivo do "Conferir agora" das atualizações: a consulta vai à rede,
    /// uma vez por conta, e um botão que não responde nesse tempo parece quebrado. Enquanto
    /// dura, o rodapé diz "consultando…" e o botão fica desabilitado — clicar de novo não
    /// adiantaria nada, e a cota é a mesma.
    /// </summary>
    private bool _aiChecking;
    public bool AiChecking
    {
        get => _aiChecking;
        private set
        {
            if (!Set(ref _aiChecking, value)) return;
            OnChanged(nameof(AiReadAt));
            OnChanged(nameof(AiCanRefresh));
        }
    }

    /// <summary>O botão de reler só aceita clique quando não há leitura em curso.</summary>
    public bool AiCanRefresh => !_aiChecking;

    /// <summary>
    /// O que o rodapé do cartão diz à esquerda: "consultando…" enquanto a resposta não vem,
    /// a hora da última leitura depois dela, e nada antes da primeira — que é quando a hora
    /// seria a de abrir o cartão, como se o "Consultando…" de cima já tivesse sido respondido.
    /// </summary>
    public string AiReadAt => _aiChecking
        ? "consultando…"
        : HasAiReading ? $"atualizado às {_aiUsage.Quando:HH:mm}" : string.Empty;

    /// <summary>
    /// O cartão está detalhado ou compacto? Mora na configuração porque é preferência de
    /// leitura: quem escolheu um dos dois não quer reescolher a cada abertura.
    /// </summary>
    public bool AiExpanded => _config.AiUsageExpanded;

    /// <summary>A seta do rodapé aponta para onde o clique leva: para baixo abre, para cima fecha.</summary>
    public string AiExpandGlyph => _config.AiUsageExpanded ? "" : "";

    public string AiExpandTooltip => _config.AiUsageExpanded ? "Mostrar só o essencial" : "Mostrar os detalhes";

    /// <summary>Alterna entre o cartão detalhado e o compacto. Chamado pela seta do rodapé.</summary>
    public void ToggleAiExpanded()
    {
        _config.AiUsageExpanded = !_config.AiUsageExpanded;
        _config.Save();
        Log.Trace($"cota: cartão agora {(_config.AiUsageExpanded ? "detalhado" : "compacto")}");

        CardResized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// O cartão acabou de mudar de tamanho por um clique dentro dele.
    ///
    /// Quem escuta é a barra, e o motivo é específico: ao **recolher**, o cartão encolhe, o
    /// botão sobe junto e o cursor parado fica do lado de fora. O vigia de clique fora corre
    /// logo depois, ainda vê o mesmo clique pendente e, agora que a geometria mudou, conclui
    /// que ele foi fora do cartão — que então se fecha sozinho, 25 ms depois de a pessoa ter
    /// clicado dentro dele (medido em 24/09, 16:19:33.980 → 16:19:34.005). Expandir não tem o
    /// problema: o cartão cresce e o cursor continua dentro.
    /// </summary>
    public event EventHandler? CardResized;

    /// <summary>As contas que a configuração manda mostrar — todas, quando ela não diz nada.</summary>
    private IReadOnlyList<AiProfile> Escolhidas()
    {
        var todas = AiUsageService.Profiles();
        var pedidas = _config.AiUsageAccounts;

        return pedidas.Count == 0
               ? todas
               : todas.Where(p => pedidas.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// A dica do botão traz a janela curta de cada conta — é o que se quer saber de relance,
    /// sem abrir nada. Com uma conta só, o nome dela sai: o cartão já é dela.
    /// </summary>
    public string AiTooltip
    {
        get
        {
            var cartoes = AiCards;
            if (cartoes.Count == 0) return "Cota de IA";

            var linhas = cartoes.Select(c =>
            {
                var curta = c.Gauges.FirstOrDefault();
                var valor = curta is null ? c.Mensagem : $"{curta.Nome}: {curta.Percent:0}%";
                return cartoes.Count == 1 ? valor : $"{c.Titulo} — {valor}";
            });

            return "Cota de IA\n" + string.Join("\n", linhas);
        }
    }

    /// <summary>
    /// Começa a acompanhar a cota, se a opção estiver ligada.
    ///
    /// De dez em dez minutos, e não de segundo em segundo: cada volta é uma ida à rede por
    /// conta, e a cota não muda em saltos que justifiquem mais que isso. Quem quiser o número
    /// na hora abre o cartão — <see cref="RefreshAiUsage"/> é chamado ali também.
    /// </summary>
    private void WatchAiUsage()
    {
        if (!HasAiUsage) return;

        _aiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(10)
        };
        _aiTimer.Tick += (_, _) => RefreshAiUsage();
        _aiTimer.Start();

        // a primeira leitura não espera os dez minutos, mas também não disputa o arranque
        var primeira = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        primeira.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); RefreshAiUsage(); };
        primeira.Start();
    }

    /// <summary>
    /// Relê a cota em segundo plano. Nada espera por ela: o cartão mostra o que já tinha até
    /// a resposta chegar.
    /// </summary>
    public void RefreshAiUsage()
    {
        if (!HasAiUsage) return;

        // a lista de escolhidas é lida aqui, na thread da interface, e não lá dentro: é a
        // configuração, e ela muda por clique de quem está olhando o painel
        var escolhidas = _config.AiUsageAccounts.ToList();

        AiChecking = true;

        _ = Task.Run(async () =>
        {
            var uso = await _ai.ReadAsync(escolhidas).ConfigureAwait(false);

            // o "consultando…" apaga junto com a chegada dos números, e não antes: apagá-lo
            // primeiro deixaria o rodapé mostrando a hora antiga por um quadro, como se a
            // leitura tivesse voltado com o valor de dez minutos atrás
            await _dispatcher.InvokeAsync(() => { AiUsage = uso; AiChecking = false; });
        });
    }

    /// <summary>
    /// Troca pelo apelido o nome dos ícones que a pessoa batizou.
    ///
    /// Fica aqui, e não no <c>TrayService</c>, porque é o modelo que conhece a configuração —
    /// a leitura da bandeja não precisa saber que apelidos existem.
    /// </summary>
    private IReadOnlyList<TrayIcon> Rename(IReadOnlyList<TrayIcon> icons)
    {
        if (_config.TrayNames.Count == 0) return icons;

        var renomeados = 0;
        var saida = icons
            .Select(i =>
            {
                if (string.IsNullOrEmpty(i.Signature) ||
                    !_config.TrayNames.TryGetValue(i.Signature, out var apelido) ||
                    string.IsNullOrWhiteSpace(apelido)) return i;

                renomeados++;

                // no Alias, nunca no Name: o Name é o que identifica o botão na hora do
                // clique (veja TrayIcon.Label)
                return i with { Alias = apelido };
            })
            .ToList();

        Log.Trace($"Rename: {renomeados} de {icons.Count} ícone(s) receberam apelido " +
                  $"({_config.TrayNames.Count} guardado(s))");
        return saida;
    }

    /// <summary>
    /// Relê a bandeja e devolve a lista pronta.
    ///
    /// Roda fora da thread da interface: conferir a bandeja custa uns 90 ms, e uma leitura
    /// completa — só necessária quando o conjunto de ícones mudou — passa de meio segundo.
    /// A barra não pode congelar nesse tempo.
    /// </summary>
    public void RefreshTray(Action? done = null)
    {
        if (!_config.PanelTray) { TrayIcons = Array.Empty<TrayIcon>(); done?.Invoke(); return; }

        var started = TrayReader.Run(() =>
        {
            var icons = Rename(TrayService.Read());
            _dispatcher.InvokeAsync(() => { TrayIcons = icons; done?.Invoke(); });
        });

        // pedido descartado (já havia uma leitura em curso): o aviso tem que sair na mesma,
        // com a lista que já existe. Sem isto, o cartão esperaria por um retorno que nunca
        // vem — que era a bandeja "travando" depois do primeiro clique.
        if (!started) done?.Invoke();
    }

    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        System.Windows.Threading.Dispatcher.CurrentDispatcher;

    // ── energia ─────────────────────────────────────────────

    /// <summary>Desligar, reiniciar, hibernar, bloquear e encerrar sessao.</summary>
    public IReadOnlyList<PowerCommand> PowerCommands => PowerService.All;

    /// <summary>Vem do serviço para o botão da barra e o menu não divergirem de desenho.</summary>
    public string PowerGlyph => PowerService.Power;

    // ── calendario ──────────────────────────────────────────

    /// <summary>O mes desenhado no painel que abre ao clicar na data.</summary>
    /// <summary>O calendário lê e grava as anotações no config — por isso ele o recebe.</summary>
    public MonthCalendar Calendar => _calendar ??= new MonthCalendar(_config);
    private MonthCalendar? _calendar;

    // ── notificações ────────────────────────────────────────

    private readonly NotificationService _notifications = new();
    private DispatcherTimer? _notificationsTimer;
    private bool _notificationsReading;
    private bool _notificationsAgain;

    /// <summary>
    /// A dock consegue ler a Central. Só então o sino abre o cartão com a lista; sem
    /// isso ele continua sendo o atalho para a Central do Windows.
    /// </summary>
    public bool HasNotificationList => _notifications.Available;

    private IReadOnlyList<NotificationItem> _notificationItems = Array.Empty<NotificationItem>();

    /// <summary>O que está na Central agora, da mais nova para a mais velha.</summary>
    public IReadOnlyList<NotificationItem> Notifications
    {
        get => _notificationItems;
        private set
        {
            if (!Set(ref _notificationItems, value)) return;
            OnChanged(nameof(HasNotifications));
            OnChanged(nameof(NotificationCount));
            OnChanged(nameof(NotificationsTooltip));
            OnChanged(nameof(BellBrush));
        }
    }

    private static readonly Brush BellIdle = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));

    /// <summary>
    /// Rosa enquanto houver o que ler. É a única cor que sobrou sem significado na barra: azul já
    /// é atualização, laranja o processador, lilás a memória, e verde/âmbar/vermelho são níveis.
    /// </summary>
    private static readonly Brush BellPending = Frozen(Color.FromRgb(0xF4, 0x72, 0xB6));

    /// <summary>O sino e o número acendem juntos quando a Central tem alguma coisa.</summary>
    public Brush BellBrush => HasNotifications ? BellPending : BellIdle;

    public bool HasNotifications => _notificationItems.Count > 0;

    /// <summary>O número ao lado do sino; vazio com a Central limpa, e "9+" dali para cima.</summary>
    public string NotificationCount => _notificationItems.Count switch
    {
        0 => "",
        > 9 => "9+",
        var n => n.ToString(CultureInfo.InvariantCulture)
    };

    public string NotificationsTooltip => _notificationItems.Count switch
    {
        0 => "Notificações",
        1 => "1 notificação",
        var n => $"{n} notificações"
    };

    /// <summary>
    /// Liga a leitura. Pelo aviso do Windows quando ele aceita a assinatura; senão, de cinco em
    /// cinco segundos — a leitura é local e barata, e mais devagar o número chegaria atrasado à
    /// mensagem que acabou de pular.
    /// </summary>
    private void WatchNotifications()
    {
        _notifications.Start();
        OnChanged(nameof(HasNotificationList));
        if (!_notifications.Available) return;

        if (_notifications.Notifies)
            _notifications.Changed += () => _dispatcher.InvokeAsync(RefreshNotifications);
        else
        {
            _notificationsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _notificationsTimer.Tick += (_, _) => RefreshNotifications();
            _notificationsTimer.Start();
        }

        RefreshNotifications();
    }

    /// <summary>
    /// Relê a Central. Avisos que chegam com uma leitura em curso viram uma leitura só, logo depois
    /// dela — uma rajada de mensagens não empilha leituras.
    /// </summary>
    public async void RefreshNotifications()
    {
        if (_notificationsReading) { _notificationsAgain = true; return; }
        _notificationsReading = true;
        try
        {
            do
            {
                _notificationsAgain = false;
                var lidas = await _notifications.ReadAsync();

                // a mesma lista de antes não troca nada na tela: o relógio pergunta a cada
                // cinco segundos e quase sempre a resposta é a mesma
                if (lidas.Select(n => n.Id).SequenceEqual(_allNotifications.Select(n => n.Id))) continue;

                Remember(lidas);
                _allNotifications = lidas;
                Notifications = Visible(lidas);
                Announce(lidas);
            }
            while (_notificationsAgain);
        }
        finally { _notificationsReading = false; }
    }

    /// <summary>Tudo o que a Central tem, inclusive o das origens que a pessoa tirou do sino.</summary>
    private IReadOnlyList<NotificationItem> _allNotifications = Array.Empty<NotificationItem>();

    /// <summary>As que entram no sino — sem as origens desmarcadas —, já com o desenho de cada uma.</summary>
    private List<NotificationItem> Visible(IEnumerable<NotificationItem> itens) =>
        itens.Where(i => SourceOf(i)?.Show ?? true).Select(Dress).ToList();

    private NotificationSource? SourceOf(NotificationItem item) =>
        _config.NotificationSources.FirstOrDefault(s =>
            string.Equals(s.Key, item.SourceKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Os Ids já vistos: só o que nunca passou por aqui ganha balão.</summary>
    private readonly HashSet<uint> _seenNotifications = new();
    private bool _notificationsPrimed;

    /// <summary>
    /// Uma notificação nova chegou e pede balão — a barra mostra (veja <c>NotificationBalloon</c>).
    /// A primeira leitura não conta: o que já estava na Central quando a dock abriu não é novidade.
    /// </summary>
    public event Action<NotificationItem>? NotificationArrived;

    private void Announce(IReadOnlyList<NotificationItem> lidas)
    {
        var novas = lidas.Where(n => _seenNotifications.Add(n.Id)).ToList();
        if (!_notificationsPrimed) { _notificationsPrimed = true; return; }
        if (!_config.NotificationPopup) return;

        // da mais velha para a mais nova, para a mais nova ficar em cima da pilha
        foreach (var item in novas.OrderBy(n => n.Time))
        {
            var origem = SourceOf(item);
            if (origem is { Show: false } or { Popup: false }) continue;
            NotificationArrived?.Invoke(Dress(item));
        }
    }

    /// <summary>Veste a notificação com o desenho escolhido para a origem dela, se houver um.</summary>
    private NotificationItem Dress(NotificationItem item)
    {
        var origem = SourceOf(item);
        if (origem is null) return item;

        var (glifo, cor) = NotificationService.Look(origem);
        return item with { Glyph = glifo, GlyphFill = cor };
    }

    /// <summary>
    /// Toda origem que aparece na Central entra na lista das Configurações, para ganhar desenho —
    /// ninguém precisa digitar "web.whatsapp.com" à mão.
    /// </summary>
    private void Remember(IEnumerable<NotificationItem> itens)
    {
        var novas = itens
            .Where(i => i.SourceKey.Length > 0)
            .Where(i => !_config.NotificationSources.Any(s => string.Equals(s.Key, i.SourceKey, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(i => i.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NotificationSource { Key = g.Key, Name = g.First().App })
            .ToList();
        if (novas.Count == 0) return;

        _config.NotificationSources.AddRange(novas);
        _config.Save();
        _config.NotifyNotificationSourcesChanged();
    }

    /// <summary>
    /// O clique na notificação: leva a quem a mandou, como o clique no balão do Windows — que a
    /// dock não consegue acionar (o "abrir a conversa certa" é do app). Do navegador, vai à janela
    /// cujo título traz o nome do site (o WhatsApp Web, a aba do Gmail); sem ela, abre o endereço.
    /// De app, a janela dele, ou o app aberto de novo. E sai da Central, como lá.
    /// </summary>
    public void OpenNotification(NotificationItem item)
    {
        var janelas = WindowService.Enumerate();

        if (item.Site.Length > 0)
        {
            var aba = janelas.FirstOrDefault(w => w.Title.Contains(item.App, StringComparison.OrdinalIgnoreCase) &&
                                                  item.App != item.Site);
            if (aba is not null) WindowService.Activate(aba.Handle);
            else OpenUrl("https://" + item.Site);
        }
        else if (item.Aumid.Length > 0)
        {
            var janela = janelas.FirstOrDefault(w => string.Equals(w.Aumid, item.Aumid, StringComparison.OrdinalIgnoreCase));
            if (janela is not null) WindowService.Activate(janela.Handle);
            else WindowService.LaunchApp(item.Aumid);
        }

        DismissNotification(item);
    }

    /// <summary>
    /// O endereço no navegador padrão, pelo explorer — como o <c>WindowService.LaunchApp</c>. A dock
    /// roda como administrador, e abrir o endereço direto subiria o navegador elevado também; o
    /// explorer que já está de pé abre como o usuário.
    /// </summary>
    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{url}\""); }
        catch (Exception ex) { Log.Write($"não deu para abrir {url}", ex); }
    }

    /// <summary>Um desenho mudou nas Configurações: a lista aberta se veste de novo.</summary>
    private void RedressNotifications() => Notifications = Visible(_allNotifications);

    public void DismissNotification(NotificationItem item)
    {
        _notifications.Remove(item.Id);
        _allNotifications = _allNotifications.Where(n => n.Id != item.Id).ToList();
        Notifications = Visible(_allNotifications);
    }

    /// <summary>Limpa o que está no sino; o das origens escondidas fica na Central, que é delas.</summary>
    public void ClearNotifications()
    {
        var ids = _notificationItems.Select(n => n.Id).ToHashSet();
        _notifications.Clear(ids);
        _allNotifications = _allNotifications.Where(n => !ids.Contains(n.Id)).ToList();
        Notifications = Array.Empty<NotificationItem>();
    }

    public void UseAudioDevice(string id)
    {
        VolumeService.SetDefault(id);
        RefreshAudioDevices();
        UpdateVolume();
    }
    // ── aparencia ───────────────────────────────────────────
    public Brush PanelBrush
    {
        get
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(_config.PanelBackground)!; }
            catch { color = Color.FromRgb(0x20, 0x21, 0x24); }

            color.A = (byte)Math.Round(_config.PanelOpacity * 2.55);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>A barra em ilhas — veja <c>DockConfig.PanelIslands</c>.</summary>
    public bool PanelIslands => _config.PanelIslands;

    /// <summary>A folga de cada ilha: metade do vão de cada lado, e as de cima e de baixo inteiras.</summary>
    public Thickness IslandMargin => new(_config.IslandGap / 2.0, _config.IslandMarginTop,
                                         _config.IslandGap / 2.0, _config.IslandMarginBottom);

    /// <summary>Das bordas da tela até as ilhas das pontas, descontado o meio vão que elas já têm.</summary>
    public Thickness IslandEdge => new(Math.Max(0, _config.IslandMarginLeft - _config.IslandGap / 2.0), 0,
                                       Math.Max(0, _config.IslandMarginRight - _config.IslandGap / 2.0), 0);

    /// <summary>Ponta redonda de verdade: metade da altura que sobra para a ilha.</summary>
    public CornerRadius IslandCorner =>
        new(Math.Max(4, (_config.PanelSize - _config.IslandMarginTop - _config.IslandMarginBottom) / 2.0));

    public HorizontalAlignment ClockAlignment =>
        _config.PanelCenterClock ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    /// <summary>Com o relogio no meio, o canto direito fica so com os indicadores.</summary>
    public bool ClockCentered => _config.PanelCenterClock;

    /// <summary>O contrario do de cima: o par de bindings que a barra usa para trocar de lugar.</summary>
    public bool ClockAtRight => !_config.PanelCenterClock;

    private void RaiseAppearance()
    {
        OnChanged(nameof(PanelBrush));
        OnChanged(nameof(PanelIslands));
        OnChanged(nameof(IslandMargin));
        OnChanged(nameof(IslandEdge));
        OnChanged(nameof(IslandCorner));
        OnChanged(nameof(ClockAlignment));
        OnChanged(nameof(ClockCentered));
        OnChanged(nameof(ClockAtRight));
        OnChanged(nameof(ShowAppVolume));
        OnChanged(nameof(ShowTray));
    }

    /// <summary>
    /// A aparencia se refaz a cada mudanca de configuracao — e barato, sao bindings.
    ///
    /// A bandeja nao: rele-la abre o painel do Windows e pisca a barra dele. Ligada ao
    /// evento inteiro, arrastar um slider no painel de configuracoes faria a barra do
    /// Windows piscar dezenas de vezes seguidas. So a opcao da bandeja a relê.
    /// </summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaiseAppearance();

        if (e.PropertyName is nameof(DockConfig.PanelTray)) RefreshTray();

        // ligar ou desligar os controles de mídia muda o item da barra na hora, sem esperar
        // a próxima troca de faixa
        if (e.PropertyName is nameof(DockConfig.PanelMedia)) OnChanged(nameof(ShowMedia));
        if (e.PropertyName is nameof(DockConfig.PanelBrightness)) OnChanged(nameof(HasBrightness));

        // ligar o sistema vale na hora; a primeira medida sai zerada (toda conta aqui é a
        // diferença entre duas leituras) e o segundo tique já traz o valor de verdade
        if (e.PropertyName is nameof(DockConfig.PanelSystem)) OnChanged(nameof(HasSystemInfo));
        if (e.PropertyName is nameof(DockConfig.PanelCompact)) OnChanged(nameof(PanelCompact));
        if (e.PropertyName is nameof(DockConfig.PanelTools)) OnChanged(nameof(HasTools));
        if (e.PropertyName is nameof(DockConfig.NotificationSources)) RedressNotifications();
        if (e.PropertyName is nameof(DockConfig.Tools))
        {
            _tools = null;
            OnChanged(nameof(Tools));
            OnChanged(nameof(HasNoTools));
        }

        // ligada agora: confere já, com a tag guardada se a consulta do dia já foi feita
        if (e.PropertyName is nameof(DockConfig.PanelSelfUpdate))
        {
            OnChanged(nameof(HasSelfUpdate));
            CheckSelfUpdate();
        }

        // ligada agora: a lista é montada na hora, senão o botão só apareceria no próximo
        // pen-drive que entrasse — e o que já está espetado é justamente o caso de quem
        // acabou de ligar a opção
        if (e.PropertyName is nameof(DockConfig.PanelRemovable))
        {
            OnChanged(nameof(HasRemovable));
            if (_config.PanelRemovable) RefreshRemovable();
        }

        // ligada agora: o ícone aparece e o número é buscado na hora, senão o cartão nasceria
        // dizendo "Consultando…" até o primeiro tique de dez minutos
        if (e.PropertyName is nameof(DockConfig.PanelAiUsage))
        {
            OnChanged(nameof(HasAiUsage));
            if (_config.PanelAiUsage) { if (_aiTimer is null) WatchAiUsage(); else RefreshAiUsage(); }
            else _aiTimer?.Stop();
        }

        if (e.PropertyName is nameof(DockConfig.PanelUpdates))
        {
            OnChanged(nameof(HasUpdates));
            if (_config.PanelUpdates) { if (_updatesTimer is null) WatchUpdates(); else RefreshUpdates(); }
            else _updatesTimer?.Stop();
        }

        if (e.PropertyName is nameof(DockConfig.AiUsageExpanded))
        {
            OnChanged(nameof(AiExpanded));
            OnChanged(nameof(AiExpandGlyph));
            OnChanged(nameof(AiExpandTooltip));
        }

        // marcar ou desmarcar uma conta muda o cartão na hora, sem esperar os dez minutos
        if (e.PropertyName is nameof(DockConfig.AiUsageAccounts))
        {
            OnChanged(nameof(AiCards));
            RefreshAiUsage();
        }

        // marcar ou desmarcar um programa reavalia qual sessão a barra segue, na hora
        if (e.PropertyName is nameof(DockConfig.MediaApps)) _media.SetAllowed(_config.MediaApps);

        // prazo novo vale na hora: diminuir de 30 para 2 dias apaga agora o que já passou dos 2
        if (e.PropertyName is nameof(DockConfig.CalendarDoneRetentionDays)) Calendar.PurgeDone(DateTime.Now);
    }

    private void Update()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;

        Time = now.ToString("t", culture);

        // em pt-BR o dia e o mes vem abreviados com ponto ("seg., 31 de ago.") e em
        // minusculas; tirar os pontos e subir so a primeira letra deixa "Seg, 31 de ago"
        // — passar tudo por ToTitleCase capitalizaria ate o "de"
        var date = now.ToString("ddd, dd 'de' MMM", culture).Replace(".", string.Empty);
        Date = date.Length > 0 ? char.ToUpper(date[0], culture) + date[1..] : date;

        UpdateBattery();
        UpdateVolume();
        UpdateStats();

        // o bluetooth muda devagar e a consulta e mais cara que as outras: de cinco em
        // cinco segundos basta. A bandeja fica de fora de proposito — ler a bandeja pisca a
        // barra do Windows, e piscar a cada cinco segundos seria intoleravel
        // O wi-fi vai junto: o sinal muda, mas não a cada segundo, e a leitura é do WinRT.
        if (++_ticks % 5 == 0) { UpdateBluetooth(); _ = RefreshWifi(); }

        // depois da meia-noite o "hoje" do calendario mudou de casa
        if (_today != now.Date) { _today = now.Date; Calendar.GoToToday(); }
    }

    private DateTime _today = DateTime.Today;

    private int _ticks;

    private void UpdateBluetooth()
    {
        BluetoothOn = BluetoothService.IsOn;

        if (!BluetoothOn)
        {
            BluetoothTooltip = "Bluetooth desligado";
            return;
        }

        var devices = BluetoothService.Devices();

        // na dica, a bateria vem junto do nome de quem informa: "AULA-F99Pro 5.0 (93%)"
        var connected = devices.Where(d => d.Connected)
                               .Select(d => d.HasBattery ? $"{d.Name} ({d.BatteryText})" : d.Name)
                               .ToList();

        BluetoothTooltip = connected.Count > 0
            ? "Conectado: " + string.Join(", ", connected)
            : devices.Count > 0
                ? "Pareados: " + string.Join(", ", devices.Take(4).Select(d => d.Name))
                : "Bluetooth ligado";
    }

    private void UpdateBattery()
    {
        if (!GetSystemPowerStatus(out var power)) { HasBattery = false; return; }

        // 128 no BatteryFlag quer dizer "esta maquina nao tem bateria"
        HasBattery = (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent != 255;
        if (!HasBattery) return;

        Charging = power.ACLineStatus == 1;
        BatteryPercent = power.BatteryLifePercent;
        OnChanged(nameof(BatteryTooltip));
    }

    /// <summary>
    /// Le o volume de fora sem reescrever no sistema: o setter das propriedades manda para
    /// o Core Audio, e usa-lo aqui faria a barra empurrar de volta o valor que acabou de ler.
    /// </summary>
    private void UpdateVolume()
    {
        var level = VolumeService.Level;
        var muted = VolumeService.Muted;

        if (level != _volume)
        {
            _volume = level;
            OnChanged(nameof(Volume));
            OnChanged(nameof(VolumeGlyph));
        }

        if (muted != _muted)
        {
            _muted = muted;
            OnChanged(nameof(Muted));
            OnChanged(nameof(VolumeGlyph));
        }
    }

    /// <summary>Painel de wi-fi, bluetooth, volume e brilho (o mesmo do Win+A).</summary>
    public const string QuickSettings = "ms-availablenetworks:";

    /// <summary>Notificacoes e calendario (o mesmo do Win+N).</summary>
    public const string ActionCenter = "ms-actioncenter:";

    /// <summary>
    /// Abre um painel do proprio Windows pelo endereco dele. E o jeito honesto de dar
    /// acesso a wi-fi, bluetooth e notificacoes com a barra escondida: quem desenha
    /// continua sendo o Windows, entao nada aqui precisa imitar o que ele faz.
    ///
    /// Mandar as teclas (Win+A, Win+N) parecia mais direto e nao funciona: clicar nesta
    /// barra deixa o foco na barra de tarefas escondida, e nesse estado o atalho so foca
    /// a barra em vez de abrir o painel. O endereco nao depende de foco nenhum.
    /// </summary>
    /// <summary>
    /// Um painel do shell esta na frente agora? Como a barra nao rouba o foco, clicar nela
    /// com o painel aberto deixaria o painel aberto: e isto que diz a hora de fechar em vez
    /// de abrir de novo.
    /// </summary>
    public static bool ShellPanelInFront()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;

        var name = new System.Text.StringBuilder(160);
        GetClassName(window, name, name.Capacity);
        var className = name.ToString();

        if (className == "ControlCenterWindow") return true;               // configuracoes rapidas
        if (className != "Windows.UI.Core.CoreWindow") return false;

        // CoreWindow e a classe de qualquer app da Store; so vale se for o shell
        GetWindowThreadProcessId(window, out var pid);
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName
                   is "ShellExperienceHost" or "ShellHost";
        }
        catch { return false; }
    }

    /// <summary>Fecha o painel do shell que estiver aberto, como o Esc do teclado faria.</summary>
    public static void CloseShellPanel()
    {
        // pelo SyntheticKeys: a barra escuta o Esc para fechar os cartões dela, e precisa saber
        // que este veio de nós
        SyntheticKeys.SendEscape();
    }

    public static void OpenSystemPanel(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch { /* recurso do shell indisponivel nesta versao do Windows */ }
    }

    public void Dispose()
    {
        _timer.Stop();
        _aiTimer?.Stop();
        _notificationsTimer?.Stop();
        _selfUpdateTimer?.Stop();
        _secondsTimer?.Stop();
        _updatesTimer?.Stop();
        _updatesWatcher?.Dispose();

        // as sessoes de audio seguram COM: soltar aqui evita deixar o servico de audio
        // com referencias de uma barra que ja nao existe
        foreach (var session in _appSessions) session.Dispose();
        _appSessions = Array.Empty<VolumeService.AppSession>();

        // solta os eventos da sessão de mídia: sem isto o WinRT segue chamando de volta uma
        // barra que já não existe
        _media.Dispose();

        // e o handle do monitor físico, que o DDC/CI mantém aberto
        _brightness.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name!);
        return true;
    }
}
