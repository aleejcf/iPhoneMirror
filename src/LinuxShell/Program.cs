// SPDX-License-Identifier: GPL-3.0-only
//
// Linux shell entry point for file playback, Apple USB capture, HID diagnostics,
// and the Android V4L2 preview window.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using IPhoneMirror.LinuxShell.Controls;

namespace IPhoneMirror.LinuxShell;

internal static class Program
{
    private static int _exitCode = 70;

    private static int Main(string[] arguments)
    {
        try
        {
            return Run(arguments);
        }
        catch (Exception exception)
        {
            // Keep launch failures in the captured stderr/journal instead of
            // allowing the desktop crash reporter to interrupt the user.
            Console.Error.WriteLine($"iPhoneMirror Linux: {exception}");
            return 70;
        }
    }

    private static int Run(string[] arguments)
    {
        if (arguments.Length >= 1 && arguments[0] == "--hid-probe")
            return RunHidProbe(arguments.Length >= 2 ? arguments[1] : "iPhoneMirror", 8);
        if (arguments.Length >= 1 && arguments[0] == "--hid-test")
        {
            return RunHidTest(arguments.Length >= 2 &&
                int.TryParse(arguments[1], out var duration) ? duration : 20);
        }
        if (arguments.Length >= 1 && arguments[0] == "--hid-serve")
        {
            var seconds = arguments.Length >= 3 && int.TryParse(arguments[2], out var s)
                ? s : 240;
            return RunHidProbe(
                arguments.Length >= 2 ? arguments[1] : "iPhoneMirror Linux", seconds);
        }
        if (arguments.Length >= 2 && arguments[0] == "--device")
        {
            return RunDevice(arguments[1], arguments[2..]);
        }
        if (arguments.Length is 3 or 4 && arguments[0] == "--v4l2-preview")
            return RunV4l2Preview(arguments[1], arguments[2],
                arguments.Length == 4 ? arguments[3] : null);
        if (arguments.Length < 3)
        {
            Console.Error.WriteLine(
                "usage: iPhoneMirror.LinuxShell <frames.nv12> <width> <height> "
                + "[--frames N]\n"
                + "       iPhoneMirror.LinuxShell --device <udid> "
                + "[<width> <height>]\n"
                + "       iPhoneMirror.LinuxShell --v4l2-preview </dev/videoN> "
                + "<vertical|horizontal> [android-serial]\n"
                + "       iPhoneMirror.LinuxShell --hid-probe [<name>]");
            return 64;
        }
        var path = arguments[0];
        if (!uint.TryParse(arguments[1], out var width) ||
            !uint.TryParse(arguments[2], out var height) ||
            width == 0 || height == 0)
        {
            Console.Error.WriteLine("the geometry must be positive integers");
            return 64;
        }
        var budget = 0;
        for (var index = 3; index < arguments.Length - 1; ++index)
        {
            if (arguments[index] == "--frames")
                _ = int.TryParse(arguments[index + 1], out budget);
        }

        // Vulkan: the exported image can only be imported by a Vulkan-backed
        // compositor, and the spike measured this as the one working combination.
        AppBuilder.Configure<ShellApp>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                // These windows are video-only. X11's optional D-Bus IME
                // registration can fail in restricted user sessions and is
                // not needed for preview input.
                EnableIme = false,
                RenderingMode = new[] { X11RenderingMode.Vulkan },
            })
            .AfterSetup(_ => ShellApp.Configure(path, width, height, budget, null,
                code => _exitCode = code))
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _exitCode;
    }

    // The preview target defaults to the highest encoder tier verified on both
    // test devices; the device decides the source geometry and Core letterboxes
    // into whatever target it is given.
    private static int RunDevice(string serial, string[] options)
    {
        uint width = 1206;
        uint height = 2622;
        var playAudio = !options.Contains("--no-audio", StringComparer.Ordinal);
        var videoSink = options.FirstOrDefault(option =>
            option.StartsWith("--v4l2-sink=", StringComparison.Ordinal))?
            ["--v4l2-sink=".Length..];
        var outputProfile = options.FirstOrDefault(option =>
            option.StartsWith("--output-profile=", StringComparison.Ordinal))?
            ["--output-profile=".Length..] ?? "vertical";
        if (outputProfile is not ("vertical" or "horizontal"))
        {
            Console.Error.WriteLine("invalid OBS output profile");
            return 64;
        }
        var projectionModeText = options.FirstOrDefault(option =>
            option.StartsWith("--projection-mode=", StringComparison.Ordinal))?
            ["--projection-mode=".Length..] ?? "0";
        if (!uint.TryParse(projectionModeText, out var projectionMode) ||
            projectionMode > 2)
        {
            Console.Error.WriteLine("invalid USB projection mode");
            return 64;
        }
        var recordingPath = options.FirstOrDefault(option =>
            option.StartsWith("--record=", StringComparison.Ordinal))?
            ["--record=".Length..];
        var microphoneEnabled = options.Contains("--microphone", StringComparer.Ordinal);
        var geometry = options.Where(option => option != "--no-audio" &&
            !option.StartsWith("--v4l2-sink=", StringComparison.Ordinal) &&
            !option.StartsWith("--output-profile=", StringComparison.Ordinal) &&
            !option.StartsWith("--projection-mode=", StringComparison.Ordinal) &&
            !option.StartsWith("--record=", StringComparison.Ordinal) &&
            option != "--microphone").ToArray();
        if (geometry.Length >= 2)
        {
            _ = uint.TryParse(geometry[0], out width);
            _ = uint.TryParse(geometry[1], out height);
        }
        AppBuilder.Configure<ShellApp>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                EnableIme = false,
                RenderingMode = new[] { X11RenderingMode.Vulkan },
            })
            .AfterSetup(_ => ShellApp.Configure(string.Empty, width, height, 0,
                serial, code => _exitCode = code, playAudio, videoSink,
                outputProfile, projectionMode, recordingPath, microphoneEnabled))
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _exitCode;
    }

    private static int RunV4l2Preview(string device, string profile,
        string? serial)
    {
        if (!device.StartsWith("/dev/video", StringComparison.Ordinal) ||
            profile is not ("vertical" or "horizontal") ||
            serial is { Length: 0 } ||
            serial?.Any(char.IsWhiteSpace) == true)
        {
            Console.Error.WriteLine("invalid V4L2 preview source or profile");
            return 64;
        }
        var (width, height) = profile == "horizontal"
            ? (1920U, 1080U) : (1080U, 1920U);
        AppBuilder.Configure<ShellApp>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                EnableIme = false,
                RenderingMode = new[] { X11RenderingMode.Vulkan },
            })
            .AfterSetup(_ => ShellApp.ConfigureV4l2Preview(device, width, height,
                serial, code => _exitCode = code))
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _exitCode;
    }

    // Registers the HID peripheral through BlueZ and reports what happened, then
    // tears it down. No window and no iPad: this checks our own D-Bus objects,
    // which is a different thing from the earlier Python probes checking whether
    // BlueZ would accept any such application at all.
    private static int RunHidProbe(string name, int seconds)
    {
        return Task.Run(async () =>
        {
            await using var service = new Services.BluezHidService();
            service.ReportSubscriptionChanged += (report, subscribed) =>
                Console.WriteLine(
                    $"report {report}: {(subscribed ? "subscribed" : "unsubscribed")}"
                    + " — iOS has attached to the HID service");
            var started = await service.StartAsync(name);
            Console.WriteLine($"bluez hid register : {(started ? "ok" : "failed")}");
            Console.WriteLine($"diagnostic         : {service.Diagnostic}");
            if (!started) return 1;
            Console.WriteLine($"holding {seconds}s — pair from the device now");
            for (var elapsed = 0; elapsed < seconds; elapsed += 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                Console.WriteLine($"  t={elapsed + 5}s subscribed={service.AnySubscribed}");
            }
            Console.WriteLine("tearing down");
            return service.AnySubscribed ? 0 : 2;
        }).GetAwaiter().GetResult();
    }

    // Sends actual mouse movement once iOS has subscribed. This is the payoff
    // check: pairing and subscription only prove iOS accepted the descriptor,
    // not that a report moves anything.
    //
    // HOGP puts the report ID in the Report Reference descriptor, not in the
    // value, so the payload is the report body alone: buttons, then X and Y as
    // 16-bit signed, then the wheel.
    private static int RunHidTest(int seconds)
    {
        return Task.Run(async () =>
        {
            await using var service = new Services.BluezHidService();
            if (!await service.StartAsync("iPhoneMirror Linux"))
            {
                Console.WriteLine($"start failed: {service.Diagnostic}");
                return 1;
            }
            Console.WriteLine($"diagnostic : {service.Diagnostic}");
            for (var wait = 0; wait < 60 && !service.AnySubscribed; ++wait)
                await Task.Delay(TimeSpan.FromSeconds(1));
            if (!service.AnySubscribed)
            {
                Console.WriteLine("no subscriber; connect the device and retry");
                return 2;
            }
            Console.WriteLine("subscribed — moving the pointer in a square");

            var sent = 0;
            // Allocated once: CA2014 rightly objects to stackalloc in a loop, and
            // a six-byte report does not need a fresh buffer per tick anyway.
            var report = new byte[6];
            // Big steps and a long sweep per side: a small square finished in
            // twenty seconds, which was too short to catch on the device.
            var steps = new (short X, short Y)[]
            {
                (60, 0), (0, 60), (-60, 0), (0, -60),
            };
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var lap = 0;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var step in steps)
                {
                    for (var repeat = 0; repeat < 12; ++repeat)
                    {
                        report[0] = 0;
                        BitConverter.TryWriteBytes(report.AsSpan(1, 2), step.X);
                        BitConverter.TryWriteBytes(report.AsSpan(3, 2), step.Y);
                        report[5] = 0;
                        if (service.SendReport(
                                Services.HidReportMap.MouseReportId, report))
                            ++sent;
                        await Task.Delay(20);
                    }
                }
                if (++lap % 10 == 0)
                    Console.WriteLine($"  lap {lap}, {sent} reports sent");
            }
            Console.WriteLine($"done: {sent} mouse reports sent over {seconds}s");
            return sent > 0 ? 0 : 3;
        }).GetAwaiter().GetResult();
    }
}
