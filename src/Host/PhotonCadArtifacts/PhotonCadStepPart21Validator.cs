using System.Buffers;
using System.Text;

namespace PhotonCadArtifacts;

internal static class PhotonCadStepPart21Validator
{
    private const int BufferSize = 1_048_576;
    private const int MaximumLineBytes = 1_048_576;
    private const int MaximumTokenBytes = 4_096;
    private const int MaximumQuotedOrCommentBytes = 1_048_576;
    private static readonly byte[] InitialMarker = Encoding.ASCII.GetBytes("ISO-10303-21;");

    internal static async ValueTask ValidateAsync(
        Stream content,
        long expectedByteLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || expectedByteLength < InitialMarker.Length)
            throw new PhotonCadArtifactException("invalid_step_envelope");
        if (content.CanSeek && content.Position != 0)
            throw new PhotonCadArtifactException("step_stream_not_rewound");

        var parser = new Parser();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > expectedByteLength)
                    throw new PhotonCadArtifactException("step_length_mismatch");
                parser.Accept(buffer.AsSpan(0, read));
            }
            if (total != expectedByteLength)
                throw new PhotonCadArtifactException("step_length_mismatch");
            parser.Complete();
            if (content.CanSeek) content.Position = 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class Parser
    {
        private readonly StringBuilder _token = new();
        private EnvelopeStage _stage = EnvelopeStage.Initial;
        private LexicalMode _mode = LexicalMode.Normal;
        private string? _firstToken;
        private bool _prefixJunk;
        private int _initialIndex;
        private int _lineBytes;
        private int _quotedOrCommentBytes;

        internal void Accept(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                ValidateByte(value);
                if (_stage == EnvelopeStage.Complete)
                {
                    if (!IsTrailingWhitespace(value))
                        throw new PhotonCadArtifactException("step_appended_content");
                    continue;
                }
                if (_stage == EnvelopeStage.Initial)
                {
                    if (_initialIndex >= InitialMarker.Length || value != InitialMarker[_initialIndex++])
                        throw new PhotonCadArtifactException("invalid_step_initial_marker");
                    if (_initialIndex == InitialMarker.Length)
                        _stage = EnvelopeStage.ExpectHeader;
                    continue;
                }
                AcceptLexical(value);
            }
        }

        internal void Complete()
        {
            if (_stage != EnvelopeStage.Complete || _mode != LexicalMode.Normal || _token.Length != 0)
                throw new PhotonCadArtifactException("invalid_step_envelope");
        }

        private void ValidateByte(byte value)
        {
            if (value == (byte)'\n')
            {
                _lineBytes = 0;
                return;
            }
            _lineBytes = checked(_lineBytes + 1);
            if (_lineBytes > MaximumLineBytes)
                throw new PhotonCadArtifactException("step_line_too_long");
            if (value is not ((byte)'\t' or (byte)'\r') && (value < 0x20 || value > 0x7e))
                throw new PhotonCadArtifactException("step_non_ascii_content");
        }

        private void AcceptLexical(byte value)
        {
            var reprocess = true;
            while (reprocess)
            {
                reprocess = false;
                switch (_mode)
                {
                    case LexicalMode.String:
                        if (++_quotedOrCommentBytes > MaximumQuotedOrCommentBytes)
                            throw new PhotonCadArtifactException("step_string_too_long");
                        if (value == (byte)'\'') _mode = LexicalMode.StringQuote;
                        return;
                    case LexicalMode.StringQuote:
                        if (value == (byte)'\'')
                        {
                            _mode = LexicalMode.String;
                            return;
                        }
                        _mode = LexicalMode.Normal;
                        _quotedOrCommentBytes = 0;
                        reprocess = true;
                        continue;
                    case LexicalMode.Comment:
                        if (++_quotedOrCommentBytes > MaximumQuotedOrCommentBytes)
                            throw new PhotonCadArtifactException("step_comment_too_long");
                        if (value == (byte)'*') _mode = LexicalMode.CommentStar;
                        return;
                    case LexicalMode.CommentStar:
                        if (++_quotedOrCommentBytes > MaximumQuotedOrCommentBytes)
                            throw new PhotonCadArtifactException("step_comment_too_long");
                        if (value == (byte)'/')
                        {
                            _mode = LexicalMode.Normal;
                            _quotedOrCommentBytes = 0;
                        }
                        else if (value != (byte)'*')
                        {
                            _mode = LexicalMode.Comment;
                        }
                        return;
                    case LexicalMode.PendingSlash:
                        if (value == (byte)'*')
                        {
                            _mode = LexicalMode.Comment;
                            _quotedOrCommentBytes = 0;
                            return;
                        }
                        MarkPunctuation();
                        _mode = LexicalMode.Normal;
                        reprocess = true;
                        continue;
                    case LexicalMode.Normal:
                        break;
                    default:
                        throw new PhotonCadArtifactException("invalid_step_lexer_state");
                }

                if (IsToken(value))
                {
                    if (_token.Length >= MaximumTokenBytes)
                        throw new PhotonCadArtifactException("step_token_too_long");
                    _token.Append(char.ToUpperInvariant((char)value));
                    return;
                }

                FinishToken();
                if (value == (byte)';')
                {
                    FinishStatement();
                    return;
                }
                if (IsWhitespace(value)) return;
                if (value == (byte)'\'')
                {
                    MarkPunctuation();
                    _mode = LexicalMode.String;
                    _quotedOrCommentBytes = 0;
                    return;
                }
                if (value == (byte)'/')
                {
                    _mode = LexicalMode.PendingSlash;
                    return;
                }
                MarkPunctuation();
            }
        }

        private void FinishToken()
        {
            if (_token.Length == 0) return;
            if (_firstToken is null) _firstToken = _token.ToString();
            _token.Clear();
        }

        private void MarkPunctuation()
        {
            if (_firstToken is null && _token.Length == 0) _prefixJunk = true;
        }

        private void FinishStatement()
        {
            FinishToken();
            var token = _firstToken;
            var clean = !_prefixJunk;
            switch (_stage)
            {
                case EnvelopeStage.ExpectHeader:
                    if (!clean || token != "HEADER") throw new PhotonCadArtifactException("step_header_required");
                    _stage = EnvelopeStage.InHeader;
                    break;
                case EnvelopeStage.InHeader:
                    if (clean && token == "ENDSEC") _stage = EnvelopeStage.ExpectData;
                    else if (clean && token is "DATA" or "END-ISO-10303-21")
                        throw new PhotonCadArtifactException("step_section_order_invalid");
                    break;
                case EnvelopeStage.ExpectData:
                    if (!clean || token != "DATA") throw new PhotonCadArtifactException("step_data_required");
                    _stage = EnvelopeStage.InData;
                    break;
                case EnvelopeStage.InData:
                    if (clean && token == "ENDSEC") _stage = EnvelopeStage.ExpectTerminal;
                    else if (clean && token == "END-ISO-10303-21")
                        throw new PhotonCadArtifactException("step_section_order_invalid");
                    break;
                case EnvelopeStage.ExpectTerminal:
                    if (!clean || token != "END-ISO-10303-21")
                        throw new PhotonCadArtifactException("step_terminal_marker_required");
                    _stage = EnvelopeStage.Complete;
                    break;
                default:
                    throw new PhotonCadArtifactException("invalid_step_envelope");
            }
            _firstToken = null;
            _prefixJunk = false;
        }

        private static bool IsToken(byte value) =>
            value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-';

        private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        private static bool IsTrailingWhitespace(byte value) => IsWhitespace(value);
    }

    private enum EnvelopeStage
    {
        Initial,
        ExpectHeader,
        InHeader,
        ExpectData,
        InData,
        ExpectTerminal,
        Complete,
    }

    private enum LexicalMode
    {
        Normal,
        PendingSlash,
        String,
        StringQuote,
        Comment,
        CommentStar,
    }
}
