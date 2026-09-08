using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using WinDock.Models;
using static WinDock.Interop.Native;

namespace WinDock.Services;

/// <summary>
/// Mantem a lista de botoes em sincronia com as janelas abertas. A atualizacao e
/// disparada por WinEvents (criar/destruir/foco) e nao por varredura em loop; o timer
/// existe so como rede de seguranca para eventos que escapam.
/// </summary>
public sealed class DockModel : IDisposable
{
    public ObservableCollection<DockItem> Items { get; } = new();

    /// <summary>Valores visuais derivados da config, consumidos direto pelo XAML.</summary>
    public DockTheme Theme { get; }

    private readonly DockConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly List<nint> _hooks = new();
    private readonly WinEventProc _proc;   // guardado em campo: o GC nao pode coletar o delegate
    private DispatcherOperation? _pending;

    public DockModel(DockConfig config, Dispatcher dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        _proc = OnWinEvent;
        Theme = new DockTheme(config);

        RepairPackagedPins();

        foreach (var p in _config.Pinned)
            Items.Add(new DockItem(p.Id, p.Path, p.Args, p.Label, pinned: true, overlayIcon: p.Overlay));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        HookEvents();

        // A varredura dos atalhos sai da frente — ela e quem resolve o nome e o icone de cada
        // app aberto — e a primeira leva de botoes sai com o que se sabe do executavel:
        // perguntar o atalho esperaria a varredura terminar, e era isso que fazia os apps ja
        // abertos demorarem a aparecer. Quando ela termina, uma segunda atualizacao troca
        // nome e icone pelos do atalho.
        ShortcutService.Ready += OnShortcutsReady;
        ShortcutService.Warm();

        // E aqui mesmo, sem passar pela fila. Esta atualizacao ja custou 735 ms de espera
        // por estar agendada em prioridade Background: ela ia para tras de todo o resto da
        // montagem — a barra de cima, o AppBar, esconder a barra do Windows — e so entao os
        // apps abertos apareciam. O trabalho em si leva 12 ms.
        Refresh();
    }

    private void HookEvents()
    {
        void Hook(uint min, uint max) =>
            _hooks.Add(SetWinEventHook(min, max, 0, _proc, 0, 0,
                                       WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
    }

    private void OnWinEvent(nint hook, uint ev, nint hWnd, int idObject, int idChild, uint t, uint time)
    {
        if (idObject != 0) return;                    // so a janela em si, nao seus controles

        // Troca de primeiro plano conta mesmo vindo de uma janela filha: um app com ilha XAML
        // — o Windows Terminal é o caso daqui — anuncia o foco pela janela de conteúdo, e a
        // pergunta do `Concerns` não a reconhecia. Sem esta atualização a dock ficava achando
        // que quem está à frente é a janela anterior, e o clique seguinte no ícone caía num
        // estado em que não minimizava nem trazia para frente: não fazia nada.
        //
        // Subir até a raiz só vale aqui. Nos eventos de criar/mostrar/esconder isso deixaria
        // passar a enxurrada de controles que o `Concerns` existe justamente para barrar.
        var target = ev == EVENT_SYSTEM_FOREGROUND ? GetAncestor(hWnd, GA_ROOT) : hWnd;

        if (!Concerns(target)) return;
        if (_pending is { Status: DispatcherOperationStatus.Pending }) return;   // coalescencia
        _pending = _dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    /// <summary>
    /// Este evento pode mudar alguma coisa na dock?
    ///
    /// A pergunta parece supérflua e não é. O gancho pega criar/mostrar/esconder/destruir de
    /// **toda janela do sistema**, e um único programa com interface rica gera dezenas delas
    /// por segundo sem nada acontecer na barra de tarefas: medido aqui, com o Delphi aberto,
    /// eram noventa e oito eventos em oito segundos — barras de ferramentas, campos de
    /// edição, listas de combo nascendo e morrendo — e cada um mandava a dock se refazer
    /// inteira. O log virava sessenta linhas de "dock atualizada" por segundo com o mesmo
    /// resultado, e essa moagem era o que deixava o clique lento e a leitura da bandeja
    /// travada, porque quem paga a conta é a thread da interface.
    ///
    /// Duas perguntas baratas resolvem, e nesta ordem:
    ///
    /// - **já conheço esta janela?** — então mudou algo em quem a dock mostra (fechou,
    ///   minimizou, trocou de título) e vale reler. É uma consulta a um conjunto em memória,
    ///   e é a única resposta possível para o <c>DESTROY</c>: quando ele chega o identificador
    ///   já morreu e nenhuma pergunta ao Windows funciona mais.
    /// - **é uma janela de topo, sem dono e com título?** — é o mínimo que uma janela precisa
    ///   ter para virar botão (veja <see cref="WindowService"/>). Filha, dona ou sem título
    ///   nunca vira, e é aí que mora a enxurrada.
    /// </summary>
    private bool Concerns(nint hWnd)
    {
        if (hWnd == 0) return false;
        if (_known.Contains(hWnd)) return true;

        return GetAncestor(hWnd, GA_ROOT) == hWnd &&
               GetWindow(hWnd, GW_OWNER) == 0 &&
               GetWindowTextLength(hWnd) > 0;
    }

    /// <summary>As janelas que a última atualização viu — a memória de que o filtro precisa.</summary>
    private readonly HashSet<nint> _known = new();

    public void Refresh()
    {
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var windows = WindowService.Enumerate();

        // o filtro de eventos precisa saber quais janelas a dock mostra agora; veja o
        // `Concerns`
        var gone = _known.Count > 0 && windows.Count < _known.Count;
        _known.Clear();
        foreach (var w in windows) _known.Add(w.Handle);

        // Programa que fecha costuma levar o ícone da bandeja junto, e o cartão guarda a
        // última leitura por alguns segundos: sem este aviso ele continuava mostrando o
        // ícone de um app já fechado. Não relê nada agora — só marca que a próxima abertura
        // não pode se servir do que está guardado.
        if (gone) TrayService.Invalidate();

        var byApp = windows.GroupBy(w => w.AppKey)
                           .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var foreground = Foreground();

        // 1. atualiza os que ja estao na dock e remove os abertos que fecharam
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var item = Items[i];
            if (byApp.Remove(item.Id, out var wins))
            {
                item.Windows = wins;
                item.IsActive = wins.Any(w => w.Handle == foreground);
            }
            else if (item.IsPinned)
            {
                item.Windows = new List<TaskWindow>();
                item.IsActive = false;
            }
            else
            {
                Items.RemoveAt(i);
            }
        }

        // 2. apps abertos que ainda nao tem botao entram depois dos fixados
        if (_config.ShowRunning)
        {
            foreach (var (_, wins) in byApp)
            {
                var w = wins[0];
                var (path, args, label, picture) = ResolveTarget(w);
                Items.Add(new DockItem(w.AppKey, path, args, label, pinned: false, overlayIcon: picture)
                {
                    Windows = wins,
                    IsActive = wins.Any(x => x.Handle == foreground)
                });
            }
        }

        MarkDivider();

        Log.Trace($"dock atualizada em {relogio.ElapsedMilliseconds} ms: {Items.Count} botões, {windows.Count} janelas");
    }

    /// <summary>
    /// Marca onde entra o traço que separa os fixados dos que só estão abertos.
    ///
    /// A marca fica no **primeiro** não fixado, e só se houver algum fixado antes dele: um
    /// traço na ponta da dock não separaria nada. A ordem é a da lista, que a pessoa pode
    /// rearranjar arrastando — por isso a conta é refeita a cada atualização, e não uma vez
    /// na montagem.
    /// </summary>
    private void MarkDivider()
    {
        var pinnedBefore = false;
        var marked = false;

        foreach (var item in Items)
        {
            var starts = !marked && !item.IsPinned && pinnedBefore;
            if (starts) marked = true;

            item.StartsRunning = starts;
            pinnedBefore |= item.IsPinned;
        }
    }

    /// <summary>
    /// Descobre o que abrir para esta janela. Quando ela tem AppUserModelID, procura o
    /// atalho do Windows com o mesmo ID: e dele que vem o argumento do perfil (por
    /// exemplo --profile-directory) e o icone especifico. Sem atalho, cai no executavel.
    /// </summary>
    private static (string Path, string Args, string Label, string Picture) ResolveTarget(TaskWindow w)
    {
        // 1. o atalho do Windows com o mesmo AppUserModelID e a fonte mais fiel
        var lnk = ShortcutService.ByAumid(w.Aumid);
        if (lnk is not null) return (lnk.LnkPath, string.Empty, lnk.Name, string.Empty);

        // 2. perfil do Chrome sem atalho: o proprio Chrome sabe o nome e o argumento
        var profile = ChromeProfiles.ByAumid(w.Aumid);
        if (profile is not null)
            return (w.ExePath, profile.Arguments, $"Chrome - {profile.Name}", profile.PicturePath);

        return (w.ExePath, string.Empty, Path.GetFileNameWithoutExtension(w.ExePath), string.Empty);
    }

    // ── acoes ───────────────────────────────────────────────

    /// <summary>
    /// Ultima janela em primeiro plano que nao e da propria dock. Mesmo com WS_EX_NOACTIVATE
    /// o foco pode passar por aqui (menu de contexto, painel de configuracoes), e nesses
    /// instantes o Windows responderia "a dock esta em foco" — o que faria o clique reativar
    /// o app em vez de minimizar.
    /// </summary>
    private nint _foreground;

    private nint Foreground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return _foreground;

        GetWindowThreadProcessId(fg, out var pid);
        if (pid != (uint)Environment.ProcessId) _foreground = fg;
        return _foreground;
    }

    /// <summary>
    /// Abre uma instancia do app deste botao.
    ///
    /// Duas tentativas, nesta ordem, porque um botao pode ter sido gravado ha meses: o
    /// caminho de sempre e, se ele nao existir mais (app reinstalado noutra pasta, versao
    /// que mudou de numero no nome), a pasta de aplicativos pelo AppUserModelID. Antes um
    /// unico palpite errado significava clicar e nao acontecer nada.
    /// </summary>
    public static void Launch(DockItem item)
    {
        if (item.IsStoreApp)
        {
            if (WindowService.LaunchApp(item.Id)) return;
            WindowService.Launch(item.LaunchPath, item.LaunchArgs);
            return;
        }

        if (WindowService.Launch(item.LaunchPath, item.LaunchArgs)) return;

        if (IconService.ShellExists(item.Id) && WindowService.LaunchApp(item.Id)) return;

        Log.Write($"nada abriu para o botao '{item.Label}' (id={item.Id}, alvo={item.LaunchPath})");
    }

    public void Activate(DockItem item)
    {
        // A lista de janelas é de até três segundos atrás. Se alguma já morreu, o clique
        // agiria sobre um identificador vazio e não aconteceria nada — e o botão fixado
        // pareceria quebrado. Reler custa ~2 ms e devolve o clique ao estado de agora:
        // ou sobra janela para ativar, ou o botão volta a ser um lançador.
        if (item.Windows.Any(w => !IsWindow(w.Handle)))
        {
            Log.Trace($"'{item.Label}' tinha janela já fechada na lista; relendo antes de agir");
            Refresh();
        }

        if (!item.HasWindows) { Launch(item); return; }

        var foreground = Foreground();

        if (item.Windows.Count == 1)
        {
            WindowService.ToggleActivate(item.Windows[0].Handle, foreground);
            return;
        }

        // varias janelas: se o app ja esta em foco, o clique passa para a proxima janela;
        // se nao esta, volta para a ultima que estava sendo mostrada. O foco real vem
        // primeiro pelo mesmo motivo do ToggleActivate: o guardado pode estar congelado
        var actual = GetForegroundWindow();
        var current = item.Windows.FindIndex(w => w.Handle == actual);
        if (current < 0) current = item.Windows.FindIndex(w => w.Handle == foreground);
        if (current >= 0) item.CycleIndex = (current + 1) % item.Windows.Count;
        else if (item.CycleIndex >= item.Windows.Count) item.CycleIndex = 0;

        WindowService.Activate(item.Windows[item.CycleIndex].Handle);
    }

    public void TogglePin(DockItem item)
    {
        item.IsPinned = !item.IsPinned;
        if (!item.IsPinned && !item.HasWindows) Items.Remove(item);
        MarkDivider();
        SavePinned();
    }

    /// <summary>
    /// Move um botao para a posicao de outro (arrastar e soltar). A ordem que fica
    /// salva e a dos fixados; os abertos so acompanham enquanto estao na tela.
    /// </summary>
    /// <param name="save">
    /// Falso enquanto o arraste esta em curso: a ordem muda a cada icone que o cursor
    /// cruza, e gravar o arquivo a cada troca seria escrever em disco dezenas de vezes
    /// num gesto so. Quem solta o botao chama <see cref="SaveOrder"/> uma vez.
    /// </param>
    public void Move(DockItem item, DockItem target, bool save = true)
    {
        var from = Items.IndexOf(item);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        Items.Move(from, to);
        MarkDivider();
        if (save) SavePinned();
    }

    /// <summary>Grava a ordem atual — o fim de um arraste.</summary>
    public void SaveOrder() => SavePinned();

    /// <summary>
    /// Acerta os fixados que apontam para uma versão de app da Store que já não está instalada.
    ///
    /// Sem isto, o dia em que o app se atualiza é o dia em que o botão dele perde o ícone e para
    /// de abrir: a pasta do pacote leva a versão no nome, e a antiga some (o Spotify indo da
    /// 1.298.301.0 para a 1.299.317.0 foi o caso que apareceu aqui). O <see cref="DockItem.Id"/>
    /// vai junto quando ele é o próprio caminho — é a chave que junta o botão às janelas abertas
    /// do app (<see cref="TaskWindow.AppKey"/>), e deixá-la velha faria o app abrir num segundo
    /// botão, ao lado do fixado.
    ///
    /// Roda uma vez, quando a dock sobe, e só toca no que está quebrado.
    /// </summary>
    private void RepairPackagedPins()
    {
        var mudou = false;

        foreach (var p in _config.Pinned)
        {
            if (!PackagedApps.TryRepair(p.Path, out var atual)) continue;

            Log.Write($"fixado '{p.Label}': o pacote mudou de versão — {p.Path} → {atual}");

            if (string.Equals(p.Id, p.Path, StringComparison.OrdinalIgnoreCase))
                p.Id = atual.ToLowerInvariant();   // o AppKey de uma janela vem em minúsculas

            p.Path = atual;
            mudou = true;
        }

        if (mudou) _config.Save();
    }

    /// <summary>Grava os fixados na ordem em que estao na dock.</summary>
    private void SavePinned()
    {
        _config.Pinned = Items.Where(i => i.IsPinned)
                              .Select(i => new PinnedApp
                              {
                                  Id = i.Id,
                                  Path = i.LaunchPath,
                                  Args = i.LaunchArgs,
                                  Label = i.Label,
                                  Overlay = i.OverlayPath
                              })
                              .ToList();
        _config.Save();
    }

    /// <summary>Fixa um atalho escolhido pelo usuario (um .lnk ou um .exe).</summary>
    public void PinShortcut(string path)
    {
        var lnk = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                  ? ShortcutService.Read(path) : null;

        // a identidade e a mesma que as janelas desse app vao expor
        var id = !string.IsNullOrEmpty(lnk?.Aumid)
                 ? lnk!.Aumid
                 : (lnk?.Target ?? path).ToLowerInvariant();

        if (Items.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            // ja existe como app aberto: so promove a fixado, apontando para o atalho
            var existing = Items.First(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            existing.Retarget(path, string.Empty, Path.GetFileNameWithoutExtension(path));
            if (!existing.IsPinned) TogglePin(existing);
            return;
        }

        var item = new DockItem(id, path, string.Empty, Path.GetFileNameWithoutExtension(path), pinned: true);
        Items.Insert(Items.Count(i => i.IsPinned), item);   // depois dos outros fixados
        SavePinned();
        Refresh();
    }

    public void CloseAll(DockItem item)
    {
        foreach (var w in item.Windows) WindowService.Close(w.Handle);
    }

    /// <summary>
    /// Mata os processos por trás das janelas do botão, e atualiza a dock em seguida.
    ///
    /// Os processos já mortos são anotados: várias janelas de um mesmo programa costumam
    /// pertencer a um processo só, e sem essa conta a segunda janela tentaria matar quem já
    /// morreu e o log encheria de falha que não é falha.
    /// </summary>
    public void KillAll(DockItem item)
    {
        var dead = new HashSet<uint>();
        foreach (var w in item.Windows)
        {
            GetWindowThreadProcessId(w.Handle, out var pid);
            if (pid == 0 || dead.Contains(pid)) continue;
            if (WindowService.Kill(w.Handle) != 0) dead.Add(pid);
        }

        Log.Write($"forçado o fechamento de '{item.Label}': {dead.Count} processo(s)");
        Refresh();
    }

    /// <summary>
    /// Os atalhos ficaram prontos: refaz os botoes dos apps abertos.
    ///
    /// Nao basta chamar o <see cref="Refresh"/>, porque ele so resolve o alvo de quem ainda
    /// nao tem botao — e esses ja tem, feitos as pressas com o nome do executavel. Tirando
    /// os nao fixados, a atualizacao seguinte os recria com o nome e o icone do atalho.
    /// </summary>
    private void OnShortcutsReady() => _dispatcher.InvokeAsync(() =>
    {
        Log.Trace("atalhos ficaram prontos: refazendo os botões dos apps abertos");
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!Items[i].IsPinned) Items.RemoveAt(i);

        Refresh();
    }, DispatcherPriority.Background);

    public void Dispose()
    {
        _timer.Stop();
        ShortcutService.Ready -= OnShortcutsReady;
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
