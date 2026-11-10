using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IPhoneMirror.DriverInstaller.Services;

internal static class AppleMuxSupportTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        const string emptyList = "<plist><dict><key>DeviceList</key><array/></dict></plist>";
        var empty = await ReplyAsync(emptyList);
        Check(empty.Available && empty.PortOccupied, "empty device lists are usable USBMux support");
        var populated = await ReplyAsync("<plist><dict><key>DeviceList</key><array><dict>" +
            "<key>DeviceID</key><integer>1</integer></dict></array></dict></plist>", fragment: true);
        Check(populated.Available, "fragmented USBMux reply with a device");
        foreach (var payload in new[] { "HTTP/1.1 200 OK", "<plist><dict/></plist>",
            "<plist><dict><key>DeviceList</key><string>wrong</string></dict></plist>",
            "<plist><dict><key>Number</key><integer>0</integer></dict></plist>",
            "<plist><dict><key>DeviceList</key><array><string>wrong</string></array></dict></plist>" })
        {
            var invalid = await ReplyAsync(payload);
            Check(!invalid.Available && invalid.PortOccupied, "unrelated listeners are not Apple support");
        }
        foreach (var field in new[] { 0, 4, 8, 12 })
        {
            var invalid = await ReplyAsync(emptyList, invalidHeaderField: field);
            Check(!invalid.Available && invalid.PortOccupied, "invalid packet header rejected");
        }
        var stalled = await ReplyAsync(emptyList, stall: true);
        Check(!stalled.Available && stalled.PortOccupied, "silent listener reaches total deadline");
        using (var closed = new TcpListener(IPAddress.Loopback, 0))
        {
            closed.Start();
            var port = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            var absent = await AppleMuxSupportProbe.ProbeAsync(port, TimeSpan.FromSeconds(1));
            Check(!absent.Available && !absent.PortOccupied, "closed port is not a conflict");
        }

        foreach (var installed in new[] { false, true })
        {
            var store = DeviceCatalog.DescribeAppleSupport(installed, false,
                installed ? "Apple Mobile Device Service" : null, true, "appleusb.inf", empty);
            Check(store.Ready, "store mux works with missing/stopped desktop service");
            Check(!AppleSupportInstaller.ShouldRecoverExistingService(store), "do not start competing service");
            Check(!AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(store), "do not reinstall working support");
            Check(!(store with { UsbDriverInstalled = false }).Ready, "mux never replaces the USB driver");
        }
        var conflict = DeviceCatalog.DescribeAppleSupport(true, false,
            "Apple Mobile Device Service", true, "appleusb.inf",
            new(false, true, "AppleMobileDeviceProcess.exe (PID 42)"));
        Check(conflict.HasMuxConflict && !conflict.Ready, "busy but unresponsive mux is diagnosed");
        Check(!AppleSupportInstaller.ShouldRecoverExistingService(conflict), "port conflict prevents service restart");
        Check(!AppleSupportInstaller.ShouldInstallAppleDevicesFromStore(conflict), "port conflict prevents reinstall");
        Check(!(conflict with { ServiceRunning = true }).Ready, "running service cannot mask a port conflict");
        var legacy = DeviceCatalog.DescribeAppleSupport(true, true,
            "Apple Mobile Device Service", true, "usbaapl64.inf", new(false, false));
        Check(legacy.Ready, "preserve legacy service readiness before a listener appears");
    }

    private static async Task<AppleMuxSupport> ReplyAsync(string xml, bool fragment = false,
        int invalidHeaderField = -1, bool stall = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owner = AppleMuxSupportProbe.ReadListener(port);
        Check(owner?.Contains($"PID {Environment.ProcessId}") == true, "listener PID is resolved without elevation");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = peer.GetStream();
            var header = new byte[16];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            Check(length is > 16 and < 4096, "bounded probe request");
            var body = new byte[length - 16];
            await stream.ReadExactlyAsync(body, deadline.Token);
            Check(Encoding.UTF8.GetString(body).Contains("<string>ListDevices</string>"), "probe is read-only ListDevices");
            if (stall)
            {
                // Wait for the client's deadline to close the socket; no sleep.
                var eof = new byte[1];
                Check(await stream.ReadAsync(eof, deadline.Token) == 0, "timed-out probe releases its socket");
                return;
            }
            var payload = Encoding.UTF8.GetBytes(xml);
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
            if (invalidHeaderField >= 0)
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(invalidHeaderField),
                    invalidHeaderField == 0 ? uint.MaxValue : 123);
            var response = header.Concat(payload).ToArray();
            if (fragment)
            {
                foreach (var part in response.Chunk(7)) await stream.WriteAsync(part, deadline.Token);
            }
            else await stream.WriteAsync(response, deadline.Token);
        }, deadline.Token);
        var timer = Stopwatch.StartNew();
        var result = await AppleMuxSupportProbe.ProbeAsync(port,
            stall ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(2));
        if (stall) Check(timer.Elapsed < TimeSpan.FromSeconds(3), "total I/O deadline enforced");
        await server;
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
