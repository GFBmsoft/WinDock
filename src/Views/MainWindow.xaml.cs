using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinDock.Interop;
using WinDock.Models;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

public partial class MainWindow : Window
{
    private readonly DockConfig _config = DockConfig.Load();
    private readonly TaskbarService _taskbar = new();
    private readonly FullScreenWatcher _fullScreen = new();
    private AppBar? _appBar;
    private DockModel? _model;
    private SettingsWindow? _settings;
    private LauncherWindow? _launcher;
    private PanelWindow? _panel;
    private TilingService? _tiling;

    /// <summary>Id do Alt+Espaco no RegisterHotKey; so precisa ser unico dentro da janela.</summary>
    private const int LauncherHotkeyId = 1;

    // ── ids dos atalhos do mosaico ───────────────────────────
    private const int TilingFocusLeftId = 2;
    private const int TilingFocusRightId = 3;
    private const int TilingFocusUpId = 4;
    private const int TilingFocusDownId = 5;
    private const int TilingFloatId = 6;
    private const int TilingHideId = 7;
    private const int TilingSwapLeftId = 8;
    private const int TilingSwapRightId = 9;
    private const int TilingSwapUpId = 10;
    private const int TilingSwapDownId = 11;
    private const int TilingCloseId = 12;
    private const int TilingResizeLeftId = 13;
    private const int TilingResizeRightId = 14;
    private const int TilingResizeUpId = 15;
    private const int TilingResizeDownId = 16;

    /// <summary>Ctrl+Alt+C: esquece o tamanho flutuante guardado do app em foco. Fica fora da
    /// faixa 17..25, que é dos Alt+número.</summary>
    private const int TilingForgetSizeId = 26;

    /// <summary>Alt+T: prende a janela em foco acima das outras.</summary>
    private const int TopmostId = 27;

    /// <summary>
    /// Id do Alt+1; os outros oito vem somando (NumberHotkeyId + 1 e o Alt+2). Deixar por
    /// ultimo mantem a faixa 17..25 livre de choque com os ids fixos acima.
    /// </summary>
    private const int NumberHotkeyId = 17;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        _previewDelay.Tick += (_, _) => ShowPreview();
        _previewClose.Tick += (_, _) => ClosePreview();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hWnd = new WindowInteropHelper(this).Handle;

        // fora do Alt+Tab: a dock nao e uma "janela de app".
        // NOACTIVATE e o que faz o clique num icone nao roubar o foco: sem ele a dock
        // passa a ser a janela em primeiro plano, o app clicado nunca "ja esta em foco"
        // e por isso o clique nunca chegava a minimizar.
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        Log.Trace("janela da dock criada; montando o modelo");
        _model = new DockModel(_config, Dispatcher);
        DataContext = _model;

        WhenShellIsUp(() =>
        {
            _appBar = new AppBar(this);
            _appBar.Register(_config.Edge, _config.Size, _config.ReserveSpace);
            _fullScreen.DockBounds = _appBar.Bounds;

            _taskbar.Apply(_config.Taskbar);
        });

        // o Alt+Espaco chega como mensagem nesta janela, entao o hook fica aqui
        HwndSource.FromHwnd(hWnd)?.AddHook(OnWindowMessage);
        SetLauncherHotkey(_config.Launcher);
        SetNumberHotkeys(_config.NumberHotkeys);

        // adianta a lista de apps e os icones: a primeira busca ja acha tudo pronto
        if (_config.Launcher) AppCatalog.Warm();

        // troca a chave Run pela tarefa de logon para quem ja tinha o arranque ligado;
        // fora da thread da interface, que aqui ainda esta montando a barra
        System.Threading.Tasks.Task.Run(StartupService.Migrate);

        SetPanel(_config.Panel);

        _tiling = new TilingService(_config, Dispatcher);
        ApplyHotkeys();

        // app em tela cheia (uma sessao remota, por exemplo): a dock sai da frente e volta
        // sozinha quando a janela deixa de ocupar a tela inteira
        _fullScreen.Changed += OnFullScreenChanged;
        _fullScreen.Start();

        _config.PropertyChanged += OnConfigChanged;

        // "WinDock.exe --settings" ja abre o painel: serve para um atalho no menu iniciar
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Trace("--settings na linha de comando: abrindo as configurações");

            // O InvokeAsync guarda a exceção do delegate na operação que devolve, em vez de mandá-la
            // para o DispatcherUnhandledException que o App registra. Sem observar a Task, uma
            // falha aqui não deixava rastro nenhum — nem janela, nem linha no log —, e foi assim
            // que o --settings passou a não abrir nada sem que houvesse como saber por quê.
            Dispatcher.InvokeAsync(OpenSettings).Task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Write("falha ao abrir as configurações pelo --settings", t.Exception!.GetBaseException());
                else
                    Log.Trace("--settings: OpenSettings terminou sem exceção");
            });
        }
    }

    /// <summary>
    /// Faz o trabalho que depende do Explorer assim que ele estiver de pé — agora, se já
    /// estiver, ou na primeira conferência em que ele aparecer.
    ///
    /// Registrar a AppBar e esconder a barra do Windows são conversas **com o shell**: sem ele,
    /// a faixa não é reservada e a barra não se deixa esconder. A defesa até aqui era a tarefa de
    /// logon esperar quatro segundos antes de abrir a dock — e é isso que se vê no logon como "a
    /// barra do Windows aparece e só depois a dock". O tempo fixo erra dos dois lados: é demais
    /// quando a máquina está rápida, e pode ser de menos num logon frio, quando o Explorer
    /// demora mais que isso e a AppBar falha em silêncio.
    ///
    /// Perguntar pelo <c>Shell_TrayWnd</c> acerta nos dois casos: a dock pode subir junto com o
    /// logon e faz sua parte no instante em que há com quem falar.
    ///
    /// O teto de um minuto existe para não deixar uma conferência rodando para sempre numa
    /// máquina sem shell (uma sessão de serviço, um Explorer que morreu e não voltou): passado
    /// isso, tenta assim mesmo — pior que tentar e falhar é nunca tentar.
    /// </summary>
    private void WhenShellIsUp(Action work)
    {
        if (ShellIsUp()) { work(); return; }

        Log.Write("o Explorer ainda não está de pé; a dock espera para reservar a faixa");

        var espera = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        var desistirEm = DateTime.UtcNow.AddMinutes(1);

        espera.Tick += (_, _) =>
        {
            if (!ShellIsUp() && DateTime.UtcNow < desistirEm) return;

            espera.Stop();
            Log.Write(ShellIsUp() ? "Explorer de pé: reservando a faixa" : "o Explorer não apareceu; tentando assim mesmo");
            work();
        };

        espera.Start();
    }

    /// <summary>A barra de tarefas do Windows existe — o sinal de que o shell terminou de subir.</summary>
    private static bool ShellIsUp() => FindWindow("Shell_TrayWnd", null) != 0;

    /// <summary>
    /// O tema cuida sozinho do visual (bindings); aqui so o que mexe na faixa
    /// reservada precisa voltar ao shell.
    /// </summary>
    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DockConfig.Edge):
            case nameof(DockConfig.Size):
            case nameof(DockConfig.ReserveSpace):
                _appBar?.Resize(_config.Edge, _config.Size, _config.ReserveSpace);
                if (_appBar is not null) _fullScreen.DockBounds = _appBar.Bounds;
                _taskbar.Refresh();   // o shell recalculou a area de trabalho: reaplica
                break;
            case nameof(DockConfig.ShowRunning):
                _model?.Refresh();
                break;
            case nameof(DockConfig.Taskbar):
                _taskbar.Apply(_config.Taskbar);
                SetPanel(_config.Panel);   // "recolher" desliga a barra de cima
                _panel?.Resize();          // escondida, ela sobe para o topo de verdade
                break;
            case nameof(DockConfig.Launcher):
                SetLauncherHotkey(_config.Launcher);
                break;
            case nameof(DockConfig.NumberHotkeys):
                SetNumberHotkeys(_config.NumberHotkeys);
                break;
            case nameof(DockConfig.Panel):
                SetPanel(_config.Panel);
                break;
            case nameof(DockConfig.PanelSize):
                _panel?.Resize();
                _taskbar.TopReserve = _config.Panel ? _config.PanelSize : 0;
                _taskbar.Refresh();
                break;
            case nameof(DockConfig.TilingEnabled):
            case nameof(DockConfig.Hotkeys):
                ApplyHotkeys();
                break;
        }
    }

    /// <summary>
    /// Some com a dock e a barra enquanto algo ocupa a tela inteira. Esconder a janela
    /// basta: a faixa continua reservada (a AppBar segue registrada), e quem esta em tela
    /// cheia ignora a area de trabalho de qualquer jeito.
    /// </summary>
    private void OnFullScreenChanged(bool fullScreen)
    {
        Dispatcher.Invoke(() =>
        {
            if (fullScreen)
            {
                Hide();
                _panel?.Hide();
                _launcher?.Hide();
            }
            else
            {
                Show();
                _panel?.Show();
            }
        });
    }

    /// <summary>
    /// A barra de cima e uma janela a parte, criada e destruida com a opcao: enquanto
    /// desligada ela nao existe, e nao fica uma AppBar registrada segurando pixels.
    /// </summary>
    private void SetPanel(bool on)
    {
        // com a barra do Windows so recolhida, ela reaparece por cima ao encostar o mouse
        // no topo — as duas brigariam pela mesma faixa, entao a nossa fica de fora
        on = on && _config.Taskbar != TaskbarMode.AutoHide;

        if (on == (_panel is not null)) return;

        if (!on)
        {
            _panel?.Close();
            _panel = null;
            _taskbar.TopReserve = 0;
            _taskbar.Refresh();
            return;
        }

        _panel = new PanelWindow(_config);
        _panel.Show();

        _taskbar.TopReserve = _config.PanelSize;
        _taskbar.Refresh();   // o shell recalculou a area de trabalho ao registrar a AppBar
    }

    // ── launcher (Alt+Espaco) ───────────────────────────────

    private void SetLauncherHotkey(bool on)
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == 0) return;

        UnregisterHotKey(hWnd, LauncherHotkeyId);
        if (!on) return;

        // MOD_NOREPEAT: segurar as teclas abre uma vez, nao uma por repeticao do teclado
        if (!RegisterHotKey(hWnd, LauncherHotkeyId, MOD_ALT | MOD_NOREPEAT, VK_SPACE))
            ConfirmWindow.Tell(IsLoaded ? this : null,
                               "Alt+Espaço já está em uso",
                               "Outro programa registrou esse atalho antes, e o Windows só o entrega " +
                               "a um de cada vez. A busca continua disponível pelo menu de contexto " +
                               "da dock.");
    }

    // ── Alt+numero aciona o botao daquela posicao ────────────

    /// <summary>
    /// Liga ou desliga o Alt+1..Alt+9. Nove atalhos de uma vez, todos com o mesmo destino
    /// (<see cref="ActivateByPosition"/>), por isso ficam num laco em vez de uma linha cada.
    ///
    /// Sem aviso quando um deles falha: Alt+numero e atalho interno comum, e se outro
    /// programa global tiver pegado o Alt+4 (por exemplo), o certo e a dock so nao reagir
    /// aquele numero — nao encher a tela de caixas no logon.
    /// </summary>
    private void SetNumberHotkeys(bool on)
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == 0) return;

        for (var i = 0; i < 9; i++) UnregisterHotKey(hWnd, NumberHotkeyId + i);
        if (!on) return;

        for (var i = 0; i < 9; i++)
            if (!RegisterHotKey(hWnd, NumberHotkeyId + i, MOD_ALT | MOD_NOREPEAT, VK_1 + (uint)i))
                Log.Write($"Alt+{i + 1} já está registrado por outro programa: a dock não reage a ele");
    }

    /// <summary>
    /// Aciona o botao da posicao pedida (1 = o primeiro da dock), do mesmo jeito que um
    /// clique nele: abre se estiver fechado, alterna as janelas se ja estiver aberto.
    ///
    /// A conta e sobre <see cref="DockModel.Items"/>, que e a mesma lista que a barra
    /// desenha, na mesma ordem — fixados e depois abertos. Posicao sem botao nao faz nada.
    /// </summary>
    private void ActivateByPosition(int position)
    {
        var items = _model?.Items;
        if (items is null || position < 1 || position > items.Count)
        {
            Log.Trace($"Alt+{position}: a dock não tem botão nessa posição");
            return;
        }

        var item = items[position - 1];
        Log.Trace($"Alt+{position}: acionando '{item.Label}'");
        _model!.Activate(item);
    }

    private nint OnWindowMessage(nint hWnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != (int)WM_HOTKEY) return 0;

        var id = (int)wParam;
        if (id >= NumberHotkeyId && id < NumberHotkeyId + 9)
        {
            ActivateByPosition(id - NumberHotkeyId + 1);
            handled = true;
            return 0;
        }

        switch (id)
        {
            case LauncherHotkeyId: OpenLauncher(); break;
            case TilingFocusLeftId:  _tiling?.MoveFocus(TilingDirection.Left); break;
            case TilingFocusRightId: _tiling?.MoveFocus(TilingDirection.Right); break;
            case TilingFocusUpId:    _tiling?.MoveFocus(TilingDirection.Up); break;
            case TilingFocusDownId:  _tiling?.MoveFocus(TilingDirection.Down); break;
            case TilingFloatId:      _tiling?.ToggleFloat(); break;
            case TilingForgetSizeId: _tiling?.ForgetFloatingSize(); break;
            case TopmostId:          WindowService.ToggleTopmost(GetForegroundWindow()); break;
            case TilingHideId:       _tiling?.HideFocused(); break;
            case TilingCloseId:      _tiling?.CloseFocused(); break;
            case TilingSwapLeftId:   _tiling?.SwapFocused(TilingDirection.Left); break;
            case TilingSwapRightId:  _tiling?.SwapFocused(TilingDirection.Right); break;
            case TilingSwapUpId:     _tiling?.SwapFocused(TilingDirection.Up); break;
            case TilingSwapDownId:   _tiling?.SwapFocused(TilingDirection.Down); break;
            case TilingResizeLeftId:  _tiling?.Resize(TilingDirection.Left); break;
            case TilingResizeRightId: _tiling?.Resize(TilingDirection.Right); break;
            case TilingResizeUpId:    _tiling?.Resize(TilingDirection.Up); break;
            case TilingResizeDownId:  _tiling?.Resize(TilingDirection.Down); break;
            default: return 0;
        }

        handled = true;
        return 0;
    }

    // ── atalhos configuráveis (mosaico e prender acima) ──────

    /// <summary>
    /// Os ids de cada ação no <c>RegisterHotKey</c>. As de setas ocupam quatro, na ordem de
    /// <see cref="ArrowKeys"/>. O <see cref="OnWindowMessage"/> continua decidindo pelo id, então
    /// trocar a tecla de uma ação não mexe em quem responde a ela.
    /// </summary>
    private static readonly Dictionary<HotkeyAction, int[]> HotkeyIds = new()
    {
        [HotkeyAction.Focus]      = [TilingFocusLeftId, TilingFocusRightId, TilingFocusUpId, TilingFocusDownId],
        [HotkeyAction.Swap]       = [TilingSwapLeftId, TilingSwapRightId, TilingSwapUpId, TilingSwapDownId],
        [HotkeyAction.Resize]     = [TilingResizeLeftId, TilingResizeRightId, TilingResizeUpId, TilingResizeDownId],
        [HotkeyAction.Float]      = [TilingFloatId],
        [HotkeyAction.ForgetSize] = [TilingForgetSizeId],
        [HotkeyAction.Hide]       = [TilingHideId],
        [HotkeyAction.Close]      = [TilingCloseId],
        [HotkeyAction.Topmost]    = [TopmostId],
    };

    private static readonly uint[] ArrowKeys = [VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN];

    /// <summary>
    /// Registra os atalhos do jeito que estão no config agora — na subida, ao ligar ou desligar o
    /// mosaico e a cada troca no painel.
    ///
    /// Um hotkey global captura a tecla para o sistema inteiro, e é por isso que a escolha é entre
    /// uma lista fechada (<see cref="HotkeyCatalog"/>): Shift+seta sozinho quebraria a seleção de
    /// texto em todo programa, e as combinações com Win são do Explorer — registrar falharia sem
    /// ninguém ver. Os do mosaico só existem enquanto <see cref="DockConfig.TilingEnabled"/> está
    /// ligado; o de prender acima vale sempre.
    ///
    /// Tudo é desregistrado antes de registrar de novo: trocar duas ações de combinação entre si
    /// esbarraria na própria dock ainda segurando a tecla da outra. Um grupo de setas que não
    /// consegue as quatro fica sem nenhuma — metade das direções respondendo parece defeito.
    ///
    /// Tecla tomada por outro programa não abre caixa na tela (no logon seria uma por tecla): fica
    /// no log e aparece na linha da ação, no painel.
    /// </summary>
    private void ApplyHotkeys()
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == 0) return;

        foreach (var id in HotkeyIds.Values.SelectMany(ids => ids)) UnregisterHotKey(hWnd, id);

        var repetidas = HotkeyCatalog.Duplicates(_config.Hotkeys);
        var estados = new Dictionary<HotkeyAction, HotkeyState>();

        foreach (var info in HotkeyCatalog.All)
        {
            var combo = HotkeyCatalog.Of(_config.Hotkeys, info.Action);
            var nome = HotkeyCatalog.Display(combo, info.Arrows);

            if (info.Tiling && !_config.TilingEnabled) { estados[info.Action] = HotkeyState.TilingOff; continue; }
            if (!HotkeyCatalog.TryParse(combo, info.Arrows, out var mods, out var tecla)) { estados[info.Action] = HotkeyState.Off; continue; }

            if (repetidas.Contains(info.Action))
            {
                Log.Write($"'{info.Title}' repete {nome} de outra ação: ficou sem atalho");
                estados[info.Action] = HotkeyState.Duplicate;
                continue;
            }

            var ids = HotkeyIds[info.Action];
            var ok = true;
            for (var i = 0; i < ids.Length && ok; i++)
                ok = RegisterHotKey(hWnd, ids[i], mods | MOD_NOREPEAT, info.Arrows ? ArrowKeys[i] : tecla);

            if (!ok)
            {
                foreach (var id in ids) UnregisterHotKey(hWnd, id);
                Log.Write($"{nome} já está em uso por outro programa: '{info.Title}' ficou sem atalho");
            }

            estados[info.Action] = ok ? HotkeyState.Active : HotkeyState.InUse;
        }

        HotkeyCatalog.Publish(estados);
        Log.Trace("atalhos: " + string.Join(" | ", HotkeyCatalog.All.Select(i =>
            $"{i.Title} = {HotkeyCatalog.Display(HotkeyCatalog.Of(_config.Hotkeys, i.Action), i.Arrows)} ({estados[i.Action]})")));
    }

    /// <summary>Abre a busca; a janela e criada uma vez e depois so escondida e mostrada.</summary>
    private void OpenLauncher()
    {
        if (_model is null) return;

        _launcher ??= new LauncherWindow(new Launcher(_model), _config);

        // ja aberto: o mesmo atalho fecha, como o menu iniciar
        if (_launcher.IsVisible) { _launcher.Hide(); return; }

        _launcher.Open();
    }

    private static DockItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as DockItem;

    // ── preview das janelas ─────────────────────────────────

    private ThumbnailWindow? _thumbnails;

    /// <summary>Botão sobre o qual o preview está (ou vai) aparecer.</summary>
    private FrameworkElement? _previewOf;

    /// <summary>
    /// Espera antes de o preview aparecer.
    ///
    /// Sem ela, atravessar a dock com o mouse abriria e fecharia um painel por ícone no
    /// caminho. É a mesma pausa que a barra de tarefas faz.
    /// </summary>
    private readonly DispatcherTimer _previewDelay = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };

    /// <summary>
    /// Espera antes de o preview sumir: o mouse precisa de tempo para sair do botão e
    /// chegar no painel, e no meio do caminho ele não está em nenhum dos dois.
    /// </summary>
    private readonly DispatcherTimer _previewClose = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };

    /// <summary>
    /// O preview só faz sentido com mais de uma janela.
    ///
    /// Com uma só, o clique já faz a coisa certa e o painel seria um passo a mais para o
    /// mesmo resultado. E os dois perfis do Chrome não caem aqui: eles são dois botões
    /// separados, de uma janela cada — o preview é para o botão que agrupa várias.
    /// </summary>
    private const int MinimumWindows = 2;

    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        _previewClose.Stop();

        var item = ItemOf(sender);
        if (item is null || item.WindowCount < MinimumWindows) { _previewDelay.Stop(); ClosePreview(); return; }

        // A dica de mouse é desligada já aqui, e não quando o preview aparece: as duas têm
        // mais ou menos o mesmo atraso, e desligar depois chegava tarde — a dica já estava
        // aberta e ficava por cima do painel, repetindo o que ele mostra.
        if (sender is FrameworkElement button) ToolTipService.SetIsEnabled(button, false);

        // já mostrando este mesmo botão: nada a fazer
        if (ReferenceEquals(_previewOf, sender) && _thumbnails is { IsVisible: true }) return;

        _previewOf = sender as FrameworkElement;
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private void OnItemMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement button) ToolTipService.SetIsEnabled(button, true);

        _previewDelay.Stop();
        _previewClose.Stop();
        _previewClose.Start();
    }

    private void ShowPreview()
    {
        _previewDelay.Stop();

        if (_previewOf is null || ItemOf(_previewOf) is not { } item ||
            item.WindowCount < MinimumWindows) return;

        _thumbnails ??= new ThumbnailWindow();
        _thumbnails.ShowFor(item.Windows, ButtonBounds(_previewOf), above: _config.Edge == DockEdge.Bottom);
    }

    /// <summary>Faixa do botão na tela, em pixels físicos — é a âncora do preview.</summary>
    private RECT ButtonBounds(FrameworkElement button)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = button.PointToScreen(new Point(0, 0));

        return new RECT
        {
            Left   = (int)Math.Round(origin.X),
            Top    = (int)Math.Round(origin.Y),
            Right  = (int)Math.Round(origin.X + button.ActualWidth * dpi.DpiScaleX),
            Bottom = (int)Math.Round(origin.Y + button.ActualHeight * dpi.DpiScaleY)
        };
    }

    /// <summary>
    /// Fecha o preview, a menos que o mouse esteja dentro dele — quem foi até lá está
    /// escolhendo uma janela, e o painel sumir debaixo do cursor seria o pior momento.
    /// </summary>
    private void ClosePreview()
    {
        _previewClose.Stop();

        if (_thumbnails is { IsVisible: true } && _thumbnails.IsMouseOver)
        {
            _previewClose.Start();
            return;
        }

        _thumbnails?.HideThumbnails();

        if (_previewOf is not null) ToolTipService.SetIsEnabled(_previewOf, true);
        _previewOf = null;
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        // um arrastar que termina em cima do proprio botao nao deve ativar o app
        if (_justDragged) { Log.Trace("clique na dock ignorado: veio no fim de um arrasto"); return; }

        var item = ItemOf(sender);
        if (item is null)
        {
            // o botao existe na tela mas nao tem mais item por tras: a lista foi refeita
            // entre o apertar e o soltar. Era comum quando a dock se atualizava dezenas de
            // vezes por segundo (veja o `DockModel.Concerns`); se voltar a aparecer, o
            // problema e esse, e nao o app de destino
            Log.Write("clique na dock caiu num botão sem item — a lista foi refeita no meio do clique");
            return;
        }

        Log.Trace($"clique na dock em '{item.Label}': {item.WindowCount} janela(s), alvo={item.LaunchPath}");
        _model?.Activate(item);
    }

    // ── arrastar para reordenar ─────────────────────────────
    private Point _dragOrigin;
    private DockItem? _dragCandidate;
    private bool _justDragged;

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(this);
        _dragCandidate = ItemOf(sender);

        // zera aqui, e nao no clique: um arraste que termina fora da dock nao gera clique
        // nenhum, e a marca ficava para tras engolindo o proximo clique de verdade
        _justDragged = false;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        // so vira arrasto depois do limiar do sistema; abaixo disso ainda e um clique
        var delta = _dragOrigin - e.GetPosition(this);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragCandidate;
        _dragCandidate = null;
        _justDragged = true;

        // o botao de origem fica apagado enquanto o gesto dura, para ficar claro qual
        // icone esta na mao
        item.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
        }
        finally
        {
            item.IsDragging = false;
            _model?.SaveOrder();   // a ordem mudou durante o arraste; grava uma vez so
        }
    }

    /// <summary>
    /// A troca acontece aqui, e nao ao soltar: os icones se afastam enquanto o cursor
    /// passa por eles, como na barra de tarefas. Sem isso o arraste parece travado — nada
    /// se mexia ate largar o botao.
    /// </summary>
    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DockItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        if (_model is null || e.Data.GetData(typeof(DockItem)) is not DockItem dragged) return;
        if (ItemOf(sender) is not { } target || ReferenceEquals(dragged, target)) return;
        if (sender is not FrameworkElement button) return;

        var from = _model.Items.IndexOf(dragged);
        var to = _model.Items.IndexOf(target);
        if (from < 0 || to < 0) return;

        // so troca depois que o cursor passa do meio do icone-alvo: parando bem na
        // divisa, os dois ficariam trocando de lugar sem parar
        var position = e.GetPosition(button);
        var vertical = _config.Edge is Interop.DockEdge.Left or Interop.DockEdge.Right;
        var pastMiddle = vertical
            ? position.Y > button.ActualHeight / 2
            : position.X > button.ActualWidth / 2;

        if (to > from != pastMiddle) return;

        Ui.Reorder.Animate(ItemsHost, () => _model.Move(dragged, target, save: false));
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        // a posicao ja foi ajustada no arraste; aqui so falta aceitar o gesto
        e.Handled = true;
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _model is null) return;

        var menu = new ContextMenu();

        if (item.Windows.Count > 1)
        {
            foreach (var w in item.Windows)
            {
                var handle = w.Handle;
                var mi = new MenuItem { Header = Trim(w.Title) };
                mi.Click += (_, _) => WindowService.Activate(handle);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
        }

        // O menu vai em três blocos, separados por linha: o que mexe no **app** (abrir,
        // fechar, matar), o que mexe na **dock** (fixar) e o que é do **WinDock** inteiro.
        // Misturados, "Desafixar da dock" ficava colado em "Fechar" — dois verbos parecidos
        // com consequências muito diferentes.
        Add(menu, "Nova janela", () => DockModel.Launch(item));

        // Fechar e forçar aparecem sempre, apagados quando o app não está aberto — como na
        // barra do Windows. Sumir com eles fazia o menu mudar de tamanho e de ordem conforme
        // o app estivesse ou não rodando, e o item que se procurava não estava onde a última
        // vez o deixou.
        Add(menu, item.Windows.Count > 1 ? "Fechar todas" : "Fechar",
            () => _model.CloseAll(item), enabled: item.HasWindows);
        // "Finalizar tarefa" é o nome que a própria barra do Windows usa para a mesma coisa
        // (matar o processo, sem pedir licença) — segue esse nome em vez de inventar um novo.
        Add(menu, "Finalizar tarefa", () => ForceClose(item), enabled: item.HasWindows);

        menu.Items.Add(new Separator());
        Add(menu, item.IsPinned ? "Desafixar da dock" : "Fixar na dock", () => _model.TogglePin(item));

        menu.Items.Add(new Separator());
        Add(menu, "Buscar aplicativos", OpenLauncher, "Alt+Espaço");
        Add(menu, "Adicionar atalho...", PinShortcut);
        Add(menu, "Configurações...", OpenSettings);
        Add(menu, "Sair do WinDock", Close);

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        WatchMenu(menu);
        e.Handled = true;
    }

    /// <summary>
    /// Fecha o menu no Esc ou num clique fora dele.
    ///
    /// O WPF já faria as duas coisas sozinho — num programa comum. Aqui não faz: a dock é
    /// <c>WS_EX_NOACTIVATE</c> (é o que impede o clique num ícone de roubar o foco de quem
    /// está trabalhando), e um menu que nasce numa janela que não ativa nunca recebe o
    /// teclado. O Esc ia para o app que estava em primeiro plano, e o menu ficava aberto até
    /// alguém escolher alguma coisa.
    ///
    /// A saída é a mesma que o cartão da bandeja usa: um relógio curto lendo o estado do
    /// mouse e do teclado por fora. O bit 0x0001 do <c>GetAsyncKeyState</c> é o que importa —
    /// um clique dura menos que o intervalo e passaria despercebido entre duas leituras.
    /// </summary>
    private void WatchMenu(ContextMenu menu)
    {
        // zera o histórico das teclas: o próprio clique direito que abriu o menu ainda está
        // marcado como "apertado desde a última consulta" e fecharia o menu na hora
        GetAsyncKeyState(VK_LBUTTON);
        GetAsyncKeyState(VK_RBUTTON);
        GetAsyncKeyState(VK_ESCAPE);

        var watch = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };

        watch.Tick += (_, _) =>
        {
            if (!menu.IsOpen) { watch.Stop(); return; }

            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0)
            {
                Log.Trace("Esc com o menu aberto: fechando");
                menu.IsOpen = false;
                watch.Stop();
                return;
            }

            var clicked = (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0 ||
                          (GetAsyncKeyState(VK_RBUTTON) & 0x0001) != 0;
            if (!clicked || !GetCursorPos(out var cursor)) return;

            // dentro do próprio menu o clique é uma escolha, e quem cuida dele é o item
            if (menu.IsOpen && OverMenu(menu, new Point(cursor.X, cursor.Y))) return;

            Log.Trace($"clique fora do menu em ({cursor.X},{cursor.Y}): fechando");
            menu.IsOpen = false;
            watch.Stop();
        };

        menu.Closed += (_, _) => watch.Stop();
        watch.Start();
    }

    private static bool OverMenu(ContextMenu menu, Point screenPoint)
    {
        try
        {
            var origin = menu.PointToScreen(new Point(0, 0));
            return screenPoint.X >= origin.X && screenPoint.X <= origin.X + menu.ActualWidth &&
                   screenPoint.Y >= origin.Y && screenPoint.Y <= origin.Y + menu.ActualHeight;
        }
        catch { return false; }   // o menu já se foi entre a leitura e esta pergunta
    }

    /// <summary>
    /// Pergunta antes de matar o processo.
    ///
    /// "Fechar" pede licença ao app e ele pode salvar o que estiver aberto; isto aqui não —
    /// o trabalho não salvo se perde. Uma escolha dessas não pode acontecer por um clique
    /// que escorregou uma linha no menu, ainda mais estando logo abaixo de "Fechar".
    /// </summary>
    private void ForceClose(DockItem item)
    {
        if (_model is null) return;

        var many = item.Windows.Count > 1;
        // o dono é a dock, que é topmost: sem isso a pergunta pode nascer por baixo dela.
        // O título e o botão dizem só "Encerrar {app}" — sem "à força": é o mesmo texto que
        // o menu já usa ("Finalizar tarefa"), e repetir "à força" nos dois lugares (título e
        // botão) era barulho, não informação nova.
        var yes = ConfirmWindow.Ask(this,
            $"Encerrar {item.Label}?",
            (many ? $"São {item.Windows.Count} janelas abertas. " : string.Empty) +
            "O programa é encerrado na hora, sem chance de salvar: o que estiver aberto e não " +
            "tiver sido gravado se perde.",
            accept: "Encerrar");

        if (yes) _model.KillAll(item);
    }

    /// <param name="gesture">Atalho mostrado à direita do item, quando existe um.</param>
    /// <param name="enabled">Falso deixa o item à vista, mas apagado.</param>
    private static void Add(ContextMenu menu, string header, Action action,
                            string gesture = "", bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    /// <summary>
    /// Abre a pasta dos atalhos fixados na barra de tarefas — e la que estao os .lnk
    /// de cada perfil do Chrome, com o argumento e o icone certos.
    /// </summary>
    private void PinShortcut()
    {
        var taskbar = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha o atalho ou o programa",
            Filter = "Atalhos e programas (*.lnk;*.exe)|*.lnk;*.exe|Todos os arquivos (*.*)|*.*",
            InitialDirectory = Directory.Exists(taskbar) ? taskbar : Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory)
        };

        if (dlg.ShowDialog() == true) _model?.PinShortcut(dlg.FileName);
    }

    private void OpenSettings()
    {
        if (_settings is { IsVisible: true })
        {
            // ja aberto: traz para frente de verdade. So o Activate nao basta — a dock nao
            // ativa nada (WS_EX_NOACTIVATE), entao o painel ficava piscando atras da janela
            // em que a pessoa estava
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            WindowService.Focus(new WindowInteropHelper(_settings).Handle);
            return;
        }

        _settings = new SettingsWindow(_config);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();

        // a dock nao ativa nada (WS_EX_NOACTIVATE), entao o painel abriria atras da
        // janela em que a pessoa estava trabalhando
        _settings.Activate();
        WindowService.Focus(new WindowInteropHelper(_settings).Handle);
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private void OnClosed(object? sender, EventArgs e)
    {
        _config.PropertyChanged -= OnConfigChanged;
        _config.Save();

        var hWnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hWnd, LauncherHotkeyId);
        for (var i = 0; i < 9; i++) UnregisterHotKey(hWnd, NumberHotkeyId + i);
        foreach (var id in HotkeyIds.Values.SelectMany(ids => ids)) UnregisterHotKey(hWnd, id);

        // as miniaturas seguram registros no compositor: soltar antes de sair
        _previewDelay.Stop();
        _previewClose.Stop();
        _thumbnails?.Close();

        _launcher?.CloseForReal();
        _panel?.Close();      // devolve a faixa de cima antes de sair
        _tiling?.Dispose();
        _model?.Dispose();
        _appBar?.Dispose();   // devolve a area de trabalho ao tamanho normal
        _taskbar.Dispose();   // e a barra do Windows ao estado em que estava
        _fullScreen.Dispose();
    }
}
