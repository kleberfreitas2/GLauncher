using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Views;

public partial class PlatformLibraryDialog : Window
{
    private readonly ICollectionView _gamesView;
    private readonly Action<PlatformLibraryEntry> _gameAction;
    private readonly string? _clientUri;
    private readonly Action? _disconnectAction;

    public PlatformLibraryDialog(
        string platformName,
        string accountName,
        IEnumerable<PlatformLibraryEntry> games,
        string? clientUri,
        Action<PlatformLibraryEntry> gameAction,
        Action? disconnectAction = null,
        string? notice = null)
    {
        InitializeComponent();
        PlatformTitle.Text = platformName;
        AccountText.Text = accountName;
        NoticeText.Text = notice ?? string.Empty;
        _clientUri = clientUri;
        _gameAction = gameAction;
        _disconnectAction = disconnectAction;

        var entries = new ObservableCollection<PlatformLibraryEntry>(games);
        _gamesView = CollectionViewSource.GetDefaultView(entries);
        _gamesView.Filter = FilterEntry;
        GamesItems.ItemsSource = _gamesView;
        RefreshCount();

        if (_disconnectAction is null)
            DisconnectButtonVisibility = Visibility.Collapsed;
    }

    private Visibility DisconnectButtonVisibility
    {
        set
        {
                if (DisconnectButton is Button button)
                button.Visibility = value;
        }
    }

    private bool FilterEntry(object item)
    {
        if (item is not PlatformLibraryEntry game)
            return false;

        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(query) && !game.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        return StatusFilter.SelectedIndex switch
        {
            1 => game.IsInstalled,
            2 => !game.IsInstalled,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _gamesView.Refresh();
        RefreshCount();
    }

    private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_gamesView is null)
            return;

        _gamesView.Refresh();
        RefreshCount();
    }

    private void RefreshCount()
    {
        var count = _gamesView.Cast<object>().Count();
        CountText.Text = $"{count:N0} jogo(s)";
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GameAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PlatformLibraryEntry game)
            _gameAction(game);
    }

    private void OpenClient_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_clientUri))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_clientUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NoticeText.Text = $"Nao foi possivel abrir a plataforma: {ex.Message}";
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _disconnectAction?.Invoke();
        DialogResult = true;
    }

    public void HandleGamepadInput(GamepadButton button)
    {
        switch (button)
        {
            case GamepadButton.DPadUp:
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                break;
            case GamepadButton.DPadDown:
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                break;
            case GamepadButton.DPadLeft:
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
                break;
            case GamepadButton.DPadRight:
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
                break;
            case GamepadButton.A when Keyboard.FocusedElement is Button buttonElement:
                buttonElement.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            case GamepadButton.B:
            case GamepadButton.Back:
                DialogResult = false;
                break;
        }
    }
}
