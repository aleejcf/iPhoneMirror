using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.Shared.Networking;

internal static class ReviewFixRegressionTests
{
    internal static async Task RunAsync()
    {
        await TestOutputReadersAsync();
        var root = Path.Combine(Path.GetTempPath(), "iphone-mirror-review-fixes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestDownloadOwnershipAsync(root);
            await TestRecordingCopyAsync(root);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Review fixes passed: parallel native readback, download file ownership and recording save fallback.");
    }

    private static async Task TestOutputReadersAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var block = true;
        int CopyVideo(ulong handle, ref NativeVideoFrameInfo info, byte[]? buffer,
            ref uint size, uint width, uint height)
        {
            Check(handle == 123, "video read lost its captured session");
            Check(size <= (buffer?.Length ?? 0), "native capacity exceeds the actual video array");
            var required = checked(width * height * 3 / 2);
            var capacity = size;
            size = required;
            info.Width = width; info.Height = height; info.Stride = width; info.PixelFormat = 2;
            if (buffer is null || capacity < required) return (int)NativeResult.BufferTooSmall;
            if (width == 320 && block)
            {
                entered.Set();
                Check(resume.Wait(TimeSpan.FromSeconds(5)), "parallel readback did not resume");
            }
            buffer.AsSpan(0, (int)required).Fill(width == 320 ? (byte)0x32 : (byte)0x16);
            return 0;
        }
        int CopyAudio(ulong handle, ulong after, ref NativeAudioPacketInfo info,
            byte[]? buffer, ref uint size)
        {
            Check(handle == 123, "audio read lost its captured session");
            Check(size <= (buffer?.Length ?? 0), "native capacity exceeds the actual audio array");
            var required = after % 2 == 0 ? 32u : 64u;
            var capacity = size;
            size = required;
            info.Sequence = after + 1; info.SampleRate = 48000; info.Channels = 2; info.BitsPerSample = 16;
            if (buffer is null || capacity < required) return (int)NativeResult.BufferTooSmall;
            buffer.AsSpan(0, (int)required).Fill((byte)info.Sequence);
            return 0;
        }
        var recording = new NativeOutputReader(CopyVideo, CopyAudio);
        var streaming = new NativeOutputReader(CopyVideo, CopyAudio);
        using var handle = new NativeSessionHandle(123, ownsHandle: false);
        var pendingRecording = Task.Run(() => recording.GetVideoFrame(handle, 320, 320));
        Nv12VideoFrame? streamFrame = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "recording readback did not enter");
            streamFrame = streaming.GetVideoFrame(handle, 160, 160);
        }
        finally { resume.Set(); }
        var recordFrame = await pendingRecording;
        block = false;
        Check(recordFrame is not null && streamFrame is not null, "parallel readback returned no frame");
        Check(!ReferenceEquals(recordFrame!.Pixels, streamFrame!.Pixels), "outputs share a video buffer");
        Check(streamFrame.Pixels.All(value => value == 0x16), "recording overwrote a retained streaming frame");
        Check(recordFrame.Pixels.All(value => value == 0x32), "streaming overwrote a retained recording frame");
        var latch = new MediaOutputService.FrameLatch();
        latch.Publish(MediaOutputService.GetNv12FramePayload(recordFrame, 320, 320));
        var reused = recording.GetVideoFrame(handle, 320, 320)!;
        Check(ReferenceEquals(recordFrame.Pixels, reused.Pixels), "one output stopped reusing its readback buffer");
        Check(latch.Get().Span[0] == 0x32, "independent frame latch lost its bytes");
        var firstAudio = recording.GetAudioPacket(handle, 0)!;
        _ = streaming.GetAudioPacket(handle, 1);
        _ = recording.GetAudioPacket(handle, 1);
        Check(firstAudio.Sequence == 1 && firstAudio.Pcm.Length == 32 && firstAudio.Pcm.All(value => value == 1),
            "a later audio read overwrote an earlier packet");
        Check(recording.GetVideoFrame(null, 320, 320) is null && recording.GetAudioPacket(null, 0) is null,
            "an absent session reached native readback");
        handle.Dispose();
        Check(recording.GetVideoFrame(handle, 320, 320) is null && recording.GetAudioPacket(handle, 0) is null,
            "a closed session reached native readback");
    }

    private static async Task TestDownloadOwnershipAsync(string root)
    {
        byte[] original = [7, 8, 9];
        foreach (var segmented in new[] { false, true })
        {
            using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                var content = new ByteArrayContent(segmented ? [1] : [1, 2, 3, 4]);
                if (segmented) content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 4);
                return Task.FromResult(new HttpResponseMessage(segmented ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { RequestMessage = request, Content = content });
            }));
            var path = Path.Combine(root, segmented ? "existing-segmented.bin" : "existing-single.bin");
            File.WriteAllBytes(path, original);
            await ThrowsAsync<IOException>(() => SegmentedHttpDownloader.DownloadAsync(client,
                new Uri("https://example.test/file"), path,
                new SegmentedDownloadOptions(1024, MaximumConcurrency: 2, MinimumSegmentBytes: 1), _ => true));
            Check(File.ReadAllBytes(path).SequenceEqual(original), "a failed download deleted or changed a pre-existing file");
        }

        using var incomplete = new HttpClient(new StubHttpMessageHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new UnknownLengthContent([1, 2]) })));
        var partial = Path.Combine(root, "owned-partial.bin");
        await ThrowsAsync<EndOfStreamException>(() => SegmentedHttpDownloader.DownloadAsync(incomplete,
            new Uri("https://example.test/file"), partial, new SegmentedDownloadOptions(1024, ExpectedBytes: 4), _ => true));
        Check(!File.Exists(partial), "failed single-stream download retained its own partial file");

        using var badRanges = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            if (range.From != 0 || range.To != 0) throw new IOException("simulated segment failure");
            var content = new ByteArrayContent([1]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 4);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            { RequestMessage = request, Content = content });
        }));
        var segmentedPartial = Path.Combine(root, "owned-segmented-partial.bin");
        await ThrowsAsync<IOException>(() => SegmentedHttpDownloader.DownloadAsync(badRanges,
            new Uri("https://example.test/file"), segmentedPartial,
            new SegmentedDownloadOptions(1024, MaximumConcurrency: 2, MinimumSegmentBytes: 1), _ => true));
        Check(!File.Exists(segmentedPartial), "failed segmented download retained its own partial file");
    }

    private static async Task TestRecordingCopyAsync(string root)
    {
        byte[] payload = [3, 1, 4, 1, 5, 9];
        var pending = Path.Combine(root, "pending.mp4");
        var saved = Path.Combine(root, "saved.mp4");
        File.WriteAllBytes(pending, payload);
        File.WriteAllText(saved, "old destination");
        await PendingRecordingStore.CopyAndDeleteAsync(pending, saved);
        Check(!File.Exists(pending) && File.ReadAllBytes(saved).SequenceEqual(payload),
            "recording copy did not complete before deleting its source");
        using (File.Open(saved, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.WriteAllBytes(pending, payload);
        using (File.Open(saved, FileMode.Open, FileAccess.Read, FileShare.Read))
            await ThrowsAsync<IOException>(() => PendingRecordingStore.CopyAndDeleteAsync(pending, saved));
        Check(File.ReadAllBytes(pending).SequenceEqual(payload), "failed recording save deleted its source");
        await PendingRecordingStore.CopyAndDeleteAsync(pending, saved);
        Check(!File.Exists(pending), "recording save could not retry after a destination failure");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
