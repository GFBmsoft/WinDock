using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinDock.Services;

/// <summary>
/// O papel de parede pela dock (pedido de 16/11/2026, para não precisar do Wallpaper Engine): lê a
/// pasta escolhida, mostra o arquivo atual e troca de tempos em tempos.
///
/// Os dois tipos vão por caminhos diferentes, de propósito. A imagem parada vira o papel de
/// parede do próprio Windows (<c>SPI_SETDESKWALLPAPER</c>): custa zero, vale para a tela de
/// bloqueio de quem a usa assim, e continua lá com a dock fechada. O animado (vídeo ou GIF) toca
/// atrás dos ícones pelo <see cref="AnimatedWallpaper"/>, que só existe enquanto há um animado na
/// tela. Fechar a dock ou desligar a opção tira o animado e deixa à mostra o papel do Windows.
///
/// Com "Dar vida às imagens paradas" (17/11/2026), a imagem parada vira o papel do Windows, como
/// antes, e o <see cref="AnimatedWallpaper"/> põe por cima dela só os efeitos da
/// <see cref="LivingScene"/>, numa janela transparente. Fechar a dock só tira o movimento.
///
/// Mora na thread da interface, como as outras peças da dock; o que demora (converter um GIF,
/// avisar o Windows da troca do papel estático) corre fora dela.
/// </summary>
internal sealed class WallpaperService : IDisposable
{
    /// <summary>O serviço aberto, para o botão "Próximo" das Configurações.</summary>
    public static WallpaperService? Running { get; private set; }

    /// <summary>O que está na tela agora, em texto, para as Configurações.</summary>
    public static string Status { get; private set; } = string.Empty;

    public static event Action? StatusChanged;

    private readonly DockConfig _config;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _rotation;
    private readonly Random _random = new();
    private AnimatedWallpaper? _animated;
    private string? _shown;
    private int _generation;

    public WallpaperService(DockConfig config, Dispatcher ui)
    {
        _config = config;
        _ui = ui;
        _rotation = new DispatcherTimer(DispatcherPriority.Background, ui);
        _rotation.Tick += (_, _) => Advance();
        _config.PropertyChanged += OnConfigChanged;
        Running = this;
        Apply();
    }

    /// <summary>Os arquivos da pasta que entram, na ordem da rotação.</summary>
    public static List<string> Files(string folder, WallpaperKind kind)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
        try { return WallpaperRules.Filter(Directory.EnumerateFiles(folder), kind); }
        catch (Exception ex)
        {
            Log.Write($"papel de parede: não deu para ler a pasta {folder}: {ex.Message}");
            return new();
        }
    }

    private List<string> Files() => Files(_config.WallpaperFolder, _config.WallpaperKind);

    /// <summary>Troca já para o próximo da lista e recomeça a contagem da rotação.</summary>
    public void Advance()
    {
        if (!_config.Wallpaper) return;
        var proximo = WallpaperRules.Next(Files(), _shown ?? _config.WallpaperCurrent, _config.WallpaperShuffle, _random);
        if (proximo is null) { Apply(); return; }

        Show(proximo);
        RestartRotation();
        _config.Save();   // a dock reaberta continua do mesmo papel
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DockConfig.Wallpaper):
            case nameof(DockConfig.WallpaperFolder):
            case nameof(DockConfig.WallpaperKind):
                Apply();
                break;
            case nameof(DockConfig.WallpaperCurrent):
                // escolhido na lista das Configurações; quando é a própria rotação que muda, já é o _shown
                if (_config.Wallpaper && !Same(_config.WallpaperCurrent, _shown) && File.Exists(_config.WallpaperCurrent))
                {
                    Show(_config.WallpaperCurrent);
                    RestartRotation();
                }
                break;
            case nameof(DockConfig.WallpaperRotate):
            case nameof(DockConfig.WallpaperRotateMinutes):
                RestartRotation();
                break;
            case nameof(DockConfig.WallpaperPauseOnBattery):
                _animated?.SetPauseOnBattery(_config.WallpaperPauseOnBattery);
                break;
            case nameof(DockConfig.WallpaperLiving):
                // só a imagem parada muda: com vida ela vai para as janelas da dock, sem, fica só a do Windows
                if (_config.Wallpaper && _shown is not null && WallpaperRules.IsStatic(_shown)) Show(_shown);
                break;
        }
    }

    /// <summary>Põe na tela o que a configuração pede agora: nada, o atual ou o primeiro da pasta.</summary>
    private void Apply()
    {
        if (!_config.Wallpaper)
        {
            Clear("Desligado");
            return;
        }

        var lista = Files();
        if (lista.Count == 0)
        {
            var pasta = _config.WallpaperFolder;
            Clear(string.IsNullOrWhiteSpace(pasta) ? "Escolha a pasta dos papéis de parede"
                : !Directory.Exists(pasta) ? $"A pasta {pasta} não existe"
                : $"Nenhum papel de parede {Describe(_config.WallpaperKind)} em {pasta}");
            return;
        }

        var i = WallpaperRules.IndexOf(lista, _config.WallpaperCurrent);
        var atual = lista[i < 0 ? 0 : i];
        if (!Same(atual, _shown)) Show(atual);
        RestartRotation();
    }

    private void Clear(string status)
    {
        _generation++;
        _shown = null;
        _animated?.Stop();
        _rotation.Stop();
        SetStatus(status);
    }

    private async void Show(string path)
    {
        _shown = path;
        if (!Same(_config.WallpaperCurrent, path)) _config.WallpaperCurrent = path;
        var vez = ++_generation;
        var nome = Path.GetFileName(path);

        try
        {
            if (WallpaperRules.IsStatic(path))
            {
                SetStatus($"Aplicando {nome}…");
                await Task.Run(() => SetWindowsWallpaper(path));
                if (vez != _generation) return;

                // o que estava na tela sai já: efeitos de outra imagem, ou um vídeo. Os efeitos
                // desta entram por cima do papel do Windows, que agora já é ela
                _animated?.Stop();

                if (_config.WallpaperLiving)
                {
                    var ajuste = WindowsFit();
                    if (ajuste is null)
                    {
                        Log.Write($"papel de parede: '{nome}' sem vida — o Windows está em Lado a lado ou Estender");
                        SetStatus($"Papel de parede do Windows: {nome}. Para dar vida, o ajuste dele precisa ser " +
                                  "Preencher, Ajustar, Ampliar ou Centralizar (em Lado a lado e Estender, a dock não sabe onde cada vela foi parar)");
                        return;
                    }

                    SetStatus($"Dando vida a {nome}…");
                    var telas = AnimatedWallpaper.MonitorSizes();
                    LivingImage imagem;
                    try { imagem = await Task.Run(() => LivingImage.LoadAsync(path, telas, ajuste.Value)); }
                    catch (Exception ex)
                    {
                        Log.Write($"papel de parede: não deu para dar vida a '{nome}'", ex);
                        if (vez == _generation) SetStatus($"Papel de parede do Windows: {nome} (sem vida: {ex.Message})");
                        return;
                    }
                    if (vez != _generation) return;

                    _animated ??= CreateAnimated();
                    _animated.ShowLiving(imagem, nome, _config.WallpaperPauseOnBattery, ajuste.Value);
                    Log.Write($"papel de parede: '{nome}' com vida ({imagem.Map.Lights.Count} luzes, " +
                              $"céu em {imagem.Map.SkyShare:P0} da imagem, {imagem.Particles.Count} brasas e grãos" +
                              (imagem.Figures.Count > 0 ? $", figuras: {string.Join(", ", imagem.Figures.Select(f => f.Name))}" : "") + ")");
                    return;
                }

                // o animado saiu (acima) só depois do estático no lugar: na ordem contrária, o
                // papel velho do Windows aparecia por um instante entre os dois
                Log.Write($"papel de parede: '{nome}' como papel de parede do Windows");
                SetStatus($"Papel de parede do Windows: {nome}");
                return;
            }

            var video = path;
            if (WallpaperRules.IsGif(path))
            {
                SetStatus($"Preparando {nome} — o GIF vira vídeo só na primeira vez…");
                video = await Task.Run(() => GifVideo.CachedAsync(path));
                if (vez != _generation) return;
            }

            _animated ??= CreateAnimated();
            _animated.Play(video, nome, _config.WallpaperPauseOnBattery);
            Log.Write($"papel de parede: animado '{nome}'");
        }
        catch (Exception ex)
        {
            Log.Write($"papel de parede: não deu para usar '{nome}'", ex);
            if (vez == _generation) SetStatus($"Não deu para usar {nome}: {ex.Message}");
        }
    }

    /// <summary>O ajuste do papel de parede do Windows, que é onde os efeitos precisam cair.</summary>
    private static WallpaperFit? WindowsFit()
    {
        using var chave = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return LivingRules.FitOf(chave?.GetValue("WallpaperStyle") as string, chave?.GetValue("TileWallpaper") as string);
    }

    private AnimatedWallpaper CreateAnimated()
    {
        var a = new AnimatedWallpaper();
        a.StatusChanged += texto => _ui.BeginInvoke(() =>
        {
            // um aviso atrasado do animado não passa por cima do estático que entrou depois
            if (_shown is not null && (WallpaperRules.IsAnimated(_shown) || _config.WallpaperLiving)) SetStatus(texto);
        });
        return a;
    }

    private void RestartRotation()
    {
        _rotation.Stop();
        if (!_config.Wallpaper || !_config.WallpaperRotate || Files().Count < 2) return;
        _rotation.Interval = TimeSpan.FromMinutes(Math.Max(1, _config.WallpaperRotateMinutes));
        _rotation.Start();
    }

    private static void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    public static string Describe(WallpaperKind kind) => kind switch
    {
        WallpaperKind.Animated => "animado (.mp4, .gif…)",
        WallpaperKind.Static => "estático (.jpg, .png…)",
        _ => "animado ou estático",
    };

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// O papel de parede do próprio Windows, gravado no perfil, como se escolhido nas
    /// Configurações dele. Fora da thread da interface: o aviso da troca vai a todas as janelas
    /// abertas, e uma travada seguraria quem avisa.
    /// </summary>
    private static void SetWindowsWallpaper(string path)
    {
        if (!SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        _config.PropertyChanged -= OnConfigChanged;
        _rotation.Stop();
        _animated?.Dispose();
        _animated = null;
        if (Running == this) Running = null;
    }

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoString(uint action, uint param, string value, uint flags);
}
