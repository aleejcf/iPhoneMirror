using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace IPhoneMirror.App.Services;

internal sealed record DeviceHealth(int? BatteryPercent, bool? Charging, double? TemperatureC);

// Reads battery level and battery temperature from a USB-connected iPhone
// through Apple's usbmuxd (127.0.0.1:27015) and lockdownd, using the host
// pairing record Apple Mobile Device Service keeps in ProgramData. Read-only:
// it never pairs, writes settings, or starts anything on the phone besides the
// diagnostics_relay query.
internal static class DeviceHealthMonitor
{
    private const int UsbMuxPort = 27015;
    private const int LockdownPort = 62078;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(6);
    private static readonly string PairRecordDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Apple", "Lockdown");

    internal static async Task<DeviceHealth?> ReadAsync(string udid, CancellationToken cancellation)
    {
        var recordPath = Path.Combine(PairRecordDirectory, udid.ToLowerInvariant() + ".plist");
        if (!File.Exists(recordPath)) return null;
        var record = Plist.ParseDict(await File.ReadAllTextAsync(recordPath, cancellation));
        if (record.GetValueOrDefault("HostCertificate") is not byte[] certificatePem ||
            record.GetValueOrDefault("HostPrivateKey") is not byte[] keyPem ||
            record.GetValueOrDefault("HostID") is not string hostId ||
            record.GetValueOrDefault("SystemBUID") is not string systemBuid)
            return null;
        using var certificate = LoadClientCertificate(certificatePem, keyPem);

        var deviceId = await FindUsbDeviceIdAsync(udid, cancellation);
        if (deviceId is null) return null;

        int? battery = null;
        bool? charging = null;
        double? temperature = null;
        int? diagnosticsPort = null;
        var diagnosticsSsl = false;
        using (var lockdownSocket = await ConnectThroughMuxAsync(deviceId.Value, LockdownPort, cancellation))
        {
            Stream lockdown = lockdownSocket.GetStream();
            var session = await LockdownRequestAsync(lockdown, new()
            {
                ["Label"] = "iPhoneMirror", ["Request"] = "StartSession",
                ["HostID"] = hostId, ["SystemBUID"] = systemBuid,
            }, cancellation);
            if (session.ContainsKey("Error")) return null;
            SslStream? tls = null;
            if (session.GetValueOrDefault("EnableSessionSSL") is true)
            {
                tls = await StartTlsAsync(lockdown, certificate, cancellation);
                lockdown = tls;
            }
            try
            {
                var values = await LockdownRequestAsync(lockdown, new()
                {
                    ["Label"] = "iPhoneMirror", ["Request"] = "GetValue",
                    ["Domain"] = "com.apple.mobile.battery",
                }, cancellation);
                if (values.GetValueOrDefault("Value") is Dictionary<string, object> batteryValues)
                {
                    if (batteryValues.GetValueOrDefault("BatteryCurrentCapacity") is long level)
                        battery = (int)level;
                    if (batteryValues.GetValueOrDefault("BatteryIsCharging") is bool isCharging)
                        charging = isCharging;
                }
                var service = await LockdownRequestAsync(lockdown, new()
                {
                    ["Label"] = "iPhoneMirror", ["Request"] = "StartService",
                    ["Service"] = "com.apple.mobile.diagnostics_relay",
                }, cancellation);
                if (service.GetValueOrDefault("Port") is long port)
                {
                    diagnosticsPort = (int)port;
                    diagnosticsSsl = service.GetValueOrDefault("EnableServiceSSL") is true;
                }
            }
            finally
            {
                tls?.Dispose();
            }
        }

        if (diagnosticsPort is { } relayPort)
        {
            try
            {
                using var relaySocket = await ConnectThroughMuxAsync(deviceId.Value, relayPort, cancellation);
                Stream relay = relaySocket.GetStream();
                SslStream? relayTls = null;
                if (diagnosticsSsl)
                {
                    relayTls = await StartTlsAsync(relay, certificate, cancellation);
                    relay = relayTls;
                }
                try
                {
                    var registry = await LockdownRequestAsync(relay, new()
                    {
                        ["Request"] = "IORegistry", ["EntryClass"] = "IOPMPowerSource",
                    }, cancellation);
                    if (registry.GetValueOrDefault("Diagnostics") is Dictionary<string, object> diagnostics &&
                        diagnostics.GetValueOrDefault("IORegistry") is Dictionary<string, object> io)
                    {
                        if (io.GetValueOrDefault("Temperature") is long centi && centi is > 0 and < 10000)
                            temperature = centi / 100.0;
                        if (battery is null && io.GetValueOrDefault("CurrentCapacity") is long current &&
                            io.GetValueOrDefault("MaxCapacity") is long maximum && maximum > 0 && maximum <= 100)
                            battery = (int)(current * 100 / maximum);
                        if (charging is null && io.GetValueOrDefault("IsCharging") is bool ioCharging)
                            charging = ioCharging;
                    }
                    try
                    {
                        await LockdownRequestAsync(relay, new() { ["Request"] = "Goodbye" }, cancellation);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException) { }
                }
                finally
                {
                    relayTls?.Dispose();
                }
            }
            catch (Exception error) when (error is IOException or SocketException or
                AuthenticationException or InvalidDataException or TimeoutException)
            {
                DiagnosticLogger.ExceptionOnce("device-health-temperature", "device_health",
                    "temperature_read_failed", error);
            }
        }
        return battery is null && temperature is null ? null
            : new DeviceHealth(battery, charging, temperature);
    }

    private static X509Certificate2 LoadClientCertificate(byte[] certificatePem, byte[] keyPem)
    {
        using var pem = X509Certificate2.CreateFromPem(
            Encoding.ASCII.GetString(certificatePem), Encoding.ASCII.GetString(keyPem));
        // SChannel needs a persisted key; round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    private static async Task<SslStream> StartTlsAsync(Stream inner, X509Certificate2 certificate,
        CancellationToken cancellation)
    {
        var tls = new SslStream(inner, leaveInnerStreamOpen: true,
            (_, _, _, _) => true); // The phone presents its self-signed device certificate.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(IoTimeout);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "iPhone",
            ClientCertificates = [certificate],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, timeout.Token);
        return tls;
    }

    private static async Task<int?> FindUsbDeviceIdAsync(string udid, CancellationToken cancellation)
    {
        using var client = await OpenMuxAsync(cancellation);
        var stream = client.GetStream();
        var list = await MuxRequestAsync(stream, new()
        {
            ["MessageType"] = "ListDevices", ["ProgName"] = "iPhoneMirror",
            ["ClientVersionString"] = "iPhoneMirror",
        }, cancellation);
        if (list.GetValueOrDefault("DeviceList") is not List<object> devices) return null;
        foreach (var entry in devices.OfType<Dictionary<string, object>>())
        {
            if (entry.GetValueOrDefault("Properties") is not Dictionary<string, object> properties) continue;
            var serial = properties.GetValueOrDefault("SerialNumber") as string;
            var connection = properties.GetValueOrDefault("ConnectionType") as string;
            if (string.Equals(serial, udid, StringComparison.OrdinalIgnoreCase) &&
                (connection is null || connection.Equals("USB", StringComparison.OrdinalIgnoreCase)) &&
                entry.GetValueOrDefault("DeviceID") is long id)
                return (int)id;
        }
        return null;
    }

    private static async Task<TcpClient> ConnectThroughMuxAsync(int deviceId, int port,
        CancellationToken cancellation)
    {
        var client = await OpenMuxAsync(cancellation);
        try
        {
            var swapped = (ushort)((port >> 8) | ((port & 0xFF) << 8));
            var result = await MuxRequestAsync(client.GetStream(), new()
            {
                ["MessageType"] = "Connect", ["DeviceID"] = (long)deviceId,
                ["PortNumber"] = (long)swapped, ["ProgName"] = "iPhoneMirror",
            }, cancellation);
            if (result.GetValueOrDefault("Number") is not long code || code != 0)
                throw new IOException($"usbmux Connect to port {port} failed");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> OpenMuxAsync(CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(IoTimeout);
        await client.ConnectAsync("127.0.0.1", UsbMuxPort, timeout.Token);
        return client;
    }

    private static int _muxTag;

    private static async Task<Dictionary<string, object>> MuxRequestAsync(Stream stream,
        Dictionary<string, object> request, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes(Plist.Serialize(request));
        var header = new byte[16];
        BitConverter.GetBytes(16 + body.Length).CopyTo(header, 0);
        BitConverter.GetBytes(1).CopyTo(header, 4);  // protocol version
        BitConverter.GetBytes(8).CopyTo(header, 8);  // plist message
        BitConverter.GetBytes(Interlocked.Increment(ref _muxTag)).CopyTo(header, 12);
        await stream.WriteAsync(header, cancellation);
        await stream.WriteAsync(body, cancellation);
        var reply = await ReadExactAsync(stream, 16, cancellation);
        var length = BitConverter.ToInt32(reply, 0) - 16;
        if (length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("usbmux reply length");
        return Plist.ParseDict(Encoding.UTF8.GetString(await ReadExactAsync(stream, length, cancellation)));
    }

    private static async Task<Dictionary<string, object>> LockdownRequestAsync(Stream stream,
        Dictionary<string, object> request, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes(Plist.Serialize(request));
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        await stream.WriteAsync(length, cancellation);
        await stream.WriteAsync(body, cancellation);
        await stream.FlushAsync(cancellation);
        var header = await ReadExactAsync(stream, 4, cancellation);
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("lockdown reply length");
        return Plist.ParseDict(Encoding.UTF8.GetString(await ReadExactAsync(stream, size, cancellation)));
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(IoTimeout);
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    // Minimal XML property-list support for the usbmuxd/lockdownd messages above.
    private static class Plist
    {
        internal static string Serialize(Dictionary<string, object> value) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
            "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            new XElement("plist", new XAttribute("version", "1.0"), ToElement(value)) + "\n";

        private static XElement ToElement(object value) => value switch
        {
            string text => new XElement("string", text),
            bool flag => new XElement(flag ? "true" : "false"),
            long number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
            int number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
            byte[] data => new XElement("data", Convert.ToBase64String(data)),
            Dictionary<string, object> dict => new XElement("dict", dict.SelectMany(pair =>
                new[] { new XElement("key", pair.Key), ToElement(pair.Value) })),
            _ => throw new ArgumentException($"Unsupported plist value {value.GetType()}"),
        };

        internal static Dictionary<string, object> ParseDict(string xml)
        {
            // Drop the DOCTYPE so no DTD processing is ever attempted.
            xml = System.Text.RegularExpressions.Regex.Replace(xml, @"<!DOCTYPE[^>]*>", string.Empty);
            var document = XDocument.Parse(xml, LoadOptions.None);
            var root = document.Root?.Elements().FirstOrDefault();
            return root is not null && FromElement(root) is Dictionary<string, object> dict
                ? dict : throw new InvalidDataException("plist root is not a dictionary");
        }

        private static object FromElement(XElement element) => element.Name.LocalName switch
        {
            "string" => element.Value,
            "integer" => long.Parse(element.Value.Trim(), CultureInfo.InvariantCulture),
            "real" => double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture),
            "true" => true,
            "false" => false,
            "data" => Convert.FromBase64String(string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c)))),
            "date" => element.Value,
            "array" => element.Elements().Select(FromElement).ToList(),
            "dict" => ParseDictElement(element),
            _ => element.Value,
        };

        private static Dictionary<string, object> ParseDictElement(XElement element)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            string? key = null;
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "key") key = child.Value;
                else if (key is not null) { result[key] = FromElement(child); key = null; }
            }
            return result;
        }
    }
}
