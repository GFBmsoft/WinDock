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
        if (_config.PanelTray) { TrayService.ClearLeftovers(); TrayReader.Start(); WarmTray(); }
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
    /// </summary>
    private void WarmTray()
    {
        var warm = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(6)
        };

        warm.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); RefreshTray(); };
        warm.Start();
    }

    // ── relogio ─────────────────────────────────────────────
    private string _time = string.Empty;
    public string Time { get => _time; private set => Set(ref _time, value); }

    private string _date = string.Empty;
    /// <summary>Data por extenso, abreviada: "Seg, 31 de ago".</summary>
    public string Date { get => _date; private set => Set(ref _date, value); }

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

    public string WifiGlyph => "";
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
        private set { if (Set(ref _bluetoothDevices, value)) OnChanged(nameof(HasBluetoothDevices)); }
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
        private set => Set(ref _audioDevices, value);
    }

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
            var icons = TrayService.Read();
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
    public MonthCalendar Calendar { get; } = new();

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

    public HorizontalAlignment ClockAlignment =>
        _config.PanelCenterClock ? HorizontalAlignment.Center : HorizontalAlignment.Right;

    /// <summary>Com o relogio no meio, o canto direito fica so com os indicadores.</summary>
    public bool ClockCentered => _config.PanelCenterClock;

    /// <summary>O contrario do de cima: o par de bindings que a barra usa para trocar de lugar.</summary>
    public bool ClockAtRight => !_config.PanelCenterClock;

    private void RaiseAppearance()
    {
        OnChanged(nameof(PanelBrush));
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

        // o bluetooth muda devagar e a consulta e mais cara que as outras: de cinco em
        // cinco segundos basta. A bandeja fica de fora de proposito — ler a bandeja pisca a
        // barra do Windows, e piscar a cada cinco segundos seria intoleravel
        if (++_ticks % 5 == 0) UpdateBluetooth();

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
        keybd_event(VK_ESCAPE, 0, 0, 0);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
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

        // as sessoes de audio seguram COM: soltar aqui evita deixar o servico de audio
        // com referencias de uma barra que ja nao existe
        foreach (var session in _appSessions) session.Dispose();
        _appSessions = Array.Empty<VolumeService.AppSession>();
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
