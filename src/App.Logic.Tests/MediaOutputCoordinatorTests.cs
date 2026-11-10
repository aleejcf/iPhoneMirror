using System.IO;
using IPhoneMirror.App.Services;

internal static class MediaOutputCoordinatorTests
{
    internal static async Task RunAsync()
    {
        var recording = new Session();
        var streaming = new Session();
        await using var outputs = new MediaOutputCoordinator(recording, streaming);
        var states = new List<string>();
        outputs.StatusChanged += (state, _) => states.Add(state);
        var caps = new MediaOutputCapabilities(true, true, true, true, true, true, true,
            "libx264", "unused", "");
        MediaOutputRequest Request(MediaOutputKind kind) => new(kind, "unused", 160, 160, 20, 500);
        await outputs.StartAsync(11, Request(MediaOutputKind.Recording), caps);
        Check(!outputs.CanStart(MediaOutputKind.Recording, 11), "duplicate recording admitted");
        Check(!outputs.CanStart(MediaOutputKind.Rtmp, 22), "second source admitted");
        foreach (var kind in new[] { MediaOutputKind.Rtmp, MediaOutputKind.Srt, MediaOutputKind.Whip })
        {
            await outputs.StartAsync(11, Request(kind), caps);
            Check(outputs.IsRecording && outputs.IsStreaming, "parallel output missing");
            Check(states[^1] == "RecordingAndLive", "parallel status missing");
            Check(!outputs.CanStart(MediaOutputKind.Srt, 11), "duplicate streaming admitted");
            await outputs.StopAsync(kind);
            Check(recording.IsRunning && !streaming.IsRunning, "stopping stream stopped recording");
            Check(states[^1] == "Recording", "remaining recording lost from status");
        }
        await outputs.StartAsync(11, Request(MediaOutputKind.Rtmp), caps);
        await outputs.StopAsync(MediaOutputKind.Recording);
        Check(streaming.IsRunning && !recording.IsRunning, "stopping recording stopped stream");
        Check(states[^1] == "Live", "remaining stream lost from status");

        // Failed or cancelled starts must not cancel the existing output.
        recording.StartFailure = new IOException("disk unavailable");
        try { await outputs.StartAsync(11, Request(MediaOutputKind.Recording), caps); }
        catch (IOException) { }
        Check(streaming.IsRunning && !recording.IsRunning, "failed recording damaged stream");
        recording.StartFailure = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await outputs.StartAsync(11, Request(MediaOutputKind.Recording), caps, cancelled.Token); }
            catch (OperationCanceledException) { }
        }
        Check(streaming.IsRunning && !recording.IsRunning, "cancelled start damaged stream");
        await outputs.StartAsync(11, Request(MediaOutputKind.Recording), caps);
        recording.Fail("disk full");
        Check(streaming.IsRunning && !outputs.IsRecording, "recording failure cancelled stream");
        await outputs.StartAsync(11, Request(MediaOutputKind.Recording), caps);
        recording.StopFailure = new IOException("finalization failed");
        try { await outputs.StopAsync(); }
        catch (IOException) { }
        Check(!streaming.IsRunning && !recording.IsRunning, "stop-all skipped a finalizer");
        recording.StopFailure = null;
        Check(outputs.CanStart(MediaOutputKind.Recording, 22), "stopped source could not change");

        // Two concurrent requests for the same slot must serialize, not spawn
        // two encoders. The second checks state after the first start commits.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.StartBarrier = async () => { entered.SetResult(); await release.Task; };
        var first = outputs.StartAsync(22, Request(MediaOutputKind.Recording), caps);
        await entered.Task;
        var second = outputs.StartAsync(22, Request(MediaOutputKind.Recording), caps);
        release.SetResult();
        await first;
        var rejected = false;
        try { await second; }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "concurrent duplicate request was not rejected");
        await outputs.DisposeAsync();
        Check(!outputs.IsRunning && !outputs.CanStart(MediaOutputKind.Recording, 22),
            "disposed coordinator retained or admitted output");
        rejected = false;
        try { await outputs.StartAsync(22, Request(MediaOutputKind.Recording), caps); }
        catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "disposed coordinator restarted an encoder");
        Console.WriteLine("Parallel outputs passed: RTMP/SRT/WHIP slots, independent stop/failure, source identity, cancellation and concurrent starts.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class Session : IMediaOutputSession
    {
        public bool IsRunning { get; private set; }
        public ulong SessionHandle { get; private set; }
        public event Action<string, bool>? StatusChanged;
        internal Exception? StartFailure, StopFailure;
        internal Func<Task>? StartBarrier;
        public async Task StartAsync(ulong handle, MediaOutputRequest request,
            MediaOutputCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartFailure is not null) throw StartFailure;
            if (StartBarrier is not null) await StartBarrier();
            SessionHandle = handle;
            IsRunning = true;
            StatusChanged?.Invoke("started", false);
        }
        public Task StopAsync()
        {
            IsRunning = false;
            SessionHandle = 0;
            StatusChanged?.Invoke("Stopped", false);
            if (StopFailure is not null) throw StopFailure;
            return Task.CompletedTask;
        }
        internal void Fail(string message)
        {
            IsRunning = false;
            SessionHandle = 0;
            StatusChanged?.Invoke(message, true);
        }
        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}
