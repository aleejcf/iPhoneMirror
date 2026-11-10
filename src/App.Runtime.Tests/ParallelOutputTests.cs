using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestParallelMediaOutputUi()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext());
        try { TestParallelMediaOutputUiCore(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void TestParallelMediaOutputUiCore()
    {
        using var fixture = new CaptureReviewFixture();
        var vm = (MainViewModel)fixture.Vm;
        fixture.State("parallel-output-test", 811);
        SetKeyboardField(vm, "_selectedDevice", fixture.Device("parallel-output-test"));
        SetKeyboardField(vm, "_pendingRecordingPath", null);
        var original = (MediaOutputCoordinator)KeyboardField(vm, "_mediaOutput");
        var recording = new ParallelOutputSession();
        var streaming = new ParallelOutputSession();
        var outputs = new MediaOutputCoordinator(recording, streaming);
        SetKeyboardField(vm, "_mediaOutput", outputs);
        SetKeyboardField(vm, "_mediaOutputCapabilitiesLoaded", true);
        SetKeyboardField(vm, "_mediaOutputCapabilities", new MediaOutputCapabilities(
            true, true, true, true, true, true, true, "libx264", "unused", ""));
        outputs.StatusChanged += (status, failed) => KeyboardCall(vm, "OnMediaOutputStatusChanged", status, failed);
        var window = new MediaOutputSettingsWindow(vm) { ShowInTaskbar = false, ShowActivated = false };
        // Load the real controls, but don't probe external components or
        // enumerate hardware: this fixture supplies output capabilities.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            window.GetType().GetMethod("OnLoaded", KeyboardTestMembers |
                System.Reflection.BindingFlags.DeclaredOnly)!);
        Task<(bool Success, string Message)> Start(MediaOutputKind kind) =>
            (Task<(bool Success, string Message)>)KeyboardCall(vm, "StartMediaOutputAsync",
                new MediaOutputRequest(kind, "unused", 160, 160, 20, 500))!;
        try
        {
            window.Show();
            window.UpdateLayout();
            var start = Start(MediaOutputKind.Rtmp);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success && vm.CanStartRecording && !vm.CanStartStreaming,
                "A live stream must leave the recording slot available.");
            KeyboardCall(window, "UpdateStartButtons");
            InteractionAssert(((Button)window.FindName("StartRecordingButton")).IsEnabled,
                "The recording button remained disabled while streaming.");
            start = Start(MediaOutputKind.Recording);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success && vm.IsRecordingMediaOutput &&
                vm.CanStopRecording && vm.CanStopStreaming && !vm.CanStartRecording && !vm.CanStartStreaming,
                "The VM did not retain independent live/recording state.");
            KeyboardCall(vm, "OnMediaOutputStatusChanged", "Stopped", false);
            InteractionAssert(vm.MediaOutputStatus == Localization.LocalizationService.Get("MediaOutputRecordingAndStreaming"),
                "A stale stopped event hid the active parallel outputs.");
            InteractionAssert(!vm.CanUseVirtualCamera, "Virtual camera must remain exclusive.");
            WaitReviewTask(vm.StopRecordingAsync());
            InteractionAssert(vm.IsMediaOutputRunning && !vm.IsRecordingMediaOutput &&
                vm.CanStopStreaming && !vm.CanStopRecording && vm.CanStartRecording,
                "Stopping recording stopped or hid the remaining stream.");
            // Remaining checks use the VM. Unsubscribe window save prompts
            // before FindLatest can observe unrelated user pending files.
            window.Close();
            var pending = Path.Combine(Path.GetTempPath(), "parallel-output-pending-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                File.WriteAllText(pending, "completed recording fixture");
                SetKeyboardField(vm, "_pendingRecordingPath", pending);
                InteractionAssert(vm.DiscardPendingRecording() && !File.Exists(pending) && streaming.IsRunning,
                    "A completed recording could not be discarded while streaming continued.");
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            SetKeyboardField(vm, "_pendingRecordingPath", null);
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("different-source"));
            fixture.State("different-source", 812);
            InteractionAssert(!vm.CanStartRecording, "A different selected device must not join an existing stream.");
            SetKeyboardField(vm, "_selectedDevice", fixture.Device("parallel-output-test"));
            start = Start(MediaOutputKind.Recording);
            WaitReviewTask(start);
            InteractionAssert(start.Result.Success, "The recording slot did not restart.");
            WaitReviewTask(vm.StopStreamingAsync());
            InteractionAssert(vm.IsRecordingMediaOutput && !vm.CanStopStreaming,
                "Stopping streaming stopped recording.");
            WaitReviewTask(vm.StopMediaOutputAsync());
            InteractionAssert(!vm.IsMediaOutputRunning, "Stop-all left an output running.");
            Console.WriteLine("Parallel output VM/UI passed: buttons, independent stops, source identity, exclusive virtual camera and source isolation.");
        }
        finally
        {
            if (window.IsLoaded) window.Close();
            outputs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SetKeyboardField(vm, "_mediaOutput", original);
            SetKeyboardField(vm, "_mediaOutputUdid", null);
        }
    }

    private sealed class ParallelOutputSession : IMediaOutputSession
    {
        public bool IsRunning { get; private set; }
        public ulong SessionHandle { get; private set; }
        public event Action<string, bool>? StatusChanged;
        public Task StartAsync(ulong handle, MediaOutputRequest request, MediaOutputCapabilities capabilities,
            CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            SessionHandle = handle;
            StatusChanged?.Invoke("started", false);
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            IsRunning = false;
            SessionHandle = 0;
            StatusChanged?.Invoke("Stopped", false);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
