using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

// While a wired session is live the periodic refresh skips USB enumeration to
// avoid preview hitches, so a second iPhone plugged in mid-stream never showed
// up. Counting Apple composite device nodes through CfgMgr32 is cheap (no
// device is opened); when the count grows, wired enumeration is allowed again
// for a short window so the new phone lands in the source list.
internal sealed partial class MainViewModel
{
    private static readonly TimeSpan UsbArrivalWindow = TimeSpan.FromSeconds(20);
    private static readonly Regex AppleCompositeId = new(
        @"^USB\\VID_05AC&PID_12[0-9A-F]{2}\\[^&\\]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private int _lastAppleUsbCount = -1;
    private DateTime _usbArrivalUntilUtc = DateTime.MinValue;

    private bool UsbArrivalPending()
    {
        var serials = PresentAppleUsbSerials();
        if (serials is not null)
        {
            // Lets a phone blocked after a failed USB restore recover when it
            // is replugged next to a live session.
            _usbRestoreRecovery.ObservePhysicalPresence(serials);
            var count = serials.Count;
            if (_lastAppleUsbCount >= 0 && count > _lastAppleUsbCount)
            {
                _usbArrivalUntilUtc = DateTime.UtcNow + UsbArrivalWindow;
                AddDiagnosticLog(AppLog.Event("usb_arrival_detected",
                    ("previous", _lastAppleUsbCount), ("current", count)));
            }
            _lastAppleUsbCount = count;
        }
        return DateTime.UtcNow < _usbArrivalUntilUtc;
    }

    // Serial numbers (the last segment of the composite device ID) of the
    // Apple phones Windows currently lists, or null when CfgMgr32 fails.
    private static HashSet<string>? PresentAppleUsbSerials()
    {
        const uint filter = 0x1 | 0x100; // CM_GETIDLIST_FILTER_ENUMERATOR | _PRESENT
        try
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, "USB", filter) != 0 || length == 0)
                return null;
            var buffer = new char[length];
            if (CM_Get_Device_ID_ListW("USB", buffer, length, filter) != 0) return null;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => AppleCompositeId.IsMatch(id))
                .Select(id => id[(id.LastIndexOf('\\') + 1)..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint length, uint flags);
}
