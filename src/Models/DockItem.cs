using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using WinDock.Services;

namespace WinDock.Models;

/// <summary>
/// Um botao da dock: um app, com zero ou mais janelas abertas. Fixado, aberto, ou os dois.
///
/// A identidade (<see cref="Id"/>) e o AppUserModelID quando existe, senao o caminho do
/// executavel — e o que faz dois perfis do Chrome virarem dois botoes, como na barra de
/// tarefas. O alvo de lancamento (<see cref="LaunchPath"/>) e separado: costuma ser o .lnk
/// do atalho, que carrega os argumentos do perfil e o icone certo.
/// </summary>
public sealed class DockItem : INotifyPropertyChanged
{
    public string Id { get; }
    public string LaunchPath { get; private set; }
    public string LaunchArgs { get; private set; }

    private string _label;
    public string Label { get => _label; private set => Set(ref _label, value); }

    private BitmapSource? _icon;
    public BitmapSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    private bool _isActive;
    /// <summary>Alguma janela deste app esta em primeiro plano.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private bool _startsRunning;
    /// <summary>
    /// Este botao abre o grupo dos apps que estao apenas abertos — e antes dele vai o traco
    /// que os separa dos fixados.
    ///
    /// Quem decide e a dock inteira, e nao o botao sozinho: depende de quem vem antes na
    /// lista. Veja <c>DockModel.MarkDivider</c>.
    /// </summary>
    public bool StartsRunning { get => _startsRunning; set => Set(ref _startsRunning, value); }

    private bool _isDragging;
    /// <summary>Esta sendo arrastado agora: o botao fica apagado no lugar de origem.</summary>
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

    private List<TaskWindow> _windows = new();
    public List<TaskWindow> Windows
    {
        get => _windows;
        set
        {
            _windows = value;
            OnChanged(nameof(Windows));
            OnChanged(nameof(WindowCount));
            OnChanged(nameof(HasWindows));
            OnChanged(nameof(Indicators));
            OnChanged(nameof(Tooltip));
        }
    }

    public int WindowCount => _windows.Count;
    public bool HasWindows => _windows.Count > 0;

    /// <summary>Quantas bolinhas cabem embaixo do icone antes de virarem uma faixa so.</summary>
    private const int MaxIndicators = 4;

    /// <summary>Uma bolinha por janela aberta, como no Dash to Dock, ate o limite.</summary>
    public IReadOnlyList<TaskWindow> Indicators =>
        _windows.Count <= MaxIndicators ? _windows : _windows.Take(MaxIndicators).ToList();

    public string Tooltip => _windows.Count switch
    {
        0 => Label,
        1 => _windows[0].Title,
        _ => $"{Label} ({_windows.Count} janelas)"
    };

    /// <summary>Indice da janela mostrada por ultimo, para alternar entre elas a cada clique.</summary>
    public int CycleIndex { get; set; }

    /// <summary>
    /// Imagem sobreposta ao canto do icone — hoje a foto do perfil do Chrome, quando o
    /// perfil nao tem atalho proprio para dar o icone completo.
    /// </summary>
    private BitmapSource? _overlay;
    public BitmapSource? Overlay { get => _overlay; private set => Set(ref _overlay, value); }

    public bool HasOverlay => _overlay is not null;

    /// <summary>Caminho da imagem sobreposta, para o config lembrar dela.</summary>
    public string OverlayPath { get; private set; }

    public DockItem(string id, string launchPath, string launchArgs, string label, bool pinned,
                    string overlayIcon = "")
    {
        Id = id;
        LaunchPath = launchPath;
        LaunchArgs = launchArgs;
        _label = string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(launchPath) : label;
        _isPinned = pinned;
        _icon = IconService.ForApp(id, launchPath);
        OverlayPath = overlayIcon;
        _overlay = IconService.Image(overlayIcon);
    }

    /// <summary>
    /// Um item criado a partir de uma janela aberta comeca sem atalho; quando o atalho
    /// correspondente aparece (ou o usuario fixa), o alvo e o icone sao trocados.
    /// </summary>
    public void Retarget(string launchPath, string launchArgs, string? label = null)
    {
        if (string.Equals(LaunchPath, launchPath, StringComparison.OrdinalIgnoreCase)) return;

        LaunchPath = launchPath;
        LaunchArgs = launchArgs;
        if (!string.IsNullOrWhiteSpace(label)) Label = label;
        Icon = IconService.ForApp(Id, launchPath);
    }

    /// <summary>
    /// Abrir por AppUserModelID (a pasta de aplicativos do shell) em vez de pelo caminho.
    ///
    /// E o unico jeito para os apps da Store, cujo executavel nem sempre pode ser chamado
    /// direto. Mas so vale quando o shell realmente conhece o ID: a janela de um perfil do
    /// Chrome tambem expoe um AUMID (<c>Chrome.UserData.Profile2</c>) que a pasta de
    /// aplicativos nao tem — abrir por ele nao fazia nada, e era esse o botao morto.
    ///
    /// Um atalho ou um argumento tambem tiram a duvida: o .lnk ja carrega o AUMID certo, e
    /// o lancamento por AUMID nao tem onde levar o <c>--profile-directory</c>.
    /// </summary>
    public bool IsStoreApp =>
        string.IsNullOrWhiteSpace(LaunchArgs) &&
        !LaunchPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
        IconService.ShellExists(Id);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }
}
