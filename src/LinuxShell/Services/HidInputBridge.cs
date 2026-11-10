// SPDX-License-Identifier: GPL-3.0-only
//
// Turns Avalonia pointer and key events into HID reports.
//
// The mouse report is relative, not absolute — the descriptor marks X and Y as
// Data|Variable|Relative (0x81, 0x06) — so this sends deltas and never needs to
// know where the pointer is on the device. Taking an absolute position from the
// window would be wrong: iOS moves its own cursor.
//
// Reports leave on a dedicated thread, coalesced, and never from the UI thread.
// That is not tidiness. Sending inline from PointerMoved froze the window: each
// move published a D-Bus properties-changed signal synchronously, pointer moves
// arrive hundreds of times a second, and the UI thread saturated so the timer
// that presents frames stopped running. The picture stopped while the process sat
// alive at 8% CPU, which reads as a hang rather than as a backlog.
//
// Motion is merged rather than dropped, through the same
// BluetoothMouseReportCoalescer the Windows build uses, so a fast drag does not
// lose travel.

using Avalonia;
using Avalonia.Input;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.LinuxShell.Services;

internal sealed class HidInputBridge : IDisposable
{
    private const int MotionIntervalMs = 8;

    private readonly BluezHidService _hid;
    private readonly Thread _pump;
    private readonly object _sync = new();
    private readonly Queue<(byte Report, byte[] Payload)> _discrete = new();
    private readonly HashSet<byte> _pressedKeys = [];
    private readonly HashSet<byte> _pressedModifiers = [];
    private byte[]? _pendingMotion;
    private byte _buttons;
    private byte _modifiers;
    private Point? _last;
    private volatile bool _running = true;

    internal HidInputBridge(BluezHidService hid)
    {
        _hid = hid;
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "iPhoneMirror HID pump",
        };
        _pump.Start();
    }

    internal int DisplayRotation { get; set; }
    internal uint DisplayedWidth { get; set; } = 1206;
    internal uint DisplayedHeight { get; set; } = 2622;
    internal long Sent { get; private set; }

    internal void PointerMoved(Point position)
    {
        if (_last is { } previous)
        {
            var (dx, dy) = BluetoothMouseOrientationMapper.Map(
                position.X - previous.X, position.Y - previous.Y,
                DisplayedWidth, DisplayedHeight, DisplayRotation,
                BluetoothMouseDirection.Up, BluetoothMouseDirection.Up,
                reverseHorizontal: false, reverseVertical: false);
            QueueMotion(Clamp(dx), Clamp(dy), 0);
        }
        _last = position;
    }

    internal void PointerLost() => _last = null;

    internal void ButtonChanged(int index, bool pressed)
    {
        if (index is < 0 or > 4) return;
        var mask = (byte)(1 << index);
        lock (_sync)
        {
            _buttons = pressed
                ? (byte)(_buttons | mask)
                : (byte)(_buttons & ~mask);
        }
        // A button change must not be merged into pending motion, or a click
        // could be swallowed by the next move; it goes through the discrete
        // queue, which is always flushed.
        QueueDiscrete(HidReportMap.MouseReportId, BuildMouse(0, 0, 0));
    }

    internal void Wheel(double delta)
    {
        var clicks = (sbyte)Math.Clamp(Math.Round(delta), -127, 127);
        if (clicks != 0)
            QueueDiscrete(HidReportMap.MouseReportId, BuildMouse(0, 0, clicks));
    }

    internal void ReleaseAll()
    {
        lock (_sync)
        {
            _pressedKeys.Clear();
            _pressedModifiers.Clear();
            _modifiers = 0;
            _buttons = 0;
        }
        _last = null;
        QueueDiscrete(HidReportMap.KeyboardReportId, new byte[8]);
        QueueDiscrete(HidReportMap.MouseReportId, BuildMouse(0, 0, 0));
    }

    // Keyboard: HID usage IDs, not scan codes. Only the keys a mirror actually
    // needs are mapped; an unmapped key sends nothing rather than a wrong usage,
    // because wrong is worse than missing.
    internal void KeyChanged(Key key, bool pressed)
    {
        var usage = UsageFor(key);
        var modifierBit = ModifierBitFor(key);
        lock (_sync)
        {
            if (usage != 0)
            {
                if (pressed) _pressedKeys.Add(usage);
                else _pressedKeys.Remove(usage);
            }
            if (modifierBit != 0)
            {
                if (pressed) _pressedModifiers.Add(modifierBit);
                else _pressedModifiers.Remove(modifierBit);
            }

            _modifiers = _pressedModifiers.Aggregate((byte)0,
                (current, bit) => (byte)(current | bit));
            var report = BuildKeyboardReportLocked();
            QueueDiscrete(HidReportMap.KeyboardReportId, report);
        }
    }

    internal async Task SendIosShortcutAsync(string action)
    {
        if (!_hid.AnySubscribed) return;
        if (action == "home")
        {
            QueueConsumer(0x029D);
            var insertedHomeKey = SetTransientKey(0x0B, pressed: true);
            await Task.Delay(60).ConfigureAwait(false);
            if (insertedHomeKey) SetTransientKey(0x0B, pressed: false);
            QueueConsumer(0);
        }
        else if (action == "appSwitcher")
        {
            QueueNavigation(0x0002);
            await Task.Delay(100).ConfigureAwait(false);
            QueueNavigation(0);
        }
    }

    private bool SetTransientKey(byte usage, bool pressed)
    {
        lock (_sync)
        {
            var changed = pressed ? _pressedKeys.Add(usage) : _pressedKeys.Remove(usage);
            if (!changed) return false;
            QueueDiscrete(HidReportMap.KeyboardReportId, BuildKeyboardReportLocked());
            return changed;
        }
    }

    private byte[] BuildKeyboardReportLocked()
    {
        var report = new byte[8];
        report[0] = _modifiers;
        var index = 2;
        var orderedKeys = _pressedKeys.Order().ToArray();
        if (orderedKeys.Length > 6)
        {
            // Boot keyboard reports reserve usage 0x01 for ErrorRollOver.
            for (var slot = 2; slot < report.Length; ++slot)
                report[slot] = 0x01;
        }
        else foreach (var keyUsage in orderedKeys)
        {
            report[index++] = keyUsage;
        }
        return report;
    }

    private void QueueConsumer(ushort usage)
    {
        Span<byte> report = stackalloc byte[2];
        BitConverter.TryWriteBytes(report, usage);
        QueueDiscrete(HidReportMap.ConsumerReportId, report.ToArray());
    }

    private void QueueNavigation(ushort controls)
    {
        Span<byte> report = stackalloc byte[2];
        BitConverter.TryWriteBytes(report, controls);
        QueueDiscrete(HidReportMap.NavigationReportId, report.ToArray());
    }

    private static byte ModifierBitFor(Key key) => key switch
    {
        Key.LeftCtrl => 0x01,
        Key.RightCtrl => 0x10,
        Key.LeftShift => 0x02,
        Key.RightShift => 0x20,
        Key.LeftAlt => 0x04,
        Key.RightAlt => 0x40,
        Key.LWin => 0x08,
        Key.RWin => 0x80,
        _ => 0,
    };

    private void Pump()
    {
        while (_running)
        {
            (byte Report, byte[] Payload)? discrete = null;
            byte[]? motion = null;
            lock (_sync)
            {
                if (_discrete.Count > 0) discrete = _discrete.Dequeue();
                else if (_pendingMotion is { } pending)
                {
                    motion = pending;
                    _pendingMotion = null;
                }
            }
            if (discrete is { } item)
            {
                if (_hid.SendReport(item.Report, item.Payload)) ++Sent;
                // Discrete events are drained without waiting so a press and its
                // release cannot end up a frame apart.
                continue;
            }
            if (motion is not null)
            {
                if (_hid.SendReport(HidReportMap.MouseReportId, motion)) ++Sent;
            }
            Thread.Sleep(MotionIntervalMs);
        }
    }

    private void QueueMotion(short dx, short dy, sbyte wheel)
    {
        if (dx == 0 && dy == 0 && wheel == 0) return;
        var incoming = BuildMouse(dx, dy, wheel);
        lock (_sync)
        {
            _pendingMotion = BluetoothMouseReportCoalescer.MergePendingMotion(
                _pendingMotion, incoming);
        }
    }

    private void QueueDiscrete(byte report, byte[] payload)
    {
        lock (_sync)
        {
            // Bounded: a stuck link must not grow the queue without limit. Losing
            // the oldest queued event is better than losing the newest, because
            // the newest is what the user just did.
            if (_discrete.Count >= 256) _discrete.Dequeue();
            _discrete.Enqueue((report, payload));
        }
    }

    private byte[] BuildMouse(short dx, short dy, sbyte wheel)
    {
        var report = new byte[BluetoothMouseReportCoalescer.ReportLength];
        lock (_sync) report[0] = _buttons;
        BitConverter.TryWriteBytes(report.AsSpan(1, 2), dx);
        BitConverter.TryWriteBytes(report.AsSpan(3, 2), dy);
        report[5] = (byte)wheel;
        return report;
    }

    private static short Clamp(double value) =>
        (short)Math.Clamp(Math.Round(value), short.MinValue + 1, short.MaxValue);

    private static byte UsageFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (byte)(0x04 + (key - Key.A)),
        >= Key.D1 and <= Key.D9 => (byte)(0x1E + (key - Key.D1)),
        Key.D0 => 0x27,
        Key.Enter => 0x28,
        Key.Escape => 0x29,
        Key.Back => 0x2A,
        Key.Tab => 0x2B,
        Key.Space => 0x2C,
        Key.OemMinus => 0x2D,
        Key.OemPlus => 0x2E,
        Key.OemOpenBrackets or Key.Oem4 => 0x2F,
        Key.OemCloseBrackets or Key.Oem6 => 0x30,
        Key.OemBackslash or Key.Oem5 => 0x31,
        Key.OemSemicolon or Key.Oem1 => 0x33,
        Key.OemQuotes or Key.Oem7 => 0x34,
        Key.OemTilde or Key.Oem3 => 0x35,
        Key.OemComma => 0x36,
        Key.OemPeriod => 0x37,
        Key.OemQuestion or Key.Oem2 => 0x38,
        Key.CapsLock => 0x39,
        Key.F1 => 0x3A,
        Key.F2 => 0x3B,
        Key.F3 => 0x3C,
        Key.F4 => 0x3D,
        Key.F5 => 0x3E,
        Key.F6 => 0x3F,
        Key.F7 => 0x40,
        Key.F8 => 0x41,
        Key.F9 => 0x42,
        Key.F10 => 0x43,
        Key.F11 => 0x44,
        Key.F12 => 0x45,
        Key.PrintScreen => 0x46,
        Key.Scroll => 0x47,
        Key.Pause => 0x48,
        Key.Insert => 0x49,
        Key.Home => 0x4A,
        Key.PageUp => 0x4B,
        Key.Delete => 0x4C,
        Key.End => 0x4D,
        Key.PageDown => 0x4E,
        Key.Right => 0x4F,
        Key.Left => 0x50,
        Key.Down => 0x51,
        Key.Up => 0x52,
        Key.NumLock => 0x53,
        Key.Divide => 0x54,
        Key.Multiply => 0x55,
        Key.Subtract => 0x56,
        Key.Add => 0x57,
        Key.NumPad1 => 0x59,
        Key.NumPad2 => 0x5A,
        Key.NumPad3 => 0x5B,
        Key.NumPad4 => 0x5C,
        Key.NumPad5 => 0x5D,
        Key.NumPad6 => 0x5E,
        Key.NumPad7 => 0x5F,
        Key.NumPad8 => 0x60,
        Key.NumPad9 => 0x61,
        Key.NumPad0 => 0x62,
        Key.Decimal => 0x63,
        _ => 0,
    };

    public void Dispose()
    {
        _running = false;
        _pump.Join(TimeSpan.FromMilliseconds(200));
    }
}
