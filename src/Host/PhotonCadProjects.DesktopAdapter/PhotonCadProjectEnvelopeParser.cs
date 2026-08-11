using System.Text.Json;

namespace PhotonCadProjects.DesktopAdapter;

public static class PhotonCadProjectEnvelopeParser
{
    public static bool TryParse(JsonElement frame, out PhotonCadProjectDesktopRequest? request)
    {
        request = null;
        if (frame.ValueKind != JsonValueKind.Object || !TryString(frame, "type", out var type)
            || !TryInteger(frame, "version", out var version) || version != PhotonCadProjectDesktopProtocol.Version)
            return false;
        try
        {
            request = type switch
            {
                "photonCad.project.picker" => ParsePicker(frame),
                "photonCad.project.create" => ParseCreate(frame),
                "photonCad.project.open" => ParseOpen(frame),
                "photonCad.project.reopen" => ParseReopen(frame),
                "photonCad.project.refresh" => ParseRefresh(frame),
                "photonCad.project.save" => ParseSave(frame),
                "photonCad.project.saveAs" => ParseSaveAs(frame),
                "photonCad.project.close" => ParseClose(frame),
                "photonCad.project.cancel" => ParseCancel(frame),
                _ => null,
            };
            return request is not null;
        }
        catch (Exception exception) when (exception is PhotonCadProjectException or ArgumentException or InvalidOperationException or OverflowException)
        {
            request = null;
            return false;
        }
    }

    public static bool TryExtractRequestId(JsonElement frame, out string requestId)
    {
        requestId = string.Empty;
        return frame.ValueKind == JsonValueKind.Object
            && TryString(frame, "requestId", out var candidate)
            && IsIdentifier(candidate)
            && Assign(candidate, out requestId);
    }

    private static PhotonCadProjectDesktopRequest? ParsePicker(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "purpose")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "purpose", out var purpose) || purpose is not ("new" or "open" or "save-as")) return null;
        return new PhotonCadProjectDesktopPickerRequest(requestId, purpose);
    }

    private static PhotonCadProjectDesktopRequest? ParseCreate(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "workspaceHandle", "title", "units")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "workspaceHandle", out var workspaceHandle)
            || !TryString(frame, "title", out var title) || title.Length > PhotonCadProjectContract.MaximumDisplayNameLength
            || !TryString(frame, "units", out var units)) return null;
        var unit = units switch
        {
            "millimeter" => PhotonCadProjectUnit.Millimeter,
            "inch" => PhotonCadProjectUnit.Inch,
            _ => (PhotonCadProjectUnit?)null,
        };
        return unit is null ? null : new PhotonCadProjectDesktopCreateRequest(new PhotonCadProjectCreateRequest(
            requestId, new PhotonCadWorkspaceHandle(workspaceHandle), title, unit.Value));
    }

    private static PhotonCadProjectDesktopRequest? ParseOpen(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "workspaceHandle")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "workspaceHandle", out var workspaceHandle)) return null;
        return new PhotonCadProjectDesktopOpenRequest(new PhotonCadProjectOpenRequest(
            requestId, new PhotonCadWorkspaceHandle(workspaceHandle)));
    }

    private static PhotonCadProjectDesktopRequest? ParseReopen(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "reopenHandle")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "reopenHandle", out var reopenHandle)) return null;
        return new PhotonCadProjectDesktopReopenRequest(new PhotonCadProjectReopenRequest(
            requestId, new PhotonCadReopenHandle(reopenHandle)));
    }

    private static PhotonCadProjectDesktopRequest? ParseRefresh(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "projectHandle", "sessionId", "projectId", "knownRevision")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "projectHandle", out var projectHandle)
            || !TryIdentifier(frame, "sessionId", out var sessionId)
            || !TryIdentifier(frame, "projectId", out var projectId)
            || !TryRevision(frame, "knownRevision", out var knownRevision)) return null;
        return new PhotonCadProjectDesktopRefreshRequest(new PhotonCadProjectRefreshRequest(
            requestId, new PhotonCadProjectHandle(projectHandle), sessionId, projectId, knownRevision));
    }

    private static PhotonCadProjectDesktopRequest? ParseSave(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "projectHandle", "sessionId", "projectId", "baseRevision", "contentDigest")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "projectHandle", out var projectHandle)
            || !TryIdentifier(frame, "sessionId", out var sessionId)
            || !TryIdentifier(frame, "projectId", out var projectId)
            || !TryRevision(frame, "baseRevision", out var baseRevision)
            || !TryString(frame, "contentDigest", out var contentDigest)) return null;
        return new PhotonCadProjectDesktopSaveRequest(new PhotonCadProjectSaveRequest(
            requestId, new PhotonCadProjectHandle(projectHandle), sessionId, projectId, baseRevision, contentDigest));
    }

    private static PhotonCadProjectDesktopRequest? ParseSaveAs(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "sourceProjectHandle", "destinationWorkspaceHandle", "sessionId", "projectId", "baseRevision", "contentDigest")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "sourceProjectHandle", out var sourceProjectHandle)
            || !TryString(frame, "destinationWorkspaceHandle", out var destinationWorkspaceHandle)
            || !TryIdentifier(frame, "sessionId", out var sessionId)
            || !TryIdentifier(frame, "projectId", out var projectId)
            || !TryRevision(frame, "baseRevision", out var baseRevision)
            || !TryString(frame, "contentDigest", out var contentDigest)) return null;
        return new PhotonCadProjectDesktopSaveAsRequest(new PhotonCadProjectSaveAsRequest(
            requestId,
            new PhotonCadProjectHandle(sourceProjectHandle),
            new PhotonCadWorkspaceHandle(destinationWorkspaceHandle),
            sessionId,
            projectId,
            baseRevision,
            contentDigest));
    }

    private static PhotonCadProjectDesktopRequest? ParseClose(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "contractVersion", "requestId", "projectHandle", "sessionId", "projectId", "revision", "lastSavedRevision", "contentDigest", "lastSavedContentDigest", "discardUnsavedChanges")
            || !Contract(frame) || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryString(frame, "projectHandle", out var projectHandle)
            || !TryIdentifier(frame, "sessionId", out var sessionId)
            || !TryIdentifier(frame, "projectId", out var projectId)
            || !TryRevision(frame, "revision", out var revision)
            || !TryRevision(frame, "lastSavedRevision", out var lastSavedRevision)
            || !TryString(frame, "contentDigest", out var contentDigest)
            || !TryString(frame, "lastSavedContentDigest", out var lastSavedContentDigest)
            || !TryBoolean(frame, "discardUnsavedChanges", out var discard)) return null;
        return new PhotonCadProjectDesktopCloseRequest(new PhotonCadProjectCloseRequest(
            requestId,
            new PhotonCadProjectHandle(projectHandle),
            sessionId,
            projectId,
            revision,
            lastSavedRevision,
            contentDigest,
            lastSavedContentDigest,
            discard));
    }

    private static PhotonCadProjectDesktopRequest? ParseCancel(JsonElement frame)
    {
        if (!Exact(frame, "type", "version", "requestId", "targetRequestId", "operation")
            || !TryIdentifier(frame, "requestId", out var requestId)
            || !TryIdentifier(frame, "targetRequestId", out var targetRequestId)
            || !TryString(frame, "operation", out var operation)) return null;
        var target = operation switch
        {
            "picker" => PhotonCadProjectDesktopOperation.Picker,
            "create" => PhotonCadProjectDesktopOperation.Create,
            "open" => PhotonCadProjectDesktopOperation.Open,
            "reopen" => PhotonCadProjectDesktopOperation.Reopen,
            "refresh" => PhotonCadProjectDesktopOperation.Refresh,
            "save" => PhotonCadProjectDesktopOperation.Save,
            "save-as" => PhotonCadProjectDesktopOperation.SaveAs,
            "close" => PhotonCadProjectDesktopOperation.Close,
            _ => (PhotonCadProjectDesktopOperation?)null,
        };
        return target is null ? null : new PhotonCadProjectDesktopCancelRequest(requestId, targetRequestId, target.Value);
    }

    private static bool Contract(JsonElement frame) => TryInteger(frame, "contractVersion", out var version)
        && version == PhotonCadProjectContract.Version;

    private static bool Exact(JsonElement frame, params string[] expected)
    {
        var allowed = new HashSet<string>(expected, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in frame.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !observed.Add(property.Name)) return false;
        }
        return observed.Count == allowed.Count;
    }

    private static bool TryIdentifier(JsonElement frame, string name, out string value) =>
        TryString(frame, name, out value) && IsIdentifier(value);

    private static bool IsIdentifier(string value) => value.Length is > 0 and <= PhotonCadProjectContract.MaximumIdentifierLength
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');

    private static bool TryRevision(JsonElement frame, string name, out long value) =>
        TryInteger(frame, name, out value) && value is >= 0 and <= PhotonCadProjectContract.MaximumSafeInteger;

    private static bool TryInteger(JsonElement frame, string name, out long value)
    {
        value = -1;
        return frame.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static bool TryString(JsonElement frame, string name, out string value)
    {
        value = string.Empty;
        return frame.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text
            && Assign(text, out value);
    }

    private static bool TryBoolean(JsonElement frame, string name, out bool value)
    {
        value = false;
        if (!frame.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool Assign(string source, out string destination)
    {
        destination = source;
        return true;
    }
}
