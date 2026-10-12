using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GameLauncher.Services;

namespace GameLauncher.Views;

public partial class AiSettingsDialog : Window
{
    private readonly Dictionary<AiProvider, string> _keys;
    private AiProvider? _activeProvider;

    public AiSettingsDialog()
    {
        InitializeComponent();
        var settings = SettingsService.Current;
        _keys = new()
        {
            [AiProvider.Gemini] = settings.GeminiApiKey,
            [AiProvider.Groq] = SecretsService.IsLegacyGroqApiKey(settings.GroqApiKey) ? "" : settings.GroqApiKey,
            [AiProvider.OpenAI] = settings.OpenAiApiKey == GameLauncher.Models.AppSettings.DefaultOpenAiApiKey ? "" : settings.OpenAiApiKey
        };
        ProviderBox.ItemsSource = new[] { AiProvider.Gemini, AiProvider.Groq, AiProvider.OpenAI };
        ProviderBox.SelectedItem = settings.PreferredAiProvider ?? AiProvider.Gemini;
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderBox.SelectedItem is not AiProvider provider || _keys is null)
            return;
        if (_activeProvider is { } previous)
            _keys[previous] = KeyBox.Password.Trim();
        _activeProvider = provider;
        KeyBox.Password = _keys[provider];
        ValidationText.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProvider is not { } provider || string.IsNullOrWhiteSpace(KeyBox.Password))
        {
            ValidationText.Text = "Selecione o provedor e informe a chave API.";
            return;
        }
        _keys[provider] = KeyBox.Password.Trim();
        var settings = SettingsService.Current;
        settings.GeminiApiKey = _keys[AiProvider.Gemini];
        settings.GroqApiKey = _keys[AiProvider.Groq];
        settings.OpenAiApiKey = _keys[AiProvider.OpenAI];
        settings.PreferredAiProvider = provider;
        SettingsService.Save();
        DialogResult = true;
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        var url = _activeProvider switch
        {
            AiProvider.Groq => "https://console.groq.com/keys",
            AiProvider.OpenAI => "https://platform.openai.com/api-keys",
            _ => "https://aistudio.google.com/apikey"
        };
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { ValidationText.Text = $"Abra no navegador: {url}"; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
