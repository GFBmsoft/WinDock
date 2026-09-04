using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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

        InitializeComponent();
        DataContext = _model;

        _outsideClick.Tick += (_, _) => CheckOutsideClick();
        _trayCardTimeout.Tick += (_, _) => { _trayCardTimeout.Stop(); TrayPopup.IsOpen = false; };
        _volumeOsd.Tick += (_, _) => HideVolumeOsd();
        TrayPopup.CustomPopupPlacementCallback = PlaceTrayCard;

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _outsideClick.Stop();
            _volumeOsd.Stop();
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
    }

    /// <summary>Reaplica altura e posicao quando a configuracao muda.</summary>
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

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        _shellPanelWasOpen = PanelModel.ShellPanelInFront();
        _bluetoothWasOpen = BluetoothPopup.IsOpen;
        _volumeWasOpen = VolumePopup.IsOpen;
        _powerWasOpen = PowerPopup.IsOpen;
        _calendarWasOpen = CalendarPopup.IsOpen;

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

        if (_shellPanelWasOpen) PanelModel.CloseShellPanel();
        else PanelModel.OpenSystemPanel(uri);
    }

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
        _outsideClick.IsEnabled = AnyPopupOpen;
        Log.Trace($"vigia de clique fora: {(_outsideClick.IsEnabled ? "ligado" : "desligado")}");
    }

    private bool AnyPopupOpen =>
        BluetoothPopup.IsOpen || VolumePopup.IsOpen || PowerPopup.IsOpen ||
        CalendarPopup.IsOpen || TrayPopup.IsOpen;

    private void CheckOutsideClick()
    {
        if (!AnyPopupOpen)
        {
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

        // dentro do painel aberto ou na propria barra: quem cuida e o botao, nao daqui —
        // fechar aqui faria o clique no icone reabrir logo em seguida
        if (Contains(BluetoothPopup, point) || Contains(VolumePopup, point) ||
            Contains(PowerPopup, point) || Contains(CalendarPopup, point) ||
            Contains(TrayPopup, point) || ContainsBar(point)) return;

        Log.Trace($"clique fora dos painéis em ({cursor.X},{cursor.Y}): fechando");
        CloseAllPopups();
        _outsideClick.Stop();
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

    // ── energia ─────────────────────────────────────────────

    private void OnPower(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();
        if (!_powerWasOpen) PowerPopup.IsOpen = true;
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

    // ── calendário ──────────────────────────────────────────

    private void OnCalendar(object sender, RoutedEventArgs e)
    {
        CloseOpenPanels();
        if (_calendarWasOpen) { WatchOutsideClick(); return; }

        // abre sempre no mês de hoje, mesmo que da última vez tenham folheado para longe
        _model.Calendar.GoToToday();

        // o painel nasce embaixo da data que foi clicada — ela troca de lugar conforme a
        // opção de relógio centralizado
        CalendarPopup.PlacementTarget = (UIElement)sender;
        CalendarPopup.IsOpen = true;
        WatchOutsideClick();
    }

    private void OnCalendarPrevious(object sender, RoutedEventArgs e) => _model.Calendar.PreviousMonth();
    private void OnCalendarNext(object sender, RoutedEventArgs e) => _model.Calendar.NextMonth();
    private void OnCalendarToday(object sender, RoutedEventArgs e) => _model.Calendar.GoToToday();

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
    private CustomPopupPlacement[] PlaceTrayCard(Size popup, Size target, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(new Point(target.Width - popup.Width - 10, target.Height - 4),
                                     PopupPrimaryAxis.Horizontal)
        };

    private void CloseAllPopups()
    {
        BluetoothPopup.IsOpen = false;
        VolumePopup.IsOpen = false;
        PowerPopup.IsOpen = false;
        CalendarPopup.IsOpen = false;
        TrayPopup.IsOpen = false;
        _trayCardTimeout.Stop();

        // o mostrador de volume não é um painel, mas some junto: o controle de volume já
        // traz o número, e deixá-lo por cima seria a mesma informação duas vezes
        HideVolumeOsd();

        // a dica do botão da bandeja volta a valer quando o cartão sai de cena
        // a dica nao volta aqui: quem a devolve e a saida do mouse da seta
    }
}
