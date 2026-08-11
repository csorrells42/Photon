using System.Text.Json;

namespace AssistantConversationBus;

public interface IAssistantBusLedger
{
    Task<AssistantBusMessage> AppendAsync(
        AssistantIdentity sender,
        AssistantIdentity recipient,
        string body,
        string? parentMessageId,
        bool expectsReply,
        string? originMessageId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantBusMessage>> ReadAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);

    Task<AssistantBusMessage?> FindAsync(
        string messageId,
        CancellationToken cancellationToken);
}

public sealed class AssistantBusLedger : IAssistantBusLedger, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AssistantBusMessage> _messages = [];
    private long _sequence;
    private bool _disposed;

    private AssistantBusLedger(string path) => _path = path;

    public static async Task<AssistantBusLedger> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var ledger = new AssistantBusLedger(fullPath);
        if (!File.Exists(fullPath)) return ledger;

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (lines.Length > AssistantBusLimits.MaximumRoomMessages)
            throw new InvalidDataException("The assistant bus room ledger exceeds its bounded message limit.");
        long previous = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var message = JsonSerializer.Deserialize<AssistantBusMessage>(line, JsonOptions)
                ?? throw new InvalidDataException("The assistant bus room ledger contains an empty message.");
            if (message.ProtocolVersion != AssistantBusLimits.ProtocolVersion || message.Sequence != previous + 1)
                throw new InvalidDataException("The assistant bus room ledger sequence is invalid.");
            AssistantIdentityPolicy.RequireSender(message.Sender);
            AssistantIdentityPolicy.RequireRecipient(message.Sender, message.Recipient);
            ledger._messages.Add(message);
            previous = message.Sequence;
        }
        ledger._sequence = previous;
        return ledger;
    }

    public async Task<AssistantBusMessage> AppendAsync(
        AssistantIdentity sender,
        AssistantIdentity recipient,
        string body,
        string? parentMessageId,
        bool expectsReply,
        string? originMessageId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssistantIdentityPolicy.RequireSender(sender);
        AssistantIdentityPolicy.RequireRecipient(sender, recipient);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_messages.Count >= AssistantBusLimits.MaximumRoomMessages)
                throw new InvalidOperationException("The assistant bus room reached its bounded message capacity.");
            var sequence = checked(_sequence + 1);
            var id = $"bus:{Guid.NewGuid():N}";
            var message = new AssistantBusMessage(
                AssistantBusLimits.ProtocolVersion,
                "main",
                sequence,
                id,
                sender,
                recipient,
                body,
                DateTimeOffset.UtcNow,
                parentMessageId,
                expectsReply,
                originMessageId ?? id);
            var json = JsonSerializer.Serialize(message, JsonOptions) + Environment.NewLine;
            await using (var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 16 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            _messages.Add(message);
            _sequence = sequence;
            return message;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AssistantBusMessage>> ReadAsync(
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (limit is < 1 or > AssistantBusLimits.MaximumReadMessages) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _messages.Where(message => message.Sequence > afterSequence).Take(limit).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AssistantBusMessage?> FindAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _messages.FirstOrDefault(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
