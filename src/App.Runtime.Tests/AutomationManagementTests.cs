using System.IO;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void ExpectAutomationCancelled(Task task)
    {
        try { AwaitMapping(task); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancelled automation operation completed successfully.");
    }

    private static void TestAutomationManagementCancellation(MainWindow window, string root, bool verifyLongStartup)
    {
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var gate = (SemaphoreSlim)KeyboardField(vm, "_coreGate");
        var managementGate = (SemaphoreSlim)KeyboardField(window, "_automationManagementGate");
        var device = DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = "automation-cancel-test", Name = "Test phone", State = ConnectionState.Ready });
        var originalSessions = KeyboardField(vm, "_sessions");
        var originalBindings = KeyboardField(vm, "_reverseBindings");
        var originalResolver = KeyboardField(vm, "_identityResolver");
        var bindings = new DeviceBindingManager(Path.Combine(root, "management-bindings.json"));
        var teardown = new List<string>();
        var sessions = new DeviceSessionManager(_ => teardown.Add("stop"), _ => teardown.Add("destroy"));
        SetKeyboardField(vm, "_sessions", sessions);
        SetKeyboardField(vm, "_reverseBindings", bindings);
        SetKeyboardField(vm, "_identityResolver", new DeviceIdentityResolver(bindings));
        vm.Devices.Add(device); // Background mode suppresses onboarding UI.
        try
        {
            gate.Wait();
            try
            {
                // Each request must cancel and release management admission
                // while the capture gate remains held, including a retry.
                foreach (var action in new[] { "prepare", "window", "prepare", "window" })
                {
                    using var cancellation = new CancellationTokenSource();
                    var pending = (Task<object>)KeyboardCall(window, "ManageAutomationUiAsync",
                        action, AutomationDeviceId.Encode(device.Udid), false, cancellation.Token)!;
                    InteractionAssert(!pending.IsCompleted, "Preparation did not reach the occupied capture gate.");
                    cancellation.Cancel();
                    ExpectAutomationCancelled(pending);
                    InteractionAssert(managementGate.CurrentCount == 1 && gate.CurrentCount == 0,
                        "Cancellation retained management admission or released another caller's capture lock.");
                    InteractionAssert(!sessions.AnySession && teardown.Count == 0,
                        "A cancelled lock waiter created or stopped a session.");
                }

                // A cancelled joiner must not terminate a preview opening
                // that another caller already owns.
                var previews = (MultiDevicePreviewManager)KeyboardField(window, "_secondaryMirrors");
                using var ownerCancellation = new CancellationTokenSource();
                using var joinCancellation = new CancellationTokenSource();
                var owner = previews.ShowAsync(device, ownerCancellation.Token);
                var join = previews.ShowAsync(device, joinCancellation.Token);
                joinCancellation.Cancel();
                ExpectAutomationCancelled(join);
                InteractionAssert(!owner.IsCompleted, "A cancelled joiner stopped another caller's preview opening.");
                ownerCancellation.Cancel();
                ExpectAutomationCancelled(owner);
            }
            finally { gate.Release(); }

            // Cancellation never owns a session that was already capturing.
            using var reusedHandle = new NativeSessionHandle(71, ownsHandle: false);
            sessions.Set(new DeviceCaptureState { Udid = device.Udid, Handle = reusedHandle });
            var reused = vm.StartBackgroundSessionAsync(device);
            AwaitMapping(reused);
            InteractionAssert(reused.Result is { Success: true, Created: false, Handle: 71 }, "Active capture was not reused.");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                ExpectAutomationCancelled(vm.StartBackgroundSessionAsync(device, cancellation.Token));
            }
            InteractionAssert(teardown.Count == 0 && sessions.Get(device.Udid)?.Handle == reusedHandle,
                "Cancelling a request destroyed an existing session.");
            sessions.Remove(device.Udid);

            TestCancelledNativeSessionCreation();

            if (verifyLongStartup)
            {
                var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", device.Udid)!;
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Join a controlled startup without invoking the bridge or USB.
                var startup = control.WiredOperation.RunAsync(_ => release.Task);
                var preparation = vm.StartAutomationControlAsync(device.Udid, false, CancellationToken.None);
                try
                {
                    AdvanceDispatcher(TimeSpan.FromSeconds(46));
                    InteractionAssert(!preparation.IsCompleted && vm.AutomationControlStartup.Contains(device.Udid),
                        "Healthy control preparation was cut off at the former 45-second API timeout.");
                }
                finally { release.TrySetResult(); AwaitMapping(startup); AwaitMapping(preparation); }
                InteractionAssert(!vm.AutomationControlStartup.Contains(device.Udid), "Startup admission was not retired.");
                Console.WriteLine("Automation preparation remains active beyond 45 seconds and completes when its transport does.");
            }
            Console.WriteLine("Automation cancellation: prepare/window lock waiters, retries, reused sessions and native creation cleanup passed.");
        }
        finally
        {
            vm.Devices.Remove(device);
            SetKeyboardField(vm, "_sessions", originalSessions);
            SetKeyboardField(vm, "_reverseBindings", originalBindings);
            SetKeyboardField(vm, "_identityResolver", originalResolver);
        }
    }

    private static void TestCancelledNativeSessionCreation()
    {
        using var handle = new NativeSessionHandle(72, ownsHandle: false);
        var events = new List<string>();
        using var entered = new ManualResetEventSlim();
        using var finishCreate = new ManualResetEventSlim();
        using var cleaning = new ManualResetEventSlim();
        using var finishCleanup = new ManualResetEventSlim();
        var sessions = new DeviceSessionManager(_ =>
        {
            events.Add("stop"); cleaning.Set();
            if (!finishCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test cleanup was not released.");
        }, _ => events.Add("destroy"));
        using var cancellation = new CancellationTokenSource();
        var pending = sessions.CreateAsync(() =>
        {
            entered.Set();
            if (!finishCreate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test creation was not released.");
            return new(true, handle, 0, "");
        }, cancellation.Token);
        try
        {
            InteractionAssert(entered.Wait(TimeSpan.FromSeconds(2)), "Creation worker did not start.");
            cancellation.Cancel();
            InteractionAssert(!pending.IsCompleted, "Cancellation abandoned native creation.");
            finishCreate.Set();
            for (var i = 0; i < 100 && !cleaning.IsSet; i++) AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert(cleaning.IsSet && !pending.IsCompleted, "Cancellation did not wait for native teardown.");
            finishCleanup.Set();
            ExpectAutomationCancelled(pending);
            InteractionAssert(events.SequenceEqual(new[] { "stop", "destroy" }) && !sessions.AnySession,
                "Cancelled creation leaked or published a native handle.");
        }
        finally { finishCreate.Set(); finishCleanup.Set(); }

        var creates = 0;
        ExpectAutomationCancelled(sessions.CreateAsync(() =>
        {
            creates++;
            return new(true, handle, 0, "");
        }, cancellation.Token));
        InteractionAssert(creates == 0 && events.Count == 2, "Pre-cancelled creation touched native resources.");

        var destroyed = false;
        var failing = new DeviceSessionManager(_ => throw new IOException("simulated stop failure"), _ => destroyed = true);
        using var failCancellation = new CancellationTokenSource();
        var failed = failing.CreateAsync(() =>
        {
            failCancellation.Cancel();
            return new(true, handle, 0, "");
        }, failCancellation.Token);
        try { AwaitMapping(failed); throw new InvalidOperationException("Cleanup failure was swallowed."); }
        catch (IOException) { }
        InteractionAssert(destroyed, "Stop failure prevented destruction of the unpublished handle.");
    }
}
