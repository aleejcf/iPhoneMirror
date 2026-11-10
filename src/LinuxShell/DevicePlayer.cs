// SPDX-License-Identifier: GPL-3.0-only
//
// Drives the preview from a live capture session instead of a file.
//
// The pump is deliberately dumb: poll the newest decoded frame on a timer and
// present it. The mailbox behind im_linux_preview_present_latest hands out an
// immutable frame, so there is no queue to drain here and a slow tick drops
// frames rather than accumulating latency — which is the right trade for a
// mirror.

using Avalonia.Controls;
using Avalonia.Threading;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IPhoneMirror.LinuxShell.Controls;
using IPhoneMirror.LinuxShell.Interop;
using IPhoneMirror.LinuxShell.Services;

namespace IPhoneMirror.LinuxShell;

internal sealed class DevicePlayer(string serial, bool playAudio,
    string? videoSink, string outputProfile, uint projectionMode,
    string? recordingPath, bool microphoneEnabled)
{
    private DispatcherTimer? _timer;
    private int _presented;
    private int _waiting;
    private int _fpsWindowFrames;
    private long _fpsWindowStartedAt;
    private long _lastFpsFrameTimestamp = long.MinValue;
    private int _tickActive;
    private int _screenshotActive;
    private bool _started;
    private uint _candidateWidth;
    private uint _candidateHeight;
    private int _sameGeometryFrames;
    private long _geometryRetryNotBefore;
    private int _geometryRetryDelayMilliseconds = 100;
    private V4l2VideoOutput? _videoOutput;
    private MediaOutputCoordinator? _mediaOutputs;
    private readonly SemaphoreSlim _mediaOutputGate = new(1, 1);
    private readonly CancellationTokenSource _mediaCancellation = new();
    private readonly object _frameCacheGate = new();
    private Nv12VideoFrame? _cachedOutputFrame;
    private long _cachedOutputFrameTicks;
    private Task? _mediaStartTask;
    private Task? _mediaCommandTask;

    internal event Action<string>? OutputError;
    internal event Action<string>? IosShortcutRequested;

    internal bool Start(PreviewSurfaceControl preview, TextBlock status,
        Action<int> report)
    {
        Console.WriteLine(preview.Diagnostic);
        if (CorePreview.Initialize() != 0)
        {
            Report(status, report, $"im_initialize: {CorePreview.LastError()}", 1);
            return false;
        }
        var start = CorePreview.StartCapture(serial, playAudio ? 1 : 0, projectionMode);
        if (start != 0)
        {
            Report(status, report,
                $"im_start_capture_ex returned {start}: {CorePreview.LastError()}",
                1);
            return false;
        }
        _started = true;
        _fpsWindowStartedAt = Stopwatch.GetTimestamp();
        status.Text = "Iniciando la captura USB…";
        Console.WriteLine(status.Text);

        // The Linux renderer exports only the newest frame. Polling every 8 ms
        // asks the UI thread to wake at 125 Hz even though capture is capped at
        // 60 FPS, which wastes CPU/GPU scheduling time without reducing latency.
        // A 16 ms cadence tracks the product's current 60 FPS ceiling; missed
        // ticks still drop stale frames instead of building a queue.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += async (_, _) =>
        {
            if (Interlocked.Exchange(ref _tickActive, 1) != 0) return;
            try
            {
                await TickAsync(preview, status);
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
                Console.Error.WriteLine($"preview tick: {exception}");
            }
            finally
            {
                Volatile.Write(ref _tickActive, 0);
            }
        };
        _timer.Start();
        if (!string.IsNullOrWhiteSpace(videoSink))
        {
            _videoOutput = new V4l2VideoOutput(videoSink, outputProfile,
                (width, height) => CopyRecordingFrame(1, width, height)?.Pixels);
            _videoOutput.Failed += diagnostic => OutputError?.Invoke(diagnostic);
            _videoOutput.Start();
        }
        if (!string.IsNullOrWhiteSpace(recordingPath))
            _mediaStartTask = StartRecordingAsync(recordingPath, microphoneEnabled,
                _mediaCancellation.Token);
        _mediaCommandTask = Task.Run(() => ReadMediaCommandsAsync(_mediaCancellation.Token));
        return true;
    }

    private async Task StartRecordingAsync(string path, bool microphoneEnabled,
        CancellationToken cancellationToken)
    {
        await StartMediaOutputAsync(MediaOutputKind.Recording, path, string.Empty,
            microphoneEnabled, cancellationToken);
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
                    if (command == "iosShortcut")
                    {
                        var action = root.TryGetProperty("action", out var actionValue)
                            ? actionValue.GetString() : null;
                        if (action is "home" or "appSwitcher")
                            IosShortcutRequested?.Invoke(action);
                        continue;
                    }
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
                    await StartMediaOutputAsync(kind, destination, authorization,
                        microphoneEnabled, cancellationToken);
                }
                catch (JsonException)
                {
                    ReportMediaEvent("streaming", "error", "No se pudo interpretar el control multimedia.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (IOException error)
        {
            if (!cancellationToken.IsCancellationRequested)
                Console.Error.WriteLine($"media control input failed: {error.GetType().Name}");
        }
    }

    private async Task StopMediaOutputAsync(string output, CancellationToken cancellationToken)
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

    private async Task StartMediaOutputAsync(MediaOutputKind kind, string destination,
        string authorization, bool microphoneEnabled, CancellationToken cancellationToken)
    {
        await _mediaOutputGate.WaitAsync(cancellationToken);
        try
        {
            _mediaOutputs ??= CreateMediaOutputCoordinator();
            var capabilities = await MediaOutputService.ProbeAsync(cancellationToken);
            var (width, height) = outputProfile == "horizontal"
                ? (1920U, 1080U) : (1080U, 1920U);
            var bitrate = kind == MediaOutputKind.Recording ? 8000 : 6000;
            await _mediaOutputs.StartAsync(1, new MediaOutputRequest(kind,
                destination, width, height, 60, bitrate, authorization,
                microphoneEnabled ? "default" : null),
                capabilities, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReportMediaEvent(kind == MediaOutputKind.Recording ? "recording" : "streaming",
                "error", "No se pudo iniciar la salida multimedia. Revisa FFmpeg y su configuración.");
            Console.Error.WriteLine($"media output start failed: {kind} " +
                $"{error.GetType().Name}: {error.Message}");
        }
        finally { _mediaOutputGate.Release(); }
    }

    private MediaOutputCoordinator CreateMediaOutputCoordinator()
    {
        var recording = new MediaOutputService(CopyRecordingFrame,
            (_, sequence) => CorePreview.CopyNextAudioPacket(sequence));
        var streaming = new MediaOutputService(CopyRecordingFrame,
            (_, sequence) => CorePreview.CopyNextAudioPacket(sequence));
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

    private Nv12VideoFrame? CopyRecordingFrame(ulong session, uint width, uint height)
    {
        lock (_frameCacheGate)
        {
            var latestTimestamp = CorePreview.LatestNv12Timestamp(width, height);
            if (latestTimestamp is null) return null;
            var now = Stopwatch.GetTimestamp();
            if (_cachedOutputFrame is { } cached && cached.Width == width &&
                cached.Height == height &&
                (latestTimestamp.Value != 0
                    ? cached.Timestamp100Ns == latestTimestamp.Value
                    : now - _cachedOutputFrameTicks <= Stopwatch.Frequency / 240))
                return cached;
            var pixels = CorePreview.CopyLatestNv12Frame(width, height,
                out var copiedTimestamp);
            if (pixels is null) return null;
            _cachedOutputFrameTicks = Stopwatch.GetTimestamp();
            _cachedOutputFrame = new Nv12VideoFrame(width, height, width,
                copiedTimestamp, pixels);
            return _cachedOutputFrame;
        }
    }

    private async Task TickAsync(PreviewSurfaceControl preview, TextBlock status)
    {
        var hasGeometry = CorePreview.LatestGeometry(out var width, out var height) == 0;
        if (hasGeometry)
        {
            if (_candidateWidth == width && _candidateHeight == height)
                ++_sameGeometryFrames;
            else
            {
                _candidateWidth = width;
                _candidateHeight = height;
                _sameGeometryFrames = 1;
                _geometryRetryNotBefore = 0;
                _geometryRetryDelayMilliseconds = 100;
            }
            // Allow transient encoder dimensions during rotation to settle.
            // If compositor import fails, retry with capped backoff instead of
            // recreating the Vulkan surface on every 16 ms preview tick.
            if (_sameGeometryFrames >= 12 &&
                Stopwatch.GetTimestamp() >= _geometryRetryNotBefore &&
                !await preview.SetFrameGeometryAsync(width, height))
            {
                status.Text = preview.Diagnostic;
                Console.Error.WriteLine(preview.Diagnostic);
                _geometryRetryNotBefore = Stopwatch.GetTimestamp() +
                    Stopwatch.Frequency * _geometryRetryDelayMilliseconds / 1000;
                _geometryRetryDelayMilliseconds = Math.Min(
                    _geometryRetryDelayMilliseconds * 2, 1000);
                return;
            }
            if (preview.IsReady)
            {
                _geometryRetryNotBefore = 0;
                _geometryRetryDelayMilliseconds = 100;
            }
        }
        if (!preview.IsReady) return;
        var result = CorePreview.PresentLatest();
        if (result == -6)
        {
            // DeviceNotFound: no frame yet. Normal while the handshake runs.
            if (++_waiting % 125 == 0)
            {
                Console.WriteLine($"waiting for the first frame ({_waiting} polls)");
                if (_presented == 0)
                    status.Text = $"Esperando la primera imagen… ({_waiting / 125} s)";
            }
            return;
        }
        if (result != 0)
        {
            status.Text = $"No se pudo mostrar la imagen: {result} {CorePreview.LastError()}";
            Console.WriteLine(status.Text);
            return;
        }
        if (!await preview.CommitAsync())
        {
            status.Text = preview.Diagnostic;
            Console.WriteLine(status.Text);
            return;
        }
        if (hasGeometry) SamplePreviewFps(width, height);
        ++_presented;
        if (_presented <= 3 || _presented % 60 == 0)
            Console.WriteLine($"presented {_presented} frames from {serial}");
    }

    private void SamplePreviewFps(uint width, uint height)
    {
        var timestamp = CorePreview.LatestNv12Timestamp(width, height);
        if (timestamp is not null && timestamp.Value != _lastFpsFrameTimestamp)
        {
            _lastFpsFrameTimestamp = timestamp.Value;
            ++_fpsWindowFrames;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - _fpsWindowStartedAt;
        if (elapsedTicks < Stopwatch.Frequency) return;
        var fps = _fpsWindowFrames * (double)Stopwatch.Frequency / elapsedTicks;
        Console.Error.WriteLine("PREVIEW_FPS:" +
            fps.ToString("F1", CultureInfo.InvariantCulture));
        _fpsWindowFrames = 0;
        _fpsWindowStartedAt = now;
    }

    private void Report(TextBlock status, Action<int> report, string message,
        int code)
    {
        status.Text = message;
        Console.Error.WriteLine(message);
        report(code);
    }

    internal void SaveScreenshot()
    {
        if (!_started || Interlocked.Exchange(ref _screenshotActive, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var (pixels, width, height) = CorePreview.CopyLatestFrame();
                var pictures = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyPictures);
                if (string.IsNullOrWhiteSpace(pictures))
                    pictures = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile), "Pictures");
                var directory = Path.Combine(pictures, "iPhoneMirror");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory,
                    $"captura-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

                using var encoder = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                foreach (var argument in new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "rawvideo", "-pixel_format", "bgra",
                    "-video_size", $"{width}x{height}", "-i", "pipe:0",
                    "-frames:v", "1", path,
                })
                    encoder.StartInfo.ArgumentList.Add(argument);
                if (!encoder.Start())
                    throw new InvalidOperationException("No se pudo iniciar ffmpeg.");
                var errorTask = encoder.StandardError.ReadToEndAsync();
                await encoder.StandardInput.BaseStream.WriteAsync(pixels);
                encoder.StandardInput.Close();
                await encoder.WaitForExitAsync();
                var error = await errorTask;
                if (encoder.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"ffmpeg no guardó la imagen: {error.Trim()}");
                Console.WriteLine($"screenshot saved: {path}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"screenshot failed: {exception}");
            }
            finally
            {
                Volatile.Write(ref _screenshotActive, 0);
            }
        });
    }

    internal void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _videoOutput?.Stop();
        _mediaCancellation.Cancel();
        try { _mediaCommandTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        try { _mediaStartTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        if (_mediaOutputs is not null)
        {
            try { _mediaOutputs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { Console.Error.WriteLine($"media-output shutdown failed: {error.GetType().Name}"); }
            _mediaOutputs = null;
        }
        _mediaCancellation.Dispose();
        if (_videoOutput is not null)
        {
            try { _videoOutput.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { Console.Error.WriteLine($"V4L2 shutdown failed: {error.GetType().Name}"); }
            _videoOutput = null;
        }
        if (_started) CorePreview.StopCapture();
        _started = false;
    }
}
