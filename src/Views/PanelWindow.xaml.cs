using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Linq;
using System.Windows.Media;
using WinDock.Interop;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// A barra de cima. Mesma mecanica da dock — AppBar para reservar a faixa e
/// WS_EX_NOACTIVATE para nao roubar o foco de ninguem — mas com o relogio e os
/// indicadores no lugar dos icones.
/// </summary>
public partial class PanelWindow : Window
{
    private readonly DockConfig _config;
    private readonly PanelModel _model;
    private AppBar? _appBar;

    public PanelWindow(DockConfig config)
    {
        _config = config;
        _model = new PanelModel(config);
        _model.Calendar.PurgeDone(DateTime.Now);

        InitializeComponent();
        DataContext = _model;

        _outsideClick.Tick += (_, _) => CheckOutsideClick();
        _trayCardTimeout.Tick += (_, _) =>
        {
            _trayCardTimeout.Stop();

            // com rastro: este era o único jeito de o cartão sumir sem deixar linha nenhuma, e
            // por isso o clique seguinte na seta aparecia como "abrindo" — de fora parece que o
            // clique fechou, quando o cartão já tinha se fechado sozinho antes dele
            Log.Trace("cartão da bandeja: tempo parado esgotado, fechando sozinho");
            TrayPopup.IsOpen = false;
        };
        _volumeOsd.Tick += (_, _) => HideVolumeOsd();
        _mediaTick.Tick += (_, _) => UpdateMediaProgress();
        _deviceSettle.Tick += (_, _) => { _deviceSettle.Stop(); _model.RefreshRemovable(); };

        // um cartão que encolhe deixa o cursor do lado de fora sem ninguém ter mexido no
        // mouse; o vigia precisa saber disso. Veja o `PanelModel.CardResized`.
        _model.CardResized += (_, _) => IgnoreNextOutsideClick();
        TrayPopup.CustomPopupPlacementCallback = PlaceTrayCard;
        CalendarPopup.CustomPopupPlacementCallback = PlaceCalendarCard;
        ClockPopup.CustomPopupPlacementCallback = PlaceCalendarCard;   // centrado no horário, pelo mesmo motivo
        ApplyPanelOrder();

        // reordenar nas Configurações vale na hora, sem fechar e abrir a barra
        _config.PropertyChanged += OnPanelConfigChanged;

        // a faixa que troca refaz a conta do ticker: quem sabe quanto o texto mede é o
        // layout, e o modelo não tem como saber se ele cabe na barra
        _model.PropertyChanged += OnModelChanged;

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _outsideClick.Stop();
            _volumeOsd.Stop();
            _deviceSettle.Stop();
            RemovableService.Unwatch(_deviceNotify);
            _config.PropertyChanged -= OnPanelConfigChanged;
            _model.PropertyChanged -= OnModelChanged;
            MediaSlide.BeginAnimation(TranslateTransform.XProperty, null);
            _model.Dispose();
            _appBar?.Dispose();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hWnd = new WindowInteropHelper(this).Handle;

        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));

        // a barra fica sempre em cima; a dock, na borda que o usuario escolheu.
        // Com a barra do Windows escondida por nos, ela vai para o topo de verdade: a
        // barra nativa continua registrada e empurraria esta para baixo dela.
        _appBar = new AppBar(this) { IgnoreOtherBars = _config.Taskbar != TaskbarMode.Keep };
        _appBar.Register(DockEdge.Top, _config.PanelSize, reserveSpace: true);

        // espetar ou tirar um pen-drive precisa chegar até aqui: é o que faz o botão de
        // remover aparecer e sumir sozinho, sem relógio nenhum perguntando à toa
        _deviceNotify = RemovableService.Watch(hWnd);
        HwndSource.FromHwnd(hWnd)?.AddHook(OnDeviceChange);
    }

    /// <summary>O registro dos avisos de dispositivo, desfeito no fechamento.</summary>
    private nint _deviceNotify;

    /// <summary>
    /// Um volume entrou ou saiu: a lista de dispositivos externos é remontada.
    ///
    /// O aviso chega mais de uma vez por aparelho — um pen-drive com duas partições traz um
    /// por letra —, e remontar a lista é ir ao disco. Daí a pausa de meio segundo: os avisos
    /// da mesma espetada se juntam numa leitura só, e o botão aparece uma vez, e não três.
    /// </summary>
    private nint OnDeviceChange(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != RemovableService.WM_DEVICECHANGE || !RemovableService.IsArrivalOrRemoval(wParam))
            return nint.Zero;

        _deviceSettle.Stop();
        _deviceSettle.Start();
        return nint.Zero;
    }

    private readonly System.Windows.Threading.DispatcherTimer _deviceSettle = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };

    /// <summary>Reaplica altura e posicao quando a configuracao muda.</summary>
    /// <summary>Os comandos de música para o atalho de teclado — os mesmos dos botões da barra,
    /// com o mesmo filtro de programas. Sem nada tocando (ou só programas fora do filtro), não
    /// fazem nada.</summary>
    public Task PreviousMedia() => _model.PreviousMedia();
    public Task NextMedia() => _model.NextMedia();
    public Task ToggleMedia() => _model.ToggleMedia();

    public void Resize()
    {
        if (_appBar is null) return;
        _appBar.IgnoreOtherBars = _config.Taskbar != TaskbarMode.Keep;
        _appBar.Resize(DockEdge.Top, _config.PanelSize, reserveSpace: true);
    }

    /// <summary>
    /// Se um painel do shell estava aberto quando o botao foi apertado, o clique fecha em
    /// vez de abrir. A leitura e feita no apertar, e nao no soltar: a barra nao rouba o
    /// foco, entao neste instante o painel ainda esta na frente e da para saber.
    /// </summary>
    private bool _shellPanelWasOpen;

    /// <summary>
    /// Estado dos paineis no instante do aperto, e nao no clique: o clique chega no soltar,
    /// e ate la muita coisa pode ter mexido no popup.
    /// </summary>
    private bool _bluetoothWasOpen;
    private bool _volumeWasOpen;
    private bool _powerWasOpen;
    private bool _calendarWasOpen;
    private bool _mediaWasOpen;
    private bool _brightnessWasOpen;
    private bool _aiUsageWasOpen;
    private bool _updatesWasOpen;
    private bool _removableWasOpen;
    private bool _systemWasOpen;
    private bool _clockWasOpen;

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        _shellPanelWasOpen = PanelModel.ShellPanelInFront();
        _bluetoothWasOpen = BluetoothPopup.IsOpen;
        _volumeWasOpen = VolumePopup.IsOpen;
        _powerWasOpen = PowerPopup.IsOpen;
        _calendarWasOpen = CalendarPopup.IsOpen;
        _mediaWasOpen = MediaPopup.IsOpen;
        _brightnessWasOpen = BrightnessPopup.IsOpen;
        _aiUsageWasOpen = AiUsagePopup.IsOpen;
        _updatesWasOpen = UpdatesPopup.IsOpen;
        _removableWasOpen = RemovablePopup.IsOpen;
        _systemWasOpen = SystemPopup.IsOpen;
        _clockWasOpen = ClockPopup.IsOpen;
        _toolsWasOpen = ToolsPopup.IsOpen;
        _batteryWasOpen = BatteryPopup.IsOpen;

        base.OnPreviewMouseDown(e);
    }

    private void OnQuickSettings(object sender, RoutedEventArgs e) =>
        Toggle(PanelModel.QuickSettings);

    private void OnNotifications(object sender, RoutedEventArgs e) =>
        Toggle(PanelModel.ActionCenter);

    private void Toggle(string uri)
    {
        // os nossos saem de cena de qualquer jeito: este botão abre um painel do Windows,
        // e sem isto o nosso ficava aberto por baixo dele
        CloseAllPopups();

        if (_shellPanelWasOpen) { PanelModel.CloseShellPanel(); return; }

        PanelModel.OpenSystemPanel(uri);

        // o vigia passa a valer também para o painel do Windows: clicar no vazio da barra
        // fecha o do wi-fi e o do sino como fecha os nossos
        _shellPanelPedido = DateTime.Now;
        WatchOutsideClick();
    }

    /// <summary>
    /// Quando pedimos ao Windows que abrisse um painel dele.
    ///
    /// O painel não nasce no mesmo instante do clique — leva um tempo até existir e virar
    /// primeiro plano. Sem esta carência o vigia olharia uma vez, não veria painel nenhum e se
    /// desligaria antes de o painel aparecer, deixando o clique na barra sem efeito outra vez.
    /// </summary>
    private DateTime _shellPanelPedido = DateTime.MinValue;

    /// <summary>Há um painel do Windows na tela por nossa conta — ou acabou de ser pedido.</summary>
    private bool ShellPanelVivo =>
        PanelModel.ShellPanelInFront() ||
        DateTime.Now - _shellPanelPedido < TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tira da tela tudo o que estiver aberto, antes de um botão da barra abrir o seu.
    ///
    /// São duas famílias de painel e cada uma só sabia fechar a própria: os nossos popups
    /// (bluetooth, volume, energia, calendário, cartão da bandeja) e os do Windows que o
    /// wi-fi e o sino abrem. O resultado era clicar num ícone com outro painel aberto e
    /// ficar com os dois na tela ao mesmo tempo, um por cima do outro.
    ///
    /// O painel do Windows só é fechado se estava mesmo aberto: fechá-lo é um <c>Esc</c>
    /// sintético (veja <see cref="PanelModel.CloseShellPanel"/>), e um Esc solto vai parar
    /// em quem estiver em primeiro plano.
    /// </summary>
    private void CloseOpenPanels()
    {
        CloseAllPopups();
        if (_shellPanelWasOpen) PanelModel.CloseShellPanel();
    }

    // ── ordem dos itens da barra ─────────────────────────────

    /// <summary>
    /// Os itens do canto direito que a pessoa pode reordenar: a chave que vai no
    /// <c>Tag</c> de cada um no XAML, e o nome que aparece nas Configurações.
    ///
    /// Fica aqui, e não no XAML, para as duas janelas lerem a mesma lista — a de opções
    /// precisa dos nomes, e esta precisa das chaves. Item novo na barra entra aqui também,
    /// senão ele funciona mas não aparece para ser reordenado.
    /// </summary>
    public static readonly (string Key, string Name)[] PanelItems =
    [
        ("bandeja",         "Ícones da bandeja"),
        ("midia",           "Música — o que está tocando"),
        ("midia-controles", "Música — controles"),
        ("ferramentas",     "Ferramentas (comandos mais usados)"),
        ("sistema",         "CPU e memória"),
        ("bateria",         "Bateria"),
        ("wifi",            "Wi-Fi"),
        ("bluetooth",       "Bluetooth"),
        ("volume",          "Volume"),
        ("brilho",          "Brilho"),
        ("remover",         "Remover dispositivo externo"),
        ("atualizacoes",    "Atualizações esperando (e nova versão do WinDock)"),
        ("cotaIa",          "Cota de IA"),
        ("notificacoes",    "Notificações"),
        ("energia",         "Energia"),
        ("relogio",         "Relógio"),
    ];

    /// <summary>
    /// Põe os itens do canto direito na ordem que a pessoa escolheu.
    ///
    /// Reordenar os filhos que já existem é o que evita reescrever a barra como lista de
    /// dados: cada item continua sendo o XAML dele, com os bindings e handlers que já tem, e
    /// só o lugar muda. Um <c>StackPanel</c> desenha na ordem de <c>Children</c>.
    ///
    /// Quem não aparece na configuração entra ao lado do vizinho que tinha no XAML — é o que
    /// faz uma preferência antiga continuar válida quando a barra ganha um item novo, <b>e o
    /// item novo nascer onde ele foi desenhado para nascer</b>.
    ///
    /// <para>Ele já foi para o fim da fila, e isso estava errado: o "Remover dispositivo"
    /// nasceu à esquerda da cota de IA no XAML justamente porque é ali que ele faz sentido, e
    /// quem tivesse reordenado a barra alguma vez o receberia no canto oposto, depois do
    /// relógio. A preferência salva diz onde ficam os itens que ela cita; sobre um item que
    /// ela nem conhece, ela não tem opinião — e aí o desenho é quem decide.</para>
    /// </summary>
    private void ApplyPanelOrder()
    {
        // os itens soltos da primeira passagem: depois dela eles podem estar dentro de ilhas
        _items ??= RightItems.Children.Cast<UIElement>().ToList();
        var atuais = _items;

        // A ordem do XAML é guardada na primeira passagem: sem ela, "voltar ao padrão" não
        // teria a que voltar — a essa altura os filhos já estão na ordem customizada, e o
        // arranjo original teria se perdido.
        _defaultOrder ??= atuais.Select(e => (e as FrameworkElement)?.Tag as string ?? "").ToList();

        var desejada = _config.PanelOrder.Count > 0 ? _config.PanelOrder : _defaultOrder;

        var ordenados = desejada
            .Select(chave => atuais.FirstOrDefault(e => (e as FrameworkElement)?.Tag as string == chave))
            .Where(e => e is not null)
            .ToList();

        // Os que a configuração não citou entram junto do vizinho que tinham no XAML: procura-se
        // o primeiro sucessor dele na ordem original que já esteja colocado, e ele entra logo
        // antes. Sem sucessor colocado (o item era o último), vai para o fim.
        foreach (var orfao in atuais.Where(e => !ordenados.Contains(e)))
        {
            var chave = (orfao as FrameworkElement)?.Tag as string ?? "";
            var depoisNoXaml = _defaultOrder.SkipWhile(k => k != chave).Skip(1);

            var vizinho = depoisNoXaml
                .Select(k => ordenados.FirstOrDefault(e => (e as FrameworkElement)?.Tag as string == k))
                .FirstOrDefault(e => e is not null);

            var onde = vizinho is null ? ordenados.Count : ordenados.IndexOf(vizinho);
            ordenados.Insert(onde, orfao);
        }

        Place(ordenados!);
    }

    /// <summary>Os itens do canto direito, guardados na primeira passagem do <see cref="ApplyPanelOrder"/>.</summary>
    private List<UIElement>? _items;

    /// <summary>
    /// Põe os itens na fila — soltos, ou em ilhas quando a barra está em ilhas.
    ///
    /// Cada ilha é uma <see cref="Border"/> com o estilo <c>Island</c> e uma fila dentro; ela
    /// começa no primeiro item e em cada um que a pessoa marcou nas Configurações
    /// (<see cref="DockConfig.PanelIslandBreaks"/>). Uma ilha cujos itens estão todos escondidos
    /// (a mídia parada, o pen-drive que saiu) some junto, senão sobraria uma pílula vazia.
    /// </summary>
    private void Place(List<UIElement> ordenados)
    {
        foreach (var (_, fila) in _islands) fila.Children.Clear();
        foreach (var item in ordenados) VisibilityWatch.RemoveValueChanged(item, OnItemVisibilityChanged);
        _islands.Clear();
        RightItems.Children.Clear();

        if (!_config.PanelIslands)
        {
            foreach (var e in ordenados) RightItems.Children.Add(e);
            return;
        }

        StackPanel? atual = null;
        foreach (var item in ordenados)
        {
            var chave = (item as FrameworkElement)?.Tag as string ?? "";
            if (atual is null || _config.PanelIslandBreaks.Contains(chave, StringComparer.OrdinalIgnoreCase))
            {
                atual = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var ilha = new Border { Style = (Style)FindResource("Island"), Child = atual };
                RightItems.Children.Add(ilha);
                _islands.Add((ilha, atual));
            }

            atual.Children.Add(item);
            VisibilityWatch.AddValueChanged(item, OnItemVisibilityChanged);
        }

        UpdateIslandVisibility();
    }

    private readonly List<(Border Ilha, StackPanel Fila)> _islands = new();

    private static readonly System.ComponentModel.DependencyPropertyDescriptor VisibilityWatch =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));

    private void OnItemVisibilityChanged(object? sender, EventArgs e) => UpdateIslandVisibility();

    private void UpdateIslandVisibility()
    {
        foreach (var (ilha, fila) in _islands)
            ilha.Visibility = fila.Children.Cast<UIElement>().Any(c => c.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>A ordem que veio do XAML, para o "voltar ao padrão" ter destino.</summary>
    private List<string>? _defaultOrder;

    private void OnPanelConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DockConfig.PanelOrder) or nameof(DockConfig.PanelIslands)
                           or nameof(DockConfig.PanelIslandBreaks)) ApplyPanelOrder();

        // ligar ou desligar a rolagem vale na hora, com a música que já está tocando
        if (e.PropertyName is nameof(DockConfig.PanelMediaTicker)) UpdateMediaTicker();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelModel.Media)) UpdateMediaTicker();
    }

    // ── o nome da faixa rolando ──────────────────────────────

    /// <summary>O texto e o estado da última vez — para não recomeçar a rolagem à toa.</summary>
    private string _tickerText = "";
    private bool _tickerPlaying;

    /// <summary>
    /// Faz o nome da faixa rolar quando ele não cabe no espaço da barra.
    ///
    /// A conta não pode sair do XAML. Um <c>TextBlock</c> com <c>MaxWidth</c> é arranjado no
    /// tamanho da moldura e trunca ali dentro — para o texto poder andar, ele precisa ser
    /// arranjado no tamanho inteiro e ser a <b>moldura</b> quem recorta. Daí a largura
    /// explícita: sem ela, o texto rolaria mostrando sempre as mesmas reticências.
    ///
    /// <para>A medida vem de um <c>FormattedText</c>, e não de um <c>Measure</c> na marra: medir
    /// um elemento que já está na árvore, fora da passada de layout do WPF, é pedir para o
    /// próximo arranjo usar um tamanho que ninguém pediu.</para>
    ///
    /// <para>Rola só o que precisa e só enquanto toca: texto que cabe fica parado, e em pausa
    /// a animação para — movimento na barra o tempo todo, sem nada acontecendo, é o tipo de
    /// coisa que cansa quem está trabalhando ao lado dela.</para>
    /// </summary>
    private void UpdateMediaTicker()
    {
        var texto = _model.Media.Summary ?? "";
        var tocando = _model.Media.IsPlaying;

        // o mesmo texto no mesmo estado não recomeça a rolagem: a mídia avisa mudança
        // também quando só a posição da faixa andou, e sem isto o nome voltaria ao começo
        // a cada aviso, sem nunca chegar ao fim
        if (texto == _tickerText && tocando == _tickerPlaying) return;

        _tickerText = texto;
        _tickerPlaying = tocando;

        MediaSlide.BeginAnimation(TranslateTransform.XProperty, null);
        MediaSlide.X = 0;
        MediaText.Width = double.NaN;   // de volta ao automático: as reticências do WPF voltam a valer

        if (!_config.PanelMediaTicker || texto.Length == 0) return;

        var cabe = MediaClip.MaxWidth;
        var inteiro = TextWidth(texto);
        if (inteiro <= cabe + 0.5) return;

        MediaText.Width = inteiro;
        if (!tocando) return;

        // Dois segundos parado em cada ponta, e a volta rápida. Ler o começo é o mais
        // importante — é o nome da faixa —, então ele fica parado tempo de ser lido antes de
        // qualquer movimento, e o fim também espera antes de voltar.
        var sobra = inteiro - cabe;
        var rolagem = sobra / 28.0;   // 28 px/s: acompanha a leitura sem virar letreiro de loja

        var anda = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };

        void Quadro(double valor, double quando) =>
            anda.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                valor, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(quando))));

        Quadro(0, 0);
        Quadro(0, 2);
        Quadro(-sobra, 2 + rolagem);
        Quadro(-sobra, 4 + rolagem);
        Quadro(0, 4.45 + rolagem);

        MediaSlide.BeginAnimation(TranslateTransform.XProperty, anda);
    }

    /// <summary>Quanto o texto da faixa mede, na fonte em que ele é desenhado.</summary>
    private double TextWidth(string texto)
    {
        var tipo = new Typeface(MediaText.FontFamily, MediaText.FontStyle, MediaText.FontWeight,
                                MediaText.FontStretch);

        return new FormattedText(texto, System.Globalization.CultureInfo.CurrentCulture,
                                 FlowDirection.LeftToRight, tipo, MediaText.FontSize,
                                 Brushes.White, VisualTreeHelper.GetDpi(MediaText).PixelsPerDip).Width;
    }

    // ── rede e sistema ───────────────────────────────────────

    /// <summary>
    /// Abre (ou fecha) o cartão de sistema: processador, memória e rede, cada um com o
    /// último minuto em gráfico.
    /// </summary>
    private void OnSystemStats(object sender, RoutedEventArgs e)
    {
        var estavaAberto = _systemWasOpen;
        CloseOpenPanels();

        if (estavaAberto) { WatchOutsideClick(); return; }

        SystemPopup.IsOpen = true;
        WatchOutsideClick();
    }

    // ── ferramentas ──────────────────────────────────────────

    private bool _toolsWasOpen;

    /// <summary>Abre (ou fecha) o cartão com os comandos de <c>DockConfig.Tools</c>.</summary>
    private void OnTools(object sender, RoutedEventArgs e)
    {
        var estavaAberto = _toolsWasOpen;
        CloseOpenPanels();

        if (!estavaAberto)
        {
            _model.ToolsError = string.Empty;
            ToolsPopup.IsOpen = true;
        }
        WatchOutsideClick();
    }

    /// <summary>
    /// Roda o comando da linha e fecha o cartão — quem clicou quer a ferramenta, não a lista. Se
    /// não abriu, o cartão fica, com o motivo embaixo: fechar calado era o que fazia parecer que
    /// o clique não tinha pegado.
    /// </summary>
    private void OnToolRun(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PanelModel.ToolRow row) return;

        if (ToolsService.Run(row.Tool) is { } erro)
        {
            _model.ToolsError = $"{row.Name}: {erro}";
            IgnoreNextOutsideClick();   // o cartão cresce uma linha debaixo do cursor
            return;
        }

        _model.ToolsError = string.Empty;
        CloseAllPopups();
    }

    /// <summary>O lápis do cartão: as Configurações abertas já na lista de ferramentas.</summary>
    private void OnToolsEdit(object sender, RoutedEventArgs e)
    {
        CloseAllPopups();
        Application.Current.Windows.OfType<MainWindow>().FirstOrDefault()?.OpenSettingsAt("ferramentas");
    }

    // ── brilho ───────────────────────────────────────────────

    private void OnBrightness(object sender, RoutedEventArgs e)
    {
        var estavaAberto = _brightnessWasOpen;
        CloseOpenPanels();

        if (!estavaAberto) BrightnessPopup.IsOpen = true;
        WatchOutsideClick();
    }

    /// <summary>
    /// Roda do mouse sobre o ícone: ajusta sem abrir nada, como no volume.
    ///
    /// A dica é fechada junto, pelo mesmo motivo do volume: ela já está na tela quando a roda
    /// gira, e sem tirá-la ficaria empilhada com o que aparecer.
    /// </summary>
    private void OnBrightnessWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        BrightnessTip.IsOpen = false;
        _model.NudgeBrightness(e.Delta);
        e.Handled = true;
    }

    // ── mídia ────────────────────────────────────────────────

    /// <summary>
    /// Os três controles agem direto, sem abrir nada: é o ponto de estarem na barra.
    ///
    /// Nenhum deles fecha os painéis abertos, de propósito — pausar a música com o controle
    /// de volume aberto é gesto normal, e fechar o painel no meio seria um susto.
    /// </summary>
    private async void OnMediaToggle(object sender, RoutedEventArgs e) => await _model.ToggleMedia();
    private async void OnMediaNext(object sender, RoutedEventArgs e) => await _model.NextMedia();
    private async void OnMediaPrevious(object sender, RoutedEventArgs e) => await _model.PreviousMedia();

    /// <summary>O texto da faixa abre o card, como os outros botões da barra abrem os deles.</summary>
    private void OnMedia(object sender, RoutedEventArgs e)
    {
        var estavaAberto = _mediaWasOpen;
        CloseOpenPanels();

        if (!estavaAberto)
        {
            MediaPopup.IsOpen = true;
            _ = _model.RefreshMediaPosition();
            _mediaTick.Start();
        }

        WatchOutsideClick();
    }

    /// <summary>
    /// Clique na barra de progresso: pula para aquele ponto da faixa.
    ///
    /// A conta é a posição do clique dividida pela largura do trilho — e vem do trilho, não
    /// do preenchimento, que só ocupa o que já passou.
    /// </summary>
    private async void OnMediaSeek(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement trilho || trilho.ActualWidth <= 0) return;

        var x = e.GetPosition(trilho).X;
        await _model.SeekMedia(x / trilho.ActualWidth);
    }

    /// <summary>
    /// A posição da faixa é a única coisa que anda sozinha — o resto chega por evento. Este
    /// relógio existe só enquanto o card está aberto, e para junto com ele.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _mediaTick =
        new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Relê a posição e redesenha o preenchimento da barra de progresso.
    ///
    /// A largura é calculada aqui, e não por binding: o trilho tem largura de layout (ele
    /// estica com o card), e uma fração só vira pixels depois que o WPF mediu o elemento.
    /// É a mesma conta que a pilha da bateria faz.
    /// </summary>
    private async void UpdateMediaProgress()
    {
        if (!MediaPopup.IsOpen) { _mediaTick.Stop(); return; }

        await _model.RefreshMediaPosition();
        MediaFill.Width = MediaTrack.ActualWidth * _model.MediaProgress;
    }

    private void OnBluetooth(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_bluetoothWasOpen)
        {
            _model.RefreshBluetoothDevices();
            BluetoothPopup.IsOpen = true;
        }

        WatchOutsideClick();
    }

    // ── fechar ao clicar fora ───────────────────────────────

    /// <summary>
    /// Quem fecha os painéis ao clicar fora — e o único que fecha.
    ///
    /// Os popups ficam com <c>StaysOpen="True"</c> de propósito. O fechamento automático do
    /// WPF depende de o popup capturar o mouse, e numa janela <c>WS_EX_NOACTIVATE</c> essa
    /// captura funciona pela metade, o que é pior que não funcionar:
    ///
    /// - **clique num app qualquer**: a nossa janela não recebe nada, o WPF não dispensa o
    ///   popup, e a captura dele ainda atrapalha — o painel simplesmente não fechava;
    /// - **clique no próprio botão que abriu**: aí o WPF dispensa, e dispensa *antes* do
    ///   nosso handler rodar. O botão lia "estava fechado" e reabria na hora — clicar na
    ///   seta para recolher a bandeja nunca recolhia, só piscava.
    ///
    /// Com <c>StaysOpen="True"</c> o WPF não mexe em nada e este relógio é a única
    /// autoridade: enquanto houver painel aberto, ele olha se o botão do mouse foi apertado
    /// fora dele.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _outsideClick = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };

    private void WatchOutsideClick()
    {
        // Ligar o vigia joga fora o Esc pendente. O bit "foi apertado desde a última consulta"
        // acumula enquanto ninguém pergunta — e ninguém pergunta enquanto não há cartão aberto,
        // que pode ser o dia inteiro. Sem esta leitura descartada, um Esc dado em qualquer app
        // minutos antes fechava o cartão no mesmo instante em que ele abria: medido, o cartão
        // do bluetooth abria e o rastro trazia "Esc com cartão aberto" 95 ms depois, sem
        // ninguém ter encostado no teclado.
        var vigiar = AnyPopupOpen || ShellPanelVivo;

        if (!_outsideClick.IsEnabled && vigiar) GetAsyncKeyState(VK_ESCAPE);

        _outsideClick.IsEnabled = vigiar;
        Log.Trace($"vigia de clique fora: {(_outsideClick.IsEnabled ? "ligado" : "desligado")}");
    }

    /// <summary>
    /// Todos os cartões da barra, numa lista só.
    ///
    /// <para><b>Existe porque a lista estava em três lugares e um deles ficou para trás.</b> O
    /// cartão da cota de IA foi acrescentado ao "algum cartão está aberto?" e ao "feche todos",
    /// mas não ao "o clique caiu dentro de um cartão?" do <see cref="CheckOutsideClick"/> — e
    /// o efeito não se parece nem um pouco com a causa: o cartão se fechava no instante do
    /// clique, de modo que o botão de dentro dele nunca chegava a receber o evento. O relato
    /// foi "clicando na seta não mostra nada", e o rastro mostrou um "clique fora dos painéis"
    /// em coordenadas que estavam bem dentro do cartão.</para>
    ///
    /// <para>Com a lista aqui, um cartão novo entra em todos os caminhos de uma vez.</para>
    /// </summary>
    private IEnumerable<Popup> AllPopups()
    {
        yield return BluetoothPopup;
        yield return VolumePopup;
        yield return PowerPopup;
        yield return CalendarPopup;
        yield return TrayPopup;
        yield return MediaPopup;
        yield return BrightnessPopup;
        yield return AiUsagePopup;
        yield return UpdatesPopup;
        yield return RemovablePopup;
        yield return SystemPopup;
        yield return ClockPopup;
        yield return ToolsPopup;
        yield return BatteryPopup;
    }

    private bool AnyPopupOpen => AllPopups().Any(p => p.IsOpen);

    /// <summary>
    /// Até quando o vigia deve fingir que não viu o clique que está pendente.
    ///
    /// É curto de propósito — o suficiente para o tique seguinte passar. Mais que isso e um
    /// clique de verdade do lado de fora, logo depois de recolher o cartão, deixaria de
    /// fechá-lo.
    /// </summary>
    private DateTime _ignorarCliqueAte;

    private void IgnoreNextOutsideClick()
    {
        _ignorarCliqueAte = DateTime.UtcNow.AddMilliseconds(350);

        // consome o bit "foi apertado desde a consulta anterior" já: sem isto, o clique que
        // acabou de ser tratado dentro do cartão ficaria guardado e seria lido como novo no
        // primeiro tique depois da carência
        GetAsyncKeyState(VK_LBUTTON);
        GetAsyncKeyState(VK_RBUTTON);
    }

    private void CheckOutsideClick()
    {
        if (!AnyPopupOpen && !ShellPanelVivo)
        {
            _outsideClick.Stop();
            return;
        }

        if (DateTime.UtcNow < _ignorarCliqueAte) return;

        // O Esc fecha o cartão aberto — o mesmo bit "foi apertado desde a consulta anterior"
        // que serve para o mouse. Vem por aqui, e não por um KeyDown, porque a barra é
        // WS_EX_NOACTIVATE: ela nunca tem o foco do teclado e evento de tecla nenhum chega
        // nela. Só *observar* a tecla também não a rouba de ninguém — quem está digitando
        // continua recebendo o Esc normalmente, e este relógio só corre enquanto há cartão
        // aberto, que é justamente quando "fechar" é o significado óbvio do Esc.
        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0)
        {
            // ...a não ser que o Esc tenha sido nosso. A leitura da bandeja fecha o painel do
            // Windows apertando Esc, e o sistema não distingue tecla sintética de tecla física:
            // sem esta pergunta, abrir o cartão da bandeja disparava a leitura, a leitura
            // apertava Esc e o cartão se fechava sozinho 70 ms depois de aparecer.
            if (SyntheticKeys.EscapeWasOurs())
            {
                Log.Trace("Esc detectado, mas foi nosso (leitura da bandeja): mantendo o cartão");
                return;
            }

            Log.Trace("Esc com cartão aberto: fechando");
            CloseAllPopups();

            // nada de mandar Esc para o painel do Windows aqui: ele tem o foco do teclado, então
            // o mesmo Esc que chegou até nós já fechou o dele. Um segundo cairia no app de trás.
            _shellPanelPedido = DateTime.MinValue;
            _outsideClick.Stop();
            return;
        }

        // 0x8000 = apertado agora; 0x0001 = foi apertado desde a consulta anterior. O
        // segundo bit e o que importa: um clique dura menos que o intervalo deste relogio
        // e passaria despercebido entre duas leituras.
        var pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8001) != 0 ||
                      (GetAsyncKeyState(VK_RBUTTON) & 0x8001) != 0;
        if (!pressed || !GetCursorPos(out var cursor)) return;

        var point = new Point(cursor.X, cursor.Y);

        // dentro do painel aberto: quem cuida é o próprio painel. A lista vem do `AllPopups`
        // de propósito — ver o comentário de lá.
        if (AllPopups().Any(p => Contains(p, point))) return;

        // na barra, só o clique em cima de um botão é dele: fechar aqui faria o clique no
        // ícone reabrir logo em seguida. O vazio entre os ícones não é de ninguém, e é o
        // lugar natural para largar o cartão aberto sem ter de mirar em outro ícone.
        if (ContainsBar(point) && HitsButton(point)) return;

        Log.Trace($"clique fora dos painéis em ({cursor.X},{cursor.Y}): fechando");
        CloseAllPopups();

        // O painel do wi-fi e o do sino são do Windows, e o Esc que os fecha vai para quem
        // estiver em primeiro plano — por isso a pergunta é feita **agora**, coladinha no
        // envio, e não no começo do método: se o clique foi noutro app, o painel já se fechou
        // sozinho ao perder o foco, e um Esc atrasado cairia na janela de quem recebeu o
        // clique. Foi assim que um Esc destes já fechou a tela de um Delphi (ver APRENDIZADOS).
        if (PanelModel.ShellPanelInFront())
        {
            Log.Trace("  e o painel do Windows junto");
            PanelModel.CloseShellPanel();
        }

        _shellPanelPedido = DateTime.MinValue;
        _outsideClick.Stop();
    }

    /// <summary>
    /// Se o ponto da tela cai sobre um botão da barra.
    ///
    /// O teste é na árvore visual, não num retângulo calculado: os ícones mudam de lugar com a
    /// ordem escolhida nas opções, aparecem e somem conforme o que está ligado, e a bandeja
    /// ainda cresce e encolhe sozinha. Qualquer lista de posições mantida à mão aqui nasceria
    /// desatualizada. Sem nada sob o cursor (o espaço vazio não é "atingível" no WPF), a
    /// resposta é não — que é exatamente o caso que fecha o cartão.
    /// </summary>
    private bool HitsButton(Point screenPoint)
    {
        var local = PointFromScreen(screenPoint);
        if (VisualTreeHelper.HitTest(this, local)?.VisualHit is not DependencyObject hit) return false;

        for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;

        return false;
    }

    private static bool Contains(System.Windows.Controls.Primitives.Popup popup, Point screenPoint)
    {
        if (!popup.IsOpen || popup.Child is not FrameworkElement child) return false;

        var origin = child.PointToScreen(new Point(0, 0));
        return screenPoint.X >= origin.X && screenPoint.X <= origin.X + child.ActualWidth &&
               screenPoint.Y >= origin.Y && screenPoint.Y <= origin.Y + child.ActualHeight;
    }

    private bool ContainsBar(Point screenPoint)
    {
        var origin = PointToScreen(new Point(0, 0));
        return screenPoint.X >= origin.X && screenPoint.X <= origin.X + ActualWidth &&
               screenPoint.Y >= origin.Y && screenPoint.Y <= origin.Y + ActualHeight;
    }

    /// <summary>
    /// Liga ou desliga o rádio. O painel fica aberto de propósito: o estado no cabeçalho e a
    /// lista de aparelhos mudam à vista, que é a confirmação de que o clique fez algo.
    /// </summary>
    private async void OnToggleBluetooth(object sender, RoutedEventArgs e) =>
        await _model.ToggleBluetooth();

    private void OnHideBluetoothDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) _model.HideBluetoothDevice(name);
    }

    private void OnShowAllBluetooth(object sender, RoutedEventArgs e) =>
        _model.ShowAllBluetoothDevices();

    private void OnBluetoothSettings(object sender, RoutedEventArgs e)
    {
        BluetoothPopup.IsOpen = false;
        PanelModel.OpenSystemPanel(PanelModel.BluetoothSettings);
    }

    private void OnVolume(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_volumeWasOpen)
        {
            _model.RefreshAudioDevices();

            // as sessões de áudio nascem e morrem o tempo todo (um vídeo que acaba fecha a
            // sua): a lista é montada na abertura, e não guardada de um clique para o outro
            _model.RefreshAppSessions();

            VolumePopup.IsOpen = true;
        }

        WatchOutsideClick();
    }

    /// <summary>
    /// Abre o cartão das atualizações.
    ///
    /// Sem reler na abertura, ao contrário do cartão da cota: a releitura é um processo de console
    /// do winget que leva segundos, e o cartão ficaria de pé mudando de tamanho debaixo do mouse.
    /// O que ele mostra é de no máximo três horas atrás, e a hora da leitura está no rodapé.
    /// </summary>
    private void OnUpdates(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_updatesWasOpen) UpdatesPopup.IsOpen = true;

        WatchOutsideClick();
    }

    /// <summary>
    /// "Conferir agora": refaz as duas perguntas sem esperar as próximas três horas.
    ///
    /// O cartão fica aberto, e o que ele mostra muda quando a resposta chega — a do winget leva
    /// segundos. Por isso não é feito na abertura do cartão: ali o tamanho mudaria debaixo do
    /// mouse de quem só queria olhar; aqui foi pedido.
    /// </summary>
    private void OnRefreshUpdates(object sender, RoutedEventArgs e) => _model.RefreshUpdates();

    /// <summary>
    /// A versão nova do WinDock: pergunta, baixa, troca o executável e reabre.
    ///
    /// Pergunta antes porque a dock some e volta — a barra do Windows reaparece por um instante
    /// e as janelas maximizadas se reacomodam —, e isso não pode acontecer no meio de um clique
    /// dado por engano. Dando certo, esta dock fecha pelo caminho normal (devolve a área de
    /// trabalho e a barra do Windows), e a nova, que já está esperando, assume.
    /// </summary>
    private async void OnSelfUpdate(object sender, RoutedEventArgs e)
    {
        if (_model.SelfUpdating) return;
        CloseAllPopups();

        var ok = ConfirmWindow.Ask(this, $"Atualizar o WinDock para a {_model.SelfUpdateTag}?",
            $"Você está na {SelfUpdateService.CurrentVersion}. A dock baixa a versão nova do GitHub, fecha e " +
            "abre de novo já atualizada — leva alguns segundos.\n\n" +
            "O executável atual fica guardado ao lado como cópia, e volta sozinho se a troca falhar.",
            accept: "Atualizar", cancel: "Agora não");
        if (!ok) return;

        var (trocou, erro) = await _model.InstallSelfUpdateAsync();

        if (trocou)
        {
            Application.Current.MainWindow?.Close();
            return;
        }

        if (erro is not null)
            ConfirmWindow.Tell(this, "Não deu para atualizar o WinDock", erro);
    }

    /// <summary>Leva ao Windows Update, que é onde essas atualizações se instalam.</summary>
    private void OnOpenWindowsUpdate(object sender, RoutedEventArgs e)
    {
        CloseAllPopups();
        UpdatesService.OpenWindowsUpdate();
        _model.RecheckUpdatesSoon();
    }

    /// <summary>
    /// Atualiza tudo pelo winget, sem janela, com o cartão aberto mostrando o andamento.
    ///
    /// O cartão **não** se fecha aqui, ao contrário do botão do Windows Update: agora é ele que
    /// conta como está indo e o que deu errado — era o terminal que fazia isso antes.
    /// </summary>
    private void OnWingetUpgradeAll(object sender, RoutedEventArgs e) => _model.UpgradeWinget();

    /// <summary>
    /// Atualiza um pacote só — os que o <c>--all</c> pula, por exigirem alvo explícito.
    /// </summary>
    private void OnWingetUpgradeOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) _model.UpgradeWinget(id);
    }

    /// <summary>
    /// A saída para quando a atualização silenciosa falha: o terminal, com o processo à vista.
    /// </summary>
    private void OnWingetTerminal(object sender, RoutedEventArgs e)
    {
        CloseAllPopups();
        UpdatesService.UpgradeAllInTerminal();
        _model.RecheckUpdatesSoon();
    }

    /// <summary>
    /// Para de avisar sobre um pacote do winget. O cartão fica aberto de propósito: a linha some
    /// dali na mesma hora, e ver isso acontecer é o que diz que o clique fez alguma coisa.
    /// </summary>
    private void OnSilenceWinget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) _model.SilenceWinget(key);
    }

    private void OnUnsilenceWinget(object sender, RoutedEventArgs e) => _model.UnsilenceWingetAll();

    /// <summary>
    /// Abre o cartão da cota e manda reler.
    ///
    /// O cartão aparece na hora com o número da última leitura, e a nova chega por cima
    /// quando a rede responder — mesmo desenho do cartão da bandeja. Esperar pela resposta
    /// para só então abrir deixaria o clique parecendo travado quando a rede está ruim, que é
    /// justamente quando se quer olhar.
    /// </summary>
    private void OnAiUsage(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_aiUsageWasOpen)
        {
            AiUsagePopup.IsOpen = true;
            _model.RefreshAiUsage();
        }

        WatchOutsideClick();
    }

    /// <summary>
    /// Abre o cartão dos dispositivos externos e relê a lista.
    ///
    /// A releitura na abertura não é redundância do aviso do Windows: é a rede de segurança
    /// dela. Se por algum motivo um <c>WM_DEVICECHANGE</c> não chegar, quem abre o cartão vê
    /// o que está espetado agora, e não o que estava da última vez que a barra soube.
    /// </summary>
    private void OnRemovable(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_removableWasOpen)
        {
            _model.RefreshRemovable();
            RemovablePopup.IsOpen = true;
        }

        WatchOutsideClick();
    }

    /// <summary>
    /// Pede a remoção da linha clicada.
    ///
    /// O cartão fica aberto de propósito: é nele que aparece o "pode desconectar" — ou o
    /// motivo de não ter dado. Fechar na hora do clique deixaria a pessoa sem saber se podia
    /// puxar o pen-drive, que é a única pergunta que ela tinha.
    /// </summary>
    private async void OnEjectRemovable(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;

        await _model.EjectRemovable(id);
    }

    /// <summary>
    /// Abre o aparelho no Explorer e fecha o cartão.
    ///
    /// Aqui fechar é o certo, ao contrário do "Remover": o cartão já fez o que tinha para
    /// fazer, e deixá-lo de pé por cima da janela de pasta que acabou de abrir seria o
    /// resto de um clique que já terminou.
    /// </summary>
    private void OnOpenRemovable(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;

        _model.OpenRemovable(id);
        CloseAllPopups();
        WatchOutsideClick();
    }

    private void OnAudioDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) _model.UseAudioDevice(id);
    }

    /// <summary>Roda do mouse sobre o alto-falante: ajusta sem precisar abrir o controle.</summary>
    private void OnVolumeWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        _model.Nudge(e.Delta);
        ShowVolumeOsd();
        e.Handled = true;
    }

    /// <summary>
    /// Mostra o número do volume ao lado do ícone e recomeça a contagem para escondê-lo.
    ///
    /// Com o controle de volume aberto o mostrador não aparece: o painel já traz o número e
    /// a barra, e os dois juntos seriam a mesma informação duas vezes, uma por cima da outra.
    /// </summary>
    private void ShowVolumeOsd()
    {
        if (VolumePopup.IsOpen) { VolumeOsd.IsOpen = false; return; }

        // A dica do botão já está aberta quando a roda gira — o cursor ficou parado sobre o
        // ícone tempo suficiente —, e sem tirá-la os dois cartões aparecem empilhados. São
        // dois passos porque um só não basta: `IsOpen = false` tira da tela a que está
        // aberta, `SetIsEnabled(false)` impede a próxima enquanto o mostrador durar.
        VolumeTip.IsOpen = false;
        ToolTipService.SetIsEnabled(VolumeButton, false);
        VolumeOsd.IsOpen = true;

        // reiniciar é o ponto: durante uma rolagem longa a contagem nunca chega ao fim, e o
        // mostrador some uma vez só, quando a pessoa realmente parou
        _volumeOsd.Stop();
        _volumeOsd.Start();
    }

    /// <summary>Tira o mostrador e devolve a dica ao botão.</summary>
    private void HideVolumeOsd()
    {
        _volumeOsd.Stop();
        VolumeOsd.IsOpen = false;
        ToolTipService.SetIsEnabled(VolumeButton, true);
    }

    private readonly System.Windows.Threading.DispatcherTimer _volumeOsd =
        new() { Interval = TimeSpan.FromMilliseconds(1200) };

    private void OnMute(object sender, RoutedEventArgs e) => _model.Muted = !_model.Muted;

    /// <summary>Corta o som de um programa só, sem mexer no volume geral.</summary>
    private void OnMuteApp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not uint pid) return;

        var session = _model.AppSessions.FirstOrDefault(s => s.ProcessId == pid);
        if (session is not null) session.Muted = !session.Muted;
    }

    // ── bateria ─────────────────────────────────────────────

    private bool _batteryWasOpen;

    /// <summary>Abre (ou fecha) o cartão da bateria; o histórico chega em seguida, em segundo plano.</summary>
    private void OnBattery(object sender, RoutedEventArgs e)
    {
        var estavaAberto = _batteryWasOpen;
        CloseOpenPanels();

        if (!estavaAberto)
        {
            _model.LoadBatteryHistory();
            BatteryPopup.IsOpen = true;
        }
        WatchOutsideClick();
    }

    // ── energia ─────────────────────────────────────────────

    private void OnPower(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();
        if (!_powerWasOpen)
        {
            _model.RefreshPowerCard();
            PowerPopup.IsOpen = true;
        }
        WatchOutsideClick();
    }

    /// <summary>
    /// Dispara o comando de energia. O painel fecha antes: "Bloquear" e "Encerrar sessão"
    /// deixam a tela como está por um instante, e um menu pendurado ali ficaria estranho.
    /// </summary>
    private void OnPowerCommand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;

        PowerPopup.IsOpen = false;
        _outsideClick.Stop();

        PowerService.All.FirstOrDefault(c => c.Name == name)?.Run();
    }

    /// <summary>Um plano do cartão de energia: troca na hora, e o cartão fica aberto mostrando o novo.</summary>
    private void OnPowerPlan(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Guid id) return;
        _model.SetPowerPlan(id);
    }

    // ── calendário ──────────────────────────────────────────

    private void OnCalendar(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();
        if (_calendarWasOpen) { WatchOutsideClick(); return; }

        // antes de desenhar: as concluídas que já passaram do prazo não chegam a aparecer
        _model.Calendar.PurgeDone(DateTime.Now);

        // abre sempre no mês de hoje, mesmo que da última vez tenham folheado para longe, e só com
        // o mês: a lista de tarefas aparece quando a pessoa escolhe um dia
        _model.Calendar.GoToToday();
        _model.Calendar.ClearSelection();

        // o painel nasce embaixo da data que foi clicada — ela troca de lugar conforme a
        // opção de relógio centralizado
        CalendarPopup.PlacementTarget = (UIElement)sender;
        CalendarPopup.IsOpen = true;
        WatchOutsideClick();
    }

    /// <summary>
    /// Clique no horário: o cartão com a hora completa, segundos inclusive, e a data por extenso.
    /// Nasce embaixo do horário clicado — ele troca de lugar com o relógio centralizado.
    /// </summary>
    private void OnClock(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();   // um painel de cada vez

        if (!_clockWasOpen)
        {
            _model.StartSeconds();
            ClockPopup.PlacementTarget = (UIElement)sender;
            ClockPopup.IsOpen = true;
        }

        WatchOutsideClick();
    }

    /// <summary>Fechado por qualquer caminho, o relógio de segundos para junto.</summary>
    private void OnClockClosed(object? sender, EventArgs e) => _model.StopSeconds();

    private void OnCalendarPrevious(object sender, RoutedEventArgs e) => _model.Calendar.PreviousMonth();
    private void OnCalendarNext(object sender, RoutedEventArgs e) => _model.Calendar.NextMonth();
    private void OnCalendarToday(object sender, RoutedEventArgs e) => _model.Calendar.GoToToday();

    /// <summary>
    /// Clique num dia: mostra as tarefas dele embaixo do mês, no próprio cartão.
    ///
    /// Marcar como feito, remover, mover e escrever acontecem ali mesmo. Os campos de texto pedem o
    /// teclado para a barra só quando são clicados (<see cref="OnCalendarFieldMouseDown"/>).
    /// </summary>
    private void OnCalendarDay(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CalendarDay dia) return;

        Log.Trace($"clique no dia {dia.Date:yyyy-MM-dd} do calendário");

        // mover, marcar hora e corrigir são de uma tarefa do dia que estava aberto
        CancelMove();
        CancelTime();
        CancelEdit();

        // clicar de novo no dia que já está aberto recolhe a lista — o mesmo gesto abre e fecha
        if (_model.Calendar.SelectedDate == dia.Date) _model.Calendar.ClearSelection();
        else _model.Calendar.Select(dia.Date);
    }

    private void OnCalendarNoteDone(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarNote nota) _model.Calendar.ToggleDone(nota);
    }

    private void OnCalendarNoteRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CalendarNote nota) _model.Calendar.RemoveNote(nota);
    }

    private void OnCalendarNoteNotify(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarNote nota) return;

        _model.Calendar.ToggleNotify(nota);
        Log.Trace($"tarefa “{Resumo(nota.Text, 34)}”: aviso {(nota.Notify ? "desligado" : "ligado")}");
    }

    // ── calendário: corrigir o texto de uma tarefa ──────────

    /// <summary>
    /// A tarefa que está com o texto aberto para correção. Guardada aqui, e não só no
    /// <see cref="CalendarNote.IsEditing"/>, porque é por ela que se fecha a edição anterior quando
    /// outra começa — duas linhas em edição ao mesmo tempo dariam dois cursores na mesma lista.
    /// </summary>
    private CalendarNote? _editing;

    /// <summary>
    /// Clique no texto de uma tarefa: ele vira campo, com o que já estava escrito e selecionado.
    ///
    /// É o gesto que todo mundo tenta primeiro — clicar na palavra errada —, e sem ele corrigir uma
    /// letra era apagar a tarefa e escrevê-la de novo, perdendo a hora e o aviso junto.
    /// </summary>
    private void OnCalendarNoteEdit(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarNote nota) return;

        CancelEdit();
        CancelTime();
        CancelMove();

        _editing = nota;
        nota.IsEditing = true;
        e.Handled = true;
    }

    /// <summary>
    /// O campo acabou de aparecer no lugar do texto: é aqui que ele pede o teclado.
    ///
    /// Não dá para fazer isso no clique: nesse instante o campo ainda está <c>Collapsed</c> — quem
    /// o mostra é o gatilho do <c>IsEditing</c>, depois —, e um elemento escondido não aceita foco.
    /// </summary>
    private void OnNoteEditorShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox campo && campo.IsVisible) TakeKeyboard(campo, selecionar: true);
    }

    private void OnNoteEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitEdit((TextBox)sender); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
    }

    /// <summary>
    /// O campo perdeu o teclado — clicar noutra tarefa, noutro campo ou fora do cartão. Grava, que é
    /// o que se espera de quem clicou em outro lugar depois de escrever.
    /// </summary>
    private void OnNoteEditFinish(object sender, KeyboardFocusChangedEventArgs e) => CommitEdit((TextBox)sender);

    /// <summary>
    /// Passa o texto do campo para a tarefa.
    ///
    /// Fecha a edição <b>antes</b> de gravar: gravar reconstrói a lista do dia, o campo sai da tela e
    /// dispara o <c>LostKeyboardFocus</c>, que cairia aqui de novo. Com a edição já fechada, a
    /// segunda passagem não tem o que fazer.
    /// </summary>
    private void CommitEdit(TextBox campo)
    {
        if (_editing is not { } nota) return;

        _editing = null;
        nota.IsEditing = false;
        _model.Calendar.EditNote(nota, campo.Text);
    }

    private void CancelEdit()
    {
        if (_editing is not { } nota) return;

        _editing = null;
        nota.IsEditing = false;
    }

    // ── calendário: a hora de uma tarefa ────────────────────

    /// <summary>A tarefa cuja hora está sendo marcada, enquanto o campo está aberto.</summary>
    private CalendarNote? _timing;

    /// <summary>
    /// "🕘" numa tarefa: abre o campo de hora, já com a hora que ela tem — ou com a próxima hora
    /// cheia, para a tarefa que ainda não tem nenhuma.
    /// </summary>
    private void OnCalendarNoteTime(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarNote nota) return;

        CancelEdit();
        CancelMove();

        _timing = nota;
        TimeHeading.Text = $"Hora de “{Resumo(nota.Text, 34)}”:";
        TimeHint.Text = "14:30, ou 14h, ou só 14. Enter marca.";
        TimeHint.Foreground = MoveHintBrush;
        TimeBox.Text = nota.HasTime ? nota.TimeText : DateTime.Now.AddHours(1).ToString("HH:00");
        TimePanel.Visibility = Visibility.Visible;

        TakeKeyboard(TimeBox, selecionar: true);
    }

    private void OnTimeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ConfirmTime();
        e.Handled = true;
    }

    private void OnTimeConfirm(object sender, RoutedEventArgs e) => ConfirmTime();

    /// <summary>"Tirar": a tarefa volta a ser do dia inteiro.</summary>
    private void OnTimeClear(object sender, RoutedEventArgs e)
    {
        if (_timing is { } nota) _model.Calendar.SetTime(nota, null);
        CancelTime();
    }

    private void CancelTime()
    {
        _timing = null;
        TimePanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmTime()
    {
        if (_timing is not { } nota) { CancelTime(); return; }

        if (!MonthCalendar.TryParseTime(TimeBox.Text, out var hora))
        {
            TimeHint.Text = "Não entendi essa hora. Tente 14:30, 14h ou 14.";
            TimeHint.Foreground = Brushes.IndianRed;
            TimeBox.SelectAll();
            return;
        }

        _model.Calendar.SetTime(nota, hora);
        Log.Trace($"tarefa “{Resumo(nota.Text, 34)}”: hora marcada para {hora:hh\\:mm}");
        CancelTime();
    }

    // ── calendário: escrever no próprio cartão ──────────────

    /// <summary>
    /// Quem estava em primeiro plano antes de a pessoa clicar num campo de texto do cartão — é para
    /// ele que o teclado volta quando o cartão fecha. Zero quando a barra não pegou o teclado.
    /// </summary>
    private nint _typingFrom;

    /// <summary>
    /// Clique num campo de texto do calendário: a barra pega o teclado, só agora.
    ///
    /// A barra é <c>WS_EX_NOACTIVATE</c> — clicar nela não tira o foco de quem está trabalhando, e
    /// o preço disso é nenhuma tecla chegar aqui. O próprio Windows prevê a saída: uma janela assim
    /// não é ativada por clique, mas pode ser por código. Então só o gesto de clicar num campo, que
    /// é a pessoa dizendo "vou escrever", traz a barra para o primeiro plano; o resto do cartão
    /// continua sem roubar nada. O foco volta a quem o tinha quando o cartão fecha
    /// (<see cref="ReturnKeyboard"/>).
    /// </summary>
    private void OnCalendarFieldMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox campo) TakeKeyboard(campo, selecionar: false);
    }

    private void TakeKeyboard(TextBox campo, bool selecionar)
    {
        var barra = new WindowInteropHelper(this).Handle;
        var antes = GetForegroundWindow();
        if (antes != barra)
        {
            _typingFrom = antes;
            WindowService.Focus(barra);
            Log.Trace($"campo do calendário: teclado pedido para a barra (antes={antes:X}, agora={GetForegroundWindow():X})");
        }

        // O WPF devolve o foco ao último elemento da janela quando ela é ativada; o campo é focado
        // depois disso, pela fila, para ser ele quem fica com o cursor
        Dispatcher.InvokeAsync(() =>
        {
            campo.Focus();
            Keyboard.Focus(campo);
            if (selecionar) campo.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Devolve o teclado ao programa que o tinha antes do clique num campo do calendário.
    ///
    /// Só se a barra ainda estiver com ele: se a pessoa fechou o cartão clicando noutro programa, o
    /// Windows já deu o foco a esse programa, e tirar dele seria roubar o clique.
    /// </summary>
    private void ReturnKeyboard()
    {
        if (_typingFrom == 0) return;

        var anterior = _typingFrom;
        _typingFrom = 0;

        if (GetForegroundWindow() != new WindowInteropHelper(this).Handle) return;
        if (IsWindow(anterior)) WindowService.Focus(anterior);
    }

    private void OnNewNoteKeyDown(object sender, KeyEventArgs e)
    {
        // Enter grava e deixa o campo pronto para a próxima: um dia costuma ter mais de um
        // compromisso
        if (e.Key != Key.Enter) return;
        CommitNewNote();
        e.Handled = true;
    }

    private void OnNewNoteAdd(object sender, RoutedEventArgs e) => CommitNewNote();

    /// <summary>
    /// Passa o que estiver no campo para o dia escolhido — só pelo Enter ou pelo "+"; fechar o
    /// cartão descarta o texto (veja <see cref="CloseAllPopups"/>).
    ///
    /// A hora pode vir na frente do texto ("14:30 dentista"): quem está digitando não quer parar,
    /// clicar num relógio e digitar de novo. O que sobra vira o texto da tarefa.
    /// </summary>
    private void CommitNewNote()
    {
        var texto = NewNoteBox.Text.Trim();
        if (texto.Length == 0) return;

        var hora = MonthCalendar.TakeLeadingTime(ref texto);
        if (texto.Length == 0) return;   // só a hora, sem tarefa nenhuma

        _model.Calendar.AddToSelected(texto, hora);
        NewNoteBox.Clear();
    }

    /// <summary>O texto encurtado para caber num cabeçalho — o resto vira reticências.</summary>
    private static string Resumo(string texto, int limite) =>
        texto.Length <= limite ? texto : texto[..(limite - 3)] + "...";

    // ── calendário: mover uma tarefa de dia ─────────────────

    /// <summary>A tarefa escolhida para mudar de dia, enquanto o campo de data está aberto.</summary>
    private CalendarNote? _moving;

    private static readonly Brush MoveHintBrush = CreateMoveHintBrush();

    private static Brush CreateMoveHintBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// "→" numa tarefa: abre o campo de data já preenchido com o dia seguinte e selecionado — "não
    /// deu hoje" é de longe o motivo mais comum de mover, e assim o caso comum sai com um Enter. O
    /// clique no "→" já pega o teclado, como o clique num campo.
    /// </summary>
    private void OnCalendarNoteMove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarNote nota) return;
        if (_model.Calendar.SelectedDate is not { } dia) return;

        _moving = nota;
        MoveHeading.Text = $"Mover “{Resumo(nota.Text, 34)}” para:";
        MoveHint.Text = "Dia e mês bastam (15/09). Enter move.";
        MoveHint.Foreground = MoveHintBrush;
        MoveBox.Text = dia.AddDays(1).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.CurrentCulture);
        MovePanel.Visibility = Visibility.Visible;

        TakeKeyboard(MoveBox, selecionar: true);
    }

    private void OnMoveKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ConfirmMove();
        e.Handled = true;
    }

    private void OnMoveConfirm(object sender, RoutedEventArgs e) => ConfirmMove();
    private void OnMoveCancel(object sender, RoutedEventArgs e) => CancelMove();

    private void CancelMove()
    {
        _moving = null;
        MovePanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmMove()
    {
        if (_moving is not { } nota || _model.Calendar.SelectedDate is not { } dia) { CancelMove(); return; }

        if (!MonthCalendar.TryParseDay(dia, MoveBox.Text, out var destino))
        {
            MoveHint.Text = "Não entendi essa data. Tente 15/09 ou 15/09/2026.";
            MoveHint.Foreground = Brushes.IndianRed;
            MoveBox.SelectAll();
            return;
        }

        if (_model.Calendar.MoveNote(nota, destino))
            Log.Trace($"anotação movida de {dia:yyyy-MM-dd} para {destino:yyyy-MM-dd}");

        CancelMove();
    }

    /// <summary>
    /// Roda do mouse sobre o calendário: folheia os meses.
    ///
    /// Para cima volta no tempo, para baixo avança — a mesma direção das setas do cabeçalho e
    /// a mesma do calendário do Windows.
    /// </summary>
    private void OnCalendarWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) _model.Calendar.PreviousMonth();
        else _model.Calendar.NextMonth();

        e.Handled = true;
    }

    // ── bandeja ─────────────────────────────────────────────

    /// <summary>
    /// Aciona o ícone de bandeja — o mesmo que clicar nele na barra do Windows.
    ///
    /// Quem faz o trabalho é o <see cref="TrayService"/>, e ele pode terminar de duas
    /// formas: acionando o ícone, ou deixando aberto o painel de ícones ocultos do Windows
    /// quando não deu para identificar qual botão é qual. Nos dois casos a barra sai da
    /// frente fechando o que estiver aberto aqui.
    /// </summary>
    private void OnTrayIcon(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int position) return;

        var icon = _model.TrayIcons.FirstOrDefault(i => i.Position == position);
        if (icon is null) return;

        CloseAllPopups();
        _outsideClick.Stop();

        // Na mesma thread da leitura, e esperando a vez dela: mostrar a barra, abrir o painel
        // do Windows e esperar ele montar levam mais de um segundo, e a barra não pode
        // congelar nesse tempo — mas fazer isso **em paralelo** com a releitura que o cartão
        // acabou de disparar era pior ainda, com os dois abrindo o painel do shell ao mesmo
        // tempo.
        Log.Trace($"clique no ícone '{icon.Label}': entrou na fila da bandeja");
        TrayReader.Queue(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ok = TrayService.Invoke(icon);
            Log.Trace($"ícone '{icon.Label}' acionado em {clock.ElapsedMilliseconds} ms ({(ok ? "ok" : "falhou")})");
        });
    }

    /// <summary>
    /// A faixa que um elemento ocupa na tela, em pixels físicos — a mesma conta que a dock faz
    /// para ancorar o preview das janelas.
    /// </summary>
    private RECT ScreenBounds(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = element.PointToScreen(new Point(0, 0));

        return new RECT
        {
            Left   = (int)Math.Round(origin.X),
            Top    = (int)Math.Round(origin.Y),
            Right  = (int)Math.Round(origin.X + element.ActualWidth * dpi.DpiScaleX),
            Bottom = (int)Math.Round(origin.Y + element.ActualHeight * dpi.DpiScaleY)
        };
    }

    /// <summary>
    /// O botão direito abre o menu do programa dono do ícone — é onde ficam o "Sair" e o resto
    /// do que cada um oferece, e sem isso não havia como encerrar pela bandeja um programa que
    /// só se fecha por ali.
    ///
    /// O menu é desenhado pelo programa, no lugar onde o clique aconteceu: o canto da bandeja
    /// do Windows, e não junto do nosso cartão. Não há como pedir outro lugar — veja o
    /// <see cref="TrayService.ShowMenu"/>.
    /// </summary>
    private void OnTrayIconMenu(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int position) return;

        var icon = _model.TrayIcons.FirstOrDefault(i => i.Position == position);
        if (icon is null) return;

        e.Handled = true;

        // a faixa do ícone na tela é medida **antes** de o cartão fechar: depois disso o botão
        // já não está em lugar nenhum, e é ela que leva o menu para perto de onde a pessoa
        // clicou, em vez de ele nascer no canto da bandeja do Windows
        var anchor = ScreenBounds((FrameworkElement)sender);
        Log.Trace($"âncora do ícone '{icon.Label}' na tela: ({anchor.Left},{anchor.Top})-" +
                  $"({anchor.Right},{anchor.Bottom})");

        CloseAllPopups();
        _outsideClick.Stop();

        // pela mesma fila do clique comum: as duas coisas mexem no painel do Windows, e as
        // duas ao mesmo tempo é o que já fez o painel abrir duas vezes e a leitura falhar
        Log.Trace($"clique direito no ícone '{icon.Label}': entrou na fila da bandeja");
        TrayReader.Queue(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ok = TrayService.ShowMenu(icon, anchor);
            Log.Trace($"menu de '{icon.Label}' pedido em {clock.ElapsedMilliseconds} ms ({(ok ? "ok" : "falhou")})");
        });
    }

    /// <summary>
    /// A seta abre o cartão da bandeja.
    ///
    /// **O cartão aparece na hora, com o que já se sabia**, e a releitura acontece por trás:
    /// ler custa uns 470 ms — é o tempo de o painel de ícones ocultos do Windows abrir e a
    /// automação enumerar o que há dentro — e esperar por isso antes de mostrar qualquer
    /// coisa fazia o clique parecer travado.
    /// Quando a leitura volta, a lista se corrige sozinha; só a primeira abertura de cada
    /// sessão espera de verdade, porque aí não há nada para mostrar ainda.
    /// </summary>
    /// <remarks>
    /// Já houve aqui um filtro que descartava dois eventos seguidos em menos de 250 ms,
    /// posto na suspeita de que um clique físico chegasse duas vezes. Era leitura errada da
    /// prova: os pares de 20 ms vinham de cliques **de verdade** que ficaram na fila
    /// enquanto a interface esperava a leitura, e o filtro comia justamente o segundo. Isso
    /// é que fazia a seta não recolher. O `StaysOpen="True"` dos popups já resolve a causa
    /// real, que era o WPF dispensar o cartão antes de este método rodar.
    /// </remarks>
    private void OnToggleTray(object sender, RoutedEventArgs e)
    {
        // O estado vem do próprio popup, e não do que se anotou no `MouseDown`: aquele
        // caminho só existia porque o WPF fechava o cartão antes deste método rodar, o que
        // o `StaysOpen="True"` acabou. Anotar no `MouseDown` também deixava a seta cega para
        // quem não usa mouse — pelo teclado ou pela automação o estado ficava congelado no
        // da última vez que alguém clicou, e a seta só sabia fechar.
        var wasOpen = TrayPopup.IsOpen;

        CloseOpenPanels();

        // A dica fica suprimida **em qualquer clique**, inclusive no que fecha o cartão, e só
        // volta quando o mouse sai da seta (`OnTrayButtonLeave`). Devolvê-la ao fechar fazia
        // ela reaparecer na hora, debaixo do cursor que ainda estava ali — parecia que o
        // clique de fechar tinha travado em cima de uma mensagem.
        HideTrayTooltip();

        if (wasOpen)
        {
            Log.Trace("clique na seta: fechando o cartão");
            _trayCardTimeout.Stop();
            _outsideClick.Stop();
            return;
        }

        Log.Trace("clique na seta: abrindo o cartão");
        TrayPopup.IsOpen = true;
        RestartTrayCardTimeout();
        WatchOutsideClick();

        Log.Trace($"cartão na tela com {_model.TrayIcons.Count} ícones (o que já se tinha)");

        // a contagem para fechar recomeça quando a lista chega: os três segundos são para
        // olhar o conteúdo certo, não o anterior
        _model.RefreshTray(() =>
        {
            Log.Trace($"lista atualizada: {_model.TrayIcons.Count} ícones");
            if (TrayPopup.IsOpen) RestartTrayCardTimeout();
        });
    }

    /// <summary>
    /// O cartão da bandeja se fecha sozinho depois de um tempo parado.
    ///
    /// A contagem reinicia a cada movimento do mouse dentro dele: fechar debaixo do cursor
    /// de quem está mirando um ícone seria pior que não fechar nunca.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _trayCardTimeout = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    private void RestartTrayCardTimeout()
    {
        _trayCardTimeout.Stop();
        _trayCardTimeout.Start();
    }

    /// <summary>Dica do botão da bandeja, guardada enquanto o cartão está aberto.</summary>
    private object? _trayTooltip;

    /// <summary>
    /// Tira a dica de mouse do botão da bandeja de cena.
    ///
    /// **Desabilitar não basta**: o <c>ToolTipService.IsEnabled</c> impede a dica de abrir,
    /// mas não fecha a que já está aberta — e é esse o caso aqui, porque para clicar na seta
    /// a pessoa já parou o mouse nela tempo suficiente para a dica aparecer. Tirar a dica do
    /// botão fecha o que estiver na tela; ela volta quando o cartão sai.
    /// </summary>
    private void HideTrayTooltip()
    {
        if (TrayButton.ToolTip is null) return;

        _trayTooltip = TrayButton.ToolTip;
        TrayButton.ToolTip = null;
    }

    /// <summary>
    /// O mouse saiu da seta: a dica volta a valer para a próxima vez que ele passar por ali.
    /// </summary>
    private void OnTrayButtonLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        ShowTrayTooltip();

    private void ShowTrayTooltip()
    {
        if (_trayTooltip is null) return;

        TrayButton.ToolTip = _trayTooltip;
        _trayTooltip = null;
    }

    private void OnTrayCardActivity(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (TrayPopup.IsOpen) RestartTrayCardTimeout();
    }

    /// <summary>
    /// Onde o cartão da bandeja se desenha: com a borda direita alinhada à da seta.
    ///
    /// O padrão do WPF encosta o cartão pela **esquerda** e depois o empurra para caber na
    /// tela. Como a largura do cartão vem da quantidade de ícones, e essa quantidade muda
    /// quando a leitura volta, o cartão saltava de lugar debaixo do cursor: era o "às vezes
    /// muda o local da visualização". Ancorando pela direita, a borda que fica junto da seta
    /// não se mexe — o cartão cresce para dentro da tela, como o do Windows faz.
    /// </summary>
    /// <summary>
    /// O calendário nasce **centrado** no relógio, e não com a borda esquerda encostada nele.
    /// O cartão do horário usa o mesmo cálculo, centrado no horário clicado.
    ///
    /// Com o relógio no meio da barra, alinhar pela esquerda (que é o que o <c>Placement="Bottom"</c>
    /// faz) jogava o cartão inteiro para a direita do centro da tela — o cartão tem 252 px de
    /// largura e o relógio uns 130, então sobrava mais de 100 px de desvio bem no meio do monitor.
    ///
    /// O WPF cuida sozinho de trazer o cartão para dentro da tela se ele passar da borda, que é o
    /// caso de quem põe o relógio no canto direito da barra.
    /// </summary>
    private CustomPopupPlacement[] PlaceCalendarCard(Size popup, Size target, Point offset) =>
        new[]
        {
            // o +6 é a folga vertical que o cartão já tinha no `VerticalOffset`: só a conta
            // horizontal muda aqui, para a distância da barra continuar a mesma de antes
            new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, target.Height + 6),
                                     PopupPrimaryAxis.Horizontal)
        };

    private CustomPopupPlacement[] PlaceTrayCard(Size popup, Size target, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(new Point(target.Width - popup.Width - 10, target.Height - 4),
                                     PopupPrimaryAxis.Horizontal)
        };

    /// <summary>
    /// O <paramref name="origem"/> é preenchido pelo compilador com o nome de quem chamou: com
    /// meia dúzia de caminhos fechando os cartões, "sumiu" e "alguém fechou" eram indistinguíveis
    /// no rastro. Só registra quando havia mesmo algo aberto — isto roda em todo clique da barra.
    /// </summary>
    private void CloseAllPopups([System.Runtime.CompilerServices.CallerMemberName] string origem = "")
    {
        if (AnyPopupOpen) Log.Trace($"fechando os cartões (pedido por {origem})");

        // O calendário fechando descarta o que ficou escrito no campo e desiste de mover. Só Enter ou
        // o "+" gravam: Esc ou clique fora são "desisti", e gravar ali deixava na lista a tarefa que
        // a pessoa começou e abandonou
        if (CalendarPopup.IsOpen)
        {
            NewNoteBox.Clear();
            CancelMove();
            CancelTime();
            CancelEdit();
        }

        foreach (var popup in AllPopups()) popup.IsOpen = false;

        // os relógios que só correm com um cartão aberto param junto
        _trayCardTimeout.Stop();
        _mediaTick.Stop();

        // o recado da última remoção morre com o cartão: reabri-lo dez minutos depois com um
        // "pode desconectar" na tela falaria de um pen-drive que já foi embora
        _model.ClearRemovableMessage();

        // o mostrador de volume não é um painel, mas some junto: o controle de volume já
        // traz o número, e deixá-lo por cima seria a mesma informação duas vezes
        HideVolumeOsd();

        // a dica do botão da bandeja volta a valer quando o cartão sai de cena
        // a dica nao volta aqui: quem a devolve e a saida do mouse da seta

        // se um campo do calendário tinha pegado o teclado, ele volta a quem o tinha
        ReturnKeyboard();
    }
}
