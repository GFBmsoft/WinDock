using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinDock.Services;
using static WinDock.Interop.Native;

namespace WinDock;

/// <summary>
/// Painel de preferencias. Escreve direto no DockConfig, que notifica a dock —
/// por isso cada slider ja aparece na tela enquanto esta sendo arrastado.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly DockConfig _config;

    /// <summary>Espelha <see cref="DockConfig.TilingExcludedApps"/> pra tela — uma lista comum
    /// não avisa a UI sozinha quando ganha ou perde item, só quando é trocada inteira.</summary>
    private readonly ObservableCollection<string> _excludedApps = new();

    /// <summary>O mesmo espelho para os tamanhos guardados por app.</summary>
    private readonly ObservableCollection<FloatingSizeRow> _floatingSizes = new();

    public SettingsWindow(DockConfig config)
    {
        _config = config;
        InitializeComponent();
        DataContext = config;

        foreach (var name in _config.TilingExcludedApps) _excludedApps.Add(name);
        LoadTrayNames();
        LoadMediaApps();
        LoadPanelOrder();
        ExcludedAppsList.ItemsSource = _excludedApps;

        FloatingSizesList.ItemsSource = _floatingSizes;
        LoadFloatingSizes();

        // o "iniciar com o Windows" mora no registro, nao no config.json.
        // A flag existe porque marcar a caixa dispara o mesmo evento do clique: sem ela,
        // so de abrir o painel a chave Run seria reescrita com o caminho do executavel
        // atual — e abrir o painel de uma copia (a de Debug, por exemplo) trocaria o que
        // sobe com o Windows sem ninguem ter pedido.
        _loading = true;
        StartupBox.IsChecked = StartupService.IsEnabled;
        _loading = false;

        SourceInitialized += OnSourceInitialized;

        // marcar aquele interruptor la embaixo faz o WPF rolar ate ele; o painel tem que
        // abrir no comeco, e nao no meio da lista
        Loaded += (_, _) => Scroller.ScrollToTop();
    }

    /// <summary>
    /// A barra de titulo e do Windows, nao do WPF: sem avisar o DWM ela viria branca em
    /// cima de um painel escuro.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                              DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private bool _loading;

    /// <summary>Esc fecha o painel, como em qualquer caixa de diálogo do sistema.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.Set(StartupBox.IsChecked == true);
    }

    private void OnExcludedAppKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddExcludedApp();
        e.Handled = true;
    }

    private void OnAddExcludedApp(object sender, RoutedEventArgs e) => AddExcludedApp();

    /// <summary>Aceita tanto o nome do executável ("mspaint.exe") quanto um AppUserModelID
    /// ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") — só completa o ".exe" no primeiro
    /// caso; um AUMID sempre tem "!" no meio e nunca deveria ganhar essa extensão colada.</summary>
    private void AddExcludedApp()
    {
        var name = ExcludedAppBox.Text.Trim();
        if (name.Length == 0) return;
        if (!name.Contains('!') && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";

        ExcludedAppBox.Clear();
        if (_excludedApps.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return;

        _excludedApps.Add(name);
        _config.TilingExcludedApps.Add(name);
        _config.NotifyTilingExcludedAppsChanged();
    }

    private void OnRemoveExcludedApp(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not string name) return;

        _excludedApps.Remove(name);
        _config.TilingExcludedApps.Remove(name);
        _config.NotifyTilingExcludedAppsChanged();
    }

    // ── tamanho das janelas flutuantes ───────────────────────

    /// <summary>Uma linha da lista: o app e o tamanho já formatado pra tela.</summary>
    private sealed record FloatingSizeRow(string App, string Size);

    /// <summary>
    /// Mostra o que o mosaico guardou sozinho, e é a única forma de desfazer sem editar o
    /// <c>config.json</c> na mão: redimensionar de novo só troca o tamanho por outro, nunca
    /// devolve o app ao padrão de 60% da tela.
    /// </summary>
    private void LoadFloatingSizes()
    {
        _floatingSizes.Clear();

        foreach (var (app, size) in _config.FloatingSizes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            _floatingSizes.Add(new FloatingSizeRow(app, $"{size.Width}×{size.Height}"));

        // a legenda do vazio some assim que existe um item: uma lista vazia sem explicação
        // parece defeito, e a mesma explicação sobrando embaixo de dez itens é ruído
        FloatingSizesEmpty.Visibility = _floatingSizes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRemoveFloatingSize(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FloatingSizeRow row) return;

        _config.FloatingSizes.Remove(row.App);
        _config.Save();
        LoadFloatingSizes();
    }

    private void OnClearFloatingSizes(object sender, RoutedEventArgs e)
    {
        if (_config.FloatingSizes.Count == 0) return;

        _config.FloatingSizes.Clear();
        _config.Save();
        LoadFloatingSizes();
    }

    // ── ordem dos itens da barra ─────────────────────────────

    private sealed record PanelOrderRow(int Position, string Key, string Name);

    /// <summary>
    /// A ordem atual: o que está guardado, mais o que a barra tem e a configuração ainda não
    /// cita — item novo aparece no fim, igualzinho ao que a barra faz ao desenhar.
    /// </summary>
    private List<string> CurrentPanelOrder()
    {
        var conhecidos = PanelWindow.PanelItems.Select(i => i.Key).ToList();

        var ordem = _config.PanelOrder
            .Where(k => conhecidos.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        ordem.AddRange(conhecidos.Where(k => !ordem.Contains(k, StringComparer.OrdinalIgnoreCase)));
        return ordem;
    }

    private void LoadPanelOrder()
    {
        var nomes = PanelWindow.PanelItems.ToDictionary(i => i.Key, i => i.Name);

        PanelOrderList.ItemsSource = CurrentPanelOrder()
            .Select((k, i) => new PanelOrderRow(i + 1, k, nomes.TryGetValue(k, out var n) ? n : k))
            .ToList();
    }

    private void OnPanelItemUp(object sender, RoutedEventArgs e) => MovePanelItem(sender, -1);
    private void OnPanelItemDown(object sender, RoutedEventArgs e) => MovePanelItem(sender, +1);

    /// <summary>
    /// Troca o item de lugar com o vizinho e grava a lista inteira.
    ///
    /// Grava tudo, e não só a diferença, porque a lista guardada passa a ser a ordem completa
    /// — inclusive dos itens que ainda estavam implícitos. Sem isso, mover um item deixaria
    /// os outros à mercê da ordem do XAML e o resultado mudaria a cada versão.
    /// </summary>
    private void MovePanelItem(object sender, int passo)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;

        var ordem = CurrentPanelOrder();
        var de = ordem.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        var para = de + passo;

        if (de < 0 || para < 0 || para >= ordem.Count) return;

        (ordem[de], ordem[para]) = (ordem[para], ordem[de]);

        _config.PanelOrder = ordem;
        _config.Save();
        _config.NotifyPanelOrderChanged();

        LoadPanelOrder();
    }

    private void OnResetPanelOrder(object sender, RoutedEventArgs e)
    {
        _config.PanelOrder = new List<string>();
        _config.Save();
        _config.NotifyPanelOrderChanged();

        LoadPanelOrder();
    }

    // ── programas na barra de mídia ──────────────────────────

    /// <summary>
    /// Uma linha da lista: um programa de mídia e se ele pode ocupar a barra.
    ///
    /// Marcar grava na hora e a barra reavalia qual sessão seguir — sem botão de confirmar,
    /// como o resto do painel.
    /// </summary>
    private sealed class MediaAppRow(DockConfig config, string appId)
    {
        public string AppId { get; } = appId;
        public string Name { get; } = MediaService.FriendlyName(appId);

        public bool Allowed
        {
            get => config.MediaApps.Contains(AppId, StringComparer.OrdinalIgnoreCase);
            set
            {
                config.MediaApps.RemoveAll(a => a.Equals(AppId, StringComparison.OrdinalIgnoreCase));
                if (value) config.MediaApps.Add(AppId);

                config.Save();
                config.NotifyMediaAppsChanged();
            }
        }
    }

    /// <summary>
    /// Monta a lista com os programas de mídia vistos nesta sessão, mais os que já estavam
    /// marcados — senão um programa fechado sumiria da lista sem jeito de desmarcá-lo.
    /// </summary>
    private void LoadMediaApps()
    {
        var apps = MediaService.KnownApps
            .Concat(_config.MediaApps)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(MediaService.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        MediaAppsList.ItemsSource = apps.Select(a => new MediaAppRow(_config, a)).ToList();
        MediaAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── apelidos dos ícones sem nome ─────────────────────────

    /// <summary>
    /// Uma linha da lista "Ícones sem nome": o desenho, os candidatos e o apelido.
    ///
    /// O apelido é gravado a cada tecla — não há botão de confirmar, e a barra reflete a
    /// mudança na leitura seguinte. Guardar por assinatura do desenho é o que permite
    /// reconhecer o mesmo ícone depois; veja <see cref="DockConfig.TrayNames"/>.
    /// </summary>
    private sealed class TrayNameRow(DockConfig config, TrayIcon icon, string suggestion)
        : System.ComponentModel.INotifyPropertyChanged
    {
        public string Signature { get; } = icon.Signature;
        public System.Windows.Media.Imaging.BitmapSource? Image { get; } = icon.Image;
        public string Suggestion { get; } = suggestion;

        private string _alias = config.TrayNames.TryGetValue(icon.Signature, out var a) ? a : string.Empty;
        public string Alias
        {
            get => _alias;
            set
            {
                if (_alias == value) return;
                _alias = value;

                if (string.IsNullOrWhiteSpace(value)) config.TrayNames.Remove(Signature);
                else config.TrayNames[Signature] = value.Trim();

                config.Save();
                PropertyChanged?.Invoke(this, new(nameof(Alias)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Monta a lista de ícones que precisam de apelido: os que a automação não nomeou, mais
    /// os que já foram batizados (senão não haveria como reeditar o texto).
    ///
    /// Usa a bandeja em cache, e não uma leitura nova: ler de verdade levanta meio shell e
    /// passa de meio segundo. Se a bandeja ainda não foi lida nesta sessão, o cartão
    /// simplesmente não aparece.
    /// </summary>
    private void LoadTrayNames()
    {
        var icons = TrayService.Cached
            .Where(i => !string.IsNullOrEmpty(i.Signature))
            .Where(i => i.Name.StartsWith("Ícone da bandeja") || _config.TrayNames.ContainsKey(i.Signature))
            .ToList();

        if (icons.Count == 0) return;

        var suggestion = Candidates(TrayService.Cached);
        TrayNamesList.ItemsSource = icons.Select(i => new TrayNameRow(_config, i, suggestion)).ToList();
        TrayNamesCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Um palpite de quem pode ser o ícone anônimo: programas rodando agora que têm registro
    /// de ícone de bandeja e cujo nome não aparece na lista já lida.
    ///
    /// Não identifica sozinho — testado, sobram uns três candidatos —, mas encurta muito a
    /// escolha: em vez de adivinhar, a pessoa reconhece o nome do próprio programa. O
    /// processo do botão não serve para isso: todos os ícones do painel pertencem ao
    /// explorer, medido.
    /// </summary>
    private static string Candidates(IReadOnlyList<TrayIcon> icons)
    {
        try
        {
            var vivos = System.Diagnostics.Process.GetProcesses()
                .Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => System.IO.Path.GetFileName(f)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (key is null) return string.Empty;

            var nomes = icons.Select(i => i.Name).ToList();
            var achados = new List<string>();

            foreach (var sub in key.GetSubKeyNames())
            {
                using var item = key.OpenSubKey(sub);
                if (item?.GetValue("ExecutablePath") is not string path) continue;

                var exe = System.IO.Path.GetFileName(path);
                if (!vivos.Contains(exe) || achados.Contains(exe, StringComparer.OrdinalIgnoreCase)) continue;

                // já identificado na lista pela dica de mouse: não é candidato
                var dica = item.GetValue("InitialTooltip") as string;
                if (!string.IsNullOrWhiteSpace(dica) &&
                    nomes.Any(n => n.Contains(dica!, StringComparison.OrdinalIgnoreCase) ||
                                   dica!.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;

                achados.Add(exe);
            }

            return achados.Count == 0 ? string.Empty : "talvez: " + string.Join(", ", achados);
        }
        catch { return string.Empty; }
    }

    private void OnReset(object sender, RoutedEventArgs e) => _config.ResetAppearance();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _config.Save();
        base.OnClosed(e);
    }
}
