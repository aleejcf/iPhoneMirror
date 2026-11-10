using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private readonly StringBuilder _controlText = new();
    private DispatcherTimer? _controlTextTimer;
    private DirectKeyboardRoute? _controlTextRoute;
    private Func<bool>? _controlTextGuard;
    private long _lastRemoteTextFailure;

    private void OnRemoteTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && TryQueueControlText(e.Text)) e.Handled = true;
    }

    private bool TryQueueControlText(string text)
    {
        Dispatcher.VerifyAccess();
        ObserveKeyboardForeground();
        var target = ActiveInputDeviceUdid;
        var window = GetControlKeyboardWindow(target);
        if (_mappingCapture.Waiting || _shortcutSettingsWindow is not null ||
            !CanForwardControlKeyboard(target, window) || !TryEnterDirectKeyboardInputMode()) return false;
        var route = _viewModel.CaptureDirectKeyboardRoute(target);
        if (route?.PasteTextAsync is null) return false;
        if (text.Length == 0) return true; // VK_PACKET release carries no text.
        if (_controlTextRoute is not null && (!_controlTextRoute.SameSession(route) || _controlTextGuard?.Invoke() != true))
            ClearControlText();
        _controlTextRoute = route;
        _controlTextGuard ??= CaptureKeyboardSendGuard(window);
        _directKeyboardRoute = route;
        _keyboardStateUdid = target;
        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) continue;
            // A split surrogate pair is retained across dispatcher batches.
            if (_controlText.Length > 0 && char.IsHighSurrogate(_controlText[^1]) && !char.IsLowSurrogate(character))
                _controlText.Length--;
            if (char.IsLowSurrogate(character) && (_controlText.Length == 0 || !char.IsHighSurrogate(_controlText[^1]))) continue;
            _controlText.Append(character);
        }
        _controlTextTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(35), DispatcherPriority.Input,
            (_, _) => FlushControlText(), Dispatcher);
        _controlTextTimer.Stop();
        _controlTextTimer.Start();
        return true;
    }

    private void ClearControlText()
    {
        _controlTextTimer?.Stop();
        _controlText.Clear();
        _controlTextRoute = null;
        _controlTextGuard = null;
    }

    private void FlushControlText()
    {
        _controlTextTimer?.Stop();
        var route = _controlTextRoute;
        var guard = _controlTextGuard;
        if (route is null || guard is null) return;
        if (!guard() || !route.IsCurrent()) { ClearControlText(); return; }
        var length = _controlText.Length;
        if (length > 0 && char.IsHighSurrogate(_controlText[length - 1])) length--;
        if (length == 0) return;
        var text = _controlText.ToString(0, length);
        _controlText.Remove(0, length);
        if (_controlText.Length == 0) ClearControlText();
        _ = TrackKeyboardSendAsync(SendControlTextAsync(route, text, guard));
    }

    private async Task SendControlTextAsync(DirectKeyboardRoute route, string text, Func<bool> guard)
    {
        var gate = KeyboardSessionGate(route);
        await _systemShortcutGate.WaitAsync();
        await gate.WaitAsync();
        var keyPressed = false;
        var pasteRequested = false;
        try
        {
            if (!guard() || !route.IsCurrent()) return;
            if (!TryMapSingleControlCharacter(text, out var usage, out var shift))
            {
                pasteRequested = true;
                DiagnosticLogger.ReverseControl("keyboard_input", "remote_text_paste_started",
                    ("length", text.Length), ("transport", route.Transport));
                await route.PasteTextAsync!(text, guard);
                DiagnosticLogger.ReverseControl("keyboard_input", "remote_text_paste_completed",
                    ("length", text.Length), ("transport", route.Transport));
                return;
            }
            // Text commits have their own down/up lifetime. Do not feed them
            // through shortcut recognition or borrow physically held modifiers.
            keyPressed = true;
            if (shift)
            {
                await route.SendAsync(0x02, [0xE1], guard);
                await Task.Delay(20);
            }
            if (!guard() || !route.IsCurrent()) return;
            await route.SendAsync(shift ? (byte)0x02 : (byte)0,
                shift ? new byte[] { 0xE1, usage } : new byte[] { usage }, guard);
            await Task.Delay(20);
        }
        catch (Exception error)
        {
            DiagnosticLogger.ReverseControlWarning("keyboard_input", "remote_text_input_failed",
                ("transport", route.Transport), ("length", text.Length), ("error", error.GetType().Name));
            _viewModel.AddDiagnosticLog(AppLog.Event("remote_text_input_failed",
                ("device", AppLog.Device(route.Target)), ("error", AppLog.Error(error))));
            var now = Environment.TickCount64;
            if (pasteRequested && error is not OperationCanceledException && now - _lastRemoteTextFailure > 5000)
            {
                _lastRemoteTextFailure = now;
                _viewModel.AddUiLog(LocalizationService.Get("RemoteTextClipboardUnavailable"));
            }
        }
        finally
        {
            try
            {
                // Release the captured session even when focus was revoked.
                if (keyPressed) await route.SendAsync(0, [], null);
                await RestoreDirectKeyboardStateAsync(route, guard);
            }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlWarning("keyboard_input", "remote_text_restore_failed", ("error", error.Message));
                ResetKeyboardOwnership();
            }
            finally { gate.Release(); _systemShortcutGate.Release(); }
        }
    }

    private static bool TryMapSingleControlCharacter(string text, out byte usage, out bool shift)
    {
        usage = 0;
        shift = false;
        if (text.Length != 1) return false;
        var character = text[0];
        if (character is >= 'a' and <= 'z') usage = (byte)(character - 'a' + 0x04);
        else if (character is >= 'A' and <= 'Z')
        {
            usage = (byte)(character - 'A' + 0x04);
            shift = true;
        }
        else if (character is >= '1' and <= '9') usage = (byte)(character - '1' + 0x1E);
        else if (character == '0') usage = 0x27;
        else if (character == ' ') usage = 0x2C;
        else return false;
        return true;
    }
}
