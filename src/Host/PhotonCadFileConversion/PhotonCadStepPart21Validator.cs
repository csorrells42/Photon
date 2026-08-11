namespace PhotonCadFileConversion;

/// <summary>Complete-byte, lexical Part-21 envelope validator for the byte-preserving STEP intake path.</summary>
internal static class PhotonCadStepPart21Validator
{
    private static ReadOnlySpan<byte> Initial => "ISO-10303-21;"u8;
    private static ReadOnlySpan<byte> Header => "HEADER;"u8;
    private static ReadOnlySpan<byte> Data => "DATA;"u8;
    private static ReadOnlySpan<byte> EndSection => "ENDSEC;"u8;
    private static ReadOnlySpan<byte> End => "END-ISO-10303-21;"u8;

    internal static void Validate(ReadOnlySpan<byte> content)
    {
        if (content.Length < 48 || !content.StartsWith(Initial)) throw Failure("step_initial_marker_invalid");
        var scanner = new Scanner(content);
        scanner.ValidateCharacters();
        var cursor = Initial.Length;
        cursor = scanner.FindMarker(cursor, Header);
        cursor = scanner.FindMarker(cursor, EndSection);
        cursor = scanner.FindMarker(cursor, Data);
        cursor = scanner.FindMarker(cursor, EndSection);
        cursor = scanner.FindMarker(cursor, End);
        for (var index = cursor; index < content.Length; index++)
        {
            if (content[index] is not (0x09 or 0x0a or 0x0d or 0x20)) throw Failure("step_appended_content");
        }
    }

    private sealed class Scanner
    {
        private readonly byte[] _content;

        internal Scanner(ReadOnlySpan<byte> content) => _content = content.ToArray();

        internal void ValidateCharacters()
        {
            var quoted = false;
            var comment = false;
            for (var index = 0; index < _content.Length; index++)
            {
                var value = _content[index];
                if ((value < 0x20 && value is not (0x09 or 0x0a or 0x0d)) || value > 0x7e) throw Failure("step_character_invalid");
                if (comment)
                {
                    if (value == (byte)'*' && index + 1 < _content.Length && _content[index + 1] == (byte)'/') { comment = false; index++; }
                    continue;
                }
                if (quoted)
                {
                    if (value == (byte)'\'' && index + 1 < _content.Length && _content[index + 1] == (byte)'\'') index++;
                    else if (value == (byte)'\'') quoted = false;
                    continue;
                }
                if (value == (byte)'/' && index + 1 < _content.Length && _content[index + 1] == (byte)'*') { comment = true; index++; }
                else if (value == (byte)'\'') quoted = true;
            }
            if (quoted || comment) throw Failure("step_unterminated_lexical_item");
        }

        internal int FindMarker(int start, ReadOnlySpan<byte> marker)
        {
            var quoted = false;
            var comment = false;
            for (var index = start; index <= _content.Length - marker.Length; index++)
            {
                var value = _content[index];
                if (comment)
                {
                    if (value == (byte)'*' && index + 1 < _content.Length && _content[index + 1] == (byte)'/') { comment = false; index++; }
                    continue;
                }
                if (quoted)
                {
                    if (value == (byte)'\'' && index + 1 < _content.Length && _content[index + 1] == (byte)'\'') index++;
                    else if (value == (byte)'\'') quoted = false;
                    continue;
                }
                if (value == (byte)'/' && index + 1 < _content.Length && _content[index + 1] == (byte)'*') { comment = true; index++; continue; }
                if (value == (byte)'\'') { quoted = true; continue; }
                if (_content.AsSpan(index).StartsWith(marker)) return index + marker.Length;
            }
            throw Failure("step_marker_missing");
        }
    }

    private static InvalidDataException Failure(string code) => new(code);
}
