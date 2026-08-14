using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HermesDesktop;

internal sealed class WindowsAudioDeviceBridge : IMMNotificationClient, IDisposable
{
    public const int ProtocolVersion = 1;
    private const int MaximumCaptureMilliseconds = 10_000;
    private const int MaximumCaptureBytes = 4 * 1024 * 1024;
    private const int MaximumSpeechCaptureMilliseconds = 120_000;
    private const int MaximumSpeechCaptureBytes = 25 * 1024 * 1024;
    private const int MaximumPlaybackBytes = 16 * 1024 * 1024;
    private const string DefaultInputRef = "windows-default-input";
    private const string DefaultOutputRef = "windows-default-output";
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private readonly object _gate = new();
    private readonly Action<object> _post;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly string _preferencesPath;
    private AudioPreferences _preferences = new(DefaultInputRef, DefaultOutputRef);
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private MemoryStream? _captureStream;
    private Timer? _captureTimer;
    private Stopwatch? _captureClock;
    private string? _captureRequestId;
    private long _capturedPayloadBytes;
    private int _activeCaptureByteLimit = MaximumCaptureBytes;
    private bool _captureForTranscription;
    private bool _captureCancelled;
    private double _peak;
    private double _sumSquares;
    private long _sampleCount;
    private byte[]? _sample;
    private string? _sampleFormat;
    private WasapiOut? _playback;
    private WaveStream? _playbackReader;
    private string? _playbackRequestId;
    private bool _playbackIsOutput;
    private bool _disposed;

    public WindowsAudioDeviceBridge(Action<object> post)
    {
        _post = post;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HermesWorkbench");
        Directory.CreateDirectory(directory);
        _preferencesPath = Path.Combine(directory, "audio-devices.v1.json");
        _preferences = ReadPreferences();
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public void List(int version, string? requestId)
    {
        if (!Validate(version, requestId)) return;
        PostSnapshot(requestId);
    }

    public void Save(int version, string? requestId, string? kind, string? endpointRef)
    {
        if (!Validate(version, requestId)) return;
        var flow = string.Equals(kind, "input", StringComparison.Ordinal) ? DataFlow.Capture
            : string.Equals(kind, "output", StringComparison.Ordinal) ? DataFlow.Render
            : (DataFlow?)null;
        if (flow is null || string.IsNullOrWhiteSpace(endpointRef))
        {
            PostError(requestId, "invalid-request", "Choose a microphone or speaker endpoint first.");
            return;
        }
        try
        {
            using var resolved = Resolve(flow.Value, endpointRef);
            if (resolved is null)
            {
                PostError(requestId, "device-missing", "That Windows audio device is no longer available. Refresh and choose another device.");
                return;
            }
            lock (_gate)
            {
                _preferences = flow == DataFlow.Capture
                    ? _preferences with { InputRef = endpointRef }
                    : _preferences with { OutputRef = endpointRef };
                File.WriteAllText(_preferencesPath, JsonSerializer.Serialize(_preferences));
            }
            _post(new { type = "audio.devices.saved", version = ProtocolVersion, requestId, kind, endpointRef });
            PostSnapshot(null);
        }
        catch (Exception exception)
        {
            PostError(requestId, "save-failed", $"The Windows audio preference could not be saved: {Friendly(exception)}");
        }
    }

    public void StartMicrophoneTest(int version, string? requestId, string? endpointRef, int durationMilliseconds)
    {
        if (!Validate(version, requestId)) return;
        if (durationMilliseconds is < 1_000 or > MaximumCaptureMilliseconds)
        {
            PostError(requestId, "invalid-duration", "Microphone tests must be between 1 and 10 seconds.");
            return;
        }
        StartCapture(requestId, endpointRef, durationMilliseconds, false);
    }

    public void StartSpeechCapture(int version, string? requestId)
    {
        if (!Validate(version, requestId)) return;
        StartCapture(requestId, _preferences.InputRef, MaximumSpeechCaptureMilliseconds, true);
    }

    private void StartCapture(string? requestId, string? endpointRef, int durationMilliseconds, bool forTranscription)
    {
        try
        {
            lock (_gate)
            {
                EnsureIdleCapture();
                DeleteSampleLocked();
                using var resolved = Resolve(DataFlow.Capture, endpointRef ?? _preferences.InputRef);
                if (resolved is null) throw new AudioBridgeException("device-missing", "The selected microphone is unavailable.");
                _captureStream = new MemoryStream();
                _capture = new WasapiCapture(resolved);
                _writer = new WaveFileWriter(_captureStream, _capture.WaveFormat);
                _sampleFormat = _capture.WaveFormat.ToString();
                _captureRequestId = requestId;
                _capturedPayloadBytes = 0;
                _activeCaptureByteLimit = forTranscription ? MaximumSpeechCaptureBytes : MaximumCaptureBytes;
                _captureForTranscription = forTranscription;
                _captureCancelled = false;
                _peak = 0;
                _sumSquares = 0;
                _sampleCount = 0;
                _captureClock = Stopwatch.StartNew();
                _capture.DataAvailable += CaptureDataAvailable;
                _capture.RecordingStopped += CaptureRecordingStopped;
                _captureTimer = new Timer(_ => StopCapture(ProtocolVersion, requestId, forTranscription, true), null, durationMilliseconds, Timeout.Infinite);
                _capture.StartRecording();
            }
            DesktopLog.Write($"Windows audio capture started: kind={(forTranscription ? "speech" : "test")}, requestId={requestId}, endpointRef={endpointRef}, format={_sampleFormat}.");
            _post(new { type = forTranscription ? "audio.speech.started" : "audio.micTest.started", version = ProtocolVersion, requestId, durationMilliseconds });
        }
        catch (UnauthorizedAccessException)
        {
            CleanupCapture();
            PostError(requestId, "permission-denied", "Windows denied microphone access. Enable microphone access for desktop apps and try again.");
        }
        catch (AudioBridgeException exception)
        {
            CleanupCapture();
            PostError(requestId, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            CleanupCapture();
            PostError(requestId, "device-busy", $"The microphone could not start. It may be busy or disconnected: {Friendly(exception)}");
        }
    }

    public void StopSpeechCapture(int version, string? requestId) => StopCapture(version, requestId, true, false);
    public void CancelSpeechCapture(int version, string? requestId) => CancelCapture(version, requestId, true);

    public void StopMicrophoneTest(int version, string? requestId, bool automatic = false)
        => StopCapture(version, requestId, false, automatic);

    private void StopCapture(int version, string? requestId, bool forTranscription, bool automatic)
    {
        if (!Validate(version, requestId)) return;
        WasapiCapture? capture;
        string? mismatch = null;
        lock (_gate)
        {
            capture = _capture;
            if (capture is not null && (_captureForTranscription != forTranscription
                || (forTranscription && !string.Equals(_captureRequestId, requestId, StringComparison.Ordinal))))
            {
                mismatch = forTranscription
                    ? "That speech capture is no longer active."
                    : "A speech capture is active; the microphone test cannot stop it.";
                capture = null;
            }
        }
        if (mismatch is not null)
        {
            if (!automatic) PostError(requestId, "capture-mismatch", mismatch);
            return;
        }
        if (capture is null)
        {
            if (!automatic) PostError(requestId, "not-recording", "No microphone test is recording.");
            return;
        }
        try { capture.StopRecording(); }
        catch (Exception exception) { CleanupCapture(); PostError(requestId, "capture-stop-failed", Friendly(exception)); }
    }

    public void CancelMicrophoneTest(int version, string? requestId)
        => CancelCapture(version, requestId, false);

    private void CancelCapture(int version, string? requestId, bool forTranscription)
    {
        if (!Validate(version, requestId)) return;
        WasapiCapture? capture;
        string? mismatch = null;
        lock (_gate)
        {
            capture = _capture;
            if (capture is not null && (_captureForTranscription != forTranscription
                || (forTranscription && !string.Equals(_captureRequestId, requestId, StringComparison.Ordinal))))
            {
                mismatch = forTranscription
                    ? "That speech capture is no longer active."
                    : "A speech capture is active; the microphone test cannot cancel it.";
                capture = null;
            }
            else if (capture is not null)
            {
                _captureCancelled = true;
                DeleteSampleLocked();
            }
        }
        if (mismatch is not null) { PostError(requestId, "capture-mismatch", mismatch); return; }
        try { capture?.StopRecording(); } catch { CleanupCapture(); }
        _post(new { type = forTranscription ? "audio.speech.cancelled" : "audio.micTest.cancelled", version = ProtocolVersion, requestId });
    }

    public void PlaySample(int version, string? requestId, string? endpointRef)
    {
        if (!Validate(version, requestId)) return;
        try
        {
            lock (_gate)
            {
                StopPlaybackLocked();
                if (_sample is null || _sample.Length == 0) throw new AudioBridgeException("sample-missing", "Record a microphone sample first.");
                using var resolved = Resolve(DataFlow.Render, endpointRef ?? _preferences.OutputRef);
                if (resolved is null) throw new AudioBridgeException("output-missing", "The selected speaker output is unavailable.");
                _playbackReader = new WaveFileReader(new MemoryStream(_sample, writable: false));
                StartPlaybackLocked(resolved, requestId, false);
            }
            _post(new { type = "audio.micTest.playing", version = ProtocolVersion, requestId });
        }
        catch (AudioBridgeException exception) { PostError(requestId, exception.Code, exception.Message); }
        catch (Exception exception) { StopPlayback(); PostError(requestId, "output-route-failed", $"The sample could not play through the selected output: {Friendly(exception)}"); }
    }

    public void PlayOutput(int version, string? requestId, string? dataUrl)
    {
        if (!Validate(version, requestId)) return;
        try
        {
            var (mediaType, bytes) = DecodeAudio(dataUrl);
            lock (_gate)
            {
                StopPlaybackLocked();
                using var resolved = Resolve(DataFlow.Render, _preferences.OutputRef);
                if (resolved is null) throw new AudioBridgeException("output-missing", "The saved Windows speaker output is unavailable. Refresh audio devices and choose another output.");
                var stream = new MemoryStream(bytes, writable: false);
                try
                {
                    _playbackReader = mediaType == "audio/mpeg" || mediaType == "audio/mp3"
                        ? new Mp3FileReader(stream)
                        : new WaveFileReader(stream);
                }
                catch { stream.Dispose(); throw; }
                StartPlaybackLocked(resolved, requestId, true);
            }
            _post(new { type = "audio.output.started", version = ProtocolVersion, requestId });
        }
        catch (AudioBridgeException exception) { PostError(requestId, exception.Code, exception.Message); }
        catch (Exception exception) { StopPlayback(); PostError(requestId, "output-route-failed", $"Kokoro audio could not play through the selected Windows output: {Friendly(exception)}"); }
    }

    public void StopOutput(int version, string? requestId)
    {
        if (!Validate(version, requestId)) return;
        string? activeRequest;
        lock (_gate)
        {
            activeRequest = _playbackRequestId;
            if (!_playbackIsOutput || (requestId is not null && activeRequest is not null && requestId != activeRequest)) return;
            StopPlaybackLocked();
        }
        _post(new { type = "audio.output.stopped", version = ProtocolVersion, requestId = activeRequest ?? requestId });
    }

    private void StartPlaybackLocked(MMDevice resolved, string? requestId, bool output)
    {
        _playbackRequestId = requestId;
        _playbackIsOutput = output;
        _playback = new WasapiOut(resolved, AudioClientShareMode.Shared, true, 100);
        _playback.PlaybackStopped += PlaybackStopped;
        _playback.Init(_playbackReader);
        _playback.Play();
    }

    private static (string MediaType, byte[] Bytes) DecodeAudio(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) throw new AudioBridgeException("audio-missing", "Kokoro returned no audio to play.");
        var comma = dataUrl.IndexOf(',');
        if (comma <= 5 || !dataUrl.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new AudioBridgeException("audio-format-invalid", "Kokoro returned an unsupported audio payload.");
        var mediaType = dataUrl[5..dataUrl.IndexOf(';')].ToLowerInvariant();
        if (mediaType is not ("audio/wav" or "audio/wave" or "audio/x-wav" or "audio/mpeg" or "audio/mp3"))
            throw new AudioBridgeException("audio-format-invalid", "The selected Windows output supports Kokoro WAV or MP3 playback only.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]); }
        catch (FormatException) { throw new AudioBridgeException("audio-format-invalid", "Kokoro returned malformed audio bytes."); }
        if (bytes.Length == 0 || bytes.Length > MaximumPlaybackBytes)
            throw new AudioBridgeException("audio-size-invalid", "Kokoro audio is empty or exceeds the 16 MiB playback limit.");
        return (mediaType, bytes);
    }

    public void StopPlayback(int version = ProtocolVersion, string? requestId = null)
    {
        if (!Validate(version, requestId)) return;
        StopPlayback();
        _post(new { type = "audio.micTest.playbackStopped", version = ProtocolVersion, requestId });
    }

    public void DeleteSample(int version, string? requestId)
    {
        if (!Validate(version, requestId)) return;
        lock (_gate) DeleteSampleLocked();
        _post(new { type = "audio.micTest.deleted", version = ProtocolVersion, requestId });
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            if (_capturedPayloadBytes + args.BytesRecorded > _activeCaptureByteLimit)
            {
                ThreadPool.QueueUserWorkItem(_ => StopMicrophoneTest(ProtocolVersion, _captureRequestId, true));
                return;
            }
            _writer.Write(args.Buffer, 0, args.BytesRecorded);
            _capturedPayloadBytes += args.BytesRecorded;
            Analyze(args.Buffer.AsSpan(0, args.BytesRecorded), _capture?.WaveFormat);
        }
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs args)
    {
        string? requestId;
        byte[]? sample = null;
        long duration;
        double peak;
        double rms;
        string? format;
        bool forTranscription;
        bool cancelled;
        lock (_gate)
        {
            requestId = _captureRequestId;
            _captureRequestId = null;
            _captureTimer?.Dispose();
            _captureTimer = null;
            duration = _captureClock?.ElapsedMilliseconds ?? 0;
            _captureClock = null;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                sample = _captureStream?.ToArray();
            }
            catch { sample = null; }
            _captureStream?.Dispose();
            _captureStream = null;
            if (_capture is not null)
            {
                _capture.DataAvailable -= CaptureDataAvailable;
                _capture.RecordingStopped -= CaptureRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }
            peak = _peak;
            rms = _sampleCount == 0 ? 0 : Math.Sqrt(_sumSquares / _sampleCount);
            format = _sampleFormat;
            forTranscription = _captureForTranscription;
            _captureForTranscription = false;
            cancelled = _captureCancelled;
            _captureCancelled = false;
            if (!forTranscription && args.Exception is null && sample is { Length: > 44 } && _capturedPayloadBytes > 0) _sample = sample;
        }
        DesktopLog.Write($"Windows audio capture stopped: kind={(forTranscription ? "speech" : "test")}, requestId={requestId}, durationMs={duration}, payloadBytes={_capturedPayloadBytes}, peak={peak:F4}, rms={rms:F4}, cancelled={cancelled}.");
        if (cancelled) return;
        if (args.Exception is not null)
        {
            PostError(requestId, "capture-failed", $"Microphone capture stopped unexpectedly: {Friendly(args.Exception)}");
            return;
        }
        if (sample is not { Length: > 44 } || _capturedPayloadBytes <= 0)
        {
            PostError(requestId, "zero-frames", "The microphone returned no audio frames. Check the selected device and Windows input level.");
            return;
        }
        var warning = peak >= .995 ? "The sample clipped. Lower the microphone input level."
            : rms > 0 && rms < .006 ? "The sample is very quiet. Raise the microphone input level or move closer."
            : null;
        if (forTranscription)
        {
            var dataUrl = "data:audio/wav;base64," + Convert.ToBase64String(sample);
            _post(new
            {
                type = "audio.speech.captured",
                version = ProtocolVersion,
                requestId,
                durationMilliseconds = duration,
                byteCount = sample.Length,
                mimeType = "audio/wav",
                dataUrl
            });
            CryptographicOperations.ZeroMemory(sample);
            return;
        }
        _post(new { type = "audio.micTest.ready", version = ProtocolVersion, requestId, durationMilliseconds = duration, byteCount = sample.Length, peak, rms, warning, format });
    }

    private void PlaybackStopped(object? sender, StoppedEventArgs args)
    {
        string? requestId;
        bool output;
        lock (_gate) { requestId = _playbackRequestId; output = _playbackIsOutput; StopPlaybackLocked(); }
        if (args.Exception is null) _post(new { type = output ? "audio.output.completed" : "audio.micTest.playbackComplete", version = ProtocolVersion, requestId });
        else PostError(requestId, "output-route-failed", $"Playback stopped unexpectedly: {Friendly(args.Exception)}");
    }

    private void PostSnapshot(string? requestId)
    {
        try
        {
            var inputs = Enumerate(DataFlow.Capture, DefaultInputRef, "Windows default microphone");
            var outputs = Enumerate(DataFlow.Render, DefaultOutputRef, "Windows default output");
            AudioPreferences preferences;
            lock (_gate) preferences = _preferences;
            var inputMissing = !inputs.Any(item => item.Ref == preferences.InputRef);
            var outputMissing = !outputs.Any(item => item.Ref == preferences.OutputRef);
            DesktopLog.Write($"Windows audio snapshot enumerated: inputs={inputs.Count}, outputs={outputs.Count}, inputMissing={inputMissing}, outputMissing={outputMissing}.");
            _post(new
            {
                type = "audio.devices.snapshot",
                version = ProtocolVersion,
                requestId,
                inputs = inputs.Select(item => new { @ref = item.Ref, name = item.Name, isDefault = item.IsDefault }).ToArray(),
                outputs = outputs.Select(item => new { @ref = item.Ref, name = item.Name, isDefault = item.IsDefault }).ToArray(),
                selectedInputRef = inputMissing ? DefaultInputRef : preferences.InputRef,
                selectedOutputRef = outputMissing ? DefaultOutputRef : preferences.OutputRef,
                missingInputRef = inputMissing ? preferences.InputRef : null,
                missingOutputRef = outputMissing ? preferences.OutputRef : null
            });
            DesktopLog.Write("Windows audio snapshot posted to renderer.");
        }
        catch (Exception exception)
        {
            DesktopLog.Write($"Windows audio endpoint enumeration failed: {exception.GetType().Name}: {Friendly(exception)}");
            PostError(requestId, "enumeration-unavailable", $"Windows audio endpoints could not be enumerated: {Friendly(exception)}");
        }
    }

    private IReadOnlyList<AudioEndpoint> Enumerate(DataFlow flow, string defaultRef, string defaultName)
    {
        var result = new List<AudioEndpoint> { new(defaultRef, defaultName, true) };
        using var defaults = TryDefault(flow);
        var defaultId = defaults?.ID;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device) result.Add(new AudioEndpoint(ToRef(flow, device.ID), device.FriendlyName, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private MMDevice? Resolve(DataFlow flow, string endpointRef)
    {
        var defaultRef = flow == DataFlow.Capture ? DefaultInputRef : DefaultOutputRef;
        if (endpointRef == defaultRef) return TryDefault(flow);
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (ToRef(flow, device.ID) == endpointRef) return device;
            device.Dispose();
        }
        return null;
    }

    private MMDevice? TryDefault(DataFlow flow)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    private static string ToRef(DataFlow flow, string deviceId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        return $"win-{(flow == DataFlow.Capture ? "in" : "out")}-{digest[..24]}";
    }

    private void Analyze(ReadOnlySpan<byte> bytes, WaveFormat? format)
    {
        if (format is null) return;
        var extensibleSubFormat = (format as WaveFormatExtensible)?.SubFormat;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || extensibleSubFormat == FloatSubFormat;
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm || extensibleSubFormat == PcmSubFormat;
        if (isFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= bytes.Length; offset += 4) AddLevel(BitConverter.ToSingle(bytes.Slice(offset, 4)));
        }
        else if (isPcm && format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= bytes.Length; offset += 2) AddLevel(BitConverter.ToInt16(bytes.Slice(offset, 2)) / 32768d);
        }
    }

    private void AddLevel(double sample)
    {
        if (!double.IsFinite(sample)) return;
        var absolute = Math.Abs(sample);
        _peak = Math.Max(_peak, absolute);
        _sumSquares += sample * sample;
        _sampleCount++;
    }

    private bool Validate(int version, string? requestId)
    {
        if (version == ProtocolVersion && !_disposed) return true;
        PostError(requestId, "protocol-mismatch", "The Windows audio bridge protocol is unavailable. Restart Photon and try again.");
        return false;
    }

    private AudioPreferences ReadPreferences()
    {
        try { return JsonSerializer.Deserialize<AudioPreferences>(File.ReadAllText(_preferencesPath)) ?? new(DefaultInputRef, DefaultOutputRef); }
        catch { return new(DefaultInputRef, DefaultOutputRef); }
    }

    private void EnsureIdleCapture()
    {
        if (_capture is not null) throw new AudioBridgeException("already-recording", "A microphone test is already recording.");
    }

    private void CleanupCapture()
    {
        lock (_gate)
        {
            _captureTimer?.Dispose(); _captureTimer = null;
            _writer?.Dispose(); _writer = null;
            _captureStream?.Dispose(); _captureStream = null;
            _capture?.Dispose(); _capture = null;
            _captureRequestId = null;
            _captureForTranscription = false;
            _captureCancelled = false;
        }
    }

    private void StopPlayback()
    {
        lock (_gate) StopPlaybackLocked();
    }

    private void StopPlaybackLocked()
    {
        if (_playback is not null) _playback.PlaybackStopped -= PlaybackStopped;
        try { _playback?.Stop(); } catch { }
        _playback?.Dispose(); _playback = null;
        _playbackReader?.Dispose(); _playbackReader = null;
        _playbackRequestId = null;
        _playbackIsOutput = false;
    }

    private void DeleteSampleLocked() { StopPlaybackLocked(); _sample = null; _sampleFormat = null; }
    private void PostError(string? requestId, string code, string message)
    {
        DesktopLog.Write($"Windows audio error: requestId={requestId}, code={code}.");
        _post(new { type = "audio.error", version = ProtocolVersion, requestId, code, message });
    }
    private static string Friendly(Exception exception) => exception.Message.Length <= 240 ? exception.Message : exception.Message[..240];

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged("state");
    public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged("added");
    public void OnDeviceRemoved(string deviceId) => DevicesChanged("removed");
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => DevicesChanged("default");
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => DevicesChanged("property");
    private void DevicesChanged(string reason) { _post(new { type = "audio.devices.changed", version = ProtocolVersion, reason }); PostSnapshot(null); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        CleanupCapture();
        lock (_gate) DeleteSampleLocked();
        _enumerator.Dispose();
    }

    private sealed record AudioEndpoint(string Ref, string Name, bool IsDefault);
    private sealed record AudioPreferences(string InputRef, string OutputRef);
    private sealed class AudioBridgeException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
