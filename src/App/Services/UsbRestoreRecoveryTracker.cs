namespace IPhoneMirror.App.Services;

/// <summary>
/// Keeps a device out of capture startup after native teardown could not
/// confirm that iOS returned to its normal USB configuration. The block is
/// released only after the management channel disappears and returns. Device
/// cards retained for display are not inventory evidence.
/// </summary>
internal sealed class UsbRestoreRecoveryTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _blocked =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnected =
        new(StringComparer.OrdinalIgnoreCase);

    internal void MarkRecoveryRequired(string udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return;
        lock (_gate)
        {
            _blocked.Add(udid);
            _disconnected.Remove(udid);
        }
    }

    internal bool IsBlocked(string udid)
    {
        lock (_gate) return _blocked.Contains(udid);
    }

    internal bool HasBlockedDevices
    {
        get { lock (_gate) return _blocked.Count != 0; }
    }

    /// <summary>
    /// Records a blocked device as unplugged when Windows no longer lists its
    /// USB node. Live sessions skip wired enumeration, so without this a quick
    /// unplug/replug next to a streaming phone was never seen and the block
    /// stayed forever. USB serials are UDIDs without the dash.
    /// </summary>
    internal void ObservePhysicalPresence(IReadOnlySet<string> presentSerials)
    {
        lock (_gate)
        {
            foreach (var udid in _blocked)
                if (!presentSerials.Contains(udid.Replace("-", string.Empty)))
                    _disconnected.Add(udid);
        }
    }

    /// <summary>
    /// A missing observation is not enough by itself to clear the block. The
    /// next accessible observation must follow it. Callers can require fresh
    /// management access, rather than cached device metadata, for recovery.
    /// </summary>
    internal IReadOnlyList<string> Observe(IEnumerable<string> presentUdids,
        IEnumerable<string>? accessibleUdids = null)
    {
        var present = new HashSet<string>(presentUdids.Where(
            value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        var accessible = accessibleUdids is null ? present :
            new HashSet<string>(accessibleUdids, StringComparer.OrdinalIgnoreCase);
        var cleared = new List<string>();
        lock (_gate)
        {
            foreach (var udid in _blocked.ToArray())
            {
                if (!present.Contains(udid))
                {
                    _disconnected.Add(udid);
                    continue;
                }
                if (!accessible.Contains(udid) || !_disconnected.Remove(udid)) continue;
                _blocked.Remove(udid);
                cleared.Add(udid);
            }
        }
        return cleared;
    }
}
