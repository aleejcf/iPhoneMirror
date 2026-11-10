using IPhoneMirror.LinuxShell.Services;
using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using IPhoneMirror.LinuxShell.Interop;

namespace IPhoneMirror.LinuxShell.Services;

internal sealed record Nv12VideoFrame(uint Width, uint Height, uint Stride,
    long Timestamp100Ns, byte[] Pixels);

internal enum MediaOutputKind
{
    Recording,
    Rtmp,
    Srt,
    Whip,
}

internal sealed record MediaOutputRequest(
    MediaOutputKind Kind,
    string Destination,
    uint Width,
    uint Height,
    int FrameRate,
    int BitrateKbps,
    string Authorization = "",
    string? MicrophoneDevice = null,
    bool IncludeDeviceAudio = true,
    string? SystemAudioTarget = null);

internal sealed record MediaOutputCapabilities(
    bool FfmpegAvailable,
    bool HasH264Encoder,
    bool HasAacEncoder,
    bool HasOpusEncoder,
    bool HasRtmp,
    bool HasSrt,
    bool HasWhip,
    string PreferredH264Encoder,
    string FfmpegPath,
    string Detail)
{
    internal IReadOnlyList<string> H264EncoderCandidates { get; init; } = [];

    // Audio is optional: the capture pipeline can explicitly pass -an when
    // no PCM packet is available. Keep video-only output available with a
    // minimal FFmpeg build that has H.264 but no AAC/Opus encoder.
    internal bool CanRecord => FfmpegAvailable && HasH264Encoder;

    internal bool Supports(MediaOutputKind kind) => kind switch
    {
        MediaOutputKind.Recording => CanRecord,
        MediaOutputKind.Rtmp => CanRecord && HasRtmp,
        MediaOutputKind.Srt => CanRecord && HasSrt,
        MediaOutputKind.Whip => FfmpegAvailable && HasH264Encoder && HasWhip,
        _ => false,
    };
}

internal sealed class MediaOutputService : IMediaOutputSession
{
    private static readonly TimeSpan StartupObservationWindow =
        TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RecordingFinalizationTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StreamShutdownTimeout =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AudioSilenceGrace =
        TimeSpan.FromMilliseconds(100);
    private const uint DefaultAudioSampleRate = 48000;
    private const ushort DefaultAudioChannels = 2;
    private const int MaximumAudioDrainPackets = 512;
    private const int MaximumVideoCatchUpSeconds = 2;
    private readonly Func<ulong, uint, uint, Nv12VideoFrame?> _frameProvider;
    private readonly Func<ulong, ulong, AudioPacket?> _audioProvider;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Process? _process;
    private Task? _runTask;
    private string _lastError = string.Empty;

    internal event Action<string, bool>? StatusChanged;
    internal bool IsRunning => _runTask is { IsCompleted: false };
    internal ulong SessionHandle { get; private set; }

    bool IMediaOutputSession.IsRunning => IsRunning;
    ulong IMediaOutputSession.SessionHandle => SessionHandle;
    event Action<string, bool>? IMediaOutputSession.StatusChanged
    {
        add => StatusChanged += value;
        remove => StatusChanged -= value;
    }
    Task IMediaOutputSession.StartAsync(ulong sessionHandle, MediaOutputRequest request,
        MediaOutputCapabilities capabilities, CancellationToken cancellationToken) =>
        StartAsync(sessionHandle, request, capabilities, cancellationToken);
    Task IMediaOutputSession.StopAsync() => StopAsync();

    internal MediaOutputService(Func<ulong, uint, uint, Nv12VideoFrame?> frameProvider,
        Func<ulong, ulong, AudioPacket?> audioProvider)
    {
        _frameProvider = frameProvider;
        _audioProvider = audioProvider;
    }

    internal static async Task<MediaOutputCapabilities> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await FindFfmpegCandidatesAsync(cancellationToken);
        if (candidates.Count == 0)
            return MissingFfmpegCapabilities();

        Exception? lastError = null;
        MediaOutputCapabilities? best = null;
        foreach (var path in candidates)
        {
            try
            {
                var encoders = await RunProbeAsync(path,
                    ["-hide_banner", "-encoders"], cancellationToken);
                var protocols = await RunProbeAsync(path,
                    ["-hide_banner", "-protocols"], cancellationToken);
                var muxers = await RunProbeAsync(path,
                    ["-hide_banner", "-muxers"], cancellationToken);
                var capabilities = CreateCapabilities(path, encoders, protocols,
                    muxers);
                capabilities = await ResolveWorkingEncoderAsync(capabilities,
                    640, 360, cancellationToken);
                best = SelectBestCapabilities([best, capabilities]);
                if (capabilities.HasH264Encoder && CapabilityScore(capabilities) == 34 &&
                    EncoderPreferenceScore(capabilities.PreferredH264Encoder) == 5)
                    break;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                LinuxMediaOutputLog.Exception("media_output", "capability_probe_failed",
                    error, ("file", Path.GetFileName(path)));
            }
        }

        if (best is not null) return best;

        return new(false, false, false, false, false, false, false, string.Empty,
            candidates[0], lastError?.Message ?? LinuxMediaMessages.Get("FfmpegProbeFailed"));
    }

    private static MediaOutputCapabilities MissingFfmpegCapabilities() =>
        new(false, false, false, false, false, false, false, string.Empty,
            string.Empty,
            LinuxMediaMessages.Get("FfmpegNotFound"));

    internal static int CapabilityScore(MediaOutputCapabilities capabilities) =>
        (capabilities.HasH264Encoder ? 16 : 0) +
        (capabilities.HasAacEncoder ? 8 : 0) +
        (capabilities.HasOpusEncoder ? 4 : 0) +
        (capabilities.HasRtmp ? 2 : 0) +
        (capabilities.HasSrt ? 2 : 0) +
        (capabilities.HasWhip ? 2 : 0);

    internal static MediaOutputCapabilities? SelectBestCapabilities(
        IEnumerable<MediaOutputCapabilities?> candidates)
    {
        MediaOutputCapabilities? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (best is null || IsBetterCandidate(candidate, best))
                best = candidate;
        }
        return best;
    }

    private static bool IsBetterCandidate(MediaOutputCapabilities candidate,
        MediaOutputCapabilities current)
    {
        // H.264 is the hard requirement for every output mode. A build with
        // many protocols but no H.264 must never displace a usable encoder.
        if (candidate.HasH264Encoder != current.HasH264Encoder)
            return candidate.HasH264Encoder;
        if (candidate.FfmpegAvailable != current.FfmpegAvailable)
            return candidate.FfmpegAvailable;
        var candidateScore = CapabilityScore(candidate);
        var currentScore = CapabilityScore(current);
        if (candidateScore != currentScore)
            return candidateScore > currentScore;
        return EncoderPreferenceScore(candidate.PreferredH264Encoder) >
            EncoderPreferenceScore(current.PreferredH264Encoder);
    }

    private static int EncoderPreferenceScore(string encoder)
    {
        if (encoder.Equals("h264_nvenc", StringComparison.OrdinalIgnoreCase)) return 5;
        if (encoder.Equals("h264_amf", StringComparison.OrdinalIgnoreCase)) return 4;
        if (encoder.Equals("h264_qsv", StringComparison.OrdinalIgnoreCase)) return 3;
        if (encoder.Equals("h264_mf", StringComparison.OrdinalIgnoreCase)) return 2;
        if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    internal async Task StartAsync(ulong sessionHandle, MediaOutputRequest request,
        MediaOutputCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sessionHandle);
        Validate(request, capabilities);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) throw new InvalidOperationException(LinuxMediaMessages.Get("MediaOutputAlreadyRunning"));
            capabilities = await ResolveWorkingEncoderAsync(capabilities,
                request.Width, request.Height, cancellationToken);
            if (!capabilities.HasH264Encoder)
                throw new InvalidOperationException(
                    LinuxMediaMessages.Get("FfmpegEncoderSizeUnsupported"));
            _lastError = string.Empty;
            var firstAudio = request.IncludeDeviceAudio
                ? await TryWaitForAudioAsync(sessionHandle, cancellationToken) : null;
            string? recordingStagingPath = null;
            var processRequest = request;
            if (request.Kind == MediaOutputKind.Recording)
            {
                recordingStagingPath = LinuxPendingRecordingStore.CreateStagingPath(
                    request.Destination);
                TryDeleteFile(recordingStagingPath);
                processRequest = request with { Destination = recordingStagingPath };
            }
            var hasAudioEncoder = request.Kind == MediaOutputKind.Whip
                ? capabilities.HasOpusEncoder : capabilities.HasAacEncoder;
            var includeMicrophone = !string.IsNullOrWhiteSpace(request.MicrophoneDevice);
            var includeSystemAudio = !string.IsNullOrWhiteSpace(request.SystemAudioTarget);
            if ((includeMicrophone || includeSystemAudio) && !hasAudioEncoder)
                throw new InvalidOperationException(
                    LinuxMediaMessages.Get("MicrophoneAudioRequired"));
            var includeDeviceAudio = request.IncludeDeviceAudio && hasAudioEncoder;
            var audioSampleRate = firstAudio?.SampleRate ?? DefaultAudioSampleRate;
            var audioChannels = firstAudio?.Channels ?? DefaultAudioChannels;
            var audioFifo = includeDeviceAudio ? new LinuxAudioFifo() : null;
            var microphoneFifo = includeMicrophone ? new LinuxAudioFifo() : null;
            var systemAudioFifo = includeSystemAudio ? new LinuxAudioFifo() : null;
            FileStream? audioPipe = null;
            Process? process = null;
            Process? microphoneProcess = null;
            Task<string>? microphoneError = null;
            Process? systemAudioProcess = null;
            Task<string>? systemAudioError = null;
            try
            {
                process = CreateProcess(capabilities.FfmpegPath,
                    BuildArguments(processRequest, capabilities,
                        audioFifo?.Path,
                        audioSampleRate,
                        audioChannels,
                        includeDeviceAudio: includeDeviceAudio,
                        microphonePipePath: microphoneFifo?.Path,
                        systemAudioPipePath: systemAudioFifo?.Path));
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data)) _lastError = args.Data;
                };
                if (!process.Start())
                    throw new InvalidOperationException(LinuxMediaMessages.Get("FfmpegStartFailed"));
                process.BeginErrorReadLine();
                using var pipeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                pipeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var pipeConnection = audioFifo?.OpenWriterAsync(process, pipeTimeout.Token);
                await ObserveStartupAsync(process, cancellationToken);
                if (pipeConnection is not null)
                {
                    var processExit = process.WaitForExitAsync(cancellationToken);
                    var completed = await Task.WhenAny(pipeConnection, processExit);
                    if (completed == processExit)
                    {
                        await processExit;
                        await ThrowStartupExitAsync(process, cancellationToken);
                    }

                    try { audioPipe = await pipeConnection; }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            LinuxMediaMessages.Get("FfmpegAudioInputTimeout"));
                    }
                }
                if (microphoneFifo is not null)
                {
                    (microphoneProcess, microphoneError) =
                        await StartMicrophoneCaptureAsync(microphoneFifo.Path,
                            cancellationToken);
                    await ObserveMicrophoneStartupAsync(microphoneProcess,
                        microphoneError, cancellationToken);
                }
                if (systemAudioFifo is not null)
                {
                    (systemAudioProcess, systemAudioError) =
                        await StartPipeWireCaptureAsync(systemAudioFifo.Path,
                            request.SystemAudioTarget!, captureSinkMonitor: true,
                            cancellationToken);
                    await ObserveMicrophoneStartupAsync(systemAudioProcess,
                        systemAudioError, cancellationToken);
                }

                var runCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var activeProcess = process;
                var activeAudioPipe = audioPipe;
                var activeAudioFifo = audioFifo;
                var activeRecordingStagingPath = recordingStagingPath;
                _process = activeProcess;
                SessionHandle = sessionHandle;
                _runCancellation = runCancellation;
                _runTask = Task.Run(() => PumpAsync(activeProcess,
                    activeAudioPipe, activeAudioFifo, microphoneProcess,
                    microphoneError, microphoneFifo, systemAudioProcess,
                    systemAudioError, systemAudioFifo, sessionHandle, request, firstAudio,
                    audioSampleRate, audioChannels, activeRecordingStagingPath,
                    runCancellation.Token),
                    CancellationToken.None);
                process = null;
                audioPipe = null;
                audioFifo = null;
                microphoneProcess = null;
                microphoneFifo = null;
                systemAudioProcess = null;
                systemAudioFifo = null;
                recordingStagingPath = null;
                StatusChanged?.Invoke(request.Kind == MediaOutputKind.Recording
                    ? "Recording" : "Live", false);
            }
            catch (Exception error)
            {
                LinuxMediaOutputLog.Info("media_output", "start_failed",
                    ("kind", request.Kind), ("error_type", error.GetType().Name));
                throw;
            }
            finally
            {
                if (microphoneProcess is not null)
                {
                    await StopMicrophoneCaptureAsync(microphoneProcess);
                    if (microphoneError is not null) _ = await microphoneError;
                    microphoneProcess.Dispose();
                }
                if (systemAudioProcess is not null)
                {
                    await StopMicrophoneCaptureAsync(systemAudioProcess);
                    if (systemAudioError is not null) _ = await systemAudioError;
                    systemAudioProcess.Dispose();
                }
                if (process is not null) await DisposeFailedStartAsync(process);
                audioPipe?.Dispose();
                audioFifo?.Dispose();
                microphoneFifo?.Dispose();
                systemAudioFifo?.Dispose();
                if (recordingStagingPath is not null)
                    TryDeleteFile(recordingStagingPath);
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    internal async Task StopAsync()
    {
        Task? task;
        await _lifecycleGate.WaitAsync();
        try
        {
            _runCancellation?.Cancel();
            task = _runTask;
        }
        finally { _lifecycleGate.Release(); }
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PumpAsync(Process process, FileStream? audioPipe,
        LinuxAudioFifo? audioFifo, Process? microphoneProcess,
        Task<string>? microphoneError, LinuxAudioFifo? microphoneFifo,
        Process? systemAudioProcess, Task<string>? systemAudioError,
        LinuxAudioFifo? systemAudioFifo,
        ulong sessionHandle, MediaOutputRequest request, AudioPacket? firstAudio,
        uint audioSampleRate, ushort audioChannels, string? recordingStagingPath,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        Task? videoTask = null;
        Task? audioTask = null;
        Task? microphoneTask = null;
        Task? systemAudioTask = null;
        using var pumpCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var outputClock = Stopwatch.StartNew();
            videoTask = PumpVideoAsync(process, sessionHandle, request,
                outputClock, pumpCancellation.Token);
            audioTask = audioPipe is not null
                ? PumpAudioAsync(process, audioPipe, sessionHandle,
                    firstAudio, audioSampleRate, audioChannels,
                    outputClock, pumpCancellation.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, pumpCancellation.Token);
            microphoneTask = microphoneProcess is not null
                ? microphoneProcess.WaitForExitAsync(pumpCancellation.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, pumpCancellation.Token);
            systemAudioTask = systemAudioProcess is not null
                ? systemAudioProcess.WaitForExitAsync(pumpCancellation.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, pumpCancellation.Token);
            var completed = await Task.WhenAny(videoTask, audioTask,
                microphoneTask, systemAudioTask);
            await completed;
            if (completed == microphoneTask && microphoneProcess is not null)
            {
                var diagnostic = microphoneError is null ? string.Empty
                    : await microphoneError;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(diagnostic)
                    ? LinuxMediaMessages.Get("MicrophoneCaptureStopped")
                    : diagnostic.Trim());
            }
            if (completed == systemAudioTask && systemAudioProcess is not null)
            {
                var diagnostic = systemAudioError is null ? string.Empty
                    : await systemAudioError;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(diagnostic)
                    ? LinuxMediaMessages.Get("MicrophoneCaptureStopped")
                    : diagnostic.Trim());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            pumpCancellation.Cancel();
            if (microphoneProcess is not null)
            {
                await StopMicrophoneCaptureAsync(microphoneProcess);
                try
                {
                    if (microphoneError is not null) _ = await microphoneError;
                }
                catch (Exception error)
                {
                    LinuxMediaOutputLog.ExceptionOnce("microphone-stderr", "media_output",
                        "microphone_stderr_failed", error);
                }
            }
            if (systemAudioProcess is not null)
            {
                await StopMicrophoneCaptureAsync(systemAudioProcess);
                try
                {
                    if (systemAudioError is not null) _ = await systemAudioError;
                }
                catch (Exception error)
                {
                    LinuxMediaOutputLog.ExceptionOnce("system-audio-stderr", "media_output",
                        "system_audio_stderr_failed", error);
                }
            }
            try { process.StandardInput.Close(); }
            catch (Exception error)
            {
                LinuxMediaOutputLog.ExceptionOnce("media-stdin-close", "media_output",
                    "stdin_close_failed", error);
            }
            try { audioPipe?.Dispose(); }
            catch (Exception error)
            {
                LinuxMediaOutputLog.ExceptionOnce("media-audio-pipe-dispose", "media_output",
                    "audio_pipe_dispose_failed", error);
            }
            audioFifo?.Dispose();
            microphoneFifo?.Dispose();
            systemAudioFifo?.Dispose();
            try
            {
                await Task.WhenAll(videoTask ?? Task.CompletedTask,
                    audioTask ?? Task.CompletedTask, microphoneTask ?? Task.CompletedTask,
                    systemAudioTask ?? Task.CompletedTask);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure ??= error; }
            var shutdownTimeout = request.Kind == MediaOutputKind.Recording
                ? RecordingFinalizationTimeout : StreamShutdownTimeout;
            try
            {
                using var timeout = new CancellationTokenSource(shutdownTimeout);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                failure ??= new TimeoutException(request.Kind == MediaOutputKind.Recording
                    ? LinuxMediaMessages.Get("RecordingFinalizeTimeout")
                    : LinuxMediaMessages.Get("StreamingStopTimeout"));
                await KillProcessAsync(process);
            }
            catch (Exception error)
            {
                failure ??= error;
                await KillProcessAsync(process);
            }
            if (TryGetExitCode(process, out var exitCode) && exitCode != 0)
            {
                failure ??= new InvalidOperationException(
                    string.IsNullOrWhiteSpace(_lastError)
                        ? $"FFmpeg exited with code {exitCode} while finalizing output."
                        : _lastError);
            }
            if (recordingStagingPath is not null)
            {
                if (failure is null)
                {
                    try
                    {
                        File.Move(recordingStagingPath, request.Destination,
                            overwrite: false);
                    }
                    catch (Exception error)
                    {
                        failure = new IOException(
                            "The finalized recording could not be made available for saving.",
                            error);
                    }
                }
                if (failure is not null) TryDeleteFile(recordingStagingPath);
            }
            process.Dispose();
            microphoneProcess?.Dispose();
            await _lifecycleGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _runTask = null;
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                    SessionHandle = 0;
                }
            }
            finally { _lifecycleGate.Release(); }
            if (failure is not null)
                LinuxMediaOutputLog.Info("media_output", "session_failed",
                    ("kind", request.Kind), ("handle", sessionHandle),
                    ("error_type", failure.GetType().Name));
            StatusChanged?.Invoke(failure?.Message ?? "Stopped", failure is not null);
        }
    }

    private async Task PumpVideoAsync(Process process, ulong sessionHandle,
        MediaOutputRequest request, Stopwatch outputClock, CancellationToken cancellationToken)
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / request.FrameRate);
        var firstFrameWait = Stopwatch.StartNew();
        long framesWritten = 0;
        ReadOnlyMemory<byte> lastFrame = default;
        // Decouple the GPU->CPU frame readback from the pump timer. The
        // readback (materialize_gpu_frame + letterbox + SDR) is synchronous
        // and can exceed the frame interval on weak hardware, which delays
        // the PeriodicTimer tick and produces uneven frame spacing in the
        // recording. A background reader fills a latch at the frame rate;
        // the pump timer takes the latest buffered frame without blocking.
        var latch = new FrameLatch();
        using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var readerTask = Task.Run(() => FrameReaderAsync(sessionHandle, request.Width,
            request.Height, latch, frameInterval, readerCts.Token), CancellationToken.None);
        try
        {
            using var timer = new PeriodicTimer(frameInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (process.HasExited)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
                        ? LinuxMediaMessages.Format("FfmpegExitedFormat", process.ExitCode) : _lastError);
                // The rawvideo input has a fixed frame rate and therefore assigns
                // one frame interval to every frame received. A slow frame copy,
                // resize, encode, or pipe write can make a timer tick miss its
                // deadline; writing only one frame in that case would shorten the
                // resulting recording. Keep the frame count aligned with elapsed
                // wall time and repeat the newest frame to cover missed slots.
                var dueBeforeRead = CalculateDueVideoFrames(outputClock.Elapsed,
                    request.FrameRate, framesWritten);
                if (dueBeforeRead <= 0) continue;
                var buffered = latch.Get();
                if (!buffered.IsEmpty)
                    lastFrame = buffered;
                else if (lastFrame.IsEmpty)
                {
                    if (firstFrameWait.Elapsed > TimeSpan.FromSeconds(5))
                        throw new TimeoutException(LinuxMediaMessages.Get("MediaOutputFrameTimeout"));
                    continue;
                }
                var schedule = CalculateVideoWritePlan(outputClock.Elapsed,
                    request.FrameRate, framesWritten);
                framesWritten = schedule.FramesWrittenBaseline;
                for (long index = 0; index < schedule.FramesToWrite; ++index)
                {
                    await process.StandardInput.BaseStream.WriteAsync(lastFrame,
                        cancellationToken);
                    ++framesWritten;
                }
            }
        }
        finally
        {
            readerCts.Cancel();
            try { await readerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* Reader teardown failure does not affect the recording. */ }
        }
    }

    private async Task FrameReaderAsync(ulong sessionHandle, uint width,
        uint height, FrameLatch latch, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var frame = _frameProvider(sessionHandle, width, height);
                if (frame is not null)
                    latch.Publish(GetNv12FramePayload(frame, width, height));
            }
            catch { /* Transient frame read failure; best-effort. */ }
        }
    }

    internal static long CalculateDueVideoFrames(TimeSpan elapsed,
        int frameRate, long framesWritten)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        ArgumentOutOfRangeException.ThrowIfNegative(framesWritten);
        if (elapsed <= TimeSpan.Zero) return 0;
        var due = (long)Math.Round(elapsed.TotalSeconds * frameRate,
            MidpointRounding.AwayFromZero);
        return Math.Max(0, due - framesWritten);
    }

    internal static (long FramesToWrite, long FramesWrittenBaseline)
        CalculateVideoWritePlan(TimeSpan elapsed, int frameRate,
            long framesWritten)
    {
        var framesToWrite = CalculateDueVideoFrames(elapsed, frameRate,
            framesWritten);
        var maximumCatchUpFrames = checked((long)frameRate *
            MaximumVideoCatchUpSeconds);
        if (framesToWrite <= maximumCatchUpFrames)
            return (framesToWrite, framesWritten);

        // Raw video timestamps count bytes actually written. Advancing only
        // this counter drops time from video while PCM keeps its full duration,
        // permanently delaying the sound after a stall. Bound each write burst,
        // retain the debt and reuse the latest frame without allocating a queue.
        return (maximumCatchUpFrames, framesWritten);
    }

    private async Task PumpAudioAsync(Process process, Stream output,
        ulong sessionHandle, AudioPacket? firstAudio, uint outputSampleRate,
        ushort outputChannels, Stopwatch audioClock, CancellationToken cancellationToken)
    {
        var blockAlign = checked(outputChannels * sizeof(short));
        var bytesPerSecond = checked((long)outputSampleRate * blockAlign);
        var normalizer = new Pcm16AudioNormalizer(outputSampleRate, outputChannels);
        var sequence = firstAudio?.Sequence ?? 0;
        long emittedBytes = 0;
        if (firstAudio is not null)
        {
            var pcm = normalizer.Convert(firstAudio);
            if (pcm.Length != 0)
            {
                await output.WriteAsync(pcm, cancellationToken);
                emittedBytes = pcm.Length;
            }
        }
        var lastRealPacket = Stopwatch.StartNew();
        var silenceChunkBytes = checked((int)Math.Max(blockAlign,
            bytesPerSecond / 50 / blockAlign * blockAlign));
        var silence = new byte[silenceChunkBytes];
        var insertedSilence = firstAudio is null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
                    ? LinuxMediaMessages.Format("FfmpegExitedFormat", process.ExitCode) : _lastError);
            var packet = insertedSilence
                ? ReadNewestAvailableAudioPacket(
                    cursor => _audioProvider(sessionHandle, cursor), sequence)
                : _audioProvider(sessionHandle, sequence);
            if (packet is null)
            {
                if (firstAudio is null || lastRealPacket.Elapsed >= AudioSilenceGrace)
                {
                    var targetBytes = checked(
                        (long)(audioClock.Elapsed.TotalSeconds * bytesPerSecond));
                    var missingBytes = targetBytes - emittedBytes;
                    var bytesToWrite = checked((int)Math.Min(
                        Math.Max(0, missingBytes), silence.Length));
                    bytesToWrite -= bytesToWrite % blockAlign;
                    if (bytesToWrite > 0)
                    {
                        await output.WriteAsync(
                            silence.AsMemory(0, bytesToWrite), cancellationToken);
                        emittedBytes = checked(emittedBytes + bytesToWrite);
                        insertedSilence = true;
                        continue;
                    }
                }
                await Task.Delay(5, cancellationToken);
                continue;
            }
            if (packet.Sequence <= sequence)
                throw new InvalidDataException(
                    LinuxMediaMessages.Get("MediaOutputAudioStalled"));
            var normalized = normalizer.Convert(packet);
            if (normalized.Length != 0)
            {
                await output.WriteAsync(normalized, cancellationToken);
                emittedBytes = checked(emittedBytes + normalized.Length);
            }
            sequence = packet.Sequence;
            lastRealPacket.Restart();
            insertedSilence = false;
        }
    }

    internal sealed class Pcm16AudioNormalizer
    {
        private readonly uint _targetSampleRate;
        private readonly ushort _targetChannels;
        private uint _sourceSampleRate;
        private ushort _sourceChannels;
        private long _rateRemainder;

        internal Pcm16AudioNormalizer(uint targetSampleRate, ushort targetChannels)
        {
            if (targetSampleRate is < 8000 or > 192000)
                throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
            if (targetChannels is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(targetChannels));
            _targetSampleRate = targetSampleRate;
            _targetChannels = targetChannels;
        }

        internal byte[] Convert(AudioPacket packet)
        {
            if (packet.SampleRate is < 8000 or > 192000 ||
                packet.Channels is < 1 or > 8 || packet.BitsPerSample != 16)
                throw new InvalidDataException(
                    LinuxMediaMessages.Get("MediaOutputAudioUnsupported"));
            var sourceBlockAlign = checked(packet.Channels * sizeof(short));
            if (packet.Pcm.Length == 0 || packet.Pcm.Length % sourceBlockAlign != 0)
                throw new InvalidDataException(
                    LinuxMediaMessages.Get("MediaOutputAudioInvalid"));
            if (packet.SampleRate == _targetSampleRate &&
                packet.Channels == _targetChannels)
                return packet.Pcm;

            if (_sourceSampleRate != packet.SampleRate ||
                _sourceChannels != packet.Channels)
            {
                _sourceSampleRate = packet.SampleRate;
                _sourceChannels = packet.Channels;
                _rateRemainder = 0;
            }

            var sourceFrames = packet.Pcm.Length / sourceBlockAlign;
            var scaledFrames = checked((long)sourceFrames * _targetSampleRate +
                _rateRemainder);
            var targetFrames = checked((int)(scaledFrames / packet.SampleRate));
            _rateRemainder = scaledFrames % packet.SampleRate;
            if (targetFrames == 0) return [];

            var targetBlockAlign = checked(_targetChannels * sizeof(short));
            var output = new byte[checked(targetFrames * targetBlockAlign)];
            var source = packet.Pcm.AsSpan();
            for (var targetFrame = 0; targetFrame < targetFrames; ++targetFrame)
            {
                var position = targetFrames == 1 || sourceFrames == 1
                    ? 0
                    : (double)targetFrame * (sourceFrames - 1) / (targetFrames - 1);
                var lowerFrame = (int)position;
                var upperFrame = Math.Min(lowerFrame + 1, sourceFrames - 1);
                var fraction = position - lowerFrame;
                for (var targetChannel = 0;
                     targetChannel < _targetChannels; ++targetChannel)
                {
                    var sample = ReadMappedSample(source, lowerFrame, upperFrame,
                        fraction, packet.Channels, targetChannel, _targetChannels);
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(
                        (targetFrame * _targetChannels + targetChannel) * sizeof(short),
                        sizeof(short)), sample);
                }
            }
            return output;
        }

        private static short ReadMappedSample(ReadOnlySpan<byte> source,
            int lowerFrame, int upperFrame, double fraction,
            ushort sourceChannels, int targetChannel, ushort targetChannels)
        {
            if (targetChannels == 1 && sourceChannels > 1)
            {
                double mixed = 0;
                for (var channel = 0; channel < sourceChannels; ++channel)
                    mixed += Interpolate(source, lowerFrame, upperFrame,
                        fraction, sourceChannels, channel);
                return ClampSample(mixed / sourceChannels);
            }
            var sourceChannel = sourceChannels == 1 ? 0 :
                Math.Min(targetChannel, sourceChannels - 1);
            return ClampSample(Interpolate(source, lowerFrame, upperFrame,
                fraction, sourceChannels, sourceChannel));
        }

        private static double Interpolate(ReadOnlySpan<byte> source,
            int lowerFrame, int upperFrame, double fraction,
            ushort channels, int channel)
        {
            var lower = ReadSample(source, lowerFrame, channels, channel);
            var upper = ReadSample(source, upperFrame, channels, channel);
            return lower + (upper - lower) * fraction;
        }

        private static short ReadSample(ReadOnlySpan<byte> source, int frame,
            ushort channels, int channel) => BinaryPrimitives.ReadInt16LittleEndian(
                source.Slice((frame * channels + channel) * sizeof(short),
                    sizeof(short)));

        private static short ClampSample(double value) => checked((short)Math.Clamp(
            Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue,
            short.MaxValue));
    }

    private async Task<AudioPacket?> TryWaitForAudioAsync(ulong sessionHandle,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packet = ReadNewestAvailableAudioPacket(
                cursor => _audioProvider(sessionHandle, cursor), 0);
            if (packet is { SampleRate: >= 8000 and <= 192000,
                    Channels: >= 1 and <= 8, BitsPerSample: 16 } &&
                packet.Pcm.Length != 0 &&
                packet.Pcm.Length % (packet.Channels * sizeof(short)) == 0)
                return packet;
            await Task.Delay(20, cancellationToken);
        }
        return null;
    }

    internal static AudioPacket? ReadNewestAvailableAudioPacket(
        Func<ulong, AudioPacket?> provider, ulong afterSequence)
    {
        AudioPacket? newest = null;
        var cursor = afterSequence;
        for (var index = 0; index < MaximumAudioDrainPackets; ++index)
        {
            var packet = provider(cursor);
            if (packet is null) break;
            if (packet.Sequence <= cursor)
                throw new InvalidDataException(
                    LinuxMediaMessages.Get("MediaOutputAudioStalled"));
            newest = packet;
            cursor = packet.Sequence;
        }
        return newest;
    }

    internal static ReadOnlyMemory<byte> GetNv12FramePayload(Nv12VideoFrame frame,
        uint width, uint height)
    {
        var targetBytes = checked((int)((ulong)width * height * 3U / 2U));
        if (width == 0 || height == 0 || (width & 1U) != 0 ||
            (height & 1U) != 0 || frame.Width != width ||
            frame.Height != height || frame.Stride != width ||
            frame.Pixels.Length < targetBytes)
            throw new InvalidDataException(LinuxMediaMessages.Get("MediaOutputFrameInvalid"));
        return frame.Pixels.AsMemory(0, targetBytes);
    }

    private static Process CreateProcess(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start, EnableRaisingEvents = true };
    }

    internal static IReadOnlyList<string> BuildArguments(MediaOutputRequest request,
        MediaOutputCapabilities capabilities,
        string? audioPipePath = "/tmp/iphone-mirror-audio-test.fifo",
        uint audioSampleRate = 48000, ushort audioChannels = 2,
        bool includeDeviceAudio = true, string? microphonePipePath = null,
        string? systemAudioPipePath = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
        };
        var includeMicrophone = !string.IsNullOrWhiteSpace(request.MicrophoneDevice);
        var includeSystemAudio = !string.IsNullOrWhiteSpace(request.SystemAudioTarget);
        var audioInputIndex = 0;
        var audioInputIndexes = new List<int>(3);
        if (includeDeviceAudio)
        {
            if (string.IsNullOrWhiteSpace(audioPipePath))
                throw new ArgumentException("An audio pipe is required when device audio is enabled.",
                    nameof(audioPipePath));
            audioInputIndexes.Add(audioInputIndex++);
            args.AddRange([
                "-thread_queue_size", "512", "-f", "s16le",
                "-ar", audioSampleRate.ToString(), "-ac", audioChannels.ToString(),
                "-i", audioPipePath,
            ]);
        }
        if (includeMicrophone)
        {
            if (string.IsNullOrWhiteSpace(microphonePipePath))
                throw new ArgumentException("A microphone pipe is required when mixing a microphone.",
                    nameof(microphonePipePath));
            audioInputIndexes.Add(audioInputIndex++);
            args.AddRange([
                "-thread_queue_size", "128", "-f", "s16le",
                "-ar", DefaultAudioSampleRate.ToString(), "-ac",
                DefaultAudioChannels.ToString(), "-i", microphonePipePath,
            ]);
        }
        if (includeSystemAudio)
        {
            if (string.IsNullOrWhiteSpace(systemAudioPipePath))
                throw new ArgumentException("An audio pipe is required for system audio.",
                    nameof(systemAudioPipePath));
            audioInputIndexes.Add(audioInputIndex++);
            args.AddRange([
                "-thread_queue_size", "128", "-f", "s16le",
                "-ar", DefaultAudioSampleRate.ToString(), "-ac",
                DefaultAudioChannels.ToString(), "-i", systemAudioPipePath,
            ]);
        }
        var videoInputIndex = audioInputIndex;
        args.AddRange([
            "-f", "rawvideo", "-pixel_format", "nv12",
            "-video_size", $"{request.Width}x{request.Height}",
            "-framerate", request.FrameRate.ToString(),
            "-color_range", "pc", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
            "-i", "pipe:0",
        ]);
        args.AddRange(["-map", $"{videoInputIndex}:v:0"]);
        if (audioInputIndexes.Count > 1)
        {
            var mixInputs = string.Concat(audioInputIndexes.Select(index =>
                $"[{index}:a:0]"));
            args.AddRange([
                "-filter_complex", $"{mixInputs}amix=inputs={audioInputIndexes.Count}:duration=longest[mixed]",
                "-map", "[mixed]",
            ]);
        }
        else if (audioInputIndexes.Count == 1)
            args.AddRange(["-map", $"{audioInputIndexes[0]}:a:0"]);
        else
            args.Add("-an");
        args.AddRange(["-c:v", capabilities.PreferredH264Encoder]);
        AddEncoderOptions(args, capabilities.PreferredH264Encoder, request.Kind);
        args.AddRange([
            "-color_range", "pc", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
            "-pix_fmt", EncoderPixelFormat(capabilities.PreferredH264Encoder),
            "-g", (request.FrameRate * 2).ToString(),
            "-b:v", $"{request.BitrateKbps}k", "-maxrate", $"{request.BitrateKbps}k",
            "-bufsize", $"{request.BitrateKbps * 2}k",
        ]);
        if (audioInputIndexes.Count > 0)
            args.AddRange(request.Kind == MediaOutputKind.Whip
                ? ["-c:a", "libopus", "-ac", "2", "-b:a", "128k"]
                : ["-c:a", "aac", "-b:a", "192k"]);
        switch (request.Kind)
        {
            case MediaOutputKind.Recording:
                args.AddRange(["-movflags", "+faststart", "-y", request.Destination]);
                break;
            case MediaOutputKind.Rtmp:
                args.AddRange(["-f", "flv", request.Destination]);
                break;
            case MediaOutputKind.Srt:
                args.AddRange(["-f", "mpegts", request.Destination]);
                break;
            case MediaOutputKind.Whip:
                var token = NormalizeWhipToken(request.Authorization);
                if (!string.IsNullOrEmpty(token))
                    args.AddRange(["-authorization", token]);
                args.AddRange(["-f", "whip", request.Destination]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request),
                    "Unsupported media output kind.");
        }
        return args;
    }

    internal static string SelectPreferredH264Encoder(string encoders)
    {
        return FindH264EncoderCandidates(encoders).FirstOrDefault() ?? string.Empty;
    }

    internal static IReadOnlyList<string> FindH264EncoderCandidates(string encoders)
    {
        string[] preference =
        [
            "h264_nvenc",
            "h264_amf",
            "h264_qsv",
            "h264_mf",
            "libx264",
        ];
        return preference.Where(candidate => HasToken(encoders, candidate)).ToArray();
    }

    internal static MediaOutputCapabilities CreateCapabilities(string path,
        string encoders, string protocols, string muxers)
    {
        var preferredEncoder = SelectPreferredH264Encoder(encoders);
        var encoderCandidates = FindH264EncoderCandidates(encoders);
        var h264 = !string.IsNullOrWhiteSpace(preferredEncoder);
        var aac = HasToken(encoders, "aac");
        var opus = HasToken(encoders, "libopus");
        var rtmp = HasToken(protocols, "rtmp") && HasToken(muxers, "flv");
        var srt = HasToken(protocols, "srt") && HasToken(muxers, "mpegts");
        var whip = HasToken(muxers, "whip");
        return new(true, h264, aac, opus, rtmp, srt, whip, preferredEncoder, path,
            $"{Path.GetFileName(path)} / {preferredEncoder}")
        {
            H264EncoderCandidates = encoderCandidates,
        };
    }

    private static async Task<MediaOutputCapabilities> ResolveWorkingEncoderAsync(
        MediaOutputCapabilities capabilities, uint width, uint height,
        CancellationToken cancellationToken)
    {
        var candidates = capabilities.H264EncoderCandidates.Count == 0
            ? [capabilities.PreferredH264Encoder]
            : capabilities.H264EncoderCandidates;
        var ordered = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .OrderByDescending(candidate => string.Equals(candidate,
                capabilities.PreferredH264Encoder, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var encoder in ordered)
        {
            var usable = await CanEncodeNv12FrameAsync(capabilities.FfmpegPath,
                encoder, width, height, cancellationToken);
            LinuxMediaOutputLog.Info("media_output", "encoder_probe",
                ("file", Path.GetFileName(capabilities.FfmpegPath)),
                ("encoder", encoder), ("width", width), ("height", height),
                ("usable", usable));
            if (usable)
            {
                return capabilities with
                {
                    HasH264Encoder = true,
                    PreferredH264Encoder = encoder,
                    Detail = $"{Path.GetFileName(capabilities.FfmpegPath)} / {encoder}",
                };
            }
        }
        return capabilities with
        {
            HasH264Encoder = false,
            PreferredH264Encoder = string.Empty,
            Detail = LinuxMediaMessages.Format("FfmpegNoUsableEncoderFormat", Path.GetFileName(capabilities.FfmpegPath)),
        };
    }

    private static async Task<bool> CanEncodeNv12FrameAsync(string path,
        string encoder, uint width, uint height, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(encoder) ||
            width == 0 || height == 0 || (width & 1U) != 0 || (height & 1U) != 0)
            return false;
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pixel_format", "nv12",
            "-video_size", $"{width}x{height}", "-framerate", "30",
            "-color_range", "pc", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
            "-i", "pipe:0", "-frames:v", "1", "-an", "-c:v", encoder,
        };
        AddEncoderOptions(arguments, encoder, MediaOutputKind.Recording);
        arguments.AddRange([
            "-color_range", "pc", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709",
            "-pix_fmt", EncoderPixelFormat(encoder), "-f", "null", "-",
        ]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) return false;
            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var errorOutput = process.StandardError.ReadToEndAsync(timeout.Token);
            var yBytes64 = CalculateLumaPlaneSize(width, height);
            if (yBytes64 > int.MaxValue * 2L / 3L) return false;
            var yBytes = checked((int)yBytes64);
            var frame = new byte[checked(yBytes + yBytes / 2)];
            Array.Fill(frame, (byte)16, 0, yBytes);
            Array.Fill(frame, (byte)128, yBytes, frame.Length - yBytes);
            await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await errorOutput;
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
            }
        }

        static long CalculateLumaPlaneSize(uint frameWidth, uint frameHeight) =>
            checked((long)frameWidth * frameHeight);
    }

    private static void AddEncoderOptions(List<string> arguments, string encoder,
        MediaOutputKind kind)
    {
        if (string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", "veryfast", "-tune", "zerolatency"]);
        else if (string.Equals(encoder, "h264_nvenc", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", "p4", "-tune",
                kind == MediaOutputKind.Recording ? "hq" : "ll"]);
        else if (string.Equals(encoder, "h264_amf", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-usage", kind == MediaOutputKind.Recording
                ? "high_quality" : "lowlatency"]);
        else if (string.Equals(encoder, "h264_qsv", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-preset", kind == MediaOutputKind.Recording
                ? "medium" : "veryfast"]);
        else if (string.Equals(encoder, "h264_mf", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-hw_encoding", "1", "-scenario",
                kind == MediaOutputKind.Recording ? "archive" : "live_streaming"]);
    }

    private static string EncoderPixelFormat(string encoder) =>
        string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase)
            ? "yuv420p" : "nv12";

    private static void Validate(MediaOutputRequest request, MediaOutputCapabilities capabilities)
    {
        if (!capabilities.FfmpegAvailable || !capabilities.HasH264Encoder)
            throw new InvalidOperationException(LinuxMediaMessages.Get("FfmpegEncoderUnavailable"));
        if (!capabilities.Supports(request.Kind))
            throw new InvalidOperationException(LinuxMediaMessages.Get("FfmpegProtocolUnavailable"));
        if (request.Width is < 160 or > 3840 || request.Height is < 160 or > 2160 ||
            (request.Width & 1) != 0 || (request.Height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(request), LinuxMediaMessages.Get("MediaOutputInvalidDimensions"));
        if (request.FrameRate is < 10 or > 60 || request.BitrateKbps is < 500 or > 50000)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException(LinuxMediaMessages.Get("MediaOutputDestinationRequired"), nameof(request));
        if (request.Kind == MediaOutputKind.Rtmp && (!capabilities.HasRtmp ||
            !HasScheme(request.Destination, "rtmp", "rtmps")))
            throw new ArgumentException(LinuxMediaMessages.Get("MediaOutputRtmpAddressRequired"));
        if (request.Kind == MediaOutputKind.Srt && (!capabilities.HasSrt ||
            !HasScheme(request.Destination, "srt")))
            throw new ArgumentException(LinuxMediaMessages.Get("MediaOutputSrtAddressRequired"));
        if (request.Kind == MediaOutputKind.Whip && (!capabilities.HasWhip ||
            !HasScheme(request.Destination, "http", "https")))
            throw new ArgumentException(LinuxMediaMessages.Get("MediaOutputWhipAddressRequired"));
    }

    private static bool HasScheme(string value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    internal static string NormalizeWhipToken(string value)
    {
        var token = value.Trim();
        const string prefix = "Bearer";
        if (token.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (token.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            token = token[(prefix.Length + 1)..].Trim();
        return token;
    }

    private static bool HasToken(string text, string token) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(token, StringComparer.OrdinalIgnoreCase);

    private static Task<(Process Process, Task<string> Error)> StartMicrophoneCaptureAsync(
        string fifoPath, CancellationToken cancellationToken)
        => StartPipeWireCaptureAsync(fifoPath, null, captureSinkMonitor: false,
            cancellationToken);

    private static Task<(Process Process, Task<string> Error)> StartPipeWireCaptureAsync(
        string fifoPath, string? target, bool captureSinkMonitor,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("pw-record")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var arguments = new List<string>
        {
            "--raw", "--rate", DefaultAudioSampleRate.ToString(),
            "--channels", DefaultAudioChannels.ToString(), "--format", "s16",
        };
        if (!string.IsNullOrWhiteSpace(target))
        {
            arguments.Add("--target");
            arguments.Add(target);
        }
        if (captureSinkMonitor)
        {
            arguments.Add("--properties");
            arguments.Add("{\"stream.capture.sink\":true}");
        }
        arguments.Add(fifoPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(LinuxMediaMessages.Get("MicrophoneStartFailed"));
            var error = process.StandardError.ReadToEndAsync();
            return Task.FromResult((process, error));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task ObserveMicrophoneStartupAsync(Process process,
        Task<string> errorOutput, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(StartupObservationWindow);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostic = await errorOutput;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(diagnostic)
            ? LinuxMediaMessages.Get("MicrophoneStartFailed") : diagnostic.Trim());
    }

    private static async Task StopMicrophoneCaptureAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private async Task ObserveStartupAsync(Process process,
        CancellationToken cancellationToken)
    {
        using var startupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(StartupObservationWindow);
        try
        {
            await process.WaitForExitAsync(startupCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ThrowStartupExitAsync(process, cancellationToken);
    }

    private async Task ThrowStartupExitAsync(Process process,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        process.WaitForExit();
        await Task.Yield();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(_lastError)
            ? LinuxMediaMessages.Format("FfmpegStartupExitedFormat", process.ExitCode)
            : _lastError);
    }

    private static async Task DisposeFailedStartAsync(Process process)
    {
        try
        {
            try { process.StandardInput.Close(); }
            catch (Exception error)
            {
                LinuxMediaOutputLog.ExceptionOnce("media-failed-stdin-close", "media_output",
                    "failed_start_stdin_close_failed", error);
            }
            await KillProcessAsync(process);
        }
        finally { process.Dispose(); }
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            // Cleanup must not replace the original media-output failure.
            LinuxMediaOutputLog.Exception("media_output", "process_kill_failed", error);
        }
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return true;
            }
        }
        catch (Exception error)
        {
            // The caller already retains any shutdown or pump failure.
            LinuxMediaOutputLog.ExceptionOnce("media-exit-code", "media_output",
                "exit_code_read_failed", error);
        }
        exitCode = 0;
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or
                                      UnauthorizedAccessException)
        {
            LinuxMediaOutputLog.Exception("media_output", "temporary_file_delete_failed",
                error, ("file", Path.GetFileName(path)));
        }
    }

    internal static IReadOnlyList<string> ParseFfmpegLocations(string output)
    {
        var paths = new List<string>();
        foreach (var line in output.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(line) || paths.Contains(line,
                    StringComparer.OrdinalIgnoreCase)) continue;
            paths.Add(line);
        }
        return paths;
    }

    private static async Task<IReadOnlyList<string>> FindFfmpegCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "which",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("ffmpeg");
            process = Process.Start(start);
            if (process is null) return candidates;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask + await errorTask;
            foreach (var path in ParseFfmpegLocations(output))
            {
                if (File.Exists(path) &&
                    !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            LinuxMediaOutputLog.Info("media_output", "ffmpeg_discovery_timeout");
        }
        catch (Exception error)
        {
            TryKillProcess(process);
            LinuxMediaOutputLog.ExceptionOnce("ffmpeg-discovery", "media_output",
                "ffmpeg_discovery_failed", error);
        }
        finally
        {
            process?.Dispose();
        }
        return candidates;
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or
                                      System.ComponentModel.Win32Exception or
                                      NotSupportedException)
        {
            LinuxMediaOutputLog.ExceptionOnce("ffmpeg-discovery-kill", "media_output",
                "ffmpeg_discovery_kill_failed", error);
        }
    }

    private static async Task<string> RunProbeAsync(string path,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = path, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException(LinuxMediaMessages.Get("FfmpegStartFailed"));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout + await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(output.Trim());
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    internal sealed class FrameLatch
    {
        private readonly object _gate = new();
        private byte[]? _producerBuffer;
        private byte[]? _pendingBuffer;
        private byte[]? _consumerBuffer;
        private int _pendingLength;
        private int _consumerLength;
        private bool _hasPending;

        // One producer and one consumer. Three owned buffers let readback
        // replace pending frames while the consumer awaits a slow pipe write.
        internal void Publish(ReadOnlyMemory<byte> frame)
        {
            if (frame.IsEmpty) return;
            if (_producerBuffer is null || _producerBuffer.Length < frame.Length)
                _producerBuffer = new byte[frame.Length];
            frame.Span.CopyTo(_producerBuffer);
            lock (_gate)
            {
                (_producerBuffer, _pendingBuffer) = (_pendingBuffer, _producerBuffer);
                _pendingLength = frame.Length;
                _hasPending = true;
            }
        }

        // Returned bytes remain stable until this consumer calls Get again.
        // PumpVideoAsync finishes all writes before acquiring its next frame.
        internal ReadOnlyMemory<byte> Get()
        {
            lock (_gate)
            {
                if (_hasPending)
                {
                    (_consumerBuffer, _pendingBuffer) = (_pendingBuffer, _consumerBuffer);
                    _consumerLength = _pendingLength;
                    _hasPending = false;
                }
                return _consumerBuffer.AsMemory(0, _consumerLength);
            }
        }
    }
}
