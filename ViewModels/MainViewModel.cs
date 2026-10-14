using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Views;
using Microsoft.Win32;

namespace GameLauncher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string SaveFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLauncher", "games.json");

    private readonly ICollectionView _gamesView;
    private readonly ListCollectionView _bigPictureGamesView;
    private HardwareMonitorService? _hwMonitor;
    private XInputService _xinput = null!;
    public  XInputService XInput => _xinput;
    private readonly Dispatcher _dispatcher;
    private int _selectedIndex = -1;
    private string? _runningGameName;
    private string? _runningGameExecutablePath;
    public  string? RunningGameName => _runningGameName;

    // Sistema de trofeus
    private readonly TrophyService _trophyService = new();
    private DispatcherTimer? _launcherTimeTimer;
    public  TrophyService TrophyService => _trophyService;

    public enum NavZone { Header, Actions, Carousel }

    private static readonly string[] HeaderItems = ["Xbox", "Steam", "Epic", "Help", "Settings", "AddGame", "Theme"];

    [ObservableProperty] private NavZone activeZone = NavZone.Carousel;
    [ObservableProperty] private int headerIndex;
    [ObservableProperty] private string focusedHeaderItem = "";

    [ObservableProperty]
    private ObservableCollection<Game> games = new();

    [ObservableProperty]
    private string statusMessage = "Clique em '+ ADICIONAR JOGO' para começar";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty] private double cpuUsage;
    [ObservableProperty] private double gpuUsage;
    [ObservableProperty] private double ramUsage;
    [ObservableProperty] private string cpuName = "";
    [ObservableProperty] private string gpuName = "";
    [ObservableProperty] private string ramTotal = "";
    [ObservableProperty] private ObservableCollection<string> storageDrives = [];

    [ObservableProperty] private Game? selectedGame;
    [ObservableProperty] private Game? detailGame;
    [ObservableProperty] private bool showDetailPanel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGamepadBattery))]
    private bool gamepadConnected;
    [ObservableProperty] private string gamepadStatus = "";
    [ObservableProperty] private bool isAnimationLoading;
    [ObservableProperty] private bool isSoundEnabled = SettingsService.Current.SoundEnabled;
    [ObservableProperty] private bool isFpsOverlayEnabled = SettingsService.Current.FpsOverlayEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BigPictureIcon))]
    private bool isBigPictureMode;

    [ObservableProperty]
    private string bigPictureCollection = "Todos os jogos";

    [ObservableProperty]
    private bool isBigPictureDetailsOpen;

    public string BigPictureIcon => IsBigPictureMode ? "Television" : "TelevisionClassic";

    /// <summary>
    /// Callback injetado pela View para executar a animação de transição.
    /// Parâmetro: true = entrando, false = saindo do Big Picture.
    /// </summary>
    public Action<bool>? BigPictureTransitionRequested { get; set; }

    [RelayCommand]
    private void ToggleBigPictureMode()
    {
        bool entering = !IsBigPictureMode;
        IsBigPictureMode = entering;
        // O som de entrada é tocado pelo BigPictureTransitionService; apenas navegar na saída
        if (!entering) SoundService.PlayNavigate();
        StatusMessage = entering ? "Modo Big Picture ativado" : "Modo Big Picture desativado";
        if (entering) _trophyService.OnBigPictureUsed();

        if (entering)
        {
            IsBigPictureDetailsOpen = false;
            ActiveZone = NavZone.Carousel;
            var visible = _bigPictureGamesView.Cast<Game>().ToList();
            if (visible.Count > 0 && (SelectedGame is null || !visible.Contains(SelectedGame)))
            {
                _selectedIndex = 0;
                SelectedGame = visible[0];
            }
        else if (visible.Count == 0)
        {
            _selectedIndex = -1;
            SelectedGame = null;
        }
            else if (SelectedGame is not null)
            {
                _selectedIndex = visible.IndexOf(SelectedGame);
                if (_selectedIndex < 0) _selectedIndex = 0;
            }
            UpdateGamepadStatusForZone();
        }

        BigPictureTransitionRequested?.Invoke(entering);
    }

    partial void OnBigPictureCollectionChanged(string value)
    {
        _bigPictureGamesView.SortDescriptions.Clear();
        if (value == "Continuar jogando")
        {
            _bigPictureGamesView.CustomSort =
                Comparer<Game>.Create((left, right) => Nullable.Compare(right.LastPlayed, left.LastPlayed));
        }
        else
        {
            _bigPictureGamesView.CustomSort = null;
            _bigPictureGamesView.SortDescriptions.Add(
                new SortDescription(nameof(Game.SortOrder), ListSortDirection.Ascending));
        }

        _bigPictureGamesView.Refresh();
        var visible = _bigPictureGamesView.Cast<Game>().ToList();
        if (visible.Count > 0 && (SelectedGame is null || !visible.Contains(SelectedGame)))
        {
            _selectedIndex = 0;
            SelectedGame = visible[0];
        }
        else if (visible.Count == 0)
        {
            _selectedIndex = -1;
            SelectedGame = null;
        }
    }

    private bool FilterBigPictureGames(object item) => item is Game game && BigPictureCollection switch
    {
        "Continuar jogando" => game.LastPlayed.HasValue,
        "Favoritos" => game.IsFavorite,
        _ => true
    };

    public void CycleBigPictureCollection(int direction)
    {
        string[] collections = ["Continuar jogando", "Favoritos", "Todos os jogos"];
        var index = Array.IndexOf(collections, BigPictureCollection);
        BigPictureCollection = collections[(index + direction + collections.Length) % collections.Length];
        SoundService.PlayZoneChange();
    }

    [RelayCommand]
    private void SetBigPictureCollection(string collection)
    {
        if (collection is "Continuar jogando" or "Favoritos" or "Todos os jogos")
            BigPictureCollection = collection;
    }

    [RelayCommand]
    private void ToggleBigPictureDetails() => IsBigPictureDetailsOpen = !IsBigPictureDetailsOpen;

    private void NavigateBigPictureGames(int direction)
    {
        var visible = _bigPictureGamesView.Cast<Game>().ToList();
        if (visible.Count == 0)
            return;

        var index = SelectedGame is null ? 0 : visible.IndexOf(SelectedGame);
        index = Math.Clamp(index + direction, 0, visible.Count - 1);
        _selectedIndex = index;
        SelectedGame = visible[index];
        SoundService.PlayNavigate();
    }

    private GpuCapabilities? _gpuCaps;
    [ObservableProperty] private ObservableCollection<TechCompatItem> techCompatItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGamepadBattery))]
    [NotifyPropertyChangedFor(nameof(GamepadBatteryIcon))]
    private int gamepadBatteryLevel = -1;

    public bool HasGamepadBattery => GamepadBatteryLevel >= 0 && GamepadConnected;

    // Recomendacao grafica
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHwRecommendation))]
    private Models.GraphicsRecommendation? hwRecommendation;

    public bool HasHwRecommendation => HwRecommendation is not null;

    [RelayCommand]
    private void OpenHwRecommendation()
    {
        if (DetailGame is null) return;
        var dlg = new Views.HwRecommendationDialog(
            DetailGame.DisplayName,
            GpuName, CpuName, RamTotal)
        {
            Owner = Application.Current.MainWindow
        };
        dlg.ShowDialog();
    }

    public string GamepadBatteryIcon => GamepadBatteryLevel switch
    {
        >= 80 => "Battery",
        >= 50 => "Battery70",
        >= 20 => "Battery40",
        >= 0  => "Battery10",
        _     => "BatteryUnknown"
    };

    private bool _isContextMenuOpen;
    public bool IsContextMenuOpen
    {
        get => _isContextMenuOpen;
        set => SetProperty(ref _isContextMenuOpen, value);
    }

    public Action<GamepadButton>? ContextMenuNavigate { get; set; }
    public Action? OpenSelectedGameMenu { get; set; }

    private bool _isHelpDialogOpen;
    public bool IsHelpDialogOpen
    {
        get => _isHelpDialogOpen;
        set => SetProperty(ref _isHelpDialogOpen, value);
    }

    public Action<GamepadButton>? HelpDialogNavigate { get; set; }

    public Action<double>? HelpDialogScroll { get; set; }

    private bool _isProfileDialogOpen;
    public bool IsProfileDialogOpen
    {
        get => _isProfileDialogOpen;
        set => SetProperty(ref _isProfileDialogOpen, value);
    }

    public Action<GamepadButton>? ProfileDialogNavigate { get; set; }

    private bool _isTrophyDialogOpen;
    public bool IsTrophyDialogOpen
    {
        get => _isTrophyDialogOpen;
        set => SetProperty(ref _isTrophyDialogOpen, value);
    }

    public Action<double>? TrophyDialogScroll { get; set; }

    private FpsOverlayWindow? _fpsOverlay;
    private DispatcherTimer? _aiHintTimer;
    private int _gamepadGridColumns = 10;

    [ObservableProperty] private string currentTime = DateTime.Now.ToString("H:mm");
    [ObservableProperty] private string playerName = SettingsService.Current.PlayerName;

    // Trofeus: exibicao no header
    [ObservableProperty] private string trophyGamerscoreDisplay = "0G";
    [ObservableProperty] private string trophyCountDisplay      = "0/27 troféus";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    [NotifyPropertyChangedFor(nameof(HasNoAvatar))]
    private string? avatarPath = string.IsNullOrEmpty(SettingsService.Current.AvatarImagePath) ? null : SettingsService.Current.AvatarImagePath;

    public bool HasAvatar   => !string.IsNullOrEmpty(AvatarPath);
    public bool HasNoAvatar => !HasAvatar;

    private XboxLiveService? _xboxService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXboxLoggedIn))]
    [NotifyPropertyChangedFor(nameof(XboxButtonText))]
    private XboxProfile? xboxProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsXboxLoggedIn))]
    [NotifyPropertyChangedFor(nameof(XboxButtonText))]
    private bool xboxConnected;

    public bool IsXboxLoggedIn => XboxConnected && XboxProfile is not null;
    public string XboxButtonText => IsXboxLoggedIn ? XboxProfile!.Gamertag : "XBOX";

    private SteamService? _steamService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamConnected))]
    [NotifyPropertyChangedFor(nameof(SteamButtonText))]
    private SteamProfile? steamProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamConnected))]
    [NotifyPropertyChangedFor(nameof(SteamButtonText))]
    private bool steamConnected;

    public bool IsSteamConnected => SteamConnected && SteamProfile is not null;
    public string SteamButtonText => IsSteamConnected ? SteamProfile!.PersonaName : "STEAM";

    private EpicGamesService? _epicService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEpicConnected))]
    [NotifyPropertyChangedFor(nameof(EpicButtonText))]
    private EpicProfile? epicProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEpicConnected))]
    [NotifyPropertyChangedFor(nameof(EpicButtonText))]
    private bool epicConnected;

    public bool IsEpicConnected => EpicConnected && EpicProfile is not null;
    public string EpicButtonText => IsEpicConnected ? EpicProfile!.DisplayName : "EPIC";
private readonly DispatcherTimer _clockTimer;

    public ICollectionView GamesView => _gamesView;
    public ICollectionView BigPictureGamesView => _bigPictureGamesView;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        // O controle deve estar disponível antes da inicialização pesada
        // (hardware, contas e serviços online), para que a interface já abra
        // responsiva.
        _xinput = new XInputService();
        _xinput.ButtonPressed += OnGamepadButton;
        _xinput.ConnectionChanged += OnGamepadConnectionChanged;
        _xinput.RightStickY += OnRightStickY;
        _xinput.BatteryChanged += OnBatteryChanged;
        _xinput.AiComboTriggered += OnAiComboTriggered;
        _xinput.Start();

        _gamesView = CollectionViewSource.GetDefaultView(Games);
        _gamesView.Filter = obj =>
            obj is Game g &&
            (string.IsNullOrWhiteSpace(SearchText) ||
             g.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
             g.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        _gamesView.SortDescriptions.Add(new SortDescription(nameof(Game.SortOrder), ListSortDirection.Ascending));

        _bigPictureGamesView = new ListCollectionView(Games)
        {
            Filter = FilterBigPictureGames
        };
        _bigPictureGamesView.SortDescriptions.Add(new SortDescription(nameof(Game.SortOrder), ListSortDirection.Ascending));

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => CurrentTime = DateTime.Now.ToString("H:mm");
        _clockTimer.Start();
    }

    /// <summary>
    /// Heavy initialization that runs without blocking the UI thread.
    /// Call after construction, before showing the main window.
    /// </summary>
    public async Task InitializeAsync(Action<string>? statusCallback = null)
    {
        statusCallback?.Invoke("Carregando biblioteca de jogos...");
        var games = await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(SaveFilePath)) return null;
                var json = File.ReadAllText(SaveFilePath);
                return JsonSerializer.Deserialize<List<Game>>(json);
            }
            catch { return null; }
        });

        if (games is not null)
        {
            // Assign SortOrder to legacy games that don't have one
            bool needsReorder = games.All(g => g.SortOrder == 0);
            if (needsReorder)
            {
                for (int i = 0; i < games.Count; i++)
                    games[i].SortOrder = i;
            }

            foreach (var g in games)
                Games.Add(g);
            StatusMessage = $"{Games.Count} jogos na biblioteca";
            _gamesView.Refresh();
            if (_gamesView.Cast<Game>().FirstOrDefault() is { } first)
                SelectedGame = first;
        }

        // Trofeus: subscrever antes dos restores de plataformas
        _trophyService.TrophyUnlocked += OnTrophyUnlocked;
        _trophyService.OnAppStarted();
        RefreshTrophyHeader();
        _launcherTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _launcherTimeTimer.Tick += (_, _) => _trophyService.AddLauncherMinutes(1);
        _launcherTimeTimer.Start();

        // Não mantenha a tela inicial no splash aguardando sensores/WMI ou
        // serviços online. Essa etapa é executada em segundo plano após carregar
        // a biblioteca e exibir a janela principal.
        _ = InitializeBackgroundServicesAsync(statusCallback);
    }

    private async Task InitializeBackgroundServicesAsync(Action<string>? statusCallback)
    {
        await Task.Run(SoundService.Initialize);

        try
        {
            var hardwareMonitor = await Task.Run(() => new HardwareMonitorService());
            await _dispatcher.InvokeAsync(() =>
            {
                _hwMonitor = hardwareMonitor;
                _hwMonitor.MetricsUpdated += OnMetricsUpdated;
            });
            await Task.Run(hardwareMonitor.RefreshOnce);
            hardwareMonitor.Start();
        }
        catch
        {
            _hwMonitor = null;
        }

        await _dispatcher.InvokeAsync(() => statusCallback?.Invoke("Conectando contas..."));
        _ = TryRestoreXboxSessionAsync();
        _ = TryRestoreSteamSessionAsync();
        _ = TryRestoreEpicSessionAsync();
    }

    private void OnMetricsUpdated(HardwareMetrics m)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CpuUsage = Math.Round(m.CpuUsage);
            GpuUsage = Math.Round(m.GpuUsage);
            RamUsage = Math.Round(m.RamUsage);

            if (!string.IsNullOrEmpty(m.CpuName) && string.IsNullOrEmpty(CpuName))
                CpuName = m.CpuName;
            if (!string.IsNullOrEmpty(m.GpuName) && string.IsNullOrEmpty(GpuName))
            {
                GpuName = m.GpuName;
                _gpuCaps = GpuCapabilityService.Detect(m.GpuName);
                if (DetailGame is not null)
                    ScanGameTech(DetailGame);
            }

            if (!string.IsNullOrEmpty(m.RamTotal) && string.IsNullOrEmpty(RamTotal))
                RamTotal = m.RamTotal;
            if (m.StorageDrives.Count > 0 && StorageDrives.Count == 0)
            {
                foreach (var d in m.StorageDrives)
                    StorageDrives.Add(d);
            }
        });
    }

    public void Dispose()
    {
        _launcherTimeTimer?.Stop();
        _clockTimer.Stop();
        _xinput.Stop();
        _xinput.Dispose();
        _hwMonitor?.Stop();
        _hwMonitor?.Dispose();
        _xboxService?.Dispose();
        _steamService?.Dispose();
        _epicService?.Dispose();
        _fpsOverlay?.Close();
        _fpsOverlay = null;
    }

    // Fila de toasts (UI thread)
    private readonly Queue<Models.Trophy> _toastQueue = new();
    private bool _toastBusy;

    private void OnTrophyUnlocked(Models.Trophy trophy)
    {
        // Garante execucao na UI thread - TrophyService pode vir de qualquer thread
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnTrophyUnlocked(trophy));
            return;
        }

        RefreshTrophyHeader();
        _xinput.Vibrate(0.3, 0.8, 600);
        _toastQueue.Enqueue(trophy);
        ShowNextToast();
    }

    private void ShowNextToast()
    {
        if (_toastBusy || _toastQueue.Count == 0) return;
        _toastBusy = true;
        var next = _toastQueue.Dequeue();
        var toast = new Views.TrophyToastWindow(next, () =>
        {
            _toastBusy = false;
            ShowNextToast();
        });
        toast.Show();
        SoundService.PlayTrophy();
    }

    private void RefreshTrophyHeader()
    {
        TrophyGamerscoreDisplay = $"{_trophyService.EarnedGamerscore}G";
        TrophyCountDisplay      = $"{_trophyService.TrophyCount}/{_trophyService.TotalTrophies} troféus";
    }

    private void ScanGameTech(Game game)
    {
        if (game.TechInfo is null)
        {
            game.TechInfo = GameTechDetectorService.Scan(game.GameRootDirectory);
            if (game.TechInfo.HasAnyTech)
                SaveGames();
        }

        TechCompatItems.Clear();
        var tech = game.TechInfo;
        if (!tech.HasAnyTech) return;

        var gpu = _gpuCaps;

        if (!string.IsNullOrEmpty(tech.DirectXVersion))
            AddTechItem("DirectX", tech.DirectXVersion, gpu?.SupportsDirectX12 == true
                ? CompatStatus.Compatible : CompatStatus.Unknown, "Gpu");

        if (tech.HasVulkan)
            AddTechItem("Vulkan", "Sim", gpu?.SupportsVulkan == true
                ? CompatStatus.Compatible : CompatStatus.Unknown, "Gpu");

        if (tech.HasRayTracing)
            AddTechItem("Ray Tracing", "Sim", gpu is null ? CompatStatus.Unknown
                : gpu.SupportsRayTracing ? CompatStatus.Compatible : CompatStatus.Incompatible, "FlashOutline");

        if (tech.HasDLSS)
            AddTechItem("DLSS", tech.DlssVersion ?? "Sim", gpu is null ? CompatStatus.Unknown
                : gpu.SupportsDLSS ? CompatStatus.Compatible : CompatStatus.Incompatible, "NvidiaShield");

        if (tech.HasFSR)
            AddTechItem("AMD FSR", tech.FsrVersion ?? "Sim", gpu is null ? CompatStatus.Unknown
                : gpu.SupportsFSR ? CompatStatus.Compatible : CompatStatus.Incompatible, "Gpu");

        if (tech.HasXeSS)
            AddTechItem("Intel XeSS", "Sim", gpu is null ? CompatStatus.Unknown
                : gpu.SupportsXeSS ? CompatStatus.Compatible : CompatStatus.Incompatible, "IntelligenceOutline");

        if (tech.HasFrameGeneration)
            AddTechItem("Frame Generation", "Sim", gpu is null ? CompatStatus.Unknown
                : gpu.SupportsFrameGeneration ? CompatStatus.Compatible : CompatStatus.Incompatible, "MotionPlayOutline");

        if (tech.HasHDR)
            AddTechItem("HDR", "Sim", gpu?.SupportsHDR == true
                ? CompatStatus.Compatible : CompatStatus.Unknown, "Brightness7");
    }

    private void AddTechItem(string name, string detail, CompatStatus status, string icon)
    {
        TechCompatItems.Add(new TechCompatItem
        {
            TechName = name,
            Detail = detail,
            Status = status,
            IconKind = icon,
            StatusText = status switch
            {
                CompatStatus.Compatible => "OK - Compativel",
                CompatStatus.Incompatible => "NAO - Nao suportado",
                _ => "? Desconhecido"
            },
            StatusColor = status switch
            {
                CompatStatus.Compatible => "#00E676",
                CompatStatus.Incompatible => "#FF5252",
                _ => "#FFD740"
            }
        });
    }

    [RelayCommand]
    private void RescanGameTech()
    {
        if (DetailGame is null) return;
        DetailGame.TechInfo = null;
        ScanGameTech(DetailGame);
        StatusMessage = $"Tecnologias de '{DetailGame.DisplayName}' re-escaneadas";
    }

    [RelayCommand]
    private void ChangeAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha uma imagem de avatar",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
        };
        if (dialog.ShowDialog() != true) return;
        AvatarPath = dialog.FileName;
        SettingsService.Current.AvatarImagePath = dialog.FileName;
        SettingsService.Save();
        StatusMessage = "Avatar atualizado!";
        _trophyService.OnAvatarChanged();
    }

    partial void OnSearchTextChanged(string value) => _gamesView.Refresh();

    partial void OnSelectedGameChanged(Game? value)
    {
        DetailGame = value;
        ShowDetailPanel = value is not null;
        if (value is not null)
        {
            SoundService.PlayNavigate();
            ScanGameTech(value);
            _trophyService.OnGameDetailOpened();

            // Jogos salvos antes dos metadados estendidos do IGDB podem conter
            // descrição/plataforma incorretas. Atualiza esses registros uma
            // única vez ao selecioná-los, usando a busca exata/fallback seguro.
            if (!string.IsNullOrWhiteSpace(value.Summary) &&
                (!value.IsSummaryTranslated || LooksLikeEnglish(value.Summary) ||
                 string.IsNullOrWhiteSpace(value.Platforms) ||
                 value.Platforms.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                 value.Screenshots.Count == 0))
                _ = AutoFetchIgdbAsync(value);
        }
        else
        {
            TechCompatItems.Clear();
        }
    }

    private static bool LooksLikeEnglish(string text)
    {
        string[] markers = [" the ", " is ", " and ", " of ", " this ", " game ", " developed ", " published "];
        var normalized = $" {text.ToLowerInvariant()} ";
        return markers.Any(normalized.Contains);
    }

    private void ResumeGamepadAfterDialog()
    {
        _xinput.Resume();
        IsContextMenuOpen = false;
        ActiveZone = NavZone.Carousel;
        FocusedHeaderItem = "";

        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.Dispatcher.BeginInvoke(() =>
            {
                if (Application.Current.Windows.OfType<Window>()
                    .Any(window => window != mainWindow && window.IsVisible && window.IsActive))
                    return;

                mainWindow.Activate();
                mainWindow.Focus();
            }, DispatcherPriority.Input);
        }
    }

    private void LoadGames()
    {
        try
        {
            if (!File.Exists(SaveFilePath)) return;
            var json = File.ReadAllText(SaveFilePath);
            var saved = JsonSerializer.Deserialize<List<Game>>(json);
            if (saved is null) return;
            foreach (var g in saved)
                Games.Add(g);
            StatusMessage = $"{Games.Count} jogos na biblioteca";
            _gamesView.Refresh();

            if (_gamesView.Cast<Game>().FirstOrDefault() is { } first)
                SelectedGame = first;
        }
        catch { }
    }

    private void SaveGames()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SaveFilePath)!);
            var json = JsonSerializer.Serialize(Games.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SaveFilePath, json);
        }
        catch { }
    }

    public void ReorderGame(Game dragged, Game target)
    {
        if (dragged == target) return;

        // Get the visible (sorted) list
        var visible = _gamesView.Cast<Game>().ToList();
        var oldIndex = visible.IndexOf(dragged);
        var newIndex = visible.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

        visible.RemoveAt(oldIndex);
        visible.Insert(newIndex, dragged);

        // Reassign SortOrder preserving favorites-first grouping
        for (int i = 0; i < visible.Count; i++)
            visible[i].SortOrder = i;

        _gamesView.Refresh();
        SaveGames();
    }

    private int NextSortOrder() => Games.Count > 0 ? Games.Max(g => g.SortOrder) + 1 : 0;

    private static bool SameExecutable(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsExecutableRunning(string executablePath)
    {
        try
        {
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (SameExecutable(runningPath, executablePath))
                        return true;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch { }

        return false;
    }

    [RelayCommand]
    private async Task AddGame()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o executável do jogo",
            Filter = "Executável (*.exe)|*.exe",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        var newGames = new List<Game>();
        int duplicateCount = 0;

        foreach (var file in dialog.FileNames)
        {
            if (Games.Any(g => SameExecutable(g.ExecutablePath, file)))
            {
                duplicateCount++;
                continue;
            }

            var defaultName = Path.GetFileNameWithoutExtension(file);

            var nameDialog = new GameNameInputDialog(defaultName)
            {
                Owner = Application.Current.MainWindow
            };

            var gameName = nameDialog.ShowDialog() == true
                ? nameDialog.GameName
                : defaultName;

            var game = new Game
            {
                Name = gameName,
                ExecutablePath = file,
                InstallDirectory = Path.GetDirectoryName(file) ?? string.Empty,
                IconPath = IconExtractor.ExtractIcon(file),
                SortOrder = NextSortOrder()
            };

            Games.Add(game);
            newGames.Add(game);
        }

        if (newGames.Count == 0)
        {
            StatusMessage = duplicateCount > 0
                ? "Os executáveis selecionados já estão na biblioteca."
                : $"{Games.Count} jogos na biblioteca";
            return;
        }

        SaveGames();
        StatusMessage = $"{Games.Count} jogos na biblioteca";
        _trophyService.OnGameAdded(Games.Count);

        // O primeiro jogo adicionado deve assumir a seleção imediatamente,
        // exibindo o fundo e o painel de detalhes sem exigir um clique extra.
        if (SelectedGame is null)
        {
            _selectedIndex = 0;
            SelectedGame = newGames[0];
        }

        foreach (var game in newGames)
        {
            await AutoFetchAllWithProgressAsync(game);
        }
    }

    private async Task AutoFetchAllWithProgressAsync(Game game)
    {
        var progressDialog = new LoadingProgressDialog(game.DisplayName)
        {
            Owner = Application.Current.MainWindow
        };
        progressDialog.Show();

        try
        {
            progressDialog.UpdateProgress(0, "Buscando ícone...");
            var apiKey = SettingsService.Current.SteamGridDbApiKey;
            SteamGridDbService? svc = null;
            int sgdbId = 0;
            bool hasSteamGridDb = false;

            if (!string.IsNullOrEmpty(apiKey))
            {
                svc = new SteamGridDbService(apiKey);
                var sgdbGames = await svc.SearchGamesAsync(game.DisplayName);
                if (sgdbGames.Count == 0 && IsUnauthorizedError(svc.LastError))
                {
                    svc = null;
                }
                else if (sgdbGames.Count > 0)
                {
                    sgdbId = sgdbGames[0].Id;
                    hasSteamGridDb = true;
                }
            }

            if (hasSteamGridDb && svc is not null)
            {
                if (string.IsNullOrEmpty(game.IconPath) || !System.IO.File.Exists(game.IconPath))
                {
                    var icons = await svc.GetIconsAsync(sgdbId);
                    if (icons.Count > 0)
                    {
                        var path = await svc.DownloadIconAsync(icons[0].Url, game.DisplayName);
                        if (path is not null) game.IconPath = path;
                    }
                }
            }
            progressDialog.UpdateProgress(15, "Ícone concluído!");

            progressDialog.UpdateProgress(18, "Buscando logo...");
            if (hasSteamGridDb && svc is not null && string.IsNullOrEmpty(game.LogoPath))
            {
                var logos = await svc.GetLogosAsync(sgdbId);
                if (logos.Count > 0)
                {
                    var path = await svc.DownloadLogoAsync(logos[0].Url, game.DisplayName);
                    if (path is not null) game.LogoPath = path;
                }
            }
            progressDialog.UpdateProgress(30, "Logo concluída!");

            progressDialog.UpdateProgress(33, "Buscando capa...");
            if (hasSteamGridDb && svc is not null && string.IsNullOrEmpty(game.CustomImagePath))
            {
                var covers = await svc.GetCoversAsync(sgdbId);
                if (covers.Count > 0)
                {
                    var path = await svc.DownloadCoverAsync(covers[0].Url, game.DisplayName);
                    if (path is not null) game.CustomImagePath = path;
                }
            }
            progressDialog.UpdateProgress(50, "Capa concluída!");

            progressDialog.UpdateProgress(53, "Buscando fundo...");
            if (hasSteamGridDb && svc is not null && string.IsNullOrEmpty(game.BackgroundImagePath))
            {
                var heroes = await svc.GetHeroesAsync(sgdbId);
                if (heroes.Count > 0)
                {
                    var path = await svc.DownloadHeroAsync(heroes[0].Url, game.DisplayName);
                    if (path is not null) game.BackgroundImagePath = path;
                }
            }
            progressDialog.UpdateProgress(65, "Fundo concluído!");

            SaveGames();

            progressDialog.UpdateProgress(68, "Buscando descrição e informações (IGDB)...");
            var clientId     = SettingsService.Current.IgdbClientId;
            var clientSecret = SettingsService.Current.IgdbClientSecret;

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
            {
                using var igdb = new IgdbService(clientId, clientSecret);
                var results = await igdb.SearchGamesAsync(game.DisplayName);

                if (results.Count > 0)
                {
                    var igdbGame = IgdbGame.SelectBestMatch(game.DisplayName, results);

                    progressDialog.UpdateProgress(80, "Traduzindo descrição...");
                    var summary = igdbGame.Summary;
                    if (!string.IsNullOrEmpty(summary))
                        summary = await TranslationService.TranslateToPortugueseAsync(summary);

                    game.Summary     = summary;
                    ApplyIgdbMetadata(game, igdbGame);
                    await FetchIgdbScreenshotsAsync(game, igdbGame.Id);
                    game.IsSummaryTranslated = !string.IsNullOrWhiteSpace(summary) &&
                                               !string.Equals(summary, igdbGame.Summary, StringComparison.Ordinal);
                }
            }
            progressDialog.UpdateProgress(90, "Informações IGDB concluídas!");

            SaveGames();

            progressDialog.UpdateProgress(100, "Tudo pronto!");
            StatusMessage = $"'{game.DisplayName}' adicionado com sucesso!";

            await Task.Delay(600);
            progressDialog.Finish();

            _gamesView.Refresh();
            if (SelectedGame == game)
            {
                DetailGame = null;
                DetailGame = game;
                ShowDetailPanel = true;
            }
        }
        catch
        {
            StatusMessage = $"{Games.Count} jogos na biblioteca";
            try { progressDialog.Close(); } catch { }
        }
    }

    private async Task RefreshAllAssetsOnStartupAsync()
    {
        var apiKey = SettingsService.Current.SteamGridDbApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            var visualGames = Games
                .Where(g => string.IsNullOrEmpty(g.LogoPath)
                         || string.IsNullOrEmpty(g.CustomImagePath)
                         || string.IsNullOrEmpty(g.BackgroundImagePath))
                .ToList();

            if (visualGames.Count > 0)
            {
                StatusMessage = $"Atualizando visuais de {visualGames.Count} jogo(s)...";
                foreach (var game in visualGames)
                    await AutoFetchSteamGridDbAssetsAsync(game);
            }
        }

        var clientId     = SettingsService.Current.IgdbClientId;
        var clientSecret = SettingsService.Current.IgdbClientSecret;
        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            var textGames = Games
                .Where(g => !g.HasIgdbInfo)
                .ToList();

            if (textGames.Count > 0)
            {
                StatusMessage = $"Buscando informações de {textGames.Count} jogo(s) no IGDB...";
                foreach (var game in textGames)
                    await AutoFetchIgdbAsync(game);
            }

            var untranslated = Games
                .Where(g => g.HasIgdbInfo && !string.IsNullOrEmpty(g.Summary) && !g.IsSummaryTranslated)
                .ToList();

            if (untranslated.Count > 0)
            {
                StatusMessage = $"Traduzindo descrições de {untranslated.Count} jogo(s)...";
                foreach (var game in untranslated)
                {
                    try
                    {
                        StatusMessage = $"Traduzindo '{game.DisplayName}'...";
                        game.Summary = await TranslationService.TranslateToPortugueseAsync(game.Summary!);
                        game.IsSummaryTranslated = true;
                        SaveGames();
                    }
                    catch { }
                }
            }
        }

        StatusMessage = $"{Games.Count} jogos na biblioteca";
    }

    private async Task AutoFetchSteamGridDbAssetsAsync(Game game)
    {
        var apiKey = SettingsService.Current.SteamGridDbApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return;

        try
        {
            var svc = new SteamGridDbService(apiKey);
            var games = await svc.SearchGamesAsync(game.DisplayName);
            if (games.Count == 0 && IsUnauthorizedError(svc.LastError))
            {
                StatusMessage = "Erro temporário ao acessar SteamGridDB.";
                return;
            }
            if (games.Count == 0) return;

            var sgdbId = games[0].Id;

            if (string.IsNullOrEmpty(game.IconPath) || !System.IO.File.Exists(game.IconPath))
            {
                StatusMessage = $"Buscando ícone de '{game.DisplayName}'...";
                var icons = await svc.GetIconsAsync(sgdbId);
                if (icons.Count > 0)
                {
                    var path = await svc.DownloadIconAsync(icons[0].Url, game.DisplayName);
                    if (path is not null) game.IconPath = path;
                }
            }

            if (string.IsNullOrEmpty(game.LogoPath))
            {
                StatusMessage = $"Buscando logo de '{game.DisplayName}'...";
                var logos = await svc.GetLogosAsync(sgdbId);
                if (logos.Count > 0)
                {
                    var path = await svc.DownloadLogoAsync(logos[0].Url, game.DisplayName);
                    if (path is not null) game.LogoPath = path;
                }
            }

            if (string.IsNullOrEmpty(game.CustomImagePath))
            {
                StatusMessage = $"Buscando capa de '{game.DisplayName}'...";
                var covers = await svc.GetCoversAsync(sgdbId);
                if (covers.Count > 0)
                {
                    var path = await svc.DownloadCoverAsync(covers[0].Url, game.DisplayName);
                    if (path is not null) game.CustomImagePath = path;
                }
            }

            if (string.IsNullOrEmpty(game.BackgroundImagePath))
            {
                StatusMessage = $"Buscando fundo de '{game.DisplayName}'...";
                var heroes = await svc.GetHeroesAsync(sgdbId);
                if (heroes.Count > 0)
                {
                    var path = await svc.DownloadHeroAsync(heroes[0].Url, game.DisplayName);
                    if (path is not null) game.BackgroundImagePath = path;
                }
            }

            SaveGames();
            StatusMessage = $"Assets de '{game.DisplayName}' atualizados!";
        }
        catch
        {
            StatusMessage = $"{Games.Count} jogos na biblioteca";
        }
    }

    private static void ApplyIgdbMetadata(Game game, IgdbGame igdbGame)
    {
        game.IgdbId = igdbGame.Id;
        game.IgdbRating = igdbGame.Rating;
        game.ReleaseYear = igdbGame.ReleaseYear;
        game.Genres = igdbGame.GenreNames == "" ? null : TranslationService.TranslateGenres(igdbGame.GenreNames);
        game.Platforms = igdbGame.PlatformNames;
        game.GameModes = igdbGame.GameModeNames;
        game.PlayerPerspectives = igdbGame.PerspectiveNames;
        game.Themes = igdbGame.ThemeNames;
        game.Developers = igdbGame.DeveloperNames;
        game.Publishers = igdbGame.PublisherNames;
        game.Franchises = igdbGame.FranchiseNames;
        game.GameEngines = igdbGame.EngineNames;
    }

    private static async Task FetchIgdbScreenshotsAsync(Game game, int igdbId)
    {
        var clientId = SettingsService.Current.IgdbClientId;
        var clientSecret = SettingsService.Current.IgdbClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return;

        try
        {
            using var service = new IgdbService(clientId, clientSecret);
            var screenshots = await service.GetScreenshotsAsync(igdbId);
            game.Screenshots = screenshots
                .Select(screenshot => screenshot.FullUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Take(3)
                .ToList();
        }
        catch
        {
            game.Screenshots = [];
        }
    }

    private async Task AutoFetchIgdbAsync(Game game)
    {
        var clientId     = SettingsService.Current.IgdbClientId;
        var clientSecret = SettingsService.Current.IgdbClientSecret;

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return;

        try
        {
            StatusMessage = $"Buscando info de '{game.DisplayName}'...";

            using var svc = new IgdbService(clientId, clientSecret);
            var results = await svc.SearchGamesAsync(game.DisplayName);

            if (results.Count == 0)
            {
                StatusMessage = $"Nenhum resultado IGDB para '{game.DisplayName}'";
                return;
            }

            var igdbGame = IgdbGame.SelectBestMatch(game.DisplayName, results);

            var summary = igdbGame.Summary;
            if (!string.IsNullOrEmpty(summary))
            {
                StatusMessage = $"Traduzindo descrição de '{game.DisplayName}'...";
                summary = await TranslationService.TranslateToPortugueseAsync(summary);
            }

            game.Summary     = summary;
            ApplyIgdbMetadata(game, igdbGame);
            await FetchIgdbScreenshotsAsync(game, igdbGame.Id);

            SaveGames();
            StatusMessage = $"\'{game.DisplayName}\' - info IGDB aplicada!";
        }
        catch
        {
            StatusMessage = $"{Games.Count} jogos na biblioteca";
        }
    }

    [RelayCommand]
    private void ChangeImage(Game game)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Escolha uma imagem para '{game.DisplayName}'",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        game.CustomImagePath = dialog.FileName;
        SaveGames();
        StatusMessage = $"Imagem de '{game.DisplayName}' atualizada!";
    }

    [RelayCommand]
    private void ChangeLogo(Game game)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Escolha um logotipo para '{game.DisplayName}'",
            Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        game.LogoPath = dialog.FileName;
        SaveGames();
        StatusMessage = $"Logotipo de '{game.DisplayName}' atualizado!";
    }

    [RelayCommand]
    private void RemoveGame(Game game)
    {
        var visible = GetVisibleGames();
        var idx = visible.IndexOf(game);
        Games.Remove(game);
        SaveGames();
        StatusMessage = $"'{game.DisplayName}' removido. {Games.Count} jogos na biblioteca";
        visible = GetVisibleGames();
        if (visible.Count > 0)
            SelectedGame = visible[Math.Clamp(idx, 0, visible.Count - 1)];
        else
            SelectedGame = null;
    }

    [RelayCommand]
    private void ClearAllGames()
    {
        if (Games.Count == 0)
        {
            StatusMessage = "A biblioteca já está vazia.";
            return;
        }

        var result = MessageBox.Show(
            $"Deseja remover todos os {Games.Count} jogos da biblioteca?\n\nEssa ação não desinstala os jogos do computador.",
            "Limpar biblioteca",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        Games.Clear();
        SelectedGame = null;
        DetailGame = null;
        ShowDetailPanel = false;
        _selectedIndex = -1;
        _gamesView.Refresh();
        SaveGames();
        StatusMessage = "Todos os jogos foram removidos da biblioteca.";
    }

    [RelayCommand]
    private void LaunchGame(Game game)
    {
        try
        {
            var gameAlreadyRunning = !string.IsNullOrWhiteSpace(game.AppUserModelId)
                ? GameProcessMonitor.IsAnyProcessRunningInDirectory(game.InstallDirectory)
                : IsExecutableRunning(game.ExecutablePath);

            if ((!string.IsNullOrWhiteSpace(_runningGameExecutablePath)
                 && SameExecutable(_runningGameExecutablePath, game.ExecutablePath))
                || gameAlreadyRunning)
            {
                StatusMessage = $"'{game.DisplayName}' já está em execução.";
                return;
            }

            SoundService.PlayLaunch();
            _xinput.Vibrate(0.6, 0.4, 400);
            _runningGameName = game.DisplayName;
            _runningGameExecutablePath = game.ExecutablePath;

            ProcessStartInfo startInfo;
            if (!string.IsNullOrWhiteSpace(game.AppUserModelId))
            {
                startInfo = new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{game.AppUserModelId}")
                {
                    UseShellExecute = true
                };
            }
            else
            {
                var workDir = string.IsNullOrEmpty(game.InstallDirectory)
                    ? Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty
                    : game.InstallDirectory;

                startInfo = new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    UseShellExecute = true,
                    WorkingDirectory = workDir
                };
            }

            var proc = Process.Start(startInfo);

            // Mantém o jogo visível em primeiro plano sem deixar o launcher
            // aparecer por trás do overlay da IA.
            if (Application.Current.MainWindow is MainWindow launcherWindow)
                launcherWindow.Hide();

            game.LastPlayed = DateTime.Now;
            _bigPictureGamesView.Refresh();
            SaveGames();
            StatusMessage = $"Iniciando {game.DisplayName}...";


            // O launcher preserva seu estado atual durante o jogo.
            // O monitor do controle continua ativo para que a navegação
            // possa ser retomada imediatamente ao voltar ao launcher.
            var mainWin = Application.Current.MainWindow;

            if (SettingsService.Current.FpsOverlayEnabled)
            {
                _fpsOverlay = new FpsOverlayWindow();
                _fpsOverlay.Show();
            }

            // Mostra a dica da IA 10 segundos após o jogo iniciar.
            // Se o jogo fechar antes disso, o timer é cancelado em CleanupAfterGameExit.
            _aiHintTimer?.Stop();
            _aiHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _aiHintTimer.Tick += (_, _) =>
            {
                _aiHintTimer.Stop();
                _aiHintTimer = null;

                var gameProcessStarted = !string.IsNullOrWhiteSpace(game.AppUserModelId)
                    ? GameProcessMonitor.IsAnyProcessRunningInDirectory(game.InstallDirectory)
                    : IsExecutableRunning(game.ExecutablePath);
                if (gameProcessStarted)
                {
                    var aiHint = new AiHintOverlay(_xinput);
                    aiHint.Show();
                }
            };
            _aiHintTimer.Start();

            var launchTime = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                var playedMinutes = await GameProcessMonitor.WaitForGameExitAsync(
                    game.ExecutablePath,
                    proc,
                    packageInstallDirectory: game.AppUserModelId is not null ? game.InstallDirectory : null);

                await _dispatcher.BeginInvoke(() =>
                {
                    game.TotalPlayTimeMinutes += playedMinutes;
                    _trophyService.AddPlayedMinutes(playedMinutes);
                    SaveGames();
                    CleanupAfterGameExit();

                    if (mainWin is MainWindow launcherWindow)
                        launcherWindow.RestoreAfterGame();
                    _xinput.Start();
                    StatusMessage = $"{Games.Count} jogos na biblioteca";
                });
            });
        }
        catch (Exception ex)
        {
            SoundService.PlayError();
            CleanupAfterGameExit();
            _xinput.Start();
            StatusMessage = $"Erro ao iniciar {game.DisplayName}: {ex.Message}";
            Debug.WriteLine($"LaunchGame error: {ex}");
        }
    }

    private void CleanupAfterGameExit()
    {
        _runningGameName = null;
        _runningGameExecutablePath = null;
        _aiHintTimer?.Stop();
        _aiHintTimer = null;
        _fpsOverlay?.Close();
        _fpsOverlay = null;

        // Fecha o chat da IA que foi aberto como overlay durante o jogo
        foreach (var w in Application.Current.Windows.OfType<AiAssistantDialog>().ToList())
            w.Close();
    }

    [RelayCommand]
    private void ToggleFpsOverlay()
    {
        SettingsService.Current.FpsOverlayEnabled = !SettingsService.Current.FpsOverlayEnabled;
        SettingsService.Save();
        IsFpsOverlayEnabled = SettingsService.Current.FpsOverlayEnabled;
        StatusMessage = SettingsService.Current.FpsOverlayEnabled
            ? "FPS Overlay ativado - sera exibido durante os jogos"
            : "FPS Overlay desativado";
    }

    [RelayCommand]
    private void ToggleFavorite(Game game)
    {
        game.IsFavorite = !game.IsFavorite;
        SoundService.PlayFavorite();
        _gamesView.Refresh();
        _bigPictureGamesView.Refresh();
        SaveGames();
        if (game.IsFavorite) _trophyService.OnFavoriteAdded();
        StatusMessage = game.IsFavorite
            ? $"'{game.DisplayName}' adicionado aos favoritos ⭐"
            : $"'{game.DisplayName}' removido dos favoritos";
    }

    [RelayCommand]
    private async Task RenameGame(Game game)
    {
        var dialog = new RenameDialog(game.DisplayName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            game.Name = dialog.NewName;
            _gamesView.Refresh();
            SaveGames();
            _trophyService.OnGameRenamed();
            StatusMessage = $"Jogo renomeado para '{game.DisplayName}'";

            await RescanRenamedGameAsync(game);
        }
    }

    private async Task RescanRenamedGameAsync(Game game)
    {
        try
        {
            game.IsSummaryTranslated = false;
            StatusMessage = $"Procurando novamente informações e assets de '{game.DisplayName}'...";

            await AutoFetchSteamGridDbAssetsAsync(game);

            if (!game.HasIgdbInfo || !game.IsSummaryTranslated)
                await AutoFetchIgdbAsync(game);

            SaveGames();
            _gamesView.Refresh();

            if (SelectedGame == game)
            {
                DetailGame = null;
                DetailGame = game;
                ShowDetailPanel = true;
            }

            StatusMessage = $"'{game.DisplayName}' renomeado e assets atualizados!";
        }
        catch
        {
            StatusMessage = $"'{game.DisplayName}' renomeado. Não foi possível atualizar todos os assets.";
        }
    }

    [RelayCommand]
    private void SearchCover(Game game)
    {
        var apiKey = SettingsService.Current.SteamGridDbApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            var keyDialog = new ApiKeyDialog { Owner = Application.Current.MainWindow };
            if (keyDialog.ShowDialog() != true) return;
            SettingsService.Current.SteamGridDbApiKey = keyDialog.ApiKey;
            SettingsService.Save();
            apiKey = keyDialog.ApiKey;
        }

        var dialog = new CoverSearchDialog(apiKey, game.DisplayName)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true && dialog.DownloadedImagePath is not null)
        {
            game.CustomImagePath = dialog.DownloadedImagePath;
            SaveGames();
            StatusMessage = $"Capa de '{game.DisplayName}' atualizada!";
        }

        ResumeGamepadAfterDialog();
    }

    [RelayCommand]
    private void SearchBackground(Game game)
    {
        var apiKey = SettingsService.Current.SteamGridDbApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            var keyDialog = new ApiKeyDialog { Owner = Application.Current.MainWindow };
            if (keyDialog.ShowDialog() != true) return;
            SettingsService.Current.SteamGridDbApiKey = keyDialog.ApiKey;
            SettingsService.Save();
            apiKey = keyDialog.ApiKey;
        }

        var dialog = new BackgroundSearchDialog(apiKey, game.DisplayName)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.DownloadedBackgroundPath is not null)
        {
            game.BackgroundImagePath = dialog.DownloadedBackgroundPath;
            SaveGames();
            StatusMessage = $"Fundo de '{game.DisplayName}' atualizado!";
        }

        ResumeGamepadAfterDialog();
    }

    [RelayCommand]
    private void OpenTheme()
    {
        var dialog = new ThemeDialog { Owner = Application.Current.MainWindow };
        _trophyService.OnSettingsOpened();
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void ToggleSound()
    {
        SettingsService.Current.SoundEnabled = !SettingsService.Current.SoundEnabled;
        SettingsService.Save();
        IsSoundEnabled = SettingsService.Current.SoundEnabled;
        StatusMessage = SettingsService.Current.SoundEnabled
            ? "Sons ativados"
            : "Sons desativados";
        if (SettingsService.Current.SoundEnabled)
            SoundService.PlaySelect();
    }

    [RelayCommand]
    private void OpenHelp()
    {
        var dialog = new HelpDialog { Owner = Application.Current.MainWindow };
        IsHelpDialogOpen = true;
        HelpDialogNavigate = dialog.HandleGamepadInput;
        HelpDialogScroll = dialog.HandleRightStickScroll;
        dialog.ShowDialog();
        IsHelpDialogOpen = false;
        HelpDialogNavigate = null;
        HelpDialogScroll = null;
    }

    [RelayCommand]
    private void OpenAiAssistant()
    {
        // Aberto pelo launcher - chat geral, sem foco em jogo especifico
        OpenAiAssistantCore(viaGamepad: false, fromLauncher: true);
    }

    private void OnAiComboTriggered()
    {
        // Ativado por hotkey/controle durante o jogo - focado no jogo em execucao
        _dispatcher.BeginInvoke(() => OpenAiAssistantCore(viaGamepad: true, fromLauncher: false));
    }

    private void OpenAiAssistantCore(bool viaGamepad, bool fromLauncher = false)
    {
        var provider = SettingsService.Current.PreferredAiProvider
            ?? (!string.IsNullOrWhiteSpace(GetAiApiKey(AiProvider.Groq)) ? AiProvider.Groq
                : !string.IsNullOrWhiteSpace(GetAiApiKey(AiProvider.OpenAI)) ? AiProvider.OpenAI : AiProvider.Gemini);
        var apiKey = GetAiApiKey(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var dlg = new AiSettingsDialog { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() != true) return;
            provider = SettingsService.Current.PreferredAiProvider ?? AiProvider.Gemini;
            apiKey = GetAiApiKey(provider);
        }

        string? gameName = fromLauncher ? SelectedGame?.DisplayName : _runningGameName ?? SelectedGame?.DisplayName;
        var gameWindowHandle = fromLauncher ? IntPtr.Zero : FindRunningGameWindow();

        var chatDialog = new AiAssistantDialog(
            provider, apiKey, gameName, viaGamepad, _xinput,
            gameWindowHandle, fromLauncher ? null : _runningGameExecutablePath)
        {
            Owner = viaGamepad ? null : Application.Current.MainWindow,
            Topmost = true,
            ShowInTaskbar = false,
            WindowState = viaGamepad ? WindowState.Normal : WindowState.Maximized,
            Width = 620,
            Height = 640,
            ShowActivated = true
        };
        chatDialog.Show();
        _trophyService.OnAiUsed();
    }

    [RelayCommand]
    private void ConfigureAi()
    {
        var dialog = new AiSettingsDialog { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    private static string GetAiApiKey(AiProvider provider)
    {
        var settings = SettingsService.Current;
        if (provider == AiProvider.Gemini)
            return settings.GeminiApiKey;
        if (provider == AiProvider.OpenAI)
            return settings.OpenAiApiKey == AppSettings.DefaultOpenAiApiKey ? "" : settings.OpenAiApiKey;
        if (provider != AiProvider.Groq)
            return "";
        if (!string.IsNullOrWhiteSpace(settings.GroqApiKey) && !SecretsService.IsLegacyGroqApiKey(settings.GroqApiKey))
            return settings.GroqApiKey;
        return Environment.GetEnvironmentVariable("GROQ_API_KEY")?.Trim() ?? "";
    }

    private IntPtr FindRunningGameWindow()
    {
        if (string.IsNullOrWhiteSpace(_runningGameExecutablePath))
            return IntPtr.Zero;

        try
        {
            var processName = Path.GetFileNameWithoutExtension(_runningGameExecutablePath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                        return process.MainWindowHandle;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch { }

        return IntPtr.Zero;
    }

    [RelayCommand]
    private void OpenDonation()
    {
        var dialog = new PixDonationDialog { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void OpenTrophies()
    {
        var dialog = new Views.TrophiesDialog(_trophyService)
        {
            Owner = Application.Current.MainWindow
        };
        _trophyService.OnSettingsOpened();
        IsTrophyDialogOpen = true;
        TrophyDialogScroll = dialog.ScrollBy;
        dialog.ShowDialog();
        IsTrophyDialogOpen = false;
        TrophyDialogScroll = null;
    }

    /// <summary>Chamado pelo code-behind quando o Easter Egg é ativado.</summary>
    public void NotifyEasterEggFound() => _trophyService.OnEasterEggFound();


    private async Task TryRestoreXboxSessionAsync()
    {
        var clientId = SettingsService.Current.XboxClientId;
        if (string.IsNullOrEmpty(clientId) || clientId.StartsWith("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
            return;

        _xboxService?.Dispose();
        _xboxService = new XboxLiveService(clientId);

        var restored = await _xboxService.TrySilentLoginAsync();
        if (!restored) return;

        var profile = await _xboxService.GetProfileAsync();
        if (profile is not null)
        {
            XboxProfile = profile;
            XboxConnected = true;
            _trophyService.OnXboxConnected();
            StatusMessage = $"Xbox Live: {profile.Gamertag} - Gamerscore: {profile.Gamerscore:N0}";
        }
    }

    [RelayCommand]
    private async Task XboxLogin()
    {
        var clientId = SettingsService.Current.XboxClientId;

        if (string.IsNullOrEmpty(clientId) || clientId == AppSettings.DefaultXboxClientId)
        {
            if (!string.IsNullOrEmpty(AppSettings.DefaultXboxClientId)
                && AppSettings.DefaultXboxClientId != "REPLACE_WITH_YOUR_XBOX_CLIENT_ID")
            {
                clientId = AppSettings.DefaultXboxClientId;
            }
            else
            {
                var setup = new XboxSetupDialog { Owner = Application.Current.MainWindow };
                if (setup.ShowDialog() != true) return;
                SettingsService.Current.XboxClientId = setup.ClientId;
                SettingsService.Save();
                clientId = setup.ClientId;
            }
        }

        StatusMessage = "Conectando ao Xbox Live...";

        _xboxService?.Dispose();
        _xboxService = new XboxLiveService(clientId);

        var success = await _xboxService.LoginAsync();
        if (!success)
        {
            StatusMessage = "Falha ao conectar ao Xbox Live.";
            MessageBox.Show(
                "Não foi possível autenticar com o Xbox Live.\n\n" +
                "Verifique se o Client ID está correto e se o app Azure possui a permissão Xboxlive.signin.",
                "Xbox Live", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusMessage = "Carregando perfil Xbox...";
        var profile = await _xboxService.GetProfileAsync();

        if (profile is not null)
        {
            XboxProfile = profile;
            XboxConnected = true;
            _trophyService.OnXboxConnected();
            StatusMessage = $"Xbox Live: {profile.Gamertag} - Gamerscore: {profile.Gamerscore:N0}";
        }
        else
        {
            XboxConnected = true;
            StatusMessage = "Xbox Live conectado (perfil indisponível).";
        }
    }

    [RelayCommand]
    private async Task OpenXboxProfile()
    {
        if (!IsXboxLoggedIn || XboxProfile is null)
        {
            await XboxLogin();
            if (!IsXboxLoggedIn || XboxProfile is null)
                return;
        }

        StatusMessage = "Carregando biblioteca Xbox...";
        var library = await _xboxService!.GetLibraryGamesAsync();
        var installed = await Task.Run(() => _xboxService.ScanXboxInstalledGames());
        var installedByName = installed
            .GroupBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var listedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = library.Select(item =>
        {
            listedNames.Add(item.Name);
            installedByName.TryGetValue(item.Name, out var localGame);
            return new PlatformLibraryEntry
            {
                Name = item.Name,
                Platform = "Xbox",
                PlatformId = item.TitleId,
                Status = localGame is null ? "Disponível na biblioteca Xbox" : "Instalado",
                IsInstalled = localGame is not null,
                InstalledGame = localGame,
                ArtworkUrl = localGame?.CustomImagePath
            };
        }).ToList();
        entries.AddRange(installed
            .Where(game => !listedNames.Contains(game.Name))
            .Select(game => new PlatformLibraryEntry
            {
                Name = game.DisplayName,
                Platform = "Xbox",
                PlatformId = game.StoreAppId ?? string.Empty,
                Status = "Instalado neste PC",
                IsInstalled = true,
                InstalledGame = game,
                ArtworkUrl = game.CustomImagePath
            }));

        ShowPlatformLibrary("Xbox", XboxProfile.Gamertag, entries, "ms-windows-store:", entry =>
        {
            if (entry.InstalledGame is not null)
                LaunchGameCommand.Execute(entry.InstalledGame);
            else
                OpenExternalUri($"ms-windows-store://search/?query={Uri.EscapeDataString(entry.Name)}");
        }, async () =>
        {
            await _xboxService!.LogoutAsync();
            XboxProfile = null;
            XboxConnected = false;
            StatusMessage = "Desconectado do Xbox Live.";
        }, "Os jogos disponíveis são encaminhados à Microsoft Store/Xbox para instalação.");
    }

    [RelayCommand]
    private async Task ImportXboxGames()
    {
        if (_xboxService is null)
        {
            StatusMessage = "Conecte-se ao Xbox Live primeiro.";
            return;
        }

        StatusMessage = "Escaneando jogos Xbox instalados...";

        var xboxGames = await Task.Run(() => _xboxService.ScanXboxInstalledGames());

        if (xboxGames.Count == 0)
        {
            StatusMessage = "Nenhum jogo Xbox instalado foi encontrado.";
            MessageBox.Show(
                "Nenhum jogo Xbox Game Pass encontrado.\n\n" +
                "Verifique se há jogos instalados nas pastas XboxGames ou pela Microsoft Store/Xbox neste usuário do Windows.",
                "Xbox Games", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int added = 0;
        var newGames = new List<Game>();

        foreach (var xg in xboxGames)
        {
            if (Games.Any(g =>
                    g.ExecutablePath.Equals(xg.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(xg.AppUserModelId) &&
                     g.AppUserModelId?.Equals(xg.AppUserModelId, StringComparison.OrdinalIgnoreCase) == true)))
                continue;

            xg.SortOrder = NextSortOrder();
            Games.Add(xg);
            newGames.Add(xg);
            added++;
        }

        SaveGames();
        _gamesView.Refresh();
        StatusMessage = added > 0
            ? $"{added} jogo(s) Xbox importado(s)! {Games.Count} jogos na biblioteca."
            : "Todos os jogos Xbox já estavam na biblioteca.";

        foreach (var game in newGames)
        {
            await AutoFetchAllWithProgressAsync(game);
        }
    }


    private async Task TryRestoreSteamSessionAsync()
    {
        var steamId = SettingsService.Current.SteamId;

        if (string.IsNullOrEmpty(steamId))
            steamId = SteamService.DetectLocalSteamId();

        if (string.IsNullOrEmpty(steamId))
            return;

        _steamService?.Dispose();
        _steamService = new SteamService();

        var connected = await _steamService.ConnectAsync(steamId);
        if (!connected) return;

        if (string.IsNullOrEmpty(SettingsService.Current.SteamId))
        {
            SettingsService.Current.SteamId = steamId;
            SettingsService.Save();
        }

        var profile = await _steamService.GetProfileAsync();
        if (profile is not null)
        {
            profile.OwnedGamesCount = _steamService.GetInstalledGamesCount();
            SteamProfile = profile;
            SteamConnected = true;
            _trophyService.OnSteamConnected();
            StatusMessage = $"Steam: {profile.PersonaName} - {profile.OwnedGamesCount:N0} jogos";
        }
    }

    [RelayCommand]
    private async Task SteamLogin()
    {
        var steamId = SteamService.DetectLocalSteamId()
                      ?? SettingsService.Current.SteamId;

        if (string.IsNullOrEmpty(steamId))
        {
            var setup = new SteamSetupDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (setup.ShowDialog() != true) return;

            steamId = setup.SteamIdOrVanity;
        }

        StatusMessage = "Conectando à Steam...";

        _steamService?.Dispose();
        _steamService = new SteamService();

        var success = await _steamService.ConnectAsync(steamId);
        if (!success)
        {
            StatusMessage = "Falha ao conectar à Steam.";
            MessageBox.Show(
                "Não foi possível conectar à Steam.\n\n" +
                "Verifique se o Steam está instalado e você está logado.",
                "Steam", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SettingsService.Current.SteamId = _steamService.SteamId ?? steamId;
        SettingsService.Save();

        StatusMessage = "Carregando perfil Steam...";
        var profile = await _steamService.GetProfileAsync();

        if (profile is not null)
        {
            profile.OwnedGamesCount = _steamService.GetInstalledGamesCount();
            SteamProfile = profile;
            SteamConnected = true;
            _trophyService.OnSteamConnected();
            StatusMessage = $"Steam: {profile.PersonaName} - {profile.OwnedGamesCount:N0} jogos";
        }
        else
        {
            SteamConnected = true;
            StatusMessage = "Steam conectada (perfil indisponível).";
        }
    }

    [RelayCommand]
    private async Task OpenSteamProfile()
    {
        if (!IsSteamConnected || SteamProfile is null)
        {
            await SteamLogin();
            if (!IsSteamConnected || SteamProfile is null)
                return;
        }

        StatusMessage = "Carregando biblioteca Steam...";
        var installed = await Task.Run(() => _steamService!.ScanSteamInstalledGames());
        var proxyUrl = SettingsService.Current.SteamLibraryProxyUrl;
        var owned = string.IsNullOrWhiteSpace(proxyUrl)
            ? await _steamService!.GetOwnedGamesAsync(SettingsService.Current.SteamApiKey)
            : await _steamService!.GetOwnedGamesViaProxyAsync(proxyUrl);
        if (string.IsNullOrWhiteSpace(proxyUrl) && _steamService.IsOwnedGamesApiKeyRejected)
        {
            var keyDialog = new SteamApiKeyDialog { Owner = Application.Current.MainWindow };
            if (keyDialog.ShowDialog() == true)
            {
                SettingsService.Current.SteamApiKey = keyDialog.ApiKey;
                SettingsService.Save();
                owned = await _steamService.GetOwnedGamesAsync(SettingsService.Current.SteamApiKey);
            }
        }
        var installedById = installed
            .Where(game => !string.IsNullOrWhiteSpace(game.StoreAppId))
            .GroupBy(game => game.StoreAppId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var includedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = owned.Select(game =>
        {
            includedIds.Add(game.AppId);
            installedById.TryGetValue(game.AppId, out var localGame);
            return new PlatformLibraryEntry
            {
                Name = game.Name,
                Platform = "Steam",
                PlatformId = game.AppId,
                Status = localGame is null ? "Disponível na sua biblioteca" : "Instalado",
                IsInstalled = localGame is not null,
                InstalledGame = localGame,
                ArtworkUrl = localGame?.CustomImagePath
            };
        }).ToList();
        entries.AddRange(installed
            .Where(game => string.IsNullOrWhiteSpace(game.StoreAppId) || !includedIds.Contains(game.StoreAppId))
            .Select(game => new PlatformLibraryEntry
            {
                Name = game.DisplayName,
                Platform = "Steam",
                PlatformId = game.StoreAppId ?? string.Empty,
                Status = "Instalado neste PC",
                IsInstalled = true,
                InstalledGame = game,
                ArtworkUrl = game.CustomImagePath
            }));

        ShowPlatformLibrary("Steam", SteamProfile.PersonaName, entries, "steam://open/main", entry =>
        {
            if (entry.InstalledGame is not null)
                LaunchGameCommand.Execute(entry.InstalledGame);
            else if (!string.IsNullOrWhiteSpace(entry.PlatformId))
                OpenExternalUri($"steam://install/{Uri.EscapeDataString(entry.PlatformId)}");
            else
                OpenExternalUri($"https://store.steampowered.com/search/?term={Uri.EscapeDataString(entry.Name)}");
        }, () =>
        {
            _steamService?.Disconnect();
            _steamService?.Dispose();
            _steamService = null;
            SteamProfile = null;
            SteamConnected = false;
            SettingsService.Current.SteamId = string.Empty;
            SettingsService.Save();
            StatusMessage = "Desconectado da Steam.";
            return Task.CompletedTask;
        }, owned.Count > 0
            ? "A opção BAIXAR abre a instalação pelo cliente oficial da Steam."
            : $"A lista completa da Steam não foi carregada: {_steamService.LastOwnedGamesError ?? "nenhum detalhe informado"} Os jogos instalados detectados ainda são exibidos.");
    }

    [RelayCommand]
    private async Task ImportSteamGames()
    {
        if (_steamService is null)
        {
            StatusMessage = "Conecte-se à Steam primeiro.";
            return;
        }

        StatusMessage = "Escaneando jogos Steam instalados...";

        var steamGames = await Task.Run(() => _steamService.ScanSteamInstalledGames());

        if (steamGames.Count == 0)
        {
            StatusMessage = "Nenhum jogo Steam instalado encontrado.";
            MessageBox.Show(
                "Nenhum jogo Steam instalado encontrado.\n\n" +
                "Verifique se há jogos instalados nas pastas do Steam.",
                "Steam Games", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int added = 0;
        var newGames = new List<Game>();

        foreach (var sg in steamGames)
        {
            if (Games.Any(g => g.ExecutablePath.Equals(sg.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            sg.SortOrder = NextSortOrder();
            Games.Add(sg);
            newGames.Add(sg);
            added++;
        }

        SaveGames();
        _gamesView.Refresh();
        StatusMessage = added > 0
            ? $"{added} jogo(s) Steam importado(s)! {Games.Count} jogos na biblioteca."
            : "Todos os jogos Steam já estavam na biblioteca.";

        foreach (var game in newGames)
        {
            await AutoFetchAllWithProgressAsync(game);
        }
    }


    private Task TryRestoreEpicSessionAsync()
    {
        _epicService?.Dispose();
        _epicService = new EpicGamesService();

        var connected = _epicService.Connect();
        if (!connected) return Task.CompletedTask;

        var profile = _epicService.GetProfile();
        if (profile is not null)
        {
            EpicProfile = profile;
            EpicConnected = true;
            _trophyService.OnEpicConnected();
            StatusMessage = $"Epic Games: {profile.DisplayName} - {profile.InstalledGamesCount:N0} jogos";
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task EpicLogin()
    {
        var epicPath = EpicGamesService.DetectEpicInstallPath();

        if (string.IsNullOrEmpty(epicPath))
        {
            var setup = new EpicSetupDialog
            {
                Owner = Application.Current.MainWindow
            };

            if (setup.ShowDialog() != true) return Task.CompletedTask;

            epicPath = setup.EpicPath;
        }

        StatusMessage = "Conectando à Epic Games...";

        _epicService?.Dispose();
        _epicService = new EpicGamesService();

        var success = _epicService.Connect(epicPath);
        if (!success)
        {
            StatusMessage = "Falha ao conectar à Epic Games.";
            MessageBox.Show(
                "Não foi possível detectar jogos da Epic Games.\n\n" +
                "Verifique se o Epic Games Launcher está instalado e há jogos instalados.",
                "Epic Games", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        var profile = _epicService.GetProfile();

        if (profile is not null)
        {
            EpicProfile = profile;
            EpicConnected = true;
            _trophyService.OnEpicConnected();
            StatusMessage = $"Epic Games: {profile.DisplayName} - {profile.InstalledGamesCount:N0} jogos";
        }
        else
        {
            EpicConnected = true;
            StatusMessage = "Epic Games conectada (perfil indisponível).";
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenEpicProfile()
    {
        if (!IsEpicConnected || EpicProfile is null)
        {
            await EpicLogin();
            if (!IsEpicConnected || EpicProfile is null)
                return;
        }

        StatusMessage = "Carregando biblioteca Epic...";
        var installed = await Task.Run(() => _epicService!.ScanEpicInstalledGames());
        var entries = installed.Select(game => new PlatformLibraryEntry
        {
            Name = game.DisplayName,
            Platform = "Epic",
            PlatformId = game.StoreAppId ?? string.Empty,
            Status = "Instalado",
            IsInstalled = true,
            InstalledGame = game,
            ArtworkUrl = game.CustomImagePath
        }).ToList();

        ShowPlatformLibrary("Epic Games", EpicProfile.DisplayName, entries, "com.epicgames.launcher://", entry =>
        {
            if (entry.InstalledGame is not null)
                LaunchGameCommand.Execute(entry.InstalledGame);
            else if (!string.IsNullOrWhiteSpace(entry.PlatformId))
                OpenExternalUri($"com.epicgames.launcher://apps/{Uri.EscapeDataString(entry.PlatformId)}?action=install&silent=true");
            else
                OpenExternalUri($"https://store.epicgames.com/browse?q={Uri.EscapeDataString(entry.Name)}");
        }, () =>
        {
            _epicService?.Disconnect();
            _epicService?.Dispose();
            _epicService = null;
            EpicProfile = null;
            EpicConnected = false;
            StatusMessage = "Desconectado da Epic Games.";
            return Task.CompletedTask;
        }, "A integração Epic atual não autentica na conta: ela detecta somente jogos instalados neste PC. Para listar compras não instaladas, é necessário adicionar autenticação oficial à Epic. O cliente Epic é responsável pelo download e instalação.");
    }

    private void ShowPlatformLibrary(
        string platform,
        string account,
        IEnumerable<PlatformLibraryEntry> entries,
        string clientUri,
        Action<PlatformLibraryEntry> gameAction,
        Func<Task> disconnectAction,
        string notice)
    {
        var dialog = new PlatformLibraryDialog(platform, account, entries, clientUri, gameAction,
            () => _ = disconnectAction(), notice)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ContentRendered += async (_, _) => await LoadPlatformArtworkAsync(entries);

        IsProfileDialogOpen = true;
        ProfileDialogNavigate = dialog.HandleGamepadInput;
        dialog.ShowDialog();
        IsProfileDialogOpen = false;
        ProfileDialogNavigate = null;
    }

    private async Task LoadPlatformArtworkAsync(IEnumerable<PlatformLibraryEntry> entries)
    {
        var apiKey = SettingsService.Current.SteamGridDbApiKey;
        var clientId = SettingsService.Current.IgdbClientId;
        var clientSecret = SettingsService.Current.IgdbClientSecret;
        if (string.IsNullOrWhiteSpace(apiKey) &&
            (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)))
            return;

        using var service = string.IsNullOrWhiteSpace(apiKey) ? null : new SteamGridDbService(apiKey);
        using var igdb = string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            ? null
            : new IgdbService(clientId, clientSecret);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.ArtworkUrl) && File.Exists(entry.ArtworkUrl))
                continue;

            var normalizedName = NormalizeGameTitle(entry.Name);
            if (service is not null)
            {
                var matches = await service.SearchGamesAsync(entry.Name);
                var exactMatch = matches.FirstOrDefault(game => NormalizeGameTitle(game.Name) == normalizedName);
                if (exactMatch is not null)
                {
                    var covers = await service.GetCoversAsync(exactMatch.Id);
                    if (covers.Count > 0)
                    {
                        var sgdbPath = await service.DownloadCoverAsync(covers[0].Url, entry.Name);
                        if (sgdbPath is not null)
                        {
                            entry.ArtworkUrl = sgdbPath;
                            continue;
                        }
                    }
                }
            }

            if (igdb is null)
                continue;

            var igdbMatches = await igdb.SearchGamesAsync(entry.Name);
            var igdbMatch = igdbMatches.FirstOrDefault(game => NormalizeGameTitle(game.Name) == normalizedName);
            if (igdbMatch?.CoverUrl is not { Length: > 0 } coverUrl)
                continue;

            var igdbPath = await igdb.DownloadCoverAsync(coverUrl, entry.Name);
            if (igdbPath is not null)
                entry.ArtworkUrl = igdbPath;
        }
    }

    private static string NormalizeGameTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static void OpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível abrir o cliente da plataforma: {ex.Message}",
                "Plataforma", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task ImportEpicGames()
    {
        if (_epicService is null)
        {
            StatusMessage = "Conecte-se à Epic Games primeiro.";
            return;
        }

        StatusMessage = "Escaneando jogos Epic Games instalados...";

        var epicGames = await Task.Run(() => _epicService.ScanEpicInstalledGames());

        if (epicGames.Count == 0)
        {
            StatusMessage = "Nenhum jogo Epic Games instalado encontrado.";
            MessageBox.Show(
                "Nenhum jogo da Epic Games encontrado.\n\n" +
                "Verifique se há jogos instalados pelo Epic Games Launcher.",
                "Epic Games", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int added = 0;
        var newGames = new List<Game>();

        foreach (var eg in epicGames)
        {
            if (Games.Any(g => g.ExecutablePath.Equals(eg.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            eg.SortOrder = NextSortOrder();
            Games.Add(eg);
            newGames.Add(eg);
            added++;
        }

        SaveGames();
        _gamesView.Refresh();
        StatusMessage = added > 0
            ? $"{added} jogo(s) Epic importado(s)! {Games.Count} jogos na biblioteca."
            : "Todos os jogos Epic já estavam na biblioteca.";

        foreach (var game in newGames)
        {
            await AutoFetchAllWithProgressAsync(game);
        }
    }
    [RelayCommand]
    private async Task FetchIgdbInfo(Game game)
    {
        var clientId     = SettingsService.Current.IgdbClientId;
        var clientSecret = SettingsService.Current.IgdbClientSecret;

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            var setup = new IgdbSetupDialog { Owner = Application.Current.MainWindow };
            if (setup.ShowDialog() != true) return;
            SettingsService.Current.IgdbClientId     = setup.ClientId;
            SettingsService.Current.IgdbClientSecret = setup.ClientSecret;
            SettingsService.Save();
            clientId     = setup.ClientId;
            clientSecret = setup.ClientSecret;
        }

        var dialog = new IgdbGameInfoDialog(clientId, clientSecret, game.DisplayName)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.SelectedGame is { } igdbGame)
        {
            var summary = igdbGame.Summary;
            if (!string.IsNullOrEmpty(summary))
            {
                StatusMessage = $"Traduzindo descrição de '{game.DisplayName}'...";
                summary = await TranslationService.TranslateToPortugueseAsync(summary);
            }

            game.Summary     = summary;
            ApplyIgdbMetadata(game, igdbGame);
            await FetchIgdbScreenshotsAsync(game, igdbGame.Id);

            SaveGames();
            StatusMessage = $"\'{game.DisplayName}\' - info IGDB aplicada!";

            await AutoFetchSteamGridDbAssetsAsync(game);

            SaveGames();
            StatusMessage = $"'{game.DisplayName}' atualizado!";
        }

        ResumeGamepadAfterDialog();
    }

    [RelayCommand]
    private void ShowDetail(Game game)
    {
        DetailGame = game;
        ShowDetailPanel = true;
    }

    [RelayCommand]
    private void CloseDetail()
    {
        ShowDetailPanel = false;
        DetailGame = null;
    }

    [RelayCommand]
    private void LaunchDetailGame()
    {
        if (DetailGame is not null)
            LaunchGame(DetailGame);
    }


    private void OnGamepadConnectionChanged(bool connected)
    {
        _dispatcher.BeginInvoke(() =>
        {
            GamepadConnected = connected;
            var name = _xinput.ControllerName;
            bool isPlayStation = name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                              || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase);
        var buttons = isPlayStation ? "X = Jogar  |  O = Opcoes" : "A = Jogar  |  Y = Opcoes";
            GamepadStatus = connected
                ? string.IsNullOrEmpty(name)
                    ? $"[CTRL] Controle conectado  |  {buttons}"
                    : $"[CTRL] {name} conectado  |  {buttons}"
                : "";
            if (!connected)
                GamepadBatteryLevel = -1;
            if (connected)
            {
                ActiveZone = NavZone.Carousel;
                FocusedHeaderItem = "";
                if (SelectedGame is null && GetVisibleGames().Count > 0)
                {
                    _selectedIndex = 0;
                    SelectedGame = GetVisibleGames()[0];
                }
            }
            if (!connected)
            {
                // A desconexão/hibernação do controle não deve apagar a
                // seleção visual do launcher. Caso contrário, após alguns
                // minutos sem usar o controle, o painel de detalhes e o
                // fundo do jogo desaparecem, dando a impressão de que a
                // biblioteca ficou vazia.
                FocusedHeaderItem = "";
                ActiveZone = NavZone.Carousel;
            }
        });
    }

    private void OnBatteryChanged(int percent)
    {
        _dispatcher.BeginInvoke(() =>
        {
            GamepadBatteryLevel = percent;
        });
    }

    private void OnGamepadButton(GamepadButton button)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (IsAnimationLoading) return;

            // Enquanto o chat da IA estiver aberto, somente ele deve receber
            // os comandos do controle. Isso evita que X/Y/A acionem ações do
            // launcher, como buscar capa ou favoritar o jogo em segundo plano.
            if (Application.Current.Windows.OfType<AiAssistantDialog>().Any())
                return;

            var activeDialog = Application.Current.Windows
                .OfType<Window>()
                .LastOrDefault(window => window != Application.Current.MainWindow &&
                                         window.IsVisible &&
                                         (window.IsActive || window.IsKeyboardFocusWithin));

            // Diálogos com listas e grades próprias precisam receber o input
            // diretamente para que o controle consiga selecionar e aplicar
            // capas, fundos e resultados do IGDB.
            switch (activeDialog)
            {
                case CoverSearchDialog coverDialog:
                    coverDialog.HandleGamepadInput(button);
                    return;
                case BackgroundSearchDialog backgroundDialog:
                    backgroundDialog.HandleGamepadInput(button);
                    return;
                case IgdbGameInfoDialog igdbDialog:
                    igdbDialog.HandleGamepadInput(button);
                    return;
                case ScreenshotViewerDialog screenshotDialog:
                    screenshotDialog.HandleGamepadInput(button);
                    return;
            }

            if (activeDialog is not null &&
                !IsHelpDialogOpen && !IsTrophyDialogOpen && !IsProfileDialogOpen)
            {
                HandleDialogGamepadInput(activeDialog, button);
                return;
            }

            // Com um jogo em execução, o launcher não deve processar comandos
            // do controle em segundo plano. Select continua disponível para
            // reabrir o chat.
            if (!string.IsNullOrWhiteSpace(_runningGameName))
            {
                if (button == GamepadButton.Back)
                    OnAiComboTriggered();
                return;
            }

            if (IsHelpDialogOpen)
            {
                HelpDialogNavigate?.Invoke(button);
                return;
            }

            if (IsTrophyDialogOpen)
            {
                if (button == GamepadButton.B)
                    Application.Current.Windows.OfType<Views.TrophiesDialog>().FirstOrDefault()?.Close();
                return;
            }

            if (IsProfileDialogOpen)
            {
                ProfileDialogNavigate?.Invoke(button);
                return;
            }

            if (IsContextMenuOpen)
            {
                ContextMenuNavigate?.Invoke(button);
                return;
            }

            if (IsBigPictureMode)
            {
                HandleBigPictureGamepadInput(button);
                return;
            }

            switch (button)
            {
                case GamepadButton.DPadUp:
                    if (ActiveZone == NavZone.Carousel)
                    {
                        var gamesUp = GetVisibleGames();
                        if (gamesUp.Count > 0)
                        {
                            NavigateCarousel(-_gamepadGridColumns, gamesUp);
                            SoundService.PlayNavigate();
                        }
                    }
                    else
                        SwitchZone(-1);
                    return;
                case GamepadButton.DPadDown:
                    if (ActiveZone == NavZone.Carousel)
                    {
                        var gamesDown = GetVisibleGames();
                        if (gamesDown.Count > 0)
                        {
                            NavigateCarousel(_gamepadGridColumns, gamesDown);
                            SoundService.PlayNavigate();
                        }
                    }
                    else
                        SwitchZone(1);
                    return;

                case GamepadButton.DPadLeft:
                    NavigateInZone(-1);
                    return;
                case GamepadButton.DPadRight:
                    NavigateInZone(1);
                    return;

                case GamepadButton.LeftShoulder:
                    if (ActiveZone == NavZone.Carousel)
                    {
                        var vis = GetVisibleGames();
                        if (vis.Count > 0) { NavigateCarousel(-5, vis); SoundService.PlayNavigate(); }
                    }
                    return;
                case GamepadButton.RightShoulder:
                    if (ActiveZone == NavZone.Carousel)
                    {
                        var vis = GetVisibleGames();
                        if (vis.Count > 0) { NavigateCarousel(5, vis); SoundService.PlayNavigate(); }
                    }
                    return;

                case GamepadButton.A:
                    // A volta a executar imediatamente a ação principal.
                    ExecuteAAction();
                    return;

                case GamepadButton.Y:
                    if (SelectedGame is not null)
                    {
                        ActiveZone = NavZone.Carousel;
                        SoundService.PlaySelect();
                        OpenSelectedGameMenu?.Invoke();
                    }
                    return;
                case GamepadButton.X:
                    if (SelectedGame is not null)
                        SearchCover(SelectedGame);
                    return;
                case GamepadButton.B:
                    HandleBack();
                    return;

                case GamepadButton.Start:
                    // O botão Start fica livre para os comandos do jogo.
                    return;
                case GamepadButton.Back:
                    // Select (dois quadrados) abre o assistente durante o jogo.
                    OnAiComboTriggered();
                    return;

                case GamepadButton.RightThumb:
                    ToggleBigPictureMode();
                    return;
            }
        });
    }

    private void HandleBigPictureGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadLeft:
                NavigateBigPictureGames(-1);
                break;
            case GamepadButton.DPadRight:
                NavigateBigPictureGames(1);
                break;
            case GamepadButton.DPadUp:
                IsBigPictureDetailsOpen = true;
                SoundService.PlayNavigate();
                break;
            case GamepadButton.DPadDown:
                IsBigPictureDetailsOpen = false;
                SoundService.PlayNavigate();
                break;
            case GamepadButton.LeftShoulder:
                CycleBigPictureCollection(-1);
                break;
            case GamepadButton.RightShoulder:
                CycleBigPictureCollection(1);
                break;
            case GamepadButton.A:
                if (SelectedGame is not null)
                {
                    SoundService.PlaySelect();
                    LaunchGame(SelectedGame);
                }
                break;
            case GamepadButton.B:
                if (IsBigPictureDetailsOpen)
                    IsBigPictureDetailsOpen = false;
                else
                    ToggleBigPictureMode();
                break;
            case GamepadButton.X:
                IsBigPictureDetailsOpen = !IsBigPictureDetailsOpen;
                SoundService.PlaySelect();
                break;
            case GamepadButton.Y:
                if (SelectedGame is not null)
                    ToggleFavorite(SelectedGame);
                break;
            case GamepadButton.RightThumb:
                ToggleBigPictureMode();
                break;
        }
    }

    private static void HandleDialogGamepadInput(Window dialog, GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                break;
            case GamepadButton.DPadDown:
                dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                break;
            case GamepadButton.DPadLeft:
                dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
                break;
            case GamepadButton.DPadRight:
                dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
                break;
            case GamepadButton.A when Keyboard.FocusedElement is System.Windows.Controls.Button buttonElement:
                buttonElement.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                break;
            case GamepadButton.B:
            case GamepadButton.Back:
                dialog.Close();
                break;
        }
    }

    private void ExecuteAAction()
    {
        // No Big Picture, A sempre lança o jogo selecionado no carrossel.
        if (IsBigPictureMode && ActiveZone == NavZone.Carousel && SelectedGame is not null)
        {
            SoundService.PlaySelect();
            LaunchGame(SelectedGame);
        }
        else
        {
            ActivateCurrentItem();
        }
    }

    private void OnRightStickY(double value)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (IsHelpDialogOpen)
                HelpDialogScroll?.Invoke(value);
            else if (IsTrophyDialogOpen)
                TrophyDialogScroll?.Invoke(value);
        });
    }

    private void SwitchZone(int direction)
    {
        var zones = Enum.GetValues<NavZone>();
        int current = (int)ActiveZone;
        int next = Math.Clamp(current + direction, 0, zones.Length - 1);

        if ((NavZone)next == NavZone.Actions && !ShowDetailPanel)
            next = Math.Clamp(next + direction, 0, zones.Length - 1);

        if (next == current) return;

        SoundService.PlayZoneChange();
        ActiveZone = (NavZone)next;

        switch (ActiveZone)
        {
            case NavZone.Header:
                FocusedHeaderItem = HeaderItems[HeaderIndex];
                break;
            case NavZone.Carousel:
                FocusedHeaderItem = "";
                var visible = GetVisibleGames();
                if (visible.Count > 0 && (_selectedIndex < 0 || _selectedIndex >= visible.Count))
                {
                    _selectedIndex = 0;
                    SelectedGame = visible[0];
                }
                break;
            case NavZone.Actions:
                FocusedHeaderItem = "";
                break;
        }

        UpdateGamepadStatusForZone();
    }

    private void NavigateInZone(int direction)
    {
        switch (ActiveZone)
        {
            case NavZone.Header:
                HeaderIndex = Math.Clamp(HeaderIndex + direction, 0, HeaderItems.Length - 1);
                FocusedHeaderItem = HeaderItems[HeaderIndex];
                SoundService.PlayNavigate();
                break;

            case NavZone.Carousel:
                var visible = GetVisibleGames();
                if (visible.Count > 0)
                {
                    NavigateCarousel(direction, visible);
                    SoundService.PlayNavigate();
                }
                break;

            case NavZone.Actions:
                break;
        }
    }

    private void NavigateCarousel(int delta, List<Game> visible)
    {
        if (_selectedIndex < 0 || _selectedIndex >= visible.Count)
            _selectedIndex = 0;

        int newIndex = Math.Clamp(_selectedIndex + delta, 0, visible.Count - 1);
        _selectedIndex = newIndex;
        SelectedGame = visible[newIndex];
    }

    public void SetGamepadGridColumns(int columns)
        => _gamepadGridColumns = Math.Max(1, columns);

    private void ActivateCurrentItem()
    {
        SoundService.PlaySelect();
        switch (ActiveZone)
        {
            case NavZone.Header:
                ActivateHeaderItem(HeaderItems[HeaderIndex]);
                break;

            case NavZone.Carousel:
                if (SelectedGame is not null)
                    LaunchGame(SelectedGame);
                break;

            case NavZone.Actions:
                if (SelectedGame is not null)
                    LaunchGame(SelectedGame);
                break;
        }
    }

    private void ActivateHeaderItem(string item)
    {
        switch (item)
        {
            case "Xbox":
                OpenXboxProfileCommand.Execute(null);
                break;
            case "Steam":
                OpenSteamProfileCommand.Execute(null);
                break;
            case "Help":
                OpenHelp();
                break;
            case "Settings":
                OpenSettingsMenu();
                break;
            case "AddGame":
                AddGameCommand.Execute(null);
                break;
            case "Theme":
                OpenTheme();
                break;
        }
    }

    private void HandleBack()
    {
        SoundService.PlayBack();
        if (ActiveZone == NavZone.Header)
        {
            ActiveZone = NavZone.Carousel;
            FocusedHeaderItem = "";
            UpdateGamepadStatusForZone();
        }
        else if (ActiveZone == NavZone.Actions)
        {
            ActiveZone = NavZone.Carousel;
            UpdateGamepadStatusForZone();
        }
        else if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
        }
    }

    private void OpenSettingsMenu()
    {
        _dispatcher.BeginInvoke(() =>
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow?.FindName("BtnGear") is System.Windows.Controls.Button gearBtn
                && gearBtn.ContextMenu is not null)
            {
                gearBtn.ContextMenu.PlacementTarget = gearBtn;
                gearBtn.ContextMenu.IsOpen = true;
            }
        });
    }

    private void UpdateGamepadStatusForZone()
    {
        var name = _xinput.ControllerName;
        bool isPS = name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase);

        string hint = ActiveZone switch
        {
            NavZone.Header => isPS
                ? "D-Pad = Navegar  |  X = Selecionar  |  Y = Jogos"
                : "D-Pad = Navegar  |  A = Selecionar  |  Y = Jogos",
            NavZone.Actions => isPS
                ? "X = Jogar  |  B = Menu  |  Y = Jogos"
                : "A = Jogar  |  B = Menu  |  Y = Jogos",
            NavZone.Carousel => isPS
                ? "D-Pad = Jogos  |  X = Jogar  |  O = Opcoes  |  Y = Acoes"
                : "D-Pad = Jogos  |  A = Jogar  |  Y = Opcoes  |  B = Acoes",
            _ => ""
        };

        string prefix = string.IsNullOrEmpty(name) ? "[CTRL]" : $"[CTRL] {name}";
        string battery = GamepadBatteryLevel >= 0 ? $"  |  BAT {GamepadBatteryLevel}%" : "";
        GamepadStatus = $"{prefix}{battery}  |  {hint}";
    }

    private List<Game> GetVisibleGames() =>
        _gamesView.Cast<Game>().ToList();

    private static bool IsUnauthorizedError(string? error)
    {
        return error is not null &&
               (error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("401", StringComparison.Ordinal));
    }

    }




