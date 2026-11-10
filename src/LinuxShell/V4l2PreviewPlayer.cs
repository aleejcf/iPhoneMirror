// SPDX-License-Identifier: GPL-3.0-only
//
// Reads the Android V4L2 loopback output for a clean in-app preview. scrcpy
// remains the device capture/audio/recording owner; this process only consumes
// the virtual camera that is already selected as the OBS source.

using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using IPhoneMirror.LinuxShell.Controls;
using IPhoneMirror.LinuxShell.Interop;
using IPhoneMirror.LinuxShell.Services;

namespace IPhoneMirror.LinuxShell;

internal sealed class V4l2PreviewPlayer(string device, uint width, uint height)
{
    private readonly string? _androidAudioSink =
        Environment.GetEnvironmentVariable("IPHONE_MIRROR_ANDROID_AUDIO_SINK");
    private readonly object _frameGate = new();
    private readonly object _processGate = new();
    private readonly object _outputFrameGate = new();
    private readonly SemaphoreSlim _mediaOutputGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private DispatcherTimer? _timer;
    private Process? _ffmpeg;
    private byte[]? _latestFrame;
    private Task? _reader;
    private Task? _mediaCommandTask;
    private MediaOutputCoordinator? _mediaOutputs;
    private byte[]? _outputFrame;
    private Nv12VideoFrame? _cachedOutputFrame;
    private long _outputFrameVersion;
    private long _cachedOutputFrameVersion = -1;
    private int _tickActive;
    private int _hasProducedFrame;
    private int _reconnectDelaySeconds = 1;
    private bool _stopped;
    private string? _lastFrameDiagnostic;

    internal void Start(PreviewSurfaceControl preview, TextBlock status,
        Action<int> report)
    {
        var frameSize = checked((int)((long)width * height * 3 / 2));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += async (_, _) =>
        {
            if (Interlocked.Exchange(ref _tickActive, 1) != 0) return;
            byte[]? frame = null;
            try
            {
                lock (_frameGate)
                {
                    frame = _latestFrame;
                    _latestFrame = null;
                }
                if (frame is null) return;
                if (!await preview.PresentAsync(frame, width, height))
                    ReportFrameDiagnostic(preview.Diagnostic);
                else
                    _lastFrameDiagnostic = null;
            }
            catch (Exception error)
            {
                ReportFrameDiagnostic(error.Message);
            }
            finally
            {
                if (frame is not null) ArrayPool<byte>.Shared.Return(frame);
                Volatile.Write(ref _tickActive, 0);
            }
        };
        _timer.Start();
        _mediaCommandTask = Task.Run(() => ReadMediaCommandsAsync(_stop.Token));
        _reader = Task.Run(async () =>
        {
            try
            {
                await ReadFramesAsync(frameSize, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!_stop.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"Android V4L2 preview failed: {error.Message}");
                    Dispatcher.UIThread.Post(() => report(1));
                }
            }
        });
    }

    private void PublishMediaFrame(byte[] frame, int frameSize)
    {
        if (_mediaOutputs?.IsRunning != true) return;
        lock (_outputFrameGate)
        {
            _outputFrame ??= new byte[frameSize];
            frame.AsSpan(0, frameSize).CopyTo(_outputFrame);
            ++_outputFrameVersion;
            _cachedOutputFrame = null;
        }
    }

    private Nv12VideoFrame? CopyMediaFrame(ulong session, uint frameWidth,
        uint frameHeight)
    {
        if (frameWidth != width || frameHeight != height) return null;
        lock (_outputFrameGate)
        {
            if (_outputFrame is null || _outputFrameVersion == 0) return null;
            if (_cachedOutputFrame is not null &&
                _cachedOutputFrameVersion == _outputFrameVersion)
                return _cachedOutputFrame;
            var timestamp = checked((long)(Stopwatch.GetTimestamp() *
                (10_000_000.0 / Stopwatch.Frequency)));
            _cachedOutputFrame = new Nv12VideoFrame(width, height, width,
                timestamp, _outputFrame.ToArray());
            _cachedOutputFrameVersion = _outputFrameVersion;
            return _cachedOutputFrame;
        }
    }

    private async Task ReadMediaCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(cancellationToken);
                if (line is null) return;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var command = root.TryGetProperty("command", out var commandValue)
                        ? commandValue.GetString() : null;
                    var kindText = root.TryGetProperty("kind", out var kindValue)
                        ? kindValue.GetString() : null;
                    if (command == "stop")
                    {
                        if (kindText is "recording" or "streaming")
                            await StopMediaOutputAsync(kindText, cancellationToken);
                        continue;
                    }
                    if (command != "start" ||
                        !Enum.TryParse<MediaOutputKind>(kindText, true, out var kind)) continue;
                    var destination = root.TryGetProperty("destination", out var destValue)
                        ? destValue.GetString() ?? string.Empty : string.Empty;
                    var authorization = root.TryGetProperty("authorization", out var authValue)
                        ? authValue.GetString() ?? string.Empty : string.Empty;
                    var microphoneEnabled = root.TryGetProperty("microphone", out var micValue) &&
                        micValue.ValueKind == JsonValueKind.True;
                    var androidAudioEnabled = root.TryGetProperty("androidAudio", out var androidAudioValue) &&
                        androidAudioValue.ValueKind == JsonValueKind.True &&
                        !string.IsNullOrWhiteSpace(_androidAudioSink);
                    await StartMediaOutputAsync(kind, destination, authorization,
                        microphoneEnabled,
                        androidAudioEnabled ? _androidAudioSink : null,
                        cancellationToken);
                }
                catch (JsonException)
                {
                    ReportMediaEvent("streaming", "error",
                        "No se pudo interpretar el control multimedia.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (IOException error)
        {
            if (!cancellationToken.IsCancellationRequested)
                Console.Error.WriteLine($"Android media control failed: {error.GetType().Name}");
        }
    }

    private async Task StartMediaOutputAsync(MediaOutputKind kind, string destination,
        string authorization, bool microphoneEnabled, string? androidAudioTarget,
        CancellationToken cancellationToken)
    {
        await _mediaOutputGate.WaitAsync(cancellationToken);
        try
        {
            _mediaOutputs ??= CreateMediaOutputCoordinator();
            var capabilities = await MediaOutputService.ProbeAsync(cancellationToken);
            var bitrate = kind == MediaOutputKind.Recording ? 8000 : 6000;
            await _mediaOutputs.StartAsync(1, new MediaOutputRequest(kind,
                destination, width, height, 60, bitrate, authorization,
                microphoneEnabled ? "default" : null, IncludeDeviceAudio: false,
                SystemAudioTarget: androidAudioTarget),
                capabilities, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReportMediaEvent(kind == MediaOutputKind.Recording ? "recording" : "streaming",
                "error", "No se pudo iniciar la salida de Android. Revisa FFmpeg y PipeWire.");
            Console.Error.WriteLine($"Android media output start failed: {kind} " +
                error.GetType().Name);
        }
        finally { _mediaOutputGate.Release(); }
    }

    private async Task StopMediaOutputAsync(string output,
        CancellationToken cancellationToken)
    {
        await _mediaOutputGate.WaitAsync(cancellationToken);
        try
        {
            if (_mediaOutputs is null) return;
            await _mediaOutputs.StopAsync(output == "recording"
                ? MediaOutputKind.Recording : MediaOutputKind.Rtmp);
        }
        finally { _mediaOutputGate.Release(); }
    }

    private MediaOutputCoordinator CreateMediaOutputCoordinator()
    {
        var recording = new MediaOutputService(CopyMediaFrame,
            (_, _) => null);
        var streaming = new MediaOutputService(CopyMediaFrame,
            (_, _) => null);
        recording.StatusChanged += (message, failed) =>
            ReportMediaEvent("recording", MediaState(message, failed),
                failed ? "No se pudo finalizar la grabación."
                    : message == "Recording" ? "Grabación activa." : "Grabación detenida.");
        streaming.StatusChanged += (message, failed) =>
            ReportMediaEvent("streaming", MediaState(message, failed),
                failed ? "La transmisión se detuvo por un error."
                    : message == "Live" ? "Transmisión activa." : "Transmisión detenida.");
        return new MediaOutputCoordinator(recording, streaming);
    }

    private static string MediaState(string message, bool failed) => failed ? "error"
        : message is "Recording" or "Live" ? "active" : "stopped";

    private static void ReportMediaEvent(string output, string state, string message)
    {
        var payload = JsonSerializer.Serialize(new { output, state, message });
        Console.Error.WriteLine($"MEDIA_OUTPUT_EVENT:{payload}");
    }

    private void ReportFrameDiagnostic(string diagnostic)
    {
        if (string.Equals(_lastFrameDiagnostic, diagnostic,
                StringComparison.Ordinal)) return;
        _lastFrameDiagnostic = diagnostic;
        Console.Error.WriteLine($"Android preview: {diagnostic}");
    }

    private async Task ReadFramesAsync(int frameSize, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadFramesFromDeviceAsync(frameSize, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                throw new EndOfStreamException("La cámara virtual cerró el flujo.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                if (Volatile.Read(ref _hasProducedFrame) == 0) throw;
                Console.Error.WriteLine(
                    $"Android V4L2 preview reconnecting: {error.GetType().Name}: {error.Message}");
                Console.Error.WriteLine("ANDROID_V4L2_PREVIEW_RECONNECTING");
                var retryDelay = Volatile.Read(ref _reconnectDelaySeconds);
                Interlocked.Exchange(ref _reconnectDelaySeconds,
                    Math.Min(retryDelay * 2, 10));
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), cancellationToken);
            }
        }
    }

    private async Task ReadFramesFromDeviceAsync(int frameSize,
        CancellationToken cancellationToken)
    {
        var scale = $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,format=nv12";
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer",
            "-f", "v4l2", "-i", device, "-an", "-vf", scale,
            "-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1",
        }) start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("No se pudo iniciar FFmpeg.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        lock (_processGate) _ffmpeg = process;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var output = process.StandardOutput.BaseStream;
            var firstFrame = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = ArrayPool<byte>.Shared.Rent(frameSize);
                try
                {
                    var isFirstFrame = firstFrame;
                    if (firstFrame)
                    {
                        var firstFrameTimeout = Volatile.Read(ref _hasProducedFrame) == 0
                            ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(8);
                        using var timeout = new CancellationTokenSource(firstFrameTimeout);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken, timeout.Token);
                        try
                        {
                            await ReadExactlyAsync(output, frame.AsMemory(0, frameSize),
                                linked.Token);
                        }
                        catch (OperationCanceledException) when (
                            timeout.IsCancellationRequested &&
                            !cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                "La cámara virtual no entregó cuadros en 20 segundos.");
                        }
                        firstFrame = false;
                    }
                    else
                    {
                        await ReadExactlyAsync(output, frame.AsMemory(0, frameSize),
                            cancellationToken, TimeSpan.FromSeconds(8));
                    }

                    PublishMediaFrame(frame, frameSize);
                    if (isFirstFrame)
                    {
                        Interlocked.Exchange(ref _hasProducedFrame, 1);
                        Console.Error.WriteLine("ANDROID_V4L2_PREVIEW_READY");
                    }
                    Interlocked.Exchange(ref _reconnectDelaySeconds, 1);
                    byte[]? previous;
                    lock (_frameGate)
                    {
                        previous = _latestFrame;
                        _latestFrame = frame;
                    }
                    if (previous is not null) ArrayPool<byte>.Shared.Return(previous);
                }
                catch (EndOfStreamException error)
                {
                    ArrayPool<byte>.Shared.Return(frame);
                    var diagnostic = await StopAndReadDiagnosticsAsync(process, stderr);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(diagnostic)
                            ? error.Message : diagnostic.Trim(), error);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(frame);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                _ = await stderr;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Android preview process cleanup: {error.Message}");
            }
            finally
            {
                lock (_processGate)
                {
                    if (ReferenceEquals(_ffmpeg, process)) _ffmpeg = null;
                    process.Dispose();
                }
            }
        }
    }

    private static async Task<string> StopAndReadDiagnosticsAsync(Process process,
        Task<string> stderr)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        return await stderr;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var timeoutSource = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linkedSource = timeoutSource is null ? null :
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                timeoutSource.Token);
        var readToken = linkedSource?.Token ?? cancellationToken;
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[offset..], readToken);
                if (read == 0) throw new EndOfStreamException("La cámara V4L2 cerró el flujo.");
                offset += read;
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource?.IsCancellationRequested == true &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("La cámara V4L2 dejó de entregar cuadros.");
        }
    }

    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer?.Stop();
        _timer = null;
        _stop.Cancel();
        lock (_processGate)
        {
            try
            {
                if (_ffmpeg is { HasExited: false } process)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Android preview shutdown: {error.Message}");
            }
        }
        lock (_frameGate)
        {
            if (_latestFrame is { } frame) ArrayPool<byte>.Shared.Return(frame);
            _latestFrame = null;
        }
        try { _mediaCommandTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        if (_mediaOutputs is not null)
        {
            try { _mediaOutputs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Android media-output shutdown failed: {error.GetType().Name}");
            }
            _mediaOutputs = null;
        }
        try { _reader?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _mediaOutputGate.Dispose();
        lock (_outputFrameGate)
        {
            _outputFrame = null;
            _cachedOutputFrame = null;
        }
    }
}
