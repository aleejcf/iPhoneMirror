using System.IO;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestRemoteTextInput(MainWindow window, Window other, MemoryStream packets,
        Action<Window> focus, bool textSupported)
    {
        focus(window);
        KeyboardCall(window, "ResetKeyboardOwnership");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        packets.SetLength(0);
        var data = Activator.CreateInstance(typeof(MainWindow).GetNestedType("LowLevelKeyboardData", KeyboardTestMembers)!)!;
        SetKeyboardField(data, "VirtualKey", 0xE7u);
        SetKeyboardField(data, "Flags", 0x10u);
        bool Packet(char character, bool down = true)
        {
            SetKeyboardField(data, "ScanCode", (uint)character);
            return (bool)KeyboardCall(window, "ProcessKeyboardHook", data, down ? (nint)0x100 : (nint)0x101)!;
        }
        string[] TextFrames()
        {
            using var copy = new MemoryStream(packets.ToArray());
            using var reader = new BinaryReader(copy);
            var result = new List<string>();
            while (copy.Position < copy.Length)
            {
                using var document = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
                if (document.RootElement.GetProperty("kind").GetString() == "paste_text")
                    result.Add(document.RootElement.GetProperty("text").GetString()!);
            }
            return result.ToArray();
        }
        if (!textSupported)
        {
            InteractionAssert(!Packet('中'), "Bluetooth must not consume text it cannot transmit.");
            return;
        }
        const string message = "中文 abc🙂";
        foreach (var character in message)
        {
            InteractionAssert(Packet(character), "Remote Unicode down was rejected.");
            InteractionAssert(Packet(character, false), "Remote Unicode up was rejected.");
        }
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        InteractionAssert(TextFrames().SequenceEqual(new[] { message }), "Remote text was lost, duplicated or split into per-character paste operations.");

        foreach (var (character, expectedUsage, uppercase) in new[]
            { ('a', 4, false), ('Z', 29, true), ('0', 39, false), (' ', 44, false) })
        {
            packets.SetLength(0);
            Packet(character);
            Packet(character, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            var reports = MappingFrames(packets).Where(f => f.TryGetProperty("usages", out _))
                .Select(f => f.GetProperty("usages").EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            InteractionAssert(TextFrames().Length == 0 && reports.Any(r => r.Contains(expectedUsage)) && reports[^1].Length == 0,
                "Single ASCII commit must send a keyboard press/release without changing the clipboard.");
            InteractionAssert(reports.Any(r => r.Contains(0xE1)) == uppercase,
                "Single letter casing did not produce the expected Shift state.");
        }
        packets.SetLength(0);
        Packet('a'); Packet('b'); Packet('c');
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        InteractionAssert(TextFrames().SequenceEqual(new[] { "abc" }), "An ASCII burst must paste once rather than type individual keys.");

        packets.SetLength(0);
        Packet('\uD83D');
        KeyboardCall(window, "FlushControlText");
        InteractionAssert(TextFrames().Length == 0, "Incomplete surrogate was transmitted.");
        Packet('\uDE42');
        KeyboardCall(window, "FlushControlText");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
        InteractionAssert(TextFrames().SequenceEqual(new[] { "🙂" }), "Split surrogate pair was lost.");

        packets.SetLength(0);
        Packet('旧');
        focus(other);
        focus(window);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        InteractionAssert(TextFrames().Length == 0, "Text queued before focus loss survived refocus.");

        var route = KeyboardCall(KeyboardField(window, "_viewModel"), "CaptureDirectKeyboardRoute",
            KeyboardField(window, "ActiveInputDeviceUdid"), false)!;
        var sessionGate = (SemaphoreSlim)KeyboardCall(window, "KeyboardSessionGate", route)!;
        sessionGate.Wait();
        try
        {
            Packet('迟');
            KeyboardCall(window, "FlushControlText");
            focus(other);
            focus(window);
        }
        finally { sessionGate.Release(); }
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        InteractionAssert(TextFrames().Length == 0, "Text waiting on a busy transport survived focus loss/refocus.");

        SetKeyboardField(window, "_isSettingsPanelVisible", true);
        try { InteractionAssert(!Packet('中'), "Remote text leaked from the settings editor into the device."); }
        finally { SetKeyboardField(window, "_isSettingsPanelVisible", false); }

        SetKeyboardField(data, "VirtualKey", 0x42u);
        SetKeyboardField(data, "ScanCode", 0x30u);
        InteractionAssert((bool)KeyboardCall(window, "ProcessKeyboardHook", data, (nint)0x100)!, "Remote injected physical key was rejected.");
        KeyboardCall(window, "ProcessKeyboardHook", data, (nint)0x101);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        InteractionAssert(MappingFrames(packets).Any(f => f.TryGetProperty("usages", out var usages) &&
            usages.EnumerateArray().Any(v => v.GetInt32() == 5)), "Injected B never reached the device keyboard transport.");
        SetKeyboardField(data, "ExtraInformation", (nint)0x49504D52);
        InteractionAssert(!(bool)KeyboardCall(window, "ProcessKeyboardHook", data, (nint)0x100)!, "Our own replay re-entered the keyboard router.");

        KeyboardCall(window, "TryEnterKeyboardMappingInputMode");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        SetKeyboardField(data, "ExtraInformation", (nint)0);
        SetKeyboardField(data, "VirtualKey", 0xE7u);
        InteractionAssert(!Packet('中'), "Unicode text borrowed mapping ownership.");
        KeyboardCall(window, "LeaveKeyboardMappingInputMode");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));

        packets.SetLength(0);
        KeyboardCall(window, "HandleControlKeyboardInput",
            new PreviewKeyboardEventArgs(PreviewKeyboardKind.Text, 0) { Text = "直接文字" },
            null, false, null);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        InteractionAssert(TextFrames().SequenceEqual(new[] { "直接文字" }), "Native text callback was dropped while keyboard hook was active.");
        KeyboardCall(window, "ResetKeyboardOwnership");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
        Console.WriteLine("Remote text input: Unicode, split emoji, injected keys, replay and focus/mode guards passed.");
    }
}
