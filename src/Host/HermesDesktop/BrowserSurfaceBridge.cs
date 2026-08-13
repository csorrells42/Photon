using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HermesDesktop;

internal sealed class BrowserSurfaceBridge : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    private readonly Window _window;
    private readonly WebView2CompositionControl _view;
    private readonly Action<object> _postMessage;
    private Task<bool>? _initializationTask;
    private bool _initialized;
    private bool _surfaceVisible;
    private string _tabId = string.Empty;
    private readonly Dictionary<string, BrowserTabState> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, BrowserNavigationContext> _navigations = new();
    private readonly HashSet<ulong> _externalAuthenticationNavigations = [];
    private readonly BrowserSurfaceRequestGate _surfaceRequests = new();
    private bool _disposed;
    private BrowserNavigationContext? _startingNavigation;
    private string _displayedTabId = string.Empty;
    private readonly Dictionary<string, (Uri Uri, DateTimeOffset ExpiresAt)> _pendingOpenRequests = new();
    private int _processRecoveryAttempts;
    private bool _processRecoveryPending;
    private readonly HashSet<string> _suppressedTabIds = new(StringComparer.Ordinal);

    internal BrowserSurfaceBridge(Window window, WebView2CompositionControl view, Action<object> postMessage)
    {
        _window = window;
        _view = view;
        _postMessage = postMessage;
    }

    internal async Task ShowAsync(int x, int y, int width, int height, string? tabId, string? address)
    {
        if (_disposed) return;
        DesktopLog.Write($"Browser surface request: x={x}, y={y}, width={width}, height={height}.");
        var surfaceRequest = _surfaceRequests.Begin();
        if (width < 80 || height < 80 || x < 0 || y < 0)
        {
            DesktopLog.Write("Browser surface rejected: invalid layout bounds.");
            return;
        }
        var availableWidth = Math.Max(0, _window.ActualWidth - x);
        var availableHeight = Math.Max(0, _window.ActualHeight - y);
        width = (int)Math.Min(width, availableWidth);
        height = (int)Math.Min(height, availableHeight);
        if (width < 80 || height < 80)
        {
            DesktopLog.Write("Browser surface rejected: layout bounds are outside the desktop window.");
            return;
        }

        var nextTabId = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(nextTabId))
        {
            DesktopLog.Write("Browser surface rejected: missing tab identifier.");
            return;
        }
        var switchingTabs = _tabId != nextTabId;
        _tabId = nextTabId;
        _view.Margin = new Thickness(x, y, Math.Max(0, _window.ActualWidth - x - width), Math.Max(0, _window.ActualHeight - y - height));
        // ResizeObserver keeps reporting layout while the renderer displays a
        // navigation error. Do not let those placement updates resurrect the
        // failed WebView's white compositor over the recovery panel.
        if (_suppressedTabIds.Contains(_tabId))
        {
            _surfaceVisible = false;
            _view.Visibility = Visibility.Collapsed;
            DesktopLog.Write("Browser surface remains suppressed for the failed tab.");
            PostState();
            return;
        }
        _view.Visibility = Visibility.Visible;
        _surfaceVisible = true;
        DesktopLog.Write($"Browser surface visible: requested={width}x{height}, actual={_view.ActualWidth:F0}x{_view.ActualHeight:F0}, composition=true.");
        var createdTab = false;
        if (!_tabs.TryGetValue(_tabId, out var tab))
        {
            if (_tabs.Count >= 32) _tabs.Remove(_tabs.Keys.First(key => key != _tabId));
            var initial = TryNormalizeAddress(address, out var requested) ? requested : new Uri("about:blank");
            tab = new BrowserTabState(initial);
            _tabs[_tabId] = tab;
            createdTab = true;
        }
        // A WPF WebView2 must have a real, visible native surface before its
        // controller is created. Initializing while Collapsed can complete
        // navigation without ever creating a paintable child window.
        if (!await EnsureInitializedAsync())
        {
            DesktopLog.Write("Browser surface rejected: native browser initialization failed.");
            if (!_disposed && _surfaceRequests.IsCurrent(surfaceRequest))
            {
                _surfaceVisible = false;
                _view.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (_disposed || !_surfaceRequests.IsCurrent(surfaceRequest))
        {
            DesktopLog.Write("Browser surface discarded: a newer layout request superseded it.");
            return;
        }
        // React can issue several placements while the layout settles. An
        // older request may create the tab and then lose the generation race
        // during WebView2 startup. The winning request must still navigate a
        // tab that has never actually been displayed, or the surface stays
        // permanently white.
        if (createdTab || switchingTabs || _displayedTabId != _tabId)
            NavigateCore(_tabId, tab.CurrentUri, replaceCurrent: true);
        PostState();
    }

    internal void Hide()
    {
        _surfaceRequests.Invalidate();
        if (_disposed) return;
        _surfaceVisible = false;
        _view.Visibility = Visibility.Collapsed;
        DesktopLog.Write("Browser surface hidden.");
    }

    internal async Task NavigateAsync(string? tabId, string? address)
    {
        if (_disposed || !TryNormalizeAddress(address, out var uri))
        {
            DesktopLog.Write("Browser navigation rejected: invalid address.");
            PostError("That browser address could not be opened.");
            return;
        }
        var id = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(id))
        {
            DesktopLog.Write("Browser navigation rejected: missing tab identifier.");
            PostError("That browser tab is no longer available.");
            return;
        }
        if (!_tabs.TryGetValue(id, out var tab))
        {
            tab = new BrowserTabState(uri);
            _tabs[id] = tab;
        }
        else tab.Navigate(uri);
        _tabId = id;
        _suppressedTabIds.Remove(id);
        if (!_surfaceVisible)
        {
            DesktopLog.Write("Browser navigation deferred until the surface is visible.");
            return;
        }
        // A later renderer command may have selected another tab while this
        // one waited for WebView2 startup. Never let that stale continuation
        // paint over the currently active native surface.
        if (!await EnsureInitializedAsync())
        {
            DesktopLog.Write("Browser navigation rejected: native browser initialization failed.");
            return;
        }
        if (_disposed || _tabId != id)
        {
            DesktopLog.Write("Browser navigation discarded: a newer tab selection superseded it.");
            return;
        }
        NavigateCore(id, uri, replaceCurrent: true);
        PostState();
    }

    internal void Back(string? tabId) { if (TrySelectTab(tabId, out var tab) && tab.TryBack(out var uri)) ResumeAndNavigate(uri); }
    internal void Forward(string? tabId) { if (TrySelectTab(tabId, out var tab) && tab.TryForward(out var uri)) ResumeAndNavigate(uri); }
    internal void Reload(string? tabId)
    {
        if (!TrySelectTab(tabId, out _)) return;
        _suppressedTabIds.Remove(_tabId);
        _surfaceVisible = true;
        _view.Visibility = Visibility.Visible;
        _view.CoreWebView2.Reload();
    }
    internal void Stop(string? tabId) { if (TrySelectTab(tabId, out _)) _view.CoreWebView2.Stop(); }

    internal void CloseTab(string? tabId)
    {
        var id = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(id)) return;
        _tabs.Remove(id);
        if (_displayedTabId == id) _displayedTabId = string.Empty;
        _suppressedTabIds.Remove(id);
        if (_tabId == id) _tabId = string.Empty;
    }

    internal void AcceptOpenRequest(string? requestId, string? tabId)
    {
        var pendingId = Bounded(requestId, 128);
        PurgeOpenRequests();
        if (!_pendingOpenRequests.Remove(pendingId, out var request) || request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            PostError("That browser open request expired. Try the link again.");
            return;
        }
        var id = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(id)) return;
        _tabId = id;
        if (!_tabs.TryGetValue(id, out var tab))
        {
            tab = new BrowserTabState(request.Uri);
            _tabs[id] = tab;
        }
        else tab.Navigate(request.Uri);
        NavigateCore(id, request.Uri, replaceCurrent: true);
    }

    private async Task<bool> EnsureInitializedAsync()
    {
        if (_initialized) return true;
        var initialization = GetOrCreateInitializationTask(ref _initializationTask, InitializeCoreAsync);
        var initialized = await initialization;
        // A transient WebView2 startup failure must not poison this bridge for
        // the rest of the desktop session. The next visible request gets a
        // clean initialization attempt.
        if (!initialized && ReferenceEquals(_initializationTask, initialization))
            _initializationTask = null;
        return initialized;
    }

    internal static Task<T> GetOrCreateInitializationTask<T>(ref Task<T>? task, Func<Task<T>> factory) =>
        task ??= factory();

    private async Task<bool> InitializeCoreAsync()
    {
        try
        {
            if (_disposed) return false;
            if (_view.CoreWebView2 is null)
            {
                var environment = await CreateBrowserEnvironmentAsync();
                if (_disposed) return false;
                await _view.EnsureCoreWebView2Async(environment);
            }
            if (_disposed) return false;
            var core = _view.CoreWebView2
                ?? throw new InvalidOperationException("The native browser did not initialize.");
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.NavigationStarting += (_, eventArgs) =>
            {
                if (!TryNormalizeAddress(eventArgs.Uri, out var navigationUri))
                {
                    eventArgs.Cancel = true;
                    PostError("The browser could not open that navigation.");
                    return;
                }
                // Google rejects this OAuth continuation in an embedded WebView
                // (the observed result is HTTP 405). Keep the opaque callback
                // URL in the trusted host, let the default browser finish the
                // provider-owned flow, and preserve the original Claude tab.
                if (RequiresExternalAuthentication(navigationUri))
                {
                    eventArgs.Cancel = true;
                    _externalAuthenticationNavigations.Add(eventArgs.NavigationId);
                    if (_startingNavigation?.Uri.AbsoluteUri == navigationUri.AbsoluteUri)
                        _startingNavigation = null;
                    if (!OpenInDefaultBrowser(navigationUri))
                    {
                        SuppressWhiteSurface("Google sign-in needs your default browser, but Photon could not open it. Open Claude in your browser and try again.");
                        return;
                    }
                    SuppressForExternalAuthentication(_tabId);
                    DesktopLog.Write("Browser authentication handed off to the system browser: accounts.google.com.");
                    return;
                }
                BrowserNavigationContext context;
                if (_navigations.TryGetValue(eventArgs.NavigationId, out var redirect))
                {
                    context = redirect with { Uri = navigationUri };
                }
                else if (_startingNavigation is not null)
                {
                    context = _startingNavigation with { Uri = navigationUri };
                    _startingNavigation = null;
                }
                else
                {
                    context = new BrowserNavigationContext(_tabId, navigationUri, ReplaceCurrent: false);
                }
                _navigations[eventArgs.NavigationId] = context;
                DesktopLog.Write($"Browser navigation starting: {DiagnosticLocation(navigationUri)}.");
                PostState();
            };
            core.NavigationCompleted += (_, eventArgs) =>
            {
                if (_externalAuthenticationNavigations.Remove(eventArgs.NavigationId))
                {
                    DesktopLog.Write("Browser authentication navigation cancelled after system-browser handoff.");
                    PostState();
                    return;
                }
                BrowserNavigationContext? completedContext = null;
                if (_navigations.Remove(eventArgs.NavigationId, out var context))
                {
                    completedContext = context;
                    ApplyNavigationCompletion(_tabs, context, eventArgs.IsSuccess);
                    if (eventArgs.IsSuccess)
                    {
                        _suppressedTabIds.Remove(context.TabId);
                        _displayedTabId = context.TabId;
                        _processRecoveryAttempts = 0;
                        ClearError();
                    }
                }
                DesktopLog.Write($"Browser navigation completed: success={eventArgs.IsSuccess}, status={eventArgs.WebErrorStatus}, http={eventArgs.HttpStatusCode}.");
                if (!eventArgs.IsSuccess && (completedContext is null || completedContext.TabId == _tabId))
                {
                    var reason = eventArgs.WebErrorStatus switch
                    {
                        CoreWebView2WebErrorStatus.HostNameNotResolved => "The site name could not be resolved. Check the address or network connection, then reload.",
                        CoreWebView2WebErrorStatus.Timeout => "The site did not respond in time. Reload to try again.",
                        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
                            => "The site connection was interrupted. Reload to reconnect.",
                        CoreWebView2WebErrorStatus.CannotConnect => "The site refused the connection or is currently unavailable.",
                        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                            or CoreWebView2WebErrorStatus.CertificateExpired
                            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
                            or CoreWebView2WebErrorStatus.CertificateRevoked
                            or CoreWebView2WebErrorStatus.CertificateIsInvalid
                            => "The site certificate could not be validated. Photon did not bypass the browser warning.",
                        _ => "The page did not load. Check the address or connection, then reload.",
                    };
                    SuppressWhiteSurface(reason);
                }
                else if (eventArgs.IsSuccess && completedContext is not null)
                {
                    _ = VerifyUsableDocumentAsync(completedContext);
                }
                PostState();
            };
            core.ProcessFailed += (_, eventArgs) =>
            {
                DesktopLog.Write($"Browser WebView process failed: {eventArgs.ProcessFailedKind}, reason={eventArgs.Reason}.");
                if (!_surfaceVisible || _processRecoveryAttempts >= 3)
                {
                    SuppressWhiteSurface("The browser process stopped. Reload the tab to try again.");
                    return;
                }
                _processRecoveryAttempts++;
                PostError($"The browser process stopped. Photon is restarting it automatically (attempt {_processRecoveryAttempts} of 3).");
                _ = RecoverBrowserProcessAsync();
            };
            core.SourceChanged += (_, _) => PostState();
            core.DocumentTitleChanged += (_, _) => PostState();
            core.HistoryChanged += (_, _) => PostState();
            core.NewWindowRequested += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                if (TryNormalizeAddress(eventArgs.Uri, out var uri))
                {
                    PurgeOpenRequests();
                    while (_pendingOpenRequests.Count >= 32) _pendingOpenRequests.Remove(_pendingOpenRequests.Keys.First());
                    var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    _pendingOpenRequests[requestId] = (uri, DateTimeOffset.UtcNow.AddMinutes(2));
                    _postMessage(new { type = "browser.openRequested", version = ProtocolVersion, requestId, url = RendererSafeDisplayUrl(uri) });
                }
            };
            // Leave browser capabilities to WebView2's normal browser behavior.
            // Pages can use their standard permission prompt flow and downloads
            // instead of being silently denied by the host.
            _initialized = true;
            return true;
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Native browser initialization failed: {exception.GetType().Name}.");
            _postMessage(new
            {
                type = "browser.error",
                version = ProtocolVersion,
                tabId = _tabId,
                message = "The native browser could not initialize. The desktop remains available.",
            });
            return false;
        }
    }

    private static async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localData, "PhotosAgapeAphthartos");
        var candidates = new[]
        {
            Path.Combine(root, "BrowserWebView2"),
            Path.Combine(root, "BrowserWebView2-Recovery"),
        };

        Exception? lastFailure = null;
        foreach (var dataFolder in candidates)
        {
            try
            {
                Directory.CreateDirectory(dataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
                DesktopLog.Write($"Browser profile initialized: recovery={(!string.Equals(dataFolder, candidates[0], StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant()}.");
                return environment;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
            {
                lastFailure = exception;
                DesktopLog.Write($"Browser profile unavailable: recovery={(!string.Equals(dataFolder, candidates[0], StringComparison.OrdinalIgnoreCase)).ToString().ToLowerInvariant()}, error={exception.GetType().Name}.");
            }
        }

        throw new InvalidOperationException("No writable browser profile is available.", lastFailure);
    }

    private async Task RecoverBrowserProcessAsync()
    {
        if (_processRecoveryPending || _disposed) return;
        _processRecoveryPending = true;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            if (_disposed || !_surfaceVisible || !_initialized) return;
            await _window.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_surfaceVisible || _view.CoreWebView2 is null) return;
                DesktopLog.Write($"Browser process recovery dispatched: attempt={_processRecoveryAttempts}.");
                _view.CoreWebView2.Reload();
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            DesktopLog.Write($"Browser process recovery failed: {exception.GetType().Name}.");
            PostError("The browser could not restart automatically. Reload the tab to try again.");
        }
        finally
        {
            _processRecoveryPending = false;
        }
    }

    private async Task VerifyUsableDocumentAsync(BrowserNavigationContext context)
    {
        if (context.Uri.AbsoluteUri == "about:blank") return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (_disposed || !_surfaceVisible || _tabId != context.TabId || _view.CoreWebView2 is null) return;
            var result = await _view.CoreWebView2.ExecuteScriptAsync(
                "(() => { const body = document.body; if (!body) return 0; const text = (body.innerText || '').trim().length; const rendered = [...body.querySelectorAll('*')].filter(element => { const style = getComputedStyle(element); const rect = element.getBoundingClientRect(); return style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity || 1) > 0 && rect.width >= 2 && rect.height >= 2; }).length; return text + rendered; })()");
            var hasDocumentContent = int.TryParse(result, out var visibleEvidence) && visibleEvidence > 0;
            var paint = await MeasurePaintAsync();
            DesktopLog.Write($"Browser paint probe: painted={paint.Painted}, samples={paint.Samples}, distinct={paint.DistinctColors}, nonDominant={paint.NonDominantPixels}.");
            // NavigationCompleted is the browser's authority for whether a
            // navigation succeeded. CapturePreview is not a reliable liveness
            // oracle for a composition-controlled WebView2: a page can be
            // visibly rendered and interactive while the off-screen capture
            // is still empty. Keep this probe diagnostic-only. Never reload,
            // hide, or contradict a successfully rendered page because a
            // synthetic DOM/paint heuristic was inconclusive.
            if (hasDocumentContent || paint.Painted)
                DesktopLog.Write($"Browser document usable: {DiagnosticLocation(context.Uri)}.");
            else
                DesktopLog.Write($"Browser document probe inconclusive after successful navigation; preserving the live surface: {DiagnosticLocation(context.Uri)}.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or COMException)
        {
            DesktopLog.Write($"Browser document usability probe unavailable: {exception.GetType().Name}.");
        }
    }

    private async Task<BrowserPaintEvidence> MeasurePaintAsync()
    {
        try
        {
            using var capture = new MemoryStream();
            await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            if (capture.Length == 0) return BrowserPaintEvidence.Empty;
            capture.Position = 0;

            var decoder = new PngBitmapDecoder(capture, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return BrowserPaintEvidence.Empty;

            const int maximumSampleDimension = 160;
            var scale = Math.Min(1d, maximumSampleDimension / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource sampled = scale < 1d
                ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                : frame;
            if (sampled.Format != PixelFormats.Bgra32)
                sampled = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);

            var stride = sampled.PixelWidth * 4;
            var pixels = new byte[stride * sampled.PixelHeight];
            sampled.CopyPixels(pixels, stride, 0);
            if (pixels.Length < 4) return BrowserPaintEvidence.Empty;

            // A few antialiased pixels are not proof of a presented page. Use
            // color buckets and require a material non-dominant region so a
            // near-uniform white compositor frame cannot pass as usable.
            var samples = pixels.Length / 4;
            var colors = new Dictionary<int, int>();
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var key = (pixels[index + 2] >> 4) << 8
                    | (pixels[index + 1] >> 4) << 4
                    | (pixels[index] >> 4);
                colors[key] = colors.GetValueOrDefault(key) + 1;
            }

            var dominant = colors.Count == 0 ? samples : colors.Values.Max();
            var nonDominant = samples - dominant;
            var painted = colors.Count >= 4 && nonDominant >= Math.Max(64, samples / 100);
            return new BrowserPaintEvidence(painted, samples, colors.Count, nonDominant);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or COMException or NotSupportedException)
        {
            DesktopLog.Write($"Browser rendered-frame probe unavailable: {exception.GetType().Name}.");
            return BrowserPaintEvidence.Empty;
        }
    }

    private void SuppressWhiteSurface(string message)
    {
        _surfaceRequests.Invalidate();
        if (!string.IsNullOrEmpty(_tabId)) _suppressedTabIds.Add(_tabId);
        _surfaceVisible = false;
        _view.Visibility = Visibility.Collapsed;
        PostError(message);
        DesktopLog.Write("Browser surface suppressed so the renderer can present the recovery action.");
    }

    private void SuppressForExternalAuthentication(string tabId)
    {
        _surfaceRequests.Invalidate();
        if (!string.IsNullOrEmpty(tabId)) _suppressedTabIds.Add(tabId);
        _surfaceVisible = false;
        _view.Visibility = Visibility.Collapsed;
        _postMessage(new
        {
            type = "browser.externalAuthentication",
            version = ProtocolVersion,
            tabId,
            message = "Google sign-in opened in your default browser. Finish there, then return to Photon and click Return to Claude.",
        });
    }

    private void ResumeAndNavigate(Uri uri)
    {
        _suppressedTabIds.Remove(_tabId);
        _surfaceVisible = true;
        _view.Visibility = Visibility.Visible;
        NavigateCore(_tabId, uri, replaceCurrent: true);
    }

    private void PostState()
    {
        if (!_initialized) return;
        _tabs.TryGetValue(_tabId, out var tab);
        _postMessage(new
        {
            type = "browser.state",
            version = ProtocolVersion,
            tabId = _tabId,
            url = RendererSafeDisplayUrl(tab?.CurrentUri),
            title = RendererSafeTitle(tab?.CurrentUri),
            canGoBack = tab?.CanGoBack == true,
            canGoForward = tab?.CanGoForward == true,
            loading = _navigations.Values.Any(navigation => navigation.TabId == _tabId)
                || _startingNavigation?.TabId == _tabId,
        });
    }

    private void PostError(string message) => _postMessage(new { type = "browser.error", version = ProtocolVersion, tabId = _tabId, message });
    private void ClearError() => _postMessage(new { type = "browser.error", version = ProtocolVersion, tabId = _tabId, message = string.Empty });

    internal static bool TryNormalizeAddress(string? address, out Uri uri)
    {
        uri = null!;
        var text = address?.Trim();
        if (string.Equals(text, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri("about:blank");
            return true;
        }
        if (string.IsNullOrEmpty(text) || text.Length > 4_096 || text.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out uri!)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return string.IsNullOrEmpty(uri.UserInfo);
    }

    internal static bool RequiresExternalAuthentication(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.IdnHost, "accounts.google.com", StringComparison.OrdinalIgnoreCase);

    private static bool OpenInDefaultBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DesktopLog.Write($"System-browser authentication handoff failed: {exception.GetType().Name}.");
            return false;
        }
    }

    private void PurgeOpenRequests()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _pendingOpenRequests.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            _pendingOpenRequests.Remove(key);
    }

    private bool TrySelectTab(string? tabId, out BrowserTabState tab)
    {
        tab = null!;
        if (!_initialized) return false;
        var id = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(id) || !_tabs.TryGetValue(id, out var selected))
        {
            PostError("That browser tab is no longer available.");
            return false;
        }
        tab = selected;
        if (_tabId != id)
        {
            _tabId = id;
            NavigateCore(id, tab.CurrentUri, replaceCurrent: true);
        }
        return true;
    }

    private void NavigateCore(string tabId, Uri uri, bool replaceCurrent)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(tabId) || !_tabs.ContainsKey(tabId)) return;
        if (IsEquivalentNavigationPendingOrDisplayed(
            tabId,
            uri,
            _startingNavigation,
            _navigations.Values,
            [],
            _displayedTabId,
            _view.Source)) return;
        // CoreWebView2 already defines the browser's replacement semantics: a
        // newer Navigate cancels the previous navigation.  Do not add a host
        // queue that can remain blocked after a cancellation callback is lost.
        _startingNavigation = new BrowserNavigationContext(tabId, uri, replaceCurrent);
        DesktopLog.Write($"Browser navigation dispatched: {DiagnosticLocation(uri)}.");
        _view.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    internal static void ApplyNavigationCompletion(
        IDictionary<string, BrowserTabState> tabs,
        BrowserNavigationContext context,
        bool succeeded)
    {
        if (!succeeded || !tabs.TryGetValue(context.TabId, out var tab)) return;
        if (context.ReplaceCurrent) tab.ReplaceCurrent(context.Uri);
        else tab.Capture(context.Uri);
    }

    internal static bool IsEquivalentNavigationPendingOrDisplayed(
        string tabId,
        Uri uri,
        BrowserNavigationContext? starting,
        IEnumerable<BrowserNavigationContext> inFlight,
        IEnumerable<BrowserNavigationContext> queued,
        string? displayedTabId,
        Uri? displayed)
    {
        if (starting is not null && starting.TabId == tabId && starting.Uri.AbsoluteUri == uri.AbsoluteUri) return true;
        var active = inFlight.ToArray();
        if (active.Any(navigation => navigation.TabId == tabId && navigation.Uri.AbsoluteUri == uri.AbsoluteUri)) return true;
        var waiting = queued.ToArray();
        if (waiting.Any(navigation => navigation.TabId == tabId && navigation.Uri.AbsoluteUri == uri.AbsoluteUri)) return true;
        return starting is null
            && active.Length == 0
            && waiting.Length == 0
            && string.Equals(displayedTabId, tabId, StringComparison.Ordinal)
            && string.Equals(displayed?.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal);
    }

    internal static string RendererSafeDisplayUrl(Uri? uri)
    {
        if (uri is null || uri.AbsoluteUri == "about:blank") return "about:blank";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "about:blank";
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{authority}/";
    }

    internal static string RendererSafeTitle(Uri? uri)
    {
        if (uri is null || uri.AbsoluteUri == "about:blank") return "New tab";
        if (uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.IdnHost, "www.google.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/maps/", StringComparison.Ordinal)) return "Google Maps";
        return Bounded(uri.IdnHost, 256);
    }

    private static string Bounded(string? value, int maximum) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maximum)];

    private static string DiagnosticLocation(Uri uri) => uri.Scheme is "http" or "https"
        ? $"{uri.Scheme}://{uri.DnsSafeHost}"
        : uri.Scheme;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _surfaceRequests.Invalidate();
        _view.Visibility = Visibility.Collapsed;
        _view.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class BrowserSurfaceRequestGate
{
    private long _generation;

    internal long Begin() => Interlocked.Increment(ref _generation);

    internal void Invalidate() => Interlocked.Increment(ref _generation);

    internal bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);
}

internal sealed record BrowserNavigationContext(string TabId, Uri Uri, bool ReplaceCurrent);

internal sealed record BrowserPaintEvidence(bool Painted, int Samples, int DistinctColors, int NonDominantPixels)
{
    internal static BrowserPaintEvidence Empty { get; } = new(false, 0, 0, 0);
}

internal sealed class BrowserTabState
{
    private readonly List<Uri> _history;
    private int _index;

    internal BrowserTabState(Uri initial)
    {
        _history = [initial];
    }

    internal Uri CurrentUri => _history[_index];
    internal bool CanGoBack => _index > 0;
    internal bool CanGoForward => _index + 1 < _history.Count;

    internal void Navigate(Uri uri)
    {
        if (CurrentUri.AbsoluteUri == uri.AbsoluteUri) return;
        if (CanGoForward) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        _history.Add(uri);
        _index = _history.Count - 1;
    }

    internal void Capture(Uri uri) => Navigate(uri);

    internal void ReplaceCurrent(Uri uri) => _history[_index] = uri;

    internal bool TryBack(out Uri uri)
    {
        if (!CanGoBack) { uri = CurrentUri; return false; }
        uri = _history[--_index];
        return true;
    }

    internal bool TryForward(out Uri uri)
    {
        if (!CanGoForward) { uri = CurrentUri; return false; }
        uri = _history[++_index];
        return true;
    }
}
