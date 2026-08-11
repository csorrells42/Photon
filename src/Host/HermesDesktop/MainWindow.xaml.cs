using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PhotonCadProjects.Windows;

namespace HermesDesktop;

public partial class MainWindow : Window
{
    private const int DesktopHostProtocolVersion = 1;
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private readonly DesktopOptions _options;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly NativeTerminalBridge _terminalBridge;
    private readonly CodexAppServerBridge _codexBridge;
    private readonly DeveloperServicesBridge _developerServicesBridge;
    private readonly WorkspaceSearchBridge _workspaceSearchBridge;
    private readonly DockerControlBridge _dockerControlBridge;
    private readonly PhotonCadBridge _photonCadBridge;
    private readonly SourceControlBridge _sourceControlBridge;
    private readonly DocumentBridge _documentBridge;
    private readonly BrowserSurfaceBridge _browserSurfaceBridge;
    private readonly AccountLinkBridge _accountLinkBridge;
    private readonly HermesConversationBridge _conversationBridge;
    private readonly HermesConnectionsBridge? _connectionsBridge;
    private readonly HermesCredentialRuntimeBridge? _credentialRuntimeBridge;
    private readonly WindowsCredentialVault _credentialVault = new();
    private readonly OpenRouterUsageCollector _openRouterUsageCollector = new();
    private readonly ProviderUsageCollectorBridge _providerUsageCollectorBridge;
    private NativeAuthWindow? _authWindow;
    private CredentialDialog? _credentialDialog;
    private bool _initializing;
    private bool _webViewConfigured;
    private bool _rendererAuthorized;
    private ulong _activeNavigationId;
    private Task<long> _photonCadReset = Task.FromResult(0L);
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    internal MainWindow(DesktopOptions options)
    {
        _options = options;
        _terminalBridge = new NativeTerminalBridge(options.WorkspacePath, PostHostMessage);
        _codexBridge = new CodexAppServerBridge(options.WorkspacePath, PostHostMessage);
        _developerServicesBridge = new DeveloperServicesBridge(
            options.WorkspacePath,
            options.ApplicationInstallRoot,
            PostHostMessage);
        _workspaceSearchBridge = new WorkspaceSearchBridge(options.WorkspacePath, PostHostMessage);
        _dockerControlBridge = new DockerControlBridge(options.ApplicationInstallRoot, PostHostMessage);
        _photonCadBridge = new PhotonCadBridge(
            options.ApplicationInstallRoot,
            PostHostMessage,
            projectDialog: new PhotonCadWindowsFileDialog(() => this),
            workbenchOrigin: options.WorkbenchUri);
        _sourceControlBridge = new SourceControlBridge(options.WorkspacePath, PostHostMessage);
        _documentBridge = new DocumentBridge(options.WorkspacePath, PostHostMessage);
        _accountLinkBridge = new AccountLinkBridge(PostHostMessage);
        _conversationBridge = new HermesConversationBridge(PostHostMessage);
        _connectionsBridge = TryCreateConnectionsBridge(options.ApplicationInstallRoot, PostHostMessage);
        _credentialRuntimeBridge = _connectionsBridge is null
            ? null
            : new HermesCredentialRuntimeBridge(options.ApplicationInstallRoot, options.WorkbenchUri, _connectionsBridge);
        if (_connectionsBridge is not null) _connectionsBridge.RuntimeBindingsChanged += ConnectionsRuntimeBindingsChanged;
        _providerUsageCollectorBridge = new ProviderUsageCollectorBridge(_credentialVault);
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += async (_, _) =>
        {
            _reconnectTimer.Stop();
            await InitializeWorkbenchAsync();
        };

        InitializeComponent();
        _browserSurfaceBridge = new BrowserSurfaceBridge(this, BrowserView, PostHostMessage);
        SourceInitialized += (_, _) => EnableImmersiveDarkTitleBar();
        Loaded += async (_, _) =>
        {
            try { await _conversationBridge.StartAsync(); }
            catch (Exception exception) { DesktopLog.Write($"Hermes conversation bridge could not start: {exception.Message}"); }
            await InitializeWorkbenchAsync();
        };
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_shutdownCompleted) return;
        eventArgs.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        try
        {
            _reconnectTimer.Stop();
            // CAD owns disposable Docker processes and temporary runtime state.
            // It must prove teardown before any later cleanup can permit process exit.
            await _photonCadBridge.DisposeAsync();
            _authWindow?.Close();
            _credentialDialog?.Close();
            _openRouterUsageCollector.Dispose();
            _providerUsageCollectorBridge.Dispose();
            if (_connectionsBridge is not null) _connectionsBridge.RuntimeBindingsChanged -= ConnectionsRuntimeBindingsChanged;
            if (_credentialRuntimeBridge is not null) await _credentialRuntimeBridge.DisposeAsync();
            _connectionsBridge?.Dispose();
            await _terminalBridge.DisposeAsync();
            await _codexBridge.DisposeAsync();
            await _developerServicesBridge.DisposeAsync();
            await _workspaceSearchBridge.DisposeAsync();
            await _dockerControlBridge.DisposeAsync();
            _sourceControlBridge.Dispose();
            await _browserSurfaceBridge.DisposeAsync();
            await _conversationBridge.DisposeAsync();
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Workbench shutdown cleanup failed: {exception.GetType().Name}");
            if (!_photonCadBridge.IsDisposed)
            {
                _shutdownStarted = false;
                ShowFailure("Photon CAD could not prove that its runtime stopped. Close the Workbench again to retry safely.");
                return;
            }
        }
        _shutdownCompleted = true;
        Close();
    }

    private async Task InitializeWorkbenchAsync()
    {
        if (_initializing) return;

        _initializing = true;
        _reconnectTimer.Stop();
        RetryButton.Visibility = Visibility.Collapsed;
        LoadingMessage.Text = "Connecting to the local Workbench…";

        try
        {
            if (!await IsWorkbenchAvailableAsync())
            {
                ShowFailure("The local Workbench is not running yet. Waiting for the launcher…", autoRetry: true);
                return;
            }

            await RestartCredentialRuntimeAsync();

            await EnsureWebViewConfiguredAsync();
            DesktopLog.Write($"Navigating to {_options.WorkbenchUri}");
            WorkbenchView.CoreWebView2.Navigate(_options.WorkbenchUri.ToString());
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Initialization failed: {exception}");
            ShowFailure($"Could not start the desktop view: {exception.Message}", autoRetry: true);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void ConnectionsRuntimeBindingsChanged(object? sender, EventArgs eventArgs) =>
        _ = RestartCredentialRuntimeAsync();

    private async Task RestartCredentialRuntimeAsync()
    {
        if (_credentialRuntimeBridge is null || _shutdownStarted) return;
        try { await _credentialRuntimeBridge.RestartAsync(); }
        catch (Exception exception)
        {
            var code = exception is HermesCredentialBroker.Runtime.CredentialRuntimeException runtime
                ? runtime.Code
                : exception.GetType().Name;
            DesktopLog.Write($"Native credential runtime is unavailable: {code}");
        }
    }

    private async Task EnsureWebViewConfiguredAsync()
    {
        if (_webViewConfigured) return;

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HermesWorkbench",
            "WebView2");
        Directory.CreateDirectory(userData);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await WorkbenchView.EnsureCoreWebView2Async(environment);

        var core = WorkbenchView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.NavigationStarting += NavigationStarting;
        core.NavigationCompleted += NavigationCompleted;
        core.NewWindowRequested += NewWindowRequested;
        core.WebMessageReceived += WebMessageReceived;
        core.AddWebResourceRequestedFilter(
            new Uri(_options.WorkbenchUri, PhotonCadBridge.PreviewResourcePathPrefix).AbsoluteUri + "*",
            CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += PhotonCadPreviewResourceRequested;
        core.ProcessFailed += ProcessFailed;
        _webViewConfigured = true;
        DesktopLog.Write($"WebView2 initialized ({core.Environment.BrowserVersionString}).");
    }

    private void PhotonCadPreviewResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Request.Uri, UriKind.Absolute, out var requestUri)
            || !DesktopOptions.IsSameWorkbenchOrigin(_options.WorkbenchUri, requestUri)) return;
        IReadOnlyDictionary<string, string>? headers = null;
        if (eventArgs.Request.Headers.Contains("Range"))
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Range"] = eventArgs.Request.Headers.GetHeader("Range") };
        var response = _photonCadBridge.TryRespondPreviewResource(
            eventArgs.Request.Method,
            requestUri,
            _rendererAuthorized,
            headers);
        if (response is null) return;
        var serializedHeaders = string.Join("\r\n", response.Headers.Select(pair => $"{pair.Key}: {pair.Value}"));
        eventArgs.Response = WorkbenchView.CoreWebView2.Environment.CreateWebResourceResponse(
            response.Content,
            response.StatusCode,
            response.Reason,
            serializedHeaders);
    }

    private async Task<bool> IsWorkbenchAvailableAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.WorkbenchUri);
            using var response = await HealthClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            DesktopLog.Write($"Workbench probe returned HTTP {(int)response.StatusCode}.");
            return response.IsSuccessStatusCode && await VerifyWorkbenchIdentityAsync();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            DesktopLog.Write($"Workbench probe failed: {exception.Message}");
            return false;
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        DesktopLog.Write($"Navigation starting: {e.Uri}");
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            || !DesktopOptions.IsSameWorkbenchOrigin(_options.WorkbenchUri, target))
        {
            e.Cancel = true;
            OpenExternal(e.Uri);
            return;
        }
        _rendererAuthorized = false;
        _photonCadReset = _photonCadBridge.ResetAsync();
        _activeNavigationId = e.NavigationId;
    }

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        DesktopLog.Write($"Navigation completed: success={e.IsSuccess}, status={e.WebErrorStatus}, http={e.HttpStatusCode}");
        if (!e.IsSuccess)
        {
            ShowFailure("The Workbench connection was interrupted. Reconnecting…", autoRetry: true);
            return;
        }
        long resetEpoch;
        try { resetEpoch = await _photonCadReset; }
        catch (Exception exception)
        {
            _rendererAuthorized = false;
            DesktopLog.Write($"Photon CAD renderer reset failed closed: {exception.GetType().Name}");
            ShowFailure("Photon CAD could not revoke the previous renderer safely. Reconnectingâ€¦", autoRetry: true);
            return;
        }

        var completedSource = WorkbenchView.Source;
        if (completedSource is not Uri source
            || !DesktopOptions.CanAuthorizeWorkbenchNavigation(
                e.NavigationId, _activeNavigationId, _options.WorkbenchUri, source, source))
        {
            ShowFailure("The desktop renderer left the trusted Workbench origin.");
            return;
        }
        if (!await VerifyWorkbenchIdentityAsync())
        {
            ShowFailure("The page on the Workbench port could not prove launcher ownership.", autoRetry: true);
            return;
        }
        if (WorkbenchView.Source is not Uri currentSource
            || !DesktopOptions.CanAuthorizeWorkbenchNavigation(
                e.NavigationId, _activeNavigationId, _options.WorkbenchUri, source, currentSource)) return;

        if (!_photonCadBridge.TryOpenRendererGeneration(resetEpoch))
        {
            _rendererAuthorized = false;
            ShowFailure("Photon CAD renderer ownership changed during startup. Reconnectingâ€¦", autoRetry: true);
            return;
        }

        _reconnectTimer.Stop();
        LoadingSurface.Visibility = Visibility.Collapsed;
        WorkbenchView.Visibility = Visibility.Visible;
        var photonCadProjectCapability = _photonCadBridge.ProjectCapabilityAdvertisement();
        var photonCadProjectCapabilityJson = JsonSerializer.Serialize(photonCadProjectCapability);
        try
        {
            await WorkbenchView.CoreWebView2.ExecuteScriptAsync(
                $"window.__HERMES_DESKTOP_HOST__={{version:{DesktopHostProtocolVersion},platform:'windows',capabilities:{{terminal:true,terminalVersion:{NativeTerminalBridge.ProtocolVersion},codex:true,codexVersion:{CodexAppServerBridge.ProtocolVersion},auth:true,authVersion:{NativeAuthProtocol.Version},credentials:true,credentialsVersion:{NativeCredentialProtocol.Version},connections:{(_connectionsBridge is not null).ToString().ToLowerInvariant()},connectionsVersion:{HermesCredentialBroker.HermesCredentialBrokerProtocol.Version},usage:true,usageVersion:{NativeUsageProtocol.Version},developerServices:true,developerServicesVersion:{DeveloperServicesBridge.ProtocolVersion},languageTooling:true,languageToolingVersion:1,workspaceSearch:true,workspaceSearchVersion:{WorkspaceSearchBridge.ProtocolVersion},dockerControl:true,dockerControlVersion:{DockerControlBridge.ProtocolVersion},photonCad:true,photonCadVersion:{PhotonCadBridge.ProtocolVersion},photonCadProjects:{_photonCadBridge.ProjectActionsAvailable.ToString().ToLowerInvariant()},photonCadProjectsVersion:1,photonCadProjectDetails:{photonCadProjectCapabilityJson},sourceControl:true,sourceControlVersion:{SourceControlBridge.ProtocolVersion},documents:true,documentsVersion:{DocumentBridge.ProtocolVersion},browser:true,browserVersion:{BrowserSurfaceBridge.ProtocolVersion},accountLink:true,accountLinkVersion:{AccountLinkBridge.ProtocolVersion},conversationBridge:true,conversationBridgeVersion:{HermesConversationBridge.ProtocolVersion}}}}};" +
                "window.dispatchEvent(new CustomEvent('hermes-desktop-ready'));"
            );
            _rendererAuthorized = true;
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Desktop capability injection failed closed: {exception.GetType().Name}");
            _rendererAuthorized = false;
            _photonCadReset = _photonCadBridge.ResetAsync();
            ShowFailure("The desktop renderer could not establish trusted capabilities. Reconnectingâ€¦", autoRetry: true);
        }
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _rendererAuthorized = false;
        _photonCadReset = _photonCadBridge.ResetAsync();
        DesktopLog.Write($"WebView2 process failed: {e.ProcessFailedKind}, reason={e.Reason}");
        ShowFailure("The desktop renderer stopped unexpectedly. Reconnecting…", autoRetry: true);
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_rendererAuthorized
            || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || !DesktopOptions.IsSameWorkbenchOrigin(_options.WorkbenchUri, source)) return;
        try
        {
            var messageJson = e.WebMessageAsJson;
            if (messageJson.Length > 2 * 1024 * 1024)
            {
                PostHostMessage(new { type = "host.error", version = DesktopHostProtocolVersion, message = "Desktop host message exceeded the 2 MB limit." });
                return;
            }
            using var document = JsonDocument.Parse(messageJson);
            var type = document.RootElement.TryGetProperty("type", out var value) ? value.GetString() : null;
            switch (type)
            {
                case "window.minimize": WindowState = WindowState.Minimized; break;
                case "window.maximize": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                case "window.close": Close(); break;
                case "host.ping":
                    PostHostMessage(new
                    {
                        type = "host.pong",
                        version = DesktopHostProtocolVersion,
                        platform = "windows",
                        capabilities = new { terminal = true, terminalVersion = NativeTerminalBridge.ProtocolVersion, codex = true, codexVersion = CodexAppServerBridge.ProtocolVersion, auth = true, authVersion = NativeAuthProtocol.Version, credentials = true, credentialsVersion = NativeCredentialProtocol.Version, connections = _connectionsBridge is not null, connectionsVersion = HermesCredentialBroker.HermesCredentialBrokerProtocol.Version, usage = true, usageVersion = NativeUsageProtocol.Version, developerServices = true, developerServicesVersion = DeveloperServicesBridge.ProtocolVersion, languageTooling = true, languageToolingVersion = 1, workspaceSearch = true, workspaceSearchVersion = WorkspaceSearchBridge.ProtocolVersion, dockerControl = true, dockerControlVersion = DockerControlBridge.ProtocolVersion, photonCad = true, photonCadVersion = PhotonCadBridge.ProtocolVersion, photonCadProjects = _photonCadBridge.ProjectActionsAvailable, photonCadProjectsVersion = 1, photonCadProjectDetails = _photonCadBridge.ProjectCapabilityAdvertisement(), sourceControl = true, sourceControlVersion = SourceControlBridge.ProtocolVersion, documents = true, documentsVersion = DocumentBridge.ProtocolVersion, browser = true, browserVersion = BrowserSurfaceBridge.ProtocolVersion, accountLink = true, accountLinkVersion = AccountLinkBridge.ProtocolVersion, conversationBridge = true, conversationBridgeVersion = HermesConversationBridge.ProtocolVersion },
                    });
                    break;
                case "auth.open":
                    var provider = document.RootElement.TryGetProperty("provider", out var providerElement) ? providerElement.GetString() : null;
                    OpenAuthenticationWindow(provider);
                    break;
                case "credentials.list":
                    PostCredentialList();
                    break;
                case "credentials.open":
                    OpenCredentialDialog(
                        GetString(document.RootElement, "provider"),
                        GetString(document.RootElement, "credentialId"));
                    break;
                case "credentials.delete":
                    DeleteCredential(
                        GetString(document.RootElement, "provider"),
                        GetString(document.RootElement, "credentialId"));
                    break;
                case "connections.list":
                    if (_connectionsBridge is null) { PostConnectionsUnavailable(GetString(document.RootElement, "requestId")); break; }
                    await _connectionsBridge.ListAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "connections.change.begin":
                    if (_connectionsBridge is null) { PostConnectionsUnavailable(GetString(document.RootElement, "requestId")); break; }
                    _connectionsBridge.BeginChange(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "profileId"),
                        GetString(document.RootElement, "providerId"),
                        GetString(document.RootElement, "slotId"),
                        GetString(document.RootElement, "authKind"),
                        GetString(document.RootElement, "sourceKind"),
                        GetStringArray(document.RootElement, "purposes"),
                        GetString(document.RootElement, "existingReference"),
                        GetInteger(document.RootElement, "expectedRevision", -1));
                    break;
                case "connections.remove.review":
                    if (_connectionsBridge is null) { PostConnectionsUnavailable(GetString(document.RootElement, "requestId")); break; }
                    await _connectionsBridge.BeginRemoveAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "connectionRef"),
                        GetInteger(document.RootElement, "expectedRevision", -1));
                    break;
                case "connections.review.commit":
                    if (_connectionsBridge is null) { PostConnectionsUnavailable(GetString(document.RootElement, "requestId")); break; }
                    await _connectionsBridge.CommitAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "reviewHandle"));
                    break;
                case "connections.review.cancel":
                    if (_connectionsBridge is null) { PostConnectionsUnavailable(GetString(document.RootElement, "requestId")); break; }
                    _connectionsBridge.Cancel(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "reviewHandle"));
                    break;
                case "usage.collect":
                    await CollectUsageAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "provider"),
                        GetString(document.RootElement, "credentialId"),
                        GetString(document.RootElement, "periodStart"),
                        GetString(document.RootElement, "periodEnd"));
                    break;
                case "developerServices.describe":
                    await _developerServicesBridge.DescribeAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "developerServices.build":
                    await _developerServicesBridge.BuildAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetInteger(document.RootElement, "revision", 0),
                        GetString(document.RootElement, "targetPath"),
                        GetString(document.RootElement, "configuration"));
                    break;
                case "developerServices.analyze":
                    await _developerServicesBridge.AnalyzeAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetInteger(document.RootElement, "revision", 0),
                        GetString(document.RootElement, "targetPath"),
                        GetString(document.RootElement, "configuration"));
                    break;
                case "developerServices.cancel":
                    _developerServicesBridge.Cancel(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "developerServices.language.open":
                    await _developerServicesBridge.OpenLanguageDocumentAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetInteger(document.RootElement, "revision", -1),
                        GetString(document.RootElement, "documentPath"),
                        GetString(document.RootElement, "text"));
                    break;
                case "developerServices.language.change":
                    await _developerServicesBridge.ChangeLanguageDocumentAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "sessionId"),
                        GetInteger(document.RootElement, "revision", -1),
                        GetString(document.RootElement, "documentPath"),
                        GetString(document.RootElement, "text"));
                    break;
                case "developerServices.language.close":
                    await _developerServicesBridge.CloseLanguageDocumentAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "sessionId"),
                        GetString(document.RootElement, "documentPath"));
                    break;
                case "developerServices.languageTooling.inspect":
                    await _developerServicesBridge.InspectLanguageToolingProviderAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "providerId"));
                    break;
                case "developerServices.languageTooling.project.inspect":
                    await _developerServicesBridge.InspectLanguageToolingProjectAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "providerId"),
                        GetString(document.RootElement, "projectPath"));
                    break;
                case "developerServices.languageTooling.compile":
                    await _developerServicesBridge.CompileLanguageToolingAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "providerId"),
                        GetString(document.RootElement, "targetPath"),
                        GetString(document.RootElement, "mode"),
                        GetString(document.RootElement, "boardFqbn"));
                    break;
                case "developerServices.languageTooling.cancel":
                    _developerServicesBridge.CancelLanguageTooling(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "targetRequestId"));
                    break;
                case "workspaceSearch.literal":
                    await _workspaceSearchBridge.SearchLiteralAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "query"),
                        GetInteger(document.RootElement, "maxResults", 0),
                        GetInteger(document.RootElement, "maxResultsPerFile", 0),
                        GetInteger(document.RootElement, "maxPreviewCharacters", 0));
                    break;
                case "workspaceSearch.semantic":
                    await _workspaceSearchBridge.SearchSemanticAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "intent"),
                        GetInteger(document.RootElement, "maxResults", 0),
                        GetInteger(document.RootElement, "maxResultsPerFile", 0),
                        GetInteger(document.RootElement, "maxPreviewCharacters", 0));
                    break;
                case "workspaceSearch.cancel":
                    _workspaceSearchBridge.Cancel(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "targetRequestId"));
                    break;
                case "dockerControl.describe":
                    await _dockerControlBridge.DescribeAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "dockerControl.snapshot":
                    await _dockerControlBridge.SnapshotAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "dockerControl.logs":
                    await _dockerControlBridge.LogsAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "service"),
                        GetInteger(document.RootElement, "maxLines", 0));
                    break;
                case "dockerControl.review":
                    var dockerIntent = document.RootElement.TryGetProperty("intent", out var dockerIntentElement)
                        && dockerIntentElement.ValueKind == JsonValueKind.Object ? dockerIntentElement : default;
                    await _dockerControlBridge.ReviewAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetInteger(document.RootElement, "snapshotRevision", -1),
                        dockerIntent.ValueKind == JsonValueKind.Object ? GetString(dockerIntent, "kind") : null,
                        dockerIntent.ValueKind == JsonValueKind.Object ? GetString(dockerIntent, "service") : null);
                    break;
                case "dockerControl.commit":
                    await _dockerControlBridge.CommitAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "reviewToken"));
                    break;
                case "dockerControl.discard":
                    await _dockerControlBridge.DiscardAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "reviewToken"));
                    break;
                case "photonCad.describe":
                case "photonCad.execute":
                case "photonCad.verify":
                case "photonCad.release.review":
                case "photonCad.release.commit":
                case "photonCad.release.discard":
                case "photonCad.cancel":
                case "photonCad.preview.resolve":
                case "photonCad.preview.cancel":
                case "photonCad.project.picker":
                case "photonCad.project.create":
                case "photonCad.project.open":
                case "photonCad.project.reopen":
                case "photonCad.project.refresh":
                case "photonCad.project.save":
                case "photonCad.project.saveAs":
                case "photonCad.project.close":
                case "photonCad.project.cancel":
                case "photonCad.commercial.bom.review":
                case "photonCad.commercial.bom.commit":
                case "photonCad.commercial.bom.discard":
                case "photonCad.commercial.document.review":
                case "photonCad.commercial.document.approve":
                case "photonCad.commercial.document.commit":
                case "photonCad.commercial.document.discard":
                case "photonCad.commercial.cancel":
                    await _photonCadBridge.HandleAsync(type, document.RootElement);
                    break;
                case "sourceControl.describe":
                    _sourceControlBridge.Describe(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "sourceControl.repository.resolve":
                    _sourceControlBridge.ResolveRepository(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "workspaceRelativePath"));
                    break;
                case "sourceControl.status":
                    await _sourceControlBridge.StatusAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "repositoryId"));
                    break;
                case "sourceControl.cancel":
                    _sourceControlBridge.Cancel(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "targetRequestId"));
                    break;
                case "sourceControl.gitExtensions.open":
                    await _sourceControlBridge.OpenGitExtensionsAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "repositoryId"),
                        GetString(document.RootElement, "surface"));
                    break;
                case "document.pick":
                    _documentBridge.PickFile(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "document.repository.pick":
                    _documentBridge.PickRepository(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "document.save":
                case "document.saveAs":
                    await _documentBridge.SaveAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "path"),
                        GetString(document.RootElement, "content"),
                        GetString(document.RootElement, "expectedSha256"),
                        type == "document.saveAs");
                    break;
                case "browser.surface.show":
                    await _browserSurfaceBridge.ShowAsync(
                        GetInteger(document.RootElement, "x", -1),
                        GetInteger(document.RootElement, "y", -1),
                        GetInteger(document.RootElement, "width", -1),
                        GetInteger(document.RootElement, "height", -1),
                        GetString(document.RootElement, "tabId"),
                        GetString(document.RootElement, "url"));
                    break;
                case "browser.surface.hide":
                    _browserSurfaceBridge.Hide();
                    break;
                case "browser.tab.close":
                    _browserSurfaceBridge.CloseTab(GetString(document.RootElement, "tabId"));
                    break;
                case "browser.navigate":
                    _browserSurfaceBridge.Navigate(
                        GetString(document.RootElement, "tabId"),
                        GetString(document.RootElement, "url"));
                    break;
                case "browser.openRequested.accept":
                    _browserSurfaceBridge.AcceptOpenRequest(
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "tabId"));
                    break;
                case "browser.back": _browserSurfaceBridge.Back(GetString(document.RootElement, "tabId")); break;
                case "browser.forward": _browserSurfaceBridge.Forward(GetString(document.RootElement, "tabId")); break;
                case "browser.reload": _browserSurfaceBridge.Reload(GetString(document.RootElement, "tabId")); break;
                case "browser.stop": _browserSurfaceBridge.Stop(GetString(document.RootElement, "tabId")); break;
                case "accountLink.status":
                    await _accountLinkBridge.PostStatusAsync(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"));
                    break;
                case "accountLink.open":
                    _accountLinkBridge.Open(
                        GetInteger(document.RootElement, "version", 0),
                        GetString(document.RootElement, "requestId"),
                        GetString(document.RootElement, "provider"));
                    break;
                case "conversationBridge.reply":
                    _conversationBridge.TryComplete(document.RootElement);
                    break;
                case "terminal.start":
                    await _terminalBridge.StartAsync(GetInteger(document.RootElement, "columns", 100), GetInteger(document.RootElement, "rows", 24));
                    break;
                case "terminal.input":
                    if (document.RootElement.TryGetProperty("data", out var input) && input.ValueKind == JsonValueKind.String)
                    {
                        await _terminalBridge.WriteAsync(input.GetString() ?? string.Empty);
                    }
                    break;
                case "terminal.resize":
                    _terminalBridge.Resize(GetInteger(document.RootElement, "columns", 100), GetInteger(document.RootElement, "rows", 24));
                    break;
                case "terminal.stop":
                    await _terminalBridge.StopAsync();
                    break;
                case "codex.start":
                    await _codexBridge.StartAsync();
                    break;
                case "codex.send":
                    if (document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                    {
                        await _codexBridge.SendAsync(payload.Clone());
                    }
                    break;
                case "codex.stop":
                    await _codexBridge.StopAsync();
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed renderer messages; no native action is taken.
        }
    }

    private void OpenAuthenticationWindow(string? provider)
    {
        if (!_webViewConfigured || WorkbenchView.CoreWebView2 is null)
        {
            PostHostMessage(new { type = "auth.error", version = NativeAuthProtocol.Version, message = "The desktop authentication bridge is not ready." });
            return;
        }
        if (!NativeAuthProtocol.IsValidProvider(provider))
        {
            PostHostMessage(new { type = "auth.error", version = NativeAuthProtocol.Version, message = "Hermes returned an invalid authentication provider." });
            return;
        }
        if (_authWindow is not null)
        {
            _authWindow.Activate();
            return;
        }

        try
        {
            var loginUri = NativeAuthProtocol.BuildLoginUri(_options.WorkbenchUri, provider!);
            var completionUri = NativeAuthProtocol.BuildCompletionUri(_options.WorkbenchUri);
            var window = new NativeAuthWindow(WorkbenchView.CoreWebView2.Environment, loginUri, completionUri)
            {
                Owner = this,
            };
            _authWindow = window;
            window.Closed += (_, _) =>
            {
                var completed = window.Completed;
                _authWindow = null;
                PostHostMessage(new
                {
                    type = completed ? "auth.completed" : "auth.closed",
                    version = NativeAuthProtocol.Version,
                });
            };
            window.Show();
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Authentication window could not open: {exception}");
            PostHostMessage(new { type = "auth.error", version = NativeAuthProtocol.Version, message = "Hermes sign-in could not open." });
        }
    }

    private void PostCredentialList()
    {
        try
        {
            var entries = _credentialVault.List().Select(ToCredentialMetadataMessage).ToArray();
            PostHostMessage(new
            {
                type = "credentials.list.result",
                version = NativeCredentialProtocol.Version,
                entries,
            });
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Credential metadata could not be listed: {exception.GetType().Name}");
            PostCredentialError("Windows Credential Manager could not list provider connections.");
        }
    }

    private void OpenCredentialDialog(string? provider, string? credentialId)
    {
        if (!NativeCredentialProtocol.IsValidProvider(provider)
            || !NativeCredentialProtocol.IsValidCredentialId(credentialId))
        {
            PostCredentialError("The provider credential reference was invalid.");
            return;
        }
        if (_credentialDialog is not null)
        {
            _credentialDialog.Activate();
            return;
        }

        var dialog = new CredentialDialog(provider!, credentialId!) { Owner = this };
        _credentialDialog = dialog;
        try
        {
            if (dialog.ShowDialog() != true)
            {
                PostHostMessage(new { type = "credentials.cancelled", version = NativeCredentialProtocol.Version });
                return;
            }

            using var secret = dialog.TakeSecret();
            var metadata = _credentialVault.Save(provider!, credentialId!, secret);
            PostHostMessage(new
            {
                type = "credentials.changed",
                version = NativeCredentialProtocol.Version,
                entry = ToCredentialMetadataMessage(metadata),
            });
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Credential could not be saved: {exception.GetType().Name}");
            PostCredentialError("Windows Credential Manager could not save the provider connection.");
        }
        finally
        {
            dialog.ClearSecret();
            _credentialDialog = null;
        }
    }

    private void DeleteCredential(string? provider, string? credentialId)
    {
        if (!NativeCredentialProtocol.IsValidProvider(provider)
            || !NativeCredentialProtocol.IsValidCredentialId(credentialId))
        {
            PostCredentialError("The provider credential reference was invalid.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove the {provider} credential profile '{credentialId}' from Windows Credential Manager?",
            "Remove provider connection",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            PostHostMessage(new { type = "credentials.cancelled", version = NativeCredentialProtocol.Version });
            return;
        }

        try
        {
            var removed = _credentialVault.Delete(provider!, credentialId!);
            PostHostMessage(new
            {
                type = "credentials.deleted",
                version = NativeCredentialProtocol.Version,
                provider,
                credentialId,
                removed,
            });
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Credential could not be deleted: {exception.GetType().Name}");
            PostCredentialError("Windows Credential Manager could not remove the provider connection.");
        }
    }

    private static object ToCredentialMetadataMessage(CredentialMetadata metadata) => new
    {
        provider = metadata.Provider,
        credentialId = metadata.CredentialId,
        configured = true,
        updatedAt = metadata.UpdatedAt,
    };

    private void PostCredentialError(string message) => PostHostMessage(new
    {
        type = "credentials.error",
        version = NativeCredentialProtocol.Version,
        message,
    });

    private async Task CollectUsageAsync(
        int version,
        string? requestId,
        string? provider,
        string? credentialId,
        string? periodStart,
        string? periodEnd)
    {
        if (version != NativeUsageProtocol.Version
            || !NativeUsageProtocol.IsValidRequestId(requestId)
            || !NativeUsageProtocol.IsValidProvider(provider)
            || !NativeCredentialProtocol.IsValidCredentialId(credentialId))
        {
            PostUsageError(requestId, provider, "unexpected", "The usage collection request was invalid.", false);
            return;
        }

        if (NativeUsageProtocol.IsOrganizationProvider(provider))
        {
            if (!DateTimeOffset.TryParse(periodStart, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var start)
                || !DateTimeOffset.TryParse(periodEnd, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var end))
            {
                PostUsageError(requestId, provider, "unexpected", "The usage collection period was invalid.", false);
                return;
            }

            var result = await _providerUsageCollectorBridge.CollectAsync(provider!, credentialId!, start, end);
            PostHostMessage(new
            {
                type = "usage.collect.provider.result",
                version = NativeUsageProtocol.Version,
                requestId,
                provider,
                credentialId,
                data = ProviderUsageCollectorBridge.ToMessage(result),
            });
            return;
        }

        string? secret = null;
        try
        {
            secret = _credentialVault.ReadSecret(provider!, credentialId!);
            if (secret is null)
            {
                PostUsageError(requestId, provider, "not-configured", "Store an OpenRouter key in the native credential vault first.", false);
                return;
            }

            var snapshot = await _openRouterUsageCollector.CollectAsync(secret);
            PostHostMessage(new
            {
                type = "usage.collect.result",
                version = NativeUsageProtocol.Version,
                requestId,
                provider,
                collectedAt = snapshot.CollectedAt,
                data = new
                {
                    usage = snapshot.Usage,
                    usageDaily = snapshot.UsageDaily,
                    usageWeekly = snapshot.UsageWeekly,
                    usageMonthly = snapshot.UsageMonthly,
                    limit = snapshot.Limit,
                    limitRemaining = snapshot.LimitRemaining,
                    limitReset = snapshot.LimitReset,
                    isFreeTier = snapshot.IsFreeTier,
                },
            });
        }
        catch (UsageCollectionException exception)
        {
            DesktopLog.Write($"Usage collection failed for {provider}: {exception.Code} ({exception.GetType().Name})");
            PostUsageError(requestId, provider, exception.Code, exception.Message, exception.Retryable);
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Usage collection failed for {provider}: {exception.GetType().Name}");
            PostUsageError(requestId, provider, "unexpected", "The native usage collector encountered an unexpected error.", true);
        }
        finally
        {
            secret = null;
        }
    }

    private void PostUsageError(string? requestId, string? provider, string code, string message, bool retryable) => PostHostMessage(new
    {
        type = "usage.collect.error",
        version = NativeUsageProtocol.Version,
        requestId,
        provider,
        code,
        message,
        retryable,
    });

    private void PostHostMessage(object message)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_rendererAuthorized
                || !_webViewConfigured
                || WorkbenchView.CoreWebView2 is null
                || WorkbenchView.Source is not Uri source
                || !DesktopOptions.IsSameWorkbenchOrigin(_options.WorkbenchUri, source)) return;
            WorkbenchView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        });
    }

    private async Task<bool> VerifyWorkbenchIdentityAsync()
    {
        try
        {
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var endpoint = new Uri(_options.WorkbenchUri, $"/workbench-api/host-identity?challenge={challenge}");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await HealthClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            if (!response.IsSuccessStatusCode) return false;
            var content = await response.Content.ReadAsStringAsync();
            if (content.Length > 2_048) return false;
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var protocol) || protocol.GetInt32() != 1) return false;
            var returnedChallenge = GetString(root, "challenge") ?? string.Empty;
            var proof = GetString(root, "proof") ?? string.Empty;
            if (!string.Equals(returnedChallenge, challenge, StringComparison.OrdinalIgnoreCase)
                || !DesktopOptions.IsValidWorkbenchNonce(proof)) return false;
            var expected = DesktopOptions.CreateWorkbenchIdentityProof(_options.WorkbenchNonce, challenge);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(proof));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            DesktopLog.Write($"Workbench identity verification failed: {exception.GetType().Name}");
            return false;
        }
    }

    private static int GetInteger(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 16)
            return [];
        var result = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 128 } text)
                return [];
            result.Add(text);
        }
        return [.. result];
    }

    private static HermesConnectionsBridge? TryCreateConnectionsBridge(string applicationInstallRoot, Action<object> postMessage)
    {
        try { return new HermesConnectionsBridge(applicationInstallRoot, postMessage); }
        catch (Exception exception)
        {
            DesktopLog.Write($"Native connections bridge is unavailable: {exception.GetType().Name}");
            return null;
        }
    }

    private void PostConnectionsUnavailable(string? requestId) => PostHostMessage(new
    {
        type = "connections.error",
        version = HermesCredentialBroker.HermesCredentialBrokerProtocol.Version,
        requestId,
        code = "native_unavailable",
        message = "The native credential vault is unavailable on this Windows account.",
        retryable = false,
    });

    private void RetryClicked(object sender, RoutedEventArgs e)
    {
        LoadingSurface.Visibility = Visibility.Visible;
        WorkbenchView.Visibility = Visibility.Hidden;
        _ = InitializeWorkbenchAsync();
    }

    private void ShowFailure(string message, bool autoRetry = false)
    {
        LoadingMessage.Text = message;
        RetryButton.Visibility = Visibility.Visible;
        LoadingSurface.Visibility = Visibility.Visible;
        WorkbenchView.Visibility = Visibility.Hidden;
        if (autoRetry && IsLoaded)
        {
            _reconnectTimer.Stop();
            _reconnectTimer.Start();
        }
    }

    private static void OpenExternal(string? target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private void EnableImmersiveDarkTitleBar()
    {
        var enabled = 1;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
