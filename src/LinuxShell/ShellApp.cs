// SPDX-License-Identifier: GPL-3.0-only
//
// The shell application and its single window. Small on purpose: everything that
// matters happens in the preview control and in Core.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using IPhoneMirror.LinuxShell.Controls;
using IPhoneMirror.LinuxShell.Services;
using System.Runtime.InteropServices;

namespace IPhoneMirror.LinuxShell;

internal sealed class ShellApp : Application
{
    private bool _startFailed;
    private static string _path = string.Empty;
    private static uint _width;
    private static uint _height;
    private static int _frameBudget;
    private static string? _serial;
    private static bool _playAudio = true;
    private static string? _videoSink;
    private static string _outputProfile = "vertical";
    private static uint _projectionMode;
    private static string? _recordingPath;
    private static bool _microphoneEnabled;
    private static string? _v4l2PreviewDevice;
    private static string? _v4l2PreviewSerial;
    private static Action<int> _report = _ => { };

    internal static void Configure(string path, uint width, uint height,
        int frameBudget, string? serial, Action<int> report,
        bool playAudio = true, string? videoSink = null,
        string outputProfile = "vertical", uint projectionMode = 0,
        string? recordingPath = null, bool microphoneEnabled = false)
    {
        _path = path;
        _width = width;
        _height = height;
        _frameBudget = frameBudget;
        _serial = serial;
        _playAudio = playAudio;
        _videoSink = videoSink;
        _outputProfile = outputProfile;
        _projectionMode = projectionMode;
        _recordingPath = recordingPath;
        _microphoneEnabled = microphoneEnabled;
        _report = report;
    }

    internal static void ConfigureV4l2Preview(string device, uint width,
        uint height, string? serial, Action<int> report)
    {
        _path = string.Empty;
        _width = width;
        _height = height;
        _frameBudget = 0;
        _serial = null;
        _v4l2PreviewDevice = device;
        _v4l2PreviewSerial = serial;
        _report = report;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            _report(70);
            return;
        }
        var preview = new PreviewSurfaceControl
        {
            TargetWidth = _width,
            TargetHeight = _height,
            Focusable = true,
        };
        preview.PointerPressed += (_, _) => preview.Focus();
        // Keep diagnostics in memory/logs only; this window is a clean OBS source.
        var status = new TextBlock { IsVisible = false };
        preview.InitializationFailed += diagnostic =>
        {
            _startFailed = true;
            Console.Error.WriteLine($"preview initialization failed: {diagnostic}");
            _report(1);
            Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
        };

        desktop.MainWindow = new Window
        {
            Title = "iPhoneMirror",
            Width = 1120,
            Height = 820,
            MinWidth = 320,
            MinHeight = 180,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Black,
            Content = preview,
        };
        PosixSignalRegistration? terminationSignal = null;
        if (!OperatingSystem.IsWindows())
        {
            terminationSignal = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => desktop.MainWindow?.Close());
                });
        }
        var isFullScreen = false;
        preview.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11)
            {
                isFullScreen = !isFullScreen;
                desktop.MainWindow.WindowState = isFullScreen
                    ? WindowState.FullScreen : WindowState.Normal;
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (isFullScreen)
                {
                    isFullScreen = false;
                    desktop.MainWindow.WindowState = WindowState.Normal;
                }
                else
                {
                    desktop.MainWindow.Close();
                }
                e.Handled = true;
            }
        };
        if (_v4l2PreviewDevice is not null)
        {
            AndroidTouchInput? androidTouch = null;
            if (!string.IsNullOrWhiteSpace(_v4l2PreviewSerial))
            {
                var touchInput = new AndroidTouchInput(_v4l2PreviewSerial,
                    _width, _height);
                androidTouch = touchInput;
                preview.ForwardPointerGestures = true;
                preview.PointerGestureCompleted += (start, end, duration) =>
                    _ = Task.Run(() => touchInput.SendGestureAsync(start, end, duration));
                touchInput.Start();
            }
            var player = new V4l2PreviewPlayer(_v4l2PreviewDevice, _width, _height);
            preview.Ready += (_, _) => player.Start(preview, status, code =>
            {
                if (code != 0) _startFailed = true;
                _report(code);
                if (code != 0)
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(code));
            });
            desktop.MainWindow.Closed += (_, _) =>
            {
                terminationSignal?.Dispose();
                androidTouch?.Dispose();
                player.Stop();
                if (!_startFailed) _report(0);
            };
        }
        else if (_serial is null)
        {
            var player = new FramePlayer(_path, _width, _height, _frameBudget);
            preview.Ready += (_, _) => player.Start(preview, status, code =>
            {
                if (code != 0) _startFailed = true;
                _report(code);
            });
            desktop.MainWindow.Closed += (_, _) =>
            {
                terminationSignal?.Dispose();
                player.Stop();
                if (!_startFailed) _report(0);
            };
        }
        else
        {
            var player = new DevicePlayer(_serial, _playAudio, _videoSink,
                _outputProfile, _projectionMode, _recordingPath,
                _microphoneEnabled);
            Services.HidInputBridge? reverseControlBridge = null;
            player.OutputError += diagnostic =>
                Console.Error.WriteLine($"V4L2 output failed: {diagnostic}");
            player.IosShortcutRequested += action =>
            {
                if (reverseControlBridge is null) return;
                _ = Task.Run(() => reverseControlBridge.SendIosShortcutAsync(action));
            };
            preview.Ready += (_, _) =>
            {
                if (!player.Start(preview, status, _report))
                {
                    _startFailed = true;
                    Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                }
            };
            desktop.MainWindow.Closed += (_, _) =>
            {
                terminationSignal?.Dispose();
                player.Stop();
                if (!_startFailed) _report(0);
            };
            preview.KeyDown += (_, e) =>
            {
                if (e.Key != Key.S) return;
                e.Handled = true;
                player.SaveScreenshot();
            };
            // Reverse control remains opt-in while BlueZ/HID compatibility is
            // validated across adapters and iOS versions. D-Bus work and report
            // transmission run off the UI thread to keep frame presentation live.
            if (Environment.GetEnvironmentVariable("IPHONEMIRROR_REVERSE_CONTROL")
                == "1")
            {
                reverseControlBridge = AttachReverseControl(desktop.MainWindow, preview);
            }
            else
            {
                Console.WriteLine("reverse control : off "
                    + "(set IPHONEMIRROR_REVERSE_CONTROL=1 to enable)");
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Reverse control is attached only in device mode: pointing at a file would
    // move the real iPad while the window showed something unrelated, which is a
    // good way to tap things by accident.
    private static Services.HidInputBridge AttachReverseControl(Window window,
        Control preview)
    {
        var hid = new Services.BluezHidService();
        var bridge = new Services.HidInputBridge(hid)
        {
            DisplayedWidth = _width,
            DisplayedHeight = _height,
        };
        hid.ReportSubscriptionChanged += (_, _) =>
            Console.Error.WriteLine($"REVERSE_CONTROL_STATUS:{(hid.AnySubscribed ? "connected" : "disconnected")}");
        // Task.Run, not a bare call: Tmds.DBus captures the synchronization
        // context in effect when the connection is made, and starting this from
        // the UI thread made every incoming BlueZ call — every characteristic
        // read, every notification bookkeeping step — run on the UI thread. That
        // starved the timer presenting frames, so the picture froze a few frames
        // in even with no mouse input at all. A thread-pool thread has no
        // synchronization context, so the D-Bus work stays off the UI thread.
        _ = Task.Run(async () =>
        {
            var ready = await hid.StartAsync("iPhoneMirror Linux");
            Console.Error.WriteLine($"REVERSE_CONTROL_STATUS:{(ready ? "ready" : "failed")}");
            if (!ready)
                Console.Error.WriteLine($"reverse control registration failed: {hid.Diagnostic}");
        });

        preview.PointerMoved += (_, e) => bridge.PointerMoved(e.GetPosition(preview));
        preview.PointerExited += (_, _) => bridge.PointerLost();
        preview.PointerPressed += (_, e) =>
        {
            preview.Focus();
            bridge.ButtonChanged(ButtonIndex(e.GetCurrentPoint(preview)), true);
        };
        preview.PointerReleased += (_, e) =>
            bridge.ButtonChanged(ButtonIndex(e.GetCurrentPoint(preview)), false);
        preview.PointerWheelChanged += (_, e) => bridge.Wheel(e.Delta.Y);
        window.KeyDown += (_, e) => bridge.KeyChanged(e.Key, true);
        window.KeyUp += (_, e) => bridge.KeyChanged(e.Key, false);
        window.Deactivated += (_, _) => bridge.ReleaseAll();
        window.Closed += (_, _) =>
        {
            bridge.Dispose();
            _ = hid.DisposeAsync();
        };
        return bridge;
    }

    private static int ButtonIndex(PointerPoint point) =>
        point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed or
                PointerUpdateKind.RightButtonReleased => 1,
            PointerUpdateKind.MiddleButtonPressed or
                PointerUpdateKind.MiddleButtonReleased => 2,
            _ => 0,
        };
}
