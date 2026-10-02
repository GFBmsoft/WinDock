using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace WinDock.Services;

/// <summary>O Wi-Fi como o cartão da barra precisa ver: o rádio, a rede de agora e o sinal.</summary>
public sealed record WifiState(bool HasRadio, bool On, string Ssid, int Bars, bool Internet)
{
    public static readonly WifiState None = new(false, false, "", 0, false);

    public bool Connected => Ssid.Length > 0;
}

/// <summary>Uma rede ao alcance, já sem as repetições de cada ponto de acesso.</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secure, bool Connected, bool Known, WiFiAvailableNetwork Raw)
{
    public string Detail => Connected ? "conectado" : Known ? "salva" : Secure ? "protegida" : "aberta";
}

/// <summary>
/// O Wi-Fi sem o painel do Windows.
///
/// <para><b>Duas camadas, com permissões diferentes</b> — conferido em 02/10/2026, no 26H2 com
/// a localização desligada:</para>
/// <list type="bullet">
/// <item>o rádio (<c>Windows.Devices.Radios</c>) e a rede de agora, com o sinal
/// (<c>NetworkInformation</c>), respondem sempre;</item>
/// <item>as redes ao alcance (<c>WiFiAdapter</c>) respondem <c>DeniedBySystem</c>: desde o
/// 24H2, listar redes é tratado como saber onde a máquina está, e exige "Permitir que apps da
/// área de trabalho acessem sua localização". O <c>netsh wlan show networks</c> recusa igual.</item>
/// </list>
/// <para>Por isso o cartão mostra a rede de agora sempre, e a lista só com a permissão — sem ela,
/// um botão que leva direto à página da localização, dizendo o porquê.</para>
/// </summary>
public static class WifiService
{
    public const string LocationSettings = "ms-settings:privacy-location";
    public const string WifiSettings = "ms-settings:network-wifi";

    public static async Task<WifiState> ReadAsync()
    {
        try
        {
            var radio = await WifiRadio();
            if (radio is null) return WifiState.None;

            var ligado = radio.State == RadioState.On;
            if (!ligado) return new WifiState(true, false, "", 0, false);

            // A rede de internet primeiro; com cabo e Wi-Fi juntos ela é o cabo, e aí vale o
            // primeiro perfil sem fio que estiver de pé.
            var perfil = NetworkInformation.GetInternetConnectionProfile();
            if (perfil is null || !perfil.IsWlanConnectionProfile)
                perfil = NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(p => p.IsWlanConnectionProfile &&
                                         p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);

            if (perfil is null) return new WifiState(true, true, "", 0, false);

            var ssid = perfil.WlanConnectionProfileDetails?.GetConnectedSsid();
            if (string.IsNullOrEmpty(ssid)) ssid = perfil.ProfileName;

            return new WifiState(true, true, ssid ?? "", perfil.GetSignalBars() ?? 0,
                                 perfil.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess);
        }
        catch (Exception ex)
        {
            Log.Write("leitura do Wi-Fi falhou", ex);
            return WifiState.None;
        }
    }

    public static async Task SetOn(bool on)
    {
        try
        {
            var radio = await WifiRadio();
            if (radio is not null) await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Write("não foi possível mudar o rádio do Wi-Fi", ex);
        }
    }

    private static async Task<Radio?> WifiRadio()
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
        return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
    }

    /// <summary>
    /// As redes ao alcance, a de agora primeiro e as outras pelo sinal. Nulo quando o Windows não
    /// deixa listar — é a localização desligada, e quem chama mostra o caminho para ligá-la.
    /// </summary>
    public static async Task<IReadOnlyList<WifiNetwork>?> ScanAsync(string connectedSsid)
    {
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return null;

            var adaptador = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
            if (adaptador is null) return Array.Empty<WifiNetwork>();

            await adaptador.ScanAsync();

            var salvas = NetworkInformation.GetConnectionProfiles()
                .Where(p => p.IsWlanConnectionProfile)
                .Select(p => p.ProfileName)
                .ToHashSet(StringComparer.Ordinal);

            // cada ponto de acesso aparece como uma rede: o mesmo nome em 2,4 e 5 GHz, ou em
            // três roteadores da casa, vira uma linha só, com o melhor sinal
            return adaptador.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNetwork(
                    n.Ssid,
                    n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not
                        (Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211 or
                         Windows.Networking.Connectivity.NetworkAuthenticationType.None),
                    n.Ssid == connectedSsid,
                    salvas.Contains(n.Ssid),
                    n))
                .OrderByDescending(n => n.Connected)
                .ThenByDescending(n => n.Known)
                .ThenByDescending(n => n.Bars)
                .Take(12)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write("não foi possível listar as redes Wi-Fi", ex);
            return null;
        }
    }

    /// <summary>
    /// Conecta a uma rede salva ou aberta. Falso quando ela pede o que a dock não tem — a senha de
    /// uma rede nova —, e aí quem chama abre a lista do Windows, que sabe perguntar.
    /// </summary>
    public static async Task<bool> ConnectAsync(WifiNetwork rede)
    {
        try
        {
            var adaptador = (await WiFiAdapter.FindAllAdaptersAsync()).FirstOrDefault();
            if (adaptador is null) return false;

            var resultado = await adaptador.ConnectAsync(rede.Raw, WiFiReconnectionKind.Automatic);
            Log.Trace($"Wi-Fi: conectar a {rede.Ssid} → {resultado.ConnectionStatus}");
            return resultado.ConnectionStatus == WiFiConnectionStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Write($"não foi possível conectar a {rede.Ssid}", ex);
            return false;
        }
    }

    /// <summary>O desenho das ondas pelo número de barras, os mesmos da barra do Windows.</summary>
    /// Desligado é o desenho cheio, apagado pela opacidade (como o do bluetooth); ligado sem rede,
    /// as ondas com o ✕.
    public static string Glyph(WifiState s) =>
        !s.On ? ""
        : !s.Connected ? ""
        : GlyphForBars(s.Bars);

    public static string GlyphForBars(int bars) =>
        bars >= 4 ? "" : bars == 3 ? "" : bars == 2 ? "" : "";
}
