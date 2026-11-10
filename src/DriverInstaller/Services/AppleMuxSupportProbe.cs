using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record AppleMuxSupport(bool Available, bool PortOccupied,
    string? Listener = null);

// Read-only, bounded USBMux handshake. An open TCP port (or a process name)
// alone is not evidence of usable Apple support. An empty DeviceList is valid:
// readiness must not depend on whether a phone is connected or trusted yet.
internal static class AppleMuxSupportProbe
{
    internal const int Port = 27015;
    private const int MaximumPacketSize = 1024 * 1024;

    internal static AppleMuxSupport Inspect()
    {
        var result = ProbeAsync(Port, TimeSpan.FromMilliseconds(1500))
            .GetAwaiter().GetResult();
        var listener = ReadListener(Port);
        return result with
        {
            PortOccupied = result.PortOccupied || listener is not null,
            Listener = listener,
        };
    }

    internal static async Task<AppleMuxSupport> ProbeAsync(int port, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var connected = false;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
            connected = true;
            var payload = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>" +
                "<key>MessageType</key><string>ListDevices</string>" +
                "<key>ClientVersionString</key><string>iPhoneMirror.Driver</string>" +
                "<key>ProgName</key><string>iPhoneMirror.Driver</string>" +
                "<key>kLibUSBMuxVersion</key><integer>3</integer></dict></plist>");
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
            var stream = client.GetStream();
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length is <= 16 or > MaximumPacketSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 8 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != 1)
                return new(false, true);
            var response = new byte[length - 16];
            await stream.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            return new(IsDeviceList(response), true);
        }
        catch (Exception error) when (error is SocketException or IOException or
            OperationCanceledException or XmlException)
        {
            return new(false, connected);
        }
    }

    private static bool IsDeviceList(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumPacketSize,
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "plist") return false;
        var values = document.Root.Element("dict")?.Elements().ToArray();
        if (values is null || values.Length % 2 != 0) return false;
        for (var index = 0; index < values.Length; index += 2)
        {
            if (values[index].Name == "key" && values[index].Value == "DeviceList")
                return values[index + 1].Name == "array" &&
                    values[index + 1].Elements().All(item => item.Name == "dict");
        }
        return false;
    }

    internal static string? ReadListener(int port)
    {
        // IPv4 listeners that can own 127.0.0.1:port. No process is stopped.
        const int addressFamily = 2;
        const int ownerPidListeners = 3;
        const uint insufficientBuffer = 122;
        uint size = 0;
        try
        {
            if (GetExtendedTcpTable(0, ref size, false, addressFamily, ownerPidListeners, 0)
                != insufficientBuffer) return null;
            for (var attempt = 0; attempt < 3 && size is >= 4 and <= 16 * 1024 * 1024; attempt++)
            {
                var capacity = size;
                var buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    var error = GetExtendedTcpTable(buffer, ref size, false,
                        addressFamily, ownerPidListeners, 0);
                    if (error == insufficientBuffer) continue;
                    if (error != 0) return null;
                    var count = Marshal.ReadInt32(buffer);
                    const int rowSize = 24;
                    if (count < 0 || count > (capacity - 4) / rowSize) return null;
                    for (var row = 0; row < count; row++)
                    {
                        var entry = buffer + 4 + row * rowSize;
                        var address = (uint)Marshal.ReadInt32(entry, 4);
                        var localPort = BinaryPrimitives.ReverseEndianness(
                            (ushort)Marshal.ReadInt32(entry, 8));
                        if (localPort != port || (address != 0 && address != 0x0100007f)) continue;
                        var pid = Marshal.ReadInt32(entry, 20);
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            return $"{process.ProcessName}.exe (PID {pid})";
                        }
                        catch (Exception errorDetails) when (errorDetails is ArgumentException or
                            InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            return $"PID {pid}";
                        }
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
