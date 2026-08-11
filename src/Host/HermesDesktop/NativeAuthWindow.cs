using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HermesDesktop;

internal static class NativeAuthProtocol
{
    public const int Version = 1;
    public const string CompletionPath = "/workbench-auth-complete";

    public static Uri BuildLoginUri(Uri workbenchUri, string provider)
    {
        if (!DesktopOptions.IsTrustedWorkbenchUri(workbenchUri))
        {
            throw new ArgumentException("The authentication bridge requires a trusted loopback Workbench URL.", nameof(workbenchUri));
        }
        if (!IsValidProvider(provider))
        {
            throw new ArgumentException("The Hermes authentication provider name is invalid.", nameof(provider));
        }

        var builder = new UriBuilder(workbenchUri)
        {
            Path = "/auth/login",
            Query = $"provider={Uri.EscapeDataString(provider)}&next={Uri.EscapeDataString(CompletionPath)}",
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static Uri BuildCompletionUri(Uri workbenchUri) => new(workbenchUri, CompletionPath);

    public static bool IsValidProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider)
        && provider.Length <= 64
        && provider.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

internal sealed class NativeAuthWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _loginUri;
    private readonly Uri _completionUri;
    private readonly WebView2 _view = new();

    public bool Completed { get; private set; }

    public NativeAuthWindow(CoreWebView2Environment environment, Uri loginUri, Uri completionUri)
    {
        _environment = environment;
        _loginUri = loginUri;
        _completionUri = completionUri;

        Title = "Connect Hermes";
        Width = 720;
        Height = 780;
        MinWidth = 520;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.Black;
        Content = _view;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(_environment);
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += NewWindowRequested;
            core.ProcessFailed += (_, eventArgs) =>
                DesktopLog.Write($"Authentication WebView2 process failed: {eventArgs.ProcessFailedKind}, reason={eventArgs.Reason}");
            core.Navigate(_loginUri.ToString());
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Authentication window failed to initialize: {exception}");
            MessageBox.Show(this, exception.Message, "Hermes sign-in could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
        {
            eventArgs.Cancel = true;
            DesktopLog.Write("Authentication window blocked a non-HTTP navigation.");
            return;
        }

        if (MatchesCompletion(target))
        {
            eventArgs.Cancel = true;
            Completed = true;
            Close();
        }
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https")
        {
            _view.CoreWebView2.Navigate(target.ToString());
        }
    }

    private bool MatchesCompletion(Uri target) =>
        target.Scheme.Equals(_completionUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && target.Host.Equals(_completionUri.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == _completionUri.Port
        && target.AbsolutePath.Equals(_completionUri.AbsolutePath, StringComparison.Ordinal);
}
