using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HermesDesktop;

internal sealed class BrowserSurfaceBridge : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    private readonly Window _window;
    private readonly WebView2 _view;
    private readonly Action<object> _postMessage;
    private Task<bool>? _initializationTask;
    private bool _initialized;
    private string _tabId = string.Empty;
    private readonly Dictionary<string, BrowserTabState> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, BrowserNavigationContext> _navigations = new();
    private readonly Queue<BrowserNavigationContext> _navigationQueue = new();
    private BrowserNavigationContext? _startingNavigation;
    private string _displayedTabId = string.Empty;
    private readonly Dictionary<string, (Uri Uri, DateTimeOffset ExpiresAt)> _pendingOpenRequests = new();

    internal BrowserSurfaceBridge(Window window, WebView2 view, Action<object> postMessage)
    {
        _window = window;
        _view = view;
        _postMessage = postMessage;
    }

    internal async Task ShowAsync(int x, int y, int width, int height, string? tabId, string? address)
    {
        if (!await EnsureInitializedAsync()) return;
        if (width < 80 || height < 80 || x < 0 || y < 0) return;
        var availableWidth = Math.Max(0, _window.ActualWidth - x);
        var availableHeight = Math.Max(0, _window.ActualHeight - y);
        width = (int)Math.Min(width, availableWidth);
        height = (int)Math.Min(height, availableHeight);
        if (width < 80 || height < 80) return;

        var nextTabId = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(nextTabId)) return;
        var switchingTabs = _tabId != nextTabId;
        _tabId = nextTabId;
        _view.Margin = new Thickness(x, y, Math.Max(0, _window.ActualWidth - x - width), Math.Max(0, _window.ActualHeight - y - height));
        _view.Visibility = Visibility.Visible;
        var createdTab = false;
        if (!_tabs.TryGetValue(_tabId, out var tab))
        {
            if (_tabs.Count >= 32) _tabs.Remove(_tabs.Keys.First(key => key != _tabId));
            var initial = TryNormalizeAddress(address, out var requested) ? requested : new Uri("about:blank");
            tab = new BrowserTabState(initial);
            _tabs[_tabId] = tab;
            createdTab = true;
        }
        if (createdTab || switchingTabs) NavigateCore(tab.CurrentUri, replaceCurrent: true);
        PostState();
    }

    internal void Hide() => _view.Visibility = Visibility.Collapsed;

    internal void Navigate(string? tabId, string? address)
    {
        if (!_initialized || !TryNormalizeAddress(address, out var uri))
        {
            PostError("Only HTTP and HTTPS browser addresses are supported.");
            return;
        }
        if (!TrySelectTab(tabId, out var tab)) return;
        tab.Navigate(uri);
        NavigateCore(uri, replaceCurrent: true);
    }

    internal void Back(string? tabId) { if (TrySelectTab(tabId, out var tab) && tab.TryBack(out var uri)) NavigateCore(uri, replaceCurrent: true); }
    internal void Forward(string? tabId) { if (TrySelectTab(tabId, out var tab) && tab.TryForward(out var uri)) NavigateCore(uri, replaceCurrent: true); }
    internal void Reload(string? tabId) { if (TrySelectTab(tabId, out _)) _view.CoreWebView2.Reload(); }
    internal void Stop(string? tabId) { if (TrySelectTab(tabId, out _)) _view.CoreWebView2.Stop(); }

    internal void CloseTab(string? tabId)
    {
        var id = Bounded(tabId, 128);
        if (string.IsNullOrWhiteSpace(id)) return;
        _tabs.Remove(id);
        if (_displayedTabId == id) _displayedTabId = string.Empty;
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
        NavigateCore(request.Uri, replaceCurrent: true);
    }

    private Task<bool> EnsureInitializedAsync()
    {
        if (_initialized) return Task.FromResult(true);
        return GetOrCreateInitializationTask(ref _initializationTask, InitializeCoreAsync);
    }

    internal static Task<T> GetOrCreateInitializationTask<T>(ref Task<T>? task, Func<Task<T>> factory) =>
        task ??= factory();

    private async Task<bool> InitializeCoreAsync()
    {
        try
        {
            if (_view.CoreWebView2 is null)
            {
                var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotosAgapeAphthartos", "BrowserWebView2");
                Directory.CreateDirectory(dataFolder);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
                await _view.EnsureCoreWebView2Async(environment);
            }
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
                PostError("The browser blocked a non-HTTP navigation.");
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
            PostState();
        };
        core.NavigationCompleted += (_, eventArgs) =>
        {
            if (_navigations.Remove(eventArgs.NavigationId, out var context))
            {
                ApplyNavigationCompletion(_tabs, context, eventArgs.IsSuccess);
                if (eventArgs.IsSuccess) _displayedTabId = context.TabId;
            }
            StartNextNavigation();
            PostState();
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
        core.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            PostError("Downloads are blocked until the Workbench download review is available.");
        };
            _initialized = true;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
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

    private static bool TryNormalizeAddress(string? address, out Uri uri)
    {
        uri = null!;
        if (string.Equals(address?.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri("about:blank");
            return true;
        }
        return Uri.TryCreate(address?.Trim(), UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
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
            NavigateCore(tab.CurrentUri, replaceCurrent: true);
        }
        return true;
    }

    private void NavigateCore(Uri uri, bool replaceCurrent)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(_tabId)) return;
        if (IsEquivalentNavigationPendingOrDisplayed(
            _tabId,
            uri,
            _startingNavigation,
            _navigations.Values,
            _navigationQueue,
            _displayedTabId,
            _view.Source)) return;
        if (_navigationQueue.Count >= 32) _navigationQueue.Dequeue();
        _navigationQueue.Enqueue(new BrowserNavigationContext(_tabId, uri, replaceCurrent));
        if (_navigations.Count > 0)
        {
            _view.CoreWebView2.Stop();
            return;
        }
        StartNextNavigation();
    }

    private void StartNextNavigation()
    {
        if (!_initialized || _startingNavigation is not null || _navigations.Count > 0) return;
        while (_navigationQueue.TryDequeue(out var next))
        {
            if (!_tabs.ContainsKey(next.TabId)) continue;
            _startingNavigation = next;
            _view.CoreWebView2.Navigate(next.Uri.AbsoluteUri);
            return;
        }
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

    internal static string RendererSafeTitle(Uri? uri) => uri is null || uri.AbsoluteUri == "about:blank"
        ? "New tab"
        : Bounded(uri.IdnHost, 256);

    private static string Bounded(string? value, int maximum) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maximum)];

    public ValueTask DisposeAsync()
    {
        _view.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record BrowserNavigationContext(string TabId, Uri Uri, bool ReplaceCurrent);

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
