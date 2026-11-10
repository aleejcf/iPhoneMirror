using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal interface IMediaOutputSession : IAsyncDisposable
{
    bool IsRunning { get; }
    ulong SessionHandle { get; }
    event Action<string, bool>? StatusChanged;
    Task StartAsync(ulong sessionHandle, MediaOutputRequest request,
        MediaOutputCapabilities capabilities, CancellationToken cancellationToken = default);
    Task StopAsync();
}

// One recording and one stream may share a source, with independent encoders,
// audio cursors and cancellation. Virtual camera exclusivity stays with the VM.
internal sealed class MediaOutputCoordinator : IAsyncDisposable
{
    private readonly IMediaOutputSession _recording;
    private readonly IMediaOutputSession _streaming;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal MediaOutputCoordinator(IMediaOutputSession recording, IMediaOutputSession streaming)
    {
        _recording = recording;
        _streaming = streaming;
        _recording.StatusChanged += OnStatusChanged;
        _streaming.StatusChanged += OnStatusChanged;
    }

    internal bool IsRecording => _recording.IsRunning;
    internal bool IsStreaming => _streaming.IsRunning;
    internal bool IsRunning => IsRecording || IsStreaming;
    internal event Action<string, bool>? StatusChanged;

    internal bool CanStart(MediaOutputKind kind, ulong sessionHandle)
    {
        var target = kind == MediaOutputKind.Recording ? _recording : _streaming;
        var other = kind == MediaOutputKind.Recording ? _streaming : _recording;
        return !_disposed && sessionHandle != 0 && !target.IsRunning &&
            (!other.IsRunning || other.SessionHandle == sessionHandle);
    }

    internal async Task StartAsync(ulong sessionHandle, MediaOutputRequest request,
        MediaOutputCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanStart(request.Kind, sessionHandle))
                throw new InvalidOperationException(LocalizationService.Get("MediaOutputAlreadyRunning"));
            await (request.Kind == MediaOutputKind.Recording ? _recording : _streaming)
                .StartAsync(sessionHandle, request, capabilities, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task StopAsync(MediaOutputKind? kind = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            // Always attempt both on stop-all, including if one finalizer fails.
            try
            {
                if (kind is null or MediaOutputKind.Recording) await _recording.StopAsync();
            }
            finally
            {
                if (kind is not MediaOutputKind.Recording) await _streaming.StopAsync();
            }
        }
        finally { _gate.Release(); }
    }

    private void OnStatusChanged(string message, bool failed)
    {
        if (!failed)
            message = (IsRecording, IsStreaming) switch
            {
                (true, true) => "RecordingAndLive",
                (true, false) => "Recording",
                (false, true) => "Live",
                _ => "Stopped",
            };
        StatusChanged?.Invoke(message, failed);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _recording.StatusChanged -= OnStatusChanged;
            _streaming.StatusChanged -= OnStatusChanged;
            try { await _recording.DisposeAsync(); }
            finally { await _streaming.DisposeAsync(); }
        }
        finally
        {
            // Waiters must resume and observe disposal instead of being left
            // waiting on a disposed semaphore. It has no native wait handle.
            _gate.Release();
        }
    }
}
