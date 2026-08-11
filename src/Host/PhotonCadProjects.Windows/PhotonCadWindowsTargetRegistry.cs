using System.IO;

namespace PhotonCadProjects.Windows;

/// <summary>
/// Host-only registry that converts native file selections into opaque storage targets.
/// Exact paths never cross the renderer boundary.
/// </summary>
public sealed class PhotonCadWindowsTargetRegistry : IDisposable
{
    private static readonly TimeSpan SelectionTimeToLive = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly int _maximumTargets;
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pathToHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingSelection> _selections = new(StringComparer.Ordinal);
    private bool _closed;

    public PhotonCadWindowsTargetRegistry(int maximumTargets = PhotonCadProjectContract.MaximumKnownWorkspaces)
    {
        if (maximumTargets is < 1 or > PhotonCadProjectContract.MaximumKnownWorkspaces)
            throw new ArgumentOutOfRangeException(nameof(maximumTargets));
        _maximumTargets = maximumTargets;
    }

    public bool Available
    {
        get { lock (_sync) return !_closed && PhotonCadWindowsNative.IsSupported; }
    }

    public PhotonCadWorkspaceBinding RegisterExactPath(string exactPath, string? displayLabel = null)
    {
        var canonical = PhotonCadWindowsNative.CanonicalizeLocalFilePath(exactPath);
        _ = PhotonCadWindowsNative.InspectExactPath(canonical);
        var label = SafeLabel(displayLabel, canonical);
        lock (_sync)
        {
            ThrowIfClosed();
            if (_pathToHandle.TryGetValue(canonical, out var existing)) return _targets[existing].Binding;
            if (_targets.Count >= _maximumTargets)
                throw new PhotonCadProjectException("target_registry_capacity_reached", nameof(exactPath));
            PhotonCadStorageTargetHandle handle;
            do { handle = new PhotonCadStorageTargetHandle($"cad-storage-target:{PhotonCadWindowsNative.NewOpaqueToken()}"); }
            while (_targets.ContainsKey(handle.Value));
            var binding = new PhotonCadWorkspaceBinding(handle, label);
            _targets.Add(handle.Value, new TargetState(binding, canonical));
            _pathToHandle.Add(canonical, handle.Value);
            return binding;
        }
    }

    /// <summary>
    /// Creates a short-lived, single-use host-native mount token. The token is not a renderer
    /// capability and must never be serialized into web content.
    /// </summary>
    public string IssueMountToken(string exactPath, string? displayLabel = null)
    {
        var canonical = PhotonCadWindowsNative.CanonicalizeLocalFilePath(exactPath);
        _ = PhotonCadWindowsNative.InspectExactPath(canonical);
        var label = SafeLabel(displayLabel, canonical);
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            ThrowIfClosed();
            PruneSelections(now);
            while (_selections.Count >= PhotonCadProjectContract.MaximumKnownWorkspaces)
            {
                var oldest = _selections.MinBy(pair => pair.Value.IssuedAtUtc).Key;
                _selections.Remove(oldest);
            }
            string token;
            do { token = $"cad-windows-selection:{PhotonCadWindowsNative.NewOpaqueToken()}"; }
            while (_selections.ContainsKey(token));
            _selections.Add(token, new PendingSelection(canonical, label, now, now.Add(SelectionTimeToLive)));
            return token;
        }
    }

    internal PhotonCadWorkspaceBinding ConsumeMountToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > PhotonCadProjectContract.MaximumOpaqueTokenLength
            || !token.StartsWith("cad-windows-selection:", StringComparison.Ordinal))
            throw new PhotonCadProjectException("invalid_native_selection_token", nameof(token));
        PendingSelection selection;
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            ThrowIfClosed();
            PruneSelections(now);
            if (!_selections.Remove(token, out selection!))
                throw new PhotonCadProjectException("native_selection_unknown_or_used", nameof(token));
        }
        if (now >= selection.ExpiresAtUtc)
            throw new PhotonCadProjectException("native_selection_expired", nameof(token));
        return RegisterExactPath(selection.ExactPath, selection.Label);
    }

    internal string Resolve(PhotonCadStorageTargetHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_sync)
        {
            ThrowIfClosed();
            if (!_targets.TryGetValue(target.Value, out var state))
                throw new PhotonCadProjectException("storage_target_unknown", nameof(target));
            return state.ExactPath;
        }
    }

    internal PhotonCadStorageTargetHandle RestoreHostOnlyTarget(PhotonCadStorageTargetHandle target, string exactPath, string label)
    {
        ArgumentNullException.ThrowIfNull(target);
        var canonical = PhotonCadWindowsNative.CanonicalizeLocalFilePath(exactPath);
        _ = PhotonCadWindowsNative.InspectExactPath(canonical);
        var safeLabel = SafeLabel(label, canonical);
        lock (_sync)
        {
            ThrowIfClosed();
            if (_targets.TryGetValue(target.Value, out var existing))
            {
                if (!string.Equals(existing.ExactPath, canonical, StringComparison.OrdinalIgnoreCase))
                    throw new PhotonCadProjectException("restored_target_binding_mismatch", nameof(target));
                return existing.Binding.Target;
            }
            if (_pathToHandle.TryGetValue(canonical, out var existingHandle)
                && !string.Equals(existingHandle, target.Value, StringComparison.Ordinal))
                return _targets[existingHandle].Binding.Target;
            if (_targets.Count >= _maximumTargets)
                throw new PhotonCadProjectException("target_registry_capacity_reached", nameof(target));
            var binding = new PhotonCadWorkspaceBinding(target, safeLabel);
            _targets.Add(target.Value, new TargetState(binding, canonical));
            _pathToHandle.Add(canonical, target.Value);
            return target;
        }
    }

    internal IReadOnlyList<string> KnownParentDirectories()
    {
        lock (_sync)
        {
            ThrowIfClosed();
            return _targets.Values
                .Select(state => Path.GetDirectoryName(state.ExactPath)!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _selections.Clear();
            _pathToHandle.Clear();
            _targets.Clear();
        }
    }

    private static string SafeLabel(string? requested, string exactPath)
    {
        var label = string.IsNullOrWhiteSpace(requested) ? Path.GetFileName(exactPath) : requested.Trim();
        if (label.Length > PhotonCadProjectContract.MaximumPickerLabelLength)
            throw new PhotonCadProjectException("picker_label_too_long", nameof(requested));
        if (label.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
            || label.Contains('/') || label.Contains('\\'))
            throw new PhotonCadProjectException("unsafe_picker_label", nameof(requested));
        return label;
    }

    private void PruneSelections(DateTimeOffset now)
    {
        foreach (var expired in _selections.Where(pair => now >= pair.Value.ExpiresAtUtc).Select(pair => pair.Key).ToArray())
            _selections.Remove(expired);
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(PhotonCadWindowsTargetRegistry));
    }

    private sealed record TargetState(PhotonCadWorkspaceBinding Binding, string ExactPath);
    private sealed record PendingSelection(string ExactPath, string Label, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
}
