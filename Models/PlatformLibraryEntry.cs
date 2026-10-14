using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.Models;

public sealed partial class PlatformLibraryEntry : ObservableObject
{
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required string PlatformId { get; init; }
    public required string Status { get; init; }
    public required bool IsInstalled { get; init; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncStatusText))]
    private bool isImported;
    [ObservableProperty] private string? artworkUrl;
    public Game? InstalledGame { get; init; }
    public Game? StoreGame { get; init; }
    public string ActionText => IsInstalled ? "JOGAR" : "BAIXAR";
    public string SyncStatusText => IsImported ? "Já está no carrossel" : "Ainda não está no carrossel";
}
