using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>Extrai o icone do executavel uma vez por app e guarda em cache.</summary>
public static class IconService
{
    // ConcurrentDictionary porque o aquecimento (AppCatalog.Warm) preenche o cache numa
    // thread propria enquanto a interface le dele
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O melhor icone para um botao da dock, na ordem que da o resultado mais fiel:
    ///
    /// 1. o atalho, quando o alvo e um .lnk — e dele que vem o icone do perfil do Chrome;
    /// 2. a pasta de aplicativos do shell, quando a identidade e um AppUserModelID — e o
    ///    unico jeito de ter o icone de um app da Store, cujo executavel (o SystemSettings
    ///    das Configuracoes, por exemplo) traz so o icone generico de aplicativo;
    /// 3. o pacote, quando o executavel e de um app empacotado e nao traz desenho proprio;
    /// 4. a janela aberta (<paramref name="window"/>), quando o executavel nao traz desenho e
    ///    nao e de pacote — o rundll32 que hospeda as Opcoes da Internet;
    /// 5. o executavel.
    /// </summary>
    public static BitmapSource? ForApp(string id, string launchPath, nint window = 0) =>
        Fill(RawForApp(id, launchPath, window));

    private static BitmapSource? RawForApp(string id, string launchPath, nint window)
    {
        // Perfil do Chrome: o icone que o Chrome gera para o perfil vem antes ate do atalho. O
        // atalho de perfil e apagado e recriado pelo Chrome quando o perfil muda — o fixado
        // ficava apontando para um .lnk que nao existe mais, sem icone — e o perfil sem atalho
        // caia no chrome.exe, igual para todos.
        if (ChromeProfiles.ByAumid(id) is { IconPath.Length: > 0 } perfil &&
            Ico(perfil.IconPath) is { } doPerfil) return doPerfil;

        if (launchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return For(launchPath);

        if (IsAumid(id) && Shell($@"shell:AppsFolder\{id}") is { } fromShell) return fromShell;

        // App da Store pelo caminho do executável, e o executável não tem desenho para dar. Dois
        // jeitos de chegar aqui. O caminho envelheceu: a pasta do pacote leva a versão no nome e
        // some a cada atualização. Ou o executável existe mas não traz ícone nenhum gravado
        // dentro — o Photos.exe do Fotos (26/10/2026, EV03): o desenho do app mora no manifesto
        // do pacote, e o shell dava ao arquivo o ícone genérico de aplicativo. Nos dois casos o
        // identificador do pacote não muda, e por ele o shell entrega o ícone do app.
        //
        // O ícone próprio é perguntado antes do pacote de propósito: achar o pacote passa pelo
        // catálogo de apps, e montá-lo custa caro na subida da dock — o Terminal e o Spotify,
        // que têm desenho no executável, não precisam dele.
        if (!HasOwnIcon(launchPath) && PackagedApps.AumidOf(launchPath) is { } doPacote &&
            Shell($@"shell:AppsFolder\{doPacote}") is { } fromPackage) return fromPackage;

        // Executável sem desenho fora de pacote: um hospedeiro, que roda o app de outro arquivo.
        // O rundll32 é o exemplo — é ele quem abre o inetcpl.cpl e os outros itens do Painel de
        // Controle. O desenho do app quem tem é a janela, e é dela que a barra do Windows o tira.
        if (window != 0 && !HasOwnIcon(launchPath) && OfWindow(window) is { } daJanela) return daJanela;

        return For(launchPath);
    }

    /// <summary>
    /// O executável traz ícone gravado dentro? Sem nenhum, o que o shell devolve para ele é o
    /// desenho genérico de aplicativo — que não diz de que app é. Arquivo que não existe também
    /// responde não.
    /// </summary>
    private static bool HasOwnIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return ExtractIconCount(path, -1, 0, 0, 0) > 0; }
        catch { return false; }
    }

    /// <summary>
    /// O ícone que a janela anuncia, na ordem em que a barra do Windows procura: o que ela
    /// mesma diz (<c>WM_GETICON</c>), o da classe, o grande antes do pequeno. Sem nada, pergunta
    /// ao dono — as Opções da Internet não respondem por si, mas a janela invisível do rundll32
    /// que é dona delas carrega o ícone do item do Painel de Controle.
    ///
    /// O ícone é da janela, não nosso: copiado para a imagem e nunca destruído aqui.
    /// </summary>
    private static BitmapSource? OfWindow(nint hWnd)
    {
        for (var (w, passos) = (hWnd, 0); w != 0 && passos < 4; w = GetWindow(w, GW_OWNER), passos++)
        {
            var icone = Announced(w, ICON_BIG);
            if (icone == 0) icone = GetClassLongPtr(w, GCLP_HICON);
            if (icone == 0) icone = Announced(w, ICON_SMALL2);
            if (icone == 0) icone = GetClassLongPtr(w, GCLP_HICONSM);
            if (icone == 0) continue;

            // sem imagem, a janela trocou de ícone no meio da leitura: tenta o dono
            if (ToBitmap(icone) is { } result) return result;
        }

        return null;
    }

    /// <summary>A resposta da janela ao <c>WM_GETICON</c>. Com prazo curto: janela travada não
    /// responde, e quem pergunta é a thread da interface.</summary>
    private static nint Announced(nint hWnd, nint tipo) =>
        SendMessageTimeout(hWnd, WM_GETICON, tipo, 0, SMTO_ABORTIFHUNG, 100, out var icone) != 0 ? icone : 0;

    /// <summary>
    /// O desenho do icone esticado ate as bordas do quadrado, para todos ocuparem o mesmo
    /// espaco na dock.
    ///
    /// Cada fonte entrega o icone com uma folga transparente diferente em volta: o do
    /// executavel costuma vir justo, o da pasta de aplicativos (Store, Configuracoes) vem com
    /// um sexto de margem, e alguns programas desenham o simbolo pequeno no meio de um quadro
    /// vazio. Lado a lado, no mesmo tamanho de botao, uns pareciam o dobro dos outros — foi o
    /// pedido de 01/10/2026, "padronizar tamanho de icones". Recortar a folga e o que iguala;
    /// o desenho em si nao e tocado, so o quadro em volta dele.
    ///
    /// So recorta quando a folga e de verdade (mais de um vigesimo de cada lado): um icone que
    /// ja encosta nas bordas fica como veio, sem reamostragem. E o resultado e sempre
    /// quadrado, com o desenho centrado — um icone largo e baixo nao e esticado na altura.
    /// </summary>
    private static BitmapSource? Fill(BitmapSource? source)
    {
        if (source is null) return null;
        if (Filled.TryGetValue(source, out var pronto)) return pronto;

        var result = source;
        try { result = Trim(source) ?? source; }
        catch { /* formato estranho: melhor o icone com folga do que icone nenhum */ }

        Filled.AddOrUpdate(source, result);
        return result;
    }

    /// <summary>Um por icone de origem: o mesmo app aparece fixado e aberto ao mesmo tempo.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> Filled = new();

    private static BitmapSource? Trim(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        if (w < 8 || h < 8) return null;

        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        // a caixa do que se ve: alfa abaixo de 24 e sombra ou antisserrilhado, nao desenho
        int left = w, top = h, right = -1, bottom = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < 24) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        if (right < 0) return null;   // todo transparente

        int cw = right - left + 1, ch = bottom - top + 1;
        var lado = Math.Max(cw, ch);
        if (lado >= Math.Max(w, h) * 0.9) return null;   // ja justo: nada a recortar

        // quadrado do tamanho do lado maior, com o desenho no meio
        var saida = new byte[lado * lado * 4];
        int dx = (lado - cw) / 2, dy = (lado - ch) / 2;
        for (var y = 0; y < ch; y++)
            Buffer.BlockCopy(pixels, (top + y) * stride + left * 4,
                             saida, (dy + y) * lado * 4 + dx * 4, cw * 4);

        var result = BitmapSource.Create(lado, lado, 96, 96, bgra.Format, null, saida, lado * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Um AppUserModelID nao tem separador de caminho; um executavel tem.</summary>
    public static bool IsAumid(string id) =>
        !string.IsNullOrEmpty(id) && !id.Contains('\\') && !id.Contains('/');

    /// <summary>
    /// A pasta de aplicativos do shell conhece este AppUserModelID?
    ///
    /// A pergunta importa porque nem todo AUMID de janela e lancavel por ali. Os perfis do
    /// Chrome sao o exemplo: a janela do "Profile 2" se anuncia como
    /// <c>Chrome.UserData.Profile2</c>, mas esse ID nao existe na pasta de aplicativos —
    /// quem abre esse perfil e o chrome.exe com <c>--profile-directory</c>. Deduzir pelo
    /// formato do ID nao serve; aqui o shell responde de verdade, e a resposta fica em cache.
    /// </summary>
    public static bool ShellExists(string aumid)
    {
        if (!IsAumid(aumid)) return false;
        if (Existing.TryGetValue(aumid, out var known)) return known;

        var exists = false;
        IShellItemImageFactory? item = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            exists = SHCreateItemFromParsingName($@"shell:AppsFolder\{aumid}", 0, ref iid, out item) == 0
                     && item is not null;
        }
        catch { exists = false; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }

        Existing[aumid] = exists;
        return exists;
    }

    /// <summary>Cache do <see cref="ShellExists"/>: uma pergunta ao shell por AUMID.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Existing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// De qual arquivo saiu o icone guardado por <see cref="For"/> e como esse arquivo estava.
    ///
    /// O cache so por caminho segurava o icone para sempre: um programa republicado no mesmo
    /// lugar com icone novo (ou que ainda nao tinha icone quando a dock o viu pela primeira
    /// vez) ficava com o desenho antigo — o generico do Windows, no caso — ate a dock ser
    /// reiniciada. Guardando a data de modificacao, o icone e relido quando o arquivo muda.
    /// Sao duas datas porque num atalho mudam coisas diferentes: a do .lnk quando ele passa a
    /// apontar para outro lugar, a do alvo quando o programa e trocado.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Alvo, long Caminho, long DoAlvo)> Origens =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Data de modificacao do arquivo; 0 quando ele nao existe (ou nao e arquivo).</summary>
    private static long Carimbo(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// O arquivo de onde saiu o ícone guardado sob esta chave mudou depois da leitura? Só olha
    /// datas, sem abrir nada. As chaves do <see cref="Ico"/> e do <see cref="Shell"/> não são
    /// caminhos de arquivo: guardam 0 na primeira data, e essa comparação passa direto.
    /// </summary>
    private static bool Changed(string key) =>
        Origens.TryGetValue(key, out var origem) &&
        (origem.Caminho != Carimbo(key) || origem.DoAlvo != Carimbo(origem.Alvo));

    /// <summary>
    /// O ícone deste botão veio de um arquivo que mudou depois? É a pergunta de quem fica na dock
    /// a sessão inteira: o <see cref="For"/> relê, mas só quando é chamado, e um botão fixado só o
    /// chama ao nascer. AppUserModelID puro fica de fora: não há arquivo por trás para conferir.
    /// </summary>
    public static bool IsStale(string id, string launchPath) =>
        Changed(launchPath) ||
        (ChromeProfiles.ByAumid(id) is { IconPath.Length: > 0 } perfil && Changed($"ico::{perfil.IconPath}"));

    public static BitmapSource? For(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached) && Origens.ContainsKey(path) && !Changed(path)) return cached;

        // As datas vêm antes da leitura, como no Ico: um arquivo trocado no meio dela fica com a
        // data velha e é relido da próxima vez, em vez de guardar o desenho antigo com a data nova.
        var carimbo = Carimbo(path);

        // Num .lnk o shell devolve o icone com a setinha de atalho colada. Numa dock isso
        // fica ruim, entao o icone vem do que o atalho aponta: o .ico proprio quando ele
        // define um (e o caso do atalho de perfil do Chrome), senao o executavel alvo.
        var exePath = path;
        var iconIndex = 0;
        var alvo = string.Empty;   // so de atalho com icone proprio: a reserva, se esse nao vier
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var lnk = ShortcutService.Read(path);
            if (lnk is not null)
            {
                exePath = lnk.Target;
                if (!string.IsNullOrWhiteSpace(lnk.IconPath) && File.Exists(lnk.IconPath))
                    (exePath, iconIndex, alvo) = (lnk.IconPath, lnk.IconIndex, lnk.Target);
            }
        }

        var doAlvo = Carimbo(exePath);

        // O desenho gravado no arquivo vem primeiro, lido direto dele (ExtractIconEx), e o
        // shell fica de reserva. Tres motivos:
        // - indice diferente de zero so e resolvido por ExtractIconEx;
        // - executavel que nao termina em .exe: o shell escolhe o icone pela extensao. O
        //   instalador do Inno Setup roda como .tmp e aparecia na dock com a folha em branco
        //   do "Arquivo TMP" (21/10/2026);
        // - o shell guarda os icones num cache por caminho. Um programa republicado no mesmo
        //   lugar (o GRemote, 29/10/2026) seguia com o desenho antigo: a dock relia ao ver a
        //   data nova do arquivo, o shell ainda entregava o velho, e ele ficava guardado com a
        //   data nova ate a dock reiniciar.
        // O que nao tem icone dentro devolve nada e segue para o shell.
        BitmapSource? result = null;
        if (doAlvo != 0)
            result = FromExtract(exePath, iconIndex);

        // O desenho que o atalho define nao veio (o do iSCSI e iscsicpl.dll,-1): o alvo e melhor
        // reserva que o shell, que responderia a folha de "arquivo DLL". O indice nao vai junto:
        // ele e do outro arquivo, e aqui apontaria para um desenho qualquer.
        if (result is null && alvo.Length > 0)
        {
            (exePath, doAlvo) = (alvo, Carimbo(alvo));
            if (doAlvo != 0) result = FromExtract(exePath, 0);
        }

        result ??= FromShell(exePath, 0);

        // O arquivo sumiu (desinstalado, ou mudou de pasta): sem nada para ler, o botao
        // fixado ficava sem desenho nenhum — um vao na dock que nem parecia botao, e que
        // por isso nao se achava para desafixar. Pelo tipo, sem abrir o arquivo, o shell
        // ainda da o icone generico.
        if (result is null && exePath.Length > 0)
            result = FromShell(exePath, SHGFI_USEFILEATTRIBUTES);

        Cache[path] = result;
        Origens[path] = (exePath, carimbo, doAlvo);
        return result;
    }

    /// <summary>O icone que o shell mostra para o arquivo. Com <see cref="SHGFI_USEFILEATTRIBUTES"/>
    /// ele responde so pela extensao, e o arquivo nem precisa existir.</summary>
    private static BitmapSource? FromShell(string file, uint flags)
    {
        var info = new SHFILEINFO();
        var h = SHGetFileInfo(file, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                              SHGFI_ICON | SHGFI_LARGEICON | flags);
        if (h == 0 || info.hIcon == 0) return null;

        var result = ToBitmap(info.hIcon);
        DestroyIcon(info.hIcon);
        return result;
    }

    private static BitmapSource? FromExtract(string file, int index)
    {
        if (ExtractIconEx(file, index, out var large, out var small, 1) == 0) return null;

        var handle = large != 0 ? large : small;
        var result = handle != 0 ? ToBitmap(handle) : null;
        if (large != 0) DestroyIcon(large);
        if (small != 0) DestroyIcon(small);
        return result;
    }

    /// <summary>
    /// A imagem de um HICON, ou nada se a conversão falhar. Não deixa a exceção subir: quem chama
    /// ainda tem o handle para destruir e, no <see cref="For"/>, outras fontes para tentar.
    /// </summary>
    private static BitmapSource? ToBitmap(nint hIcon)
    {
        try
        {
            var result = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                                                             BitmapSizeOptions.FromEmptyOptions());
            result.Freeze();
            return result;
        }
        catch { return null; }
    }

    /// <summary>
    /// Icone de qualquer item do shell pelo nome de análise — inclusive
    /// <c>shell:AppsFolder\&lt;AppUserModelID&gt;</c>, que e como os apps da Store aparecem.
    /// Vem com transparencia de verdade, entao os pixels sao copiados do DIB em vez de
    /// passar pelo CreateBitmapSourceFromHBitmap, que descarta o canal alfa.
    /// </summary>
    public static BitmapSource? Shell(string parsingName, int size = 48)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;

        var key = $"shell::{size}::{parsingName}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var arquivo = FileBehind(parsingName);
        var carimbo = Carimbo(arquivo);

        BitmapSource? result = null;
        nint bitmap = 0;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out factory) == 0 && factory is not null)
            {
                factory.GetImage(new SIZE(size, size), SIIGBF_BIGGERSIZEOK, out bitmap);
                if (bitmap != 0 && GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) != 0
                    && info.bmBits != 0 && info.bmBitsPixel == 32)
                {
                    var length = info.bmWidthBytes * info.bmHeight;
                    var pixels = new byte[length];
                    Marshal.Copy(info.bmBits, pixels, 0, length);

                    result = BitmapSource.Create(info.bmWidth, info.bmHeight, 96, 96,
                                                 PixelFormat(pixels), null, pixels,
                                                 info.bmWidthBytes);
                    result.Freeze();
                }
            }
        }
        catch { result = null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }

        Cache[key] = result;
        if (arquivo.Length > 0) Origens[key] = (arquivo, 0, carimbo);
        return result;
    }

    /// <summary>
    /// O arquivo por trás de um nome do shell, ou vazio quando não há um (AppUserModelID, a
    /// Lixeira). A pasta de aplicativos dá os programas comuns pelo caminho, inteiro ou com a pasta
    /// conhecida na frente: <c>{1AC14E77-…}\cmd.exe</c>.
    /// </summary>
    private static string FileBehind(string parsingName)
    {
        const string pasta = @"shell:AppsFolder\";
        var nome = parsingName.StartsWith(pasta, StringComparison.OrdinalIgnoreCase)
                   ? parsingName[pasta.Length..] : parsingName;

        if (nome.Length > 38 && nome[38] == '\\' && Guid.TryParse(nome[..38], out var conhecida) &&
            SHGetKnownFolderPath(ref conhecida, 0, 0, out var raiz) == 0)
            nome = raiz + nome[38..];

        return Path.IsPathFullyQualified(nome) ? nome : string.Empty;
    }

    /// <summary>
    /// Troca os ícones do <see cref="Shell"/> cujo arquivo mudou depois da leitura. Roda ao abrir a
    /// busca, numa thread própria, e não a cada consulta ao cache: a busca chama o Shell uma vez por
    /// resultado, a cada tecla, e uma data de arquivo num disco de rede fora do ar ou num HD
    /// parado seguraria a digitação por segundos.
    ///
    /// O desenho novo vem do arquivo, pelo For: perguntar de novo ao shell não adianta. Ele guarda
    /// os ícones por caminho enquanto o processo vive (medido em 30/10/2026: exe trocado no lugar,
    /// o shell seguia no desenho antigo depois de 1,5 s e de um SHChangeNotify, e um processo
    /// novo já via o certo). Arquivo sem ícone próprio fica como estava, com a data em dia.
    /// </summary>
    public static void RefreshShell()
    {
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var (chave, origem) in Origens)
                {
                    if (!chave.StartsWith("shell::", StringComparison.Ordinal) || !Changed(chave)) continue;

                    var carimbo = Carimbo(origem.Alvo);
                    if (HasOwnIcon(origem.Alvo) && For(origem.Alvo) is { } relido) Cache[chave] = relido;
                    Origens[chave] = (origem.Alvo, 0, carimbo);
                }
            }
            catch { /* nada aqui e essencial: o icone antigo continua valendo */ }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };

        thread.SetApartmentState(ApartmentState.STA);   // o .lnk passa pelo COM do shell
        thread.Start();
    }

    /// <summary>
    /// Descobre se os pixels vieram premultiplicados pelo alfa. O shell devolve os dois
    /// tipos: a maioria dos icones vem premultiplicada, mas os das Configuracoes e de
    /// outros apps da Store vem com a cor crua. Chamar de premultiplicado o que nao e
    /// pinta de branco tudo que deveria ser transparente — era a chapa branca atras da
    /// engrenagem das Configuracoes.
    ///
    /// A prova e simples: em premultiplicado nenhum canal de cor pode passar do alfa.
    /// </summary>
    private static System.Windows.Media.PixelFormat PixelFormat(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (pixels[i] > alpha || pixels[i + 1] > alpha || pixels[i + 2] > alpha)
                return PixelFormats.Bgra32;
        }

        return PixelFormats.Pbgra32;
    }

    /// <summary>
    /// A maior imagem de um .ico — o do perfil do Chrome traz ate 256 px, e o SHGetFileInfo
    /// so entregaria a de 32. Relido quando o arquivo muda: o Chrome refaz o icone quando o
    /// perfil troca de foto.
    /// </summary>
    public static BitmapSource? Ico(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = $"ico::{path}";
        var carimbo = Carimbo(path);
        if (Cache.TryGetValue(key, out var cached) && Origens.TryGetValue(key, out var origem) &&
            origem.DoAlvo == carimbo) return cached;

        BitmapSource? result = null;
        try
        {
            if (carimbo != 0)
            {
                using var stream = File.OpenRead(path);   // nao segura o arquivo aberto
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                                                   BitmapCacheOption.OnLoad);
                result = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                result?.Freeze();
            }
        }
        catch { result = null; }

        Cache[key] = result;
        Origens[key] = (path, 0, carimbo);
        return result;
    }

    /// <summary>Carrega um PNG/JPG solto (a foto do perfil do Chrome, por exemplo).</summary>
    public static BitmapSource? Image(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // nao segura o arquivo aberto
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
        }
        catch { result = null; }

        Cache[path] = result;
        return result;
    }
}
