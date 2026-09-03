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

    public SettingsWindow(DockConfig config)
    {
        _config = config;
        InitializeComponent();
        DataContext = config;

        foreach (var name in _config.TilingExcludedApps) _excludedApps.Add(name);
        ExcludedAppsList.ItemsSource = _excludedApps;

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

    private void OnReset(object sender, RoutedEventArgs e) => _config.ResetAppearance();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _config.Save();
        base.OnClosed(e);
    }
}
