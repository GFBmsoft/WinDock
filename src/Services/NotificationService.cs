using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinDock.Services;

/// <summary>Uma notificação da Central, como o cartão do sino precisa ver.</summary>
public sealed record NotificationItem(
    uint Id,
    string App,
    string Title,
    string Body,
    DateTime Time,
    ImageSource? Icon)
{
    public bool HasBody => Body.Length > 0;
    public bool HasIcon => Icon is not null;

    /// <summary>"16:29" se foi hoje, "ontem" ou "28/09" se não — o mesmo jeito da Central.</summary>
    public string When
    {
        get
        {
            var hoje = DateTime.Today;
            if (Time.Date == hoje) return Time.ToString("HH:mm");
            if (Time.Date == hoje.AddDays(-1)) return "ontem";
            return Time.ToString("dd/MM");
        }
    }
}

/// <summary>
/// As notificações que estão na Central do Windows, de qualquer app — inclusive as de site, que o
/// Chrome e o Edge entregam ao Windows como notificação dele (WhatsApp Web, Gmail).
///
/// A peça é o <c>UserNotificationListener</c>. A documentação o trata como coisa de app de loja,
/// com a capacidade <c>userNotificationListener</c> no manifesto, mas conferido em 01/10/2026: num
/// processo sem pacote, como a dock, o <c>GetAccessStatus</c> responde <c>Allowed</c> e a lista vem
/// com app, hora e textos.
///
/// <para><b>Lista vazia não quer dizer que a Central está vazia.</b> Com as notificações desligadas
/// no Windows inteiro (Configurações › Sistema › Notificações), ou só as daquele app, o Windows nem
/// cria a notificação — e a lista volta com zero, sem erro nenhum. Foi o que fez o primeiro teste
/// parecer bloqueio.</para>
///
/// <para>O que <b>não</b> dá: acionar a notificação (o clique que abre a conversa certa) nem os
/// botões dela — isso fica com quem a mandou. Dispensar dá, e some também da Central.</para>
/// </summary>
public sealed class NotificationService
{
    private UserNotificationListener? _listener;

    /// <summary>O ícone de cada app, lido uma vez: são os mesmos poucos apps o dia inteiro.</summary>
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O Windows deixou ler. Falso numa versão sem a API ou com o acesso negado em Configurações ›
    /// Privacidade › Notificações — aí o sino volta a ser só o atalho para a Central.
    /// </summary>
    public bool Available { get; private set; }

    /// <summary>
    /// O Windows avisa sozinho quando uma notificação chega ou sai. Falso quando a assinatura foi
    /// recusada — quem usa o serviço passa a perguntar de tempos em tempos.
    /// </summary>
    public bool Notifies { get; private set; }

    /// <summary>Uma notificação chegou ou saiu. Vem de thread do WinRT — quem ouve marshalla.</summary>
    public event Action? Changed;

    public void Start()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            Available = _listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Write("as notificações do Windows não podem ser lidas", ex);
            Available = false;
            return;
        }

        if (!Available)
        {
            Log.Write("as notificações do Windows não podem ser lidas: acesso negado em Privacidade");
            return;
        }

        // Para app de loja este evento exige tarefa de fundo; sem pacote, pode ser recusado. A
        // recusa não é erro: a leitura funciona igual, só que por relógio.
        try
        {
            _listener.NotificationChanged += (_, _) => Changed?.Invoke();
            Notifies = true;
        }
        catch (Exception ex)
        {
            // a recusa vem sem mensagem: o que identifica é o código
            Log.Write($"o Windows não avisa quando chega notificação (0x{ex.HResult:X8}); a dock vai perguntar de cinco em cinco segundos");
            Notifies = false;
        }
    }

    /// <summary>As notificações da Central, da mais nova para a mais velha.</summary>
    public async Task<IReadOnlyList<NotificationItem>> ReadAsync()
    {
        if (!Available || _listener is null) return Array.Empty<NotificationItem>();

        IReadOnlyList<UserNotification> lidas;
        try
        {
            lidas = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            Log.Write("leitura das notificações falhou", ex);
            return Array.Empty<NotificationItem>();
        }

        var itens = new List<NotificationItem>(lidas.Count);
        foreach (var n in lidas)
        {
            try
            {
                itens.Add(await ToItem(n));
            }
            catch (Exception ex)
            {
                // uma notificação malformada não derruba a lista inteira
                Log.Write($"notificação {n.Id} ignorada", ex);
            }
        }

        return itens.OrderByDescending(i => i.Time).ToList();
    }

    private async Task<NotificationItem> ToItem(UserNotification n)
    {
        var app = "";
        var aumid = "";
        try
        {
            app = n.AppInfo.DisplayInfo.DisplayName;
            aumid = n.AppInfo.AppUserModelId;
        }
        catch { /* app que já foi desinstalado: fica sem nome */ }

        // O primeiro texto é o título, os outros o corpo. No Chrome o último costuma ser o
        // endereço do site ("web.whatsapp.com"), que é justamente o que diz de onde veio.
        var textos = new List<string>();
        var visual = n.Notification?.Visual;
        var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is not null)
            foreach (var t in binding.GetTextElements())
                if (!string.IsNullOrWhiteSpace(t.Text)) textos.Add(t.Text.Trim());

        var titulo = textos.Count > 0 ? textos[0] : app;
        var corpo = string.Join("\n", textos.Skip(1));

        return new NotificationItem(
            n.Id,
            string.IsNullOrEmpty(app) ? "Notificação" : app,
            titulo,
            corpo,
            n.CreationTime.LocalDateTime,
            await IconOf(n, aumid));
    }

    private async Task<ImageSource?> IconOf(UserNotification n, string aumid)
    {
        if (_icons.TryGetValue(aumid, out var guardado)) return guardado;

        ImageSource? icone = null;
        try
        {
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32));
            using var fluxo = await logo.OpenReadAsync();
            using var memoria = new MemoryStream();
            await fluxo.AsStreamForRead().CopyToAsync(memoria);
            memoria.Position = 0;

            var imagem = new BitmapImage();
            imagem.BeginInit();
            imagem.CacheOption = BitmapCacheOption.OnLoad;   // lê tudo agora; o fluxo vai fechar
            imagem.DecodePixelWidth = 32;
            imagem.StreamSource = memoria;
            imagem.EndInit();
            imagem.Freeze();
            icone = imagem;
        }
        catch { /* sem logo: comum nos apps sem pacote */ }

        // Os apps sem pacote (Chrome, PowerShell, o Explorer) não trazem logo pela notificação, mas
        // a pasta de aplicativos do shell conhece o mesmo AppUserModelID — é de lá que a dock tira
        // os ícones dela. Sem nenhum dos dois, o cartão mostra só o nome.
        if (icone is null && aumid.Length > 0) icone = IconService.Shell($@"shell:AppsFolder\{aumid}", 32);

        _icons[aumid] = icone;
        return icone;
    }

    /// <summary>Dispensa uma: some daqui e da Central do Windows.</summary>
    public void Remove(uint id)
    {
        try { _listener?.RemoveNotification(id); }
        catch (Exception ex) { Log.Write($"não foi possível dispensar a notificação {id}", ex); }
    }

    /// <summary>
    /// Limpa a Central inteira, como o "Limpar tudo" do Win+N — uma por uma.
    ///
    /// O <c>ClearNotifications</c>, que faria isto de uma vez, é recusado sem pacote com
    /// 0x80070490, o mesmo código do aviso de chegada; o <c>RemoveNotification</c> de cada uma
    /// passa. Conferido em 01/10/2026: com o atalho, a lista sumia na hora e voltava inteira na
    /// leitura seguinte, cinco segundos depois.
    /// </summary>
    public void Clear(IEnumerable<uint> ids)
    {
        foreach (var id in ids) Remove(id);
    }
}
