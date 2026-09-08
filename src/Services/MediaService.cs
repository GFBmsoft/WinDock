using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Media.Control;

namespace WinDock.Services;

/// <summary>O que está tocando agora, como a barra precisa ver.</summary>
public sealed record MediaInfo(
    string AppId,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool CanPlay,
    bool CanPause,
    bool CanNext,
    bool CanPrevious,
    TimeSpan Position,
    TimeSpan Duration,
    BitmapSource? Cover)
{
    /// <summary>Nada tocando: a barra esconde o item inteiro.</summary>
    public static readonly MediaInfo None =
        new("", "", "", "", false, false, false, false, false, TimeSpan.Zero, TimeSpan.Zero, null);

    public bool HasMedia => Title.Length > 0 || Artist.Length > 0;

    /// <summary>"Ashbringer — Subglacial", ou só o que existir.</summary>
    public string Summary => Artist.Length > 0 && Title.Length > 0 ? $"{Artist} — {Title}"
                           : Title.Length > 0 ? Title
                           : Artist;
}

/// <summary>
/// O que o Windows sabe sobre a mídia tocando, de qualquer programa.
///
/// É o mesmo papel que o D-Bus/MPRIS cumpre no Linux — a fonte que as extensões de barra do
/// GNOME usam para mostrar a música. No Windows a peça equivalente é o
/// <c>GlobalSystemMediaTransportControlsSessionManager</c>: ele enxerga qualquer programa que
/// publique mídia (Spotify, Chrome, VLC), entrega título, artista, álbum, capa e posição, e
/// aceita os comandos de reprodução. Sem API web, sem token, sem o programa estar em foco.
///
/// É WinRT, e isso <b>não</b> custou nada novo: o alvo do projeto já tinha ido para
/// <c>net8.0-windows10.0.19041.0</c> por causa do liga/desliga do bluetooth.
///
/// <para><b>Atualiza por evento, não por relógio.</b> A sessão avisa quando a faixa muda
/// (<c>MediaPropertiesChanged</c>) e quando play/pause mudam (<c>PlaybackInfoChanged</c>);
/// a única coisa que precisa de relógio é a posição da faixa, e só enquanto alguém estiver
/// olhando o card.</para>
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    /// <summary>Disparado quando a mídia muda. Vem de thread do WinRT — quem ouve marshalla.</summary>
    public event Action? Changed;

    public MediaInfo Current { get; private set; } = MediaInfo.None;

    public async Task Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach();

            // e tambem quando uma sessao nasce ou morre: com filtro ligado, a sessao do
            // Spotify pode aparecer sem nunca ser a "atual", e sem isto a barra so a veria
            // quando alguem clicasse nela
            _manager.SessionsChanged += (_, _) => Attach();
            Attach();
        }
        catch (Exception ex)
        {
            // máquina sem o serviço de mídia, ou versão do Windows sem a API: o item some da
            // barra e o resto da dock não fica sabendo
            Log.Write("não deu para ouvir a mídia do sistema", ex);
        }
    }

    /// <summary>
    /// Quais programas podem ocupar a barra. Vazio quer dizer "qualquer um".
    ///
    /// A lista é de <c>SourceAppUserModelId</c> — o Spotify se anuncia como
    /// <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>.
    /// </summary>
    public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Todo programa de mídia já visto, para as Configurações terem o que listar.
    ///
    /// Acumula em vez de refletir só o momento: a pessoa abre as opções quando quer, e a essa
    /// altura o programa que ela quer marcar pode já ter parado de tocar. E acumula **entre
    /// sessões** (<see cref="DockConfig.MediaAppsSeen"/>) — enquanto isto vivia só na memória, a
    /// lista voltava a zero a cada vez que a dock subia, e quem tinha tocado um vídeo de manhã
    /// abria as opções à tarde e encontrava a lista vazia, sem nada explicando o porquê.
    /// </summary>
    public static HashSet<string> KnownApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Onde os vistos são guardados. Fica nulo até a dock passar o config.</summary>
    private static DockConfig? _store;

    /// <summary>
    /// Liga a lista de vistos ao arquivo de configuração: carrega o que já estava lá e passa a
    /// gravar cada programa novo.
    /// </summary>
    public static void UseStore(DockConfig config)
    {
        _store = config;
        foreach (var app in config.MediaAppsSeen)
            if (!string.IsNullOrWhiteSpace(app)) KnownApps.Add(app);
    }

    /// <summary>Anota um programa recém-visto, gravando só quando ele é realmente novo — isto
    /// roda a cada mudança de sessão, e salvar o arquivo em toda troca de faixa seria escrever
    /// em disco por música.</summary>
    private static void Remember(string appId)
    {
        if (!KnownApps.Add(appId)) return;
        if (_store is null) return;

        _store.MediaAppsSeen.Add(appId);
        _store.Save();
        Log.Trace($"programa de mídia novo: '{appId}'");
    }

    /// <summary>
    /// Escolhe qual sessão a barra vai seguir e passa a ouvi-la.
    ///
    /// <para>O filtro age <b>aqui</b>, e não na hora de exibir, e isso é o que o torna útil:
    /// se ele apenas escondesse o item quando a sessão em foco não fosse permitida, um vídeo
    /// no navegador tomaria o lugar do Spotify e a barra ficaria vazia enquanto a música
    /// continuasse tocando. Filtrando na escolha, a barra segue o Spotify mesmo com o vídeo
    /// em primeiro plano.</para>
    ///
    /// <para>Entre as permitidas, prefere a que está tocando — com Spotify e navegador ambos
    /// liberados, a barra mostra quem está com som, não quem ficou pausado por último.</para>
    ///
    /// Os eventos são por sessão: sem soltar a anterior ficariam dois assinantes, e a barra
    /// pularia entre as duas mídias.
    /// </summary>
    private void Attach()
    {
        if (_disposed) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }

        _session = Choose();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }

        _ = Refresh();
    }

    private GlobalSystemMediaTransportControlsSession? Choose()
    {
        if (_manager is null) return null;

        var sessoes = _manager.GetSessions();

        // aproveita a passagem para anotar quem existe: é o que alimenta a lista das opções
        foreach (var s in sessoes)
            if (!string.IsNullOrEmpty(s.SourceAppUserModelId)) Remember(s.SourceAppUserModelId);

        if (Allowed.Count == 0) return _manager.GetCurrentSession();

        var permitidas = sessoes.Where(s => Allowed.Contains(s.SourceAppUserModelId ?? "")).ToList();
        if (permitidas.Count == 0) return null;

        return permitidas.FirstOrDefault(Tocando) ?? permitidas[0];
    }

    private static bool Tocando(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return false; }
    }

    /// <summary>Troca a lista de permitidos e reavalia qual sessão seguir, na hora.</summary>
    public void SetAllowed(IEnumerable<string> apps)
    {
        Allowed.Clear();
        foreach (var a in apps) if (!string.IsNullOrWhiteSpace(a)) Allowed.Add(a.Trim());

        Attach();
    }

    /// <summary>
    /// O nome que a pessoa reconhece, a partir do identificador do programa.
    ///
    /// O catálogo de aplicativos já resolve AUMID para nome ("Spotify"); quando não resolve —
    /// programas Win32 se anunciam pelo executável — sobra limpar o que dá.
    /// </summary>
    public static string FriendlyName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";

        var achado = AppCatalog.All()
            .FirstOrDefault(a => a.IsAumid && a.Target.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (achado is not null) return achado.Name;

        // "Spotify.exe" -> "Spotify"; um AUMID sem correspondência fica com a parte final
        var nome = appId.Contains('!') ? appId[(appId.IndexOf('!') + 1)..] : appId;
        return nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nome[..^4] : nome;
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = Refresh();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = Refresh();

    /// <summary>Relê tudo da sessão atual e avisa quem estiver ouvindo.</summary>
    public async Task Refresh()
    {
        if (_disposed) return;

        var info = await Read();

        // avisa só quando algo mudou de verdade: a posição da faixa anda sozinha e dispararia
        // o evento a cada segundo, redesenhando a barra à toa
        var mudou = info.Title != Current.Title || info.Artist != Current.Artist ||
                    info.IsPlaying != Current.IsPlaying || info.HasMedia != Current.HasMedia;

        Current = info;
        if (mudou) Changed?.Invoke();
    }

    /// <summary>
    /// Onde a faixa está **agora**, e não onde ela estava quando o programa contou.
    ///
    /// A <c>Position</c> da API é um retrato tirado no instante <c>LastUpdatedTime</c>, e cabe a
    /// quem lê somar o tempo decorrido desde então. O Spotify reescreve a timeline tantas vezes
    /// por minuto que o retrato parece um relógio, e por isso a barra parecia certa; o Chrome
    /// publica **uma vez** e nunca mais — medido com um vídeo tocando: três leituras em 6 s,
    /// `Position` parada em 00:01:01 e `LastUpdatedTime` no mesmo milissegundo nas três. Sem
    /// esta conta, a barra do navegador ficava congelada onde o vídeo estava quando começou.
    ///
    /// Pausado não anda: o retrato é justamente o ponto onde parou. O teto em <c>EndTime</c>
    /// evita a barra passar do fim quando a faixa acaba e o programa demora a avisar.
    /// </summary>
    private static TimeSpan PositionNow(GlobalSystemMediaTransportControlsSessionTimelineProperties t,
                                        bool tocando, double taxa)
    {
        var pos = t.Position;
        if (!tocando || t.LastUpdatedTime == default) return pos;

        var desde = DateTimeOffset.Now - t.LastUpdatedTime;
        if (desde <= TimeSpan.Zero) return pos;

        pos += desde * (taxa <= 0 ? 1 : taxa);

        return t.EndTime > TimeSpan.Zero && pos > t.EndTime ? t.EndTime : pos;
    }

    private async Task<MediaInfo> Read()
    {
        var s = _session;
        if (s is null) return MediaInfo.None;

        try
        {
            var p = s.GetPlaybackInfo();
            var m = await s.TryGetMediaPropertiesAsync();
            var t = s.GetTimelineProperties();

            var tocando = p.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new MediaInfo(
                s.SourceAppUserModelId ?? "",
                m.Title ?? "",
                m.Artist ?? "",
                m.AlbumTitle ?? "",
                tocando,
                p.Controls.IsPlayEnabled,
                p.Controls.IsPauseEnabled,
                p.Controls.IsNextEnabled,
                p.Controls.IsPreviousEnabled,
                PositionNow(t, tocando, p.PlaybackRate ?? 1),
                t.EndTime,
                await Cover(m));
        }
        catch
        {
            // a sessão pode morrer entre o evento e a leitura — o programa fechou
            return MediaInfo.None;
        }
    }

    /// <summary>
    /// A capa do álbum, quando o programa publica uma.
    ///
    /// Vem como fluxo, não como arquivo: é preciso copiar para memória antes de virar imagem,
    /// e congelar (<c>Freeze</c>) para poder atravessar para a thread da interface.
    /// </summary>
    private static async Task<BitmapSource?> Cover(GlobalSystemMediaTransportControlsSessionMediaProperties m)
    {
        if (m.Thumbnail is null) return null;

        try
        {
            using var fluxo = await m.Thumbnail.OpenReadAsync();
            using var memoria = new MemoryStream();
            await fluxo.AsStreamForRead().CopyToAsync(memoria);
            memoria.Position = 0;

            var imagem = new BitmapImage();
            imagem.BeginInit();
            imagem.CacheOption = BitmapCacheOption.OnLoad;   // lê tudo agora; o fluxo vai fechar
            imagem.StreamSource = memoria;
            imagem.EndInit();
            imagem.Freeze();
            return imagem;
        }
        catch { return null; }
    }

    // ── comandos ────────────────────────────────────────────
    public Task TogglePlay() => Command(s => s.TryTogglePlayPauseAsync());
    public Task Next() => Command(s => s.TrySkipNextAsync());
    public Task Previous() => Command(s => s.TrySkipPreviousAsync());

    /// <summary>Pula para um ponto da faixa. A fração vem da barra de progresso do card.</summary>
    public Task Seek(double fraction)
    {
        var duracao = Current.Duration;
        if (duracao <= TimeSpan.Zero) return Task.CompletedTask;

        var alvo = TimeSpan.FromTicks((long)(duracao.Ticks * Math.Clamp(fraction, 0, 1)));
        return Command(s => s.TryChangePlaybackPositionAsync(alvo.Ticks));
    }

    private async Task Command(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> acao)
    {
        var s = _session;
        if (s is null) return;

        try { await acao(s); await Refresh(); }
        catch (Exception ex) { Log.Write("o comando de mídia não foi aceito", ex); }
    }

    public void Dispose()
    {
        _disposed = true;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session = null;
        }

        _manager = null;
    }
}
