using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;

namespace ChatGptVoicePanelPrototype;

public partial class MainWindow : Window
{
    private static readonly Uri InitialUri = new("https://chatgpt.com/");
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.Ordinal)
    {
        "https://chatgpt.com", "https://auth.openai.com", "https://auth0.openai.com",
        "https://openai.com", "https://help.openai.com"
    };

    private readonly string _profilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hermes", "WebView2", "ChatGptVoice", "Profile-v1");

    private CoreWebView2Environment? _environment;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => Browser.Dispose();
    }

    private async Task InitializeBrowserAsync()
    {
        FailurePanel.Visibility = Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = false,
                AllowSingleSignOnUsingOSPrimaryAccount = false,
                EnableTrackingPrevention = true,
                ExclusiveUserDataFolderAccess = true
            };
            _environment = await CoreWebView2Environment.CreateAsync(null, _profilePath, options);
            await Browser.EnsureCoreWebView2Async(_environment);
            ConfigureCore(Browser.CoreWebView2);
            Browser.CoreWebView2.Navigate(InitialUri.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFailure("Microsoft Edge WebView2 Runtime is required to open the ChatGPT panel.");
        }
        catch (Exception exception)
        {
            ShowFailure($"The isolated ChatGPT profile could not be opened. {exception.GetType().Name}");
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = !IsAllowed(args.Uri);
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.PermissionRequested += PermissionRequested;
        core.ProcessFailed += (_, _) => ShowFailure("The ChatGPT browser process stopped. Select Retry to recreate it.");
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) ShowFailure($"ChatGPT did not load ({args.WebErrorStatus}). Check your connection and select Retry.");
        };
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        if (args.PermissionKind != CoreWebView2PermissionKind.Microphone || !IsAllowed(args.Uri)) return;
        var result = MessageBox.Show(this, $"Allow {new Uri(args.Uri).Host} to use this computer's microphone for this browser session?", "ChatGPT microphone permission", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            args.State = CoreWebView2PermissionState.Allow;
            args.SavesInProfile = false;
        }
    }

    private static bool IsAllowed(string uriText) => Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority));

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null) return;
        FailurePanel.Visibility = Visibility.Collapsed;
        Browser.CoreWebView2.Reload();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await InitializeBrowserAsync();

    private void ShowFailure(string message)
    {
        FailureText.Text = message;
        FailurePanel.Visibility = Visibility.Visible;
    }
}
