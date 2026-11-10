using System.IO;
using System.Text.Json;
using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

// Auto-mirror: starts wired mirroring as soon as the selected USB device is
// ready, and restarts it after a capture failure so a live stream recovers on
// its own. A manual Stop suppresses both until the cable is replugged or the
// user presses Start again.
internal sealed partial class MainViewModel
{
    private const int AutoMirrorMaxRetries = 6;
    private static readonly TimeSpan[] AutoMirrorBackoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20),
    ];
    private static readonly string AutoMirrorSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "automirror.json");

    private bool _autoMirrorEnabled = LoadAutoMirrorFlag("enabled");
    private bool _autoCameraEnabled = LoadAutoMirrorFlag("camera");
    private bool _autoObsWindowEnabled = LoadAutoMirrorFlag("obsWindow");

    // Opens the separate preview window by itself for each wired session. OBS
    // Window Capture of that window is GPU-to-GPU and much smoother than the
    // virtual camera, which copies every frame through system memory.
    public bool AutoObsWindowEnabled
    {
        get => _autoObsWindowEnabled;
        set
        {
            if (!Set(ref _autoObsWindowEnabled, value)) return;
            SaveAutoMirrorSettings();
            AddDiagnosticLog(AppLog.Event("auto_obs_window_setting", ("enabled", value)));
        }
    }
    private bool _autoCameraStartPosted;
    private DateTime _autoCameraNextAttemptUtc = DateTime.MinValue;
    private bool _autoMirrorSuppressedByUser;
    private bool _autoMirrorStartPosted;
    private int _autoMirrorFailures;
    private DateTime _autoMirrorNextAttemptUtc = DateTime.MinValue;
    private string? _autoMirrorUdid;

    public bool AutoMirrorEnabled
    {
        get => _autoMirrorEnabled;
        set
        {
            if (!Set(ref _autoMirrorEnabled, value)) return;
            SaveAutoMirrorSettings();
            _autoMirrorFailures = 0;
            _autoMirrorNextAttemptUtc = DateTime.MinValue;
            AddDiagnosticLog(AppLog.Event("auto_mirror_setting", ("enabled", value)));
        }
    }

    // Starts the virtual camera (clean OBS feed with the "be right back" card)
    // whenever wired mirroring is streaming and the camera is not running.
    public bool AutoVirtualCameraEnabled
    {
        get => _autoCameraEnabled;
        set
        {
            if (!Set(ref _autoCameraEnabled, value)) return;
            SaveAutoMirrorSettings();
            _autoCameraNextAttemptUtc = DateTime.MinValue;
            AddDiagnosticLog(AppLog.Event("auto_camera_setting", ("enabled", value)));
        }
    }

    private void AutoCameraOnStreaming(uint width, uint height)
    {
        if (!_autoCameraEnabled || _autoCameraStartPosted || _virtualCamera.IsRunning ||
            IsMediaOutputRunning || width == 0 || height == 0 ||
            SelectedDevice is not { IsWireless: false, IsMediaCast: false } ||
            DateTime.UtcNow < _autoCameraNextAttemptUtc)
            return;
        // Capabilities are otherwise probed only when the Output window opens.
        if (!_virtualCameraCapabilities.Registered) RefreshVirtualCameraCapabilities();
        if (!_virtualCameraCapabilities.Registered || _virtualCameraCapabilities.UpdateRequired)
        {
            _autoCameraNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            AddDiagnosticLog(AppLog.Event("auto_camera_unavailable",
                ("registered", _virtualCameraCapabilities.Registered),
                ("update_required", _virtualCameraCapabilities.UpdateRequired)));
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _autoCameraStartPosted = true;
        var landscape = width >= height;
        dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            try
            {
                if (_disposed || _virtualCamera.IsRunning || IsMediaOutputRunning) return;
                var result = await StartVirtualCameraAsync(
                    landscape ? 1920U : 1080U, landscape ? 1080U : 1920U, 60);
                AddDiagnosticLog(AppLog.Event("auto_camera_start",
                    ("success", result.Success), ("message", result.Message),
                    ("orientation", landscape ? "landscape" : "portrait")));
                if (!result.Success)
                    _autoCameraNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            }
            catch (Exception error)
            {
                _autoCameraNextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                DiagnosticLogger.Exception("virtual_camera", "auto_start_failed", error);
            }
            finally
            {
                _autoCameraStartPosted = false;
            }
        }));
    }

    private void AutoMirrorOnUserStart()
    {
        _autoMirrorSuppressedByUser = false;
        _autoMirrorFailures = 0;
        _autoMirrorNextAttemptUtc = DateTime.MinValue;
    }

    private void AutoMirrorOnUserStop()
    {
        _autoMirrorSuppressedByUser = true;
        AddDiagnosticLog(AppLog.Event("auto_mirror_suppressed", ("reason", "user_stop")));
    }

    private void AutoMirrorOnStreaming()
    {
        if (_autoMirrorFailures == 0) return;
        AddDiagnosticLog(AppLog.Event("auto_mirror_recovered",
            ("failures", _autoMirrorFailures)));
        _autoMirrorFailures = 0;
        _autoMirrorNextAttemptUtc = DateTime.MinValue;
    }

    // Returns true when the failure will be retried automatically, so the
    // caller can skip the modal error notice in the middle of a live stream.
    private bool AutoMirrorOnFailure()
    {
        if (!_autoMirrorEnabled || _autoMirrorSuppressedByUser ||
            SelectedDevice is not { IsWireless: false, IsMediaCast: false })
            return false;
        if (_autoMirrorFailures >= AutoMirrorMaxRetries)
        {
            AddDiagnosticLog(AppLog.Event("auto_mirror_gave_up",
                ("failures", _autoMirrorFailures)));
            _autoMirrorSuppressedByUser = true;
            return false;
        }
        var delay = AutoMirrorBackoff[Math.Min(_autoMirrorFailures, AutoMirrorBackoff.Length - 1)];
        ++_autoMirrorFailures;
        _autoMirrorNextAttemptUtc = DateTime.UtcNow + delay;
        AddDiagnosticLog(AppLog.Event("auto_mirror_retry_scheduled",
            ("attempt", _autoMirrorFailures), ("delay_ms", (int)delay.TotalMilliseconds)));
        CaptureStatus = LocalizationService.Format("AutoMirrorReconnectingFormat",
            _autoMirrorFailures, AutoMirrorMaxRetries);
        return true;
    }

    // Called after every device refresh, while the core gate is still held.
    // Starting must wait for the gate, so the attempt is posted to the UI loop.
    private void AutoMirrorAfterRefresh()
    {
        var device = SelectedDevice;
        if (device is null || device.IsWireless || device.IsMediaCast)
        {
            // Cable unplugged (or another source selected): forget the manual
            // Stop and the retry budget so the next plug-in starts cleanly.
            if (_autoMirrorUdid is not null || _autoMirrorSuppressedByUser || _autoMirrorFailures > 0)
                AddDiagnosticLog(AppLog.Event("auto_mirror_reset", ("reason", "device_gone")));
            _autoMirrorUdid = null;
            _autoMirrorSuppressedByUser = false;
            _autoMirrorFailures = 0;
            _autoMirrorNextAttemptUtc = DateTime.MinValue;
            return;
        }
        if (!DeviceViewModel.UdidEquals(_autoMirrorUdid, device.Udid))
        {
            _autoMirrorUdid = device.Udid;
            _autoMirrorSuppressedByUser = false;
            _autoMirrorFailures = 0;
        }
        if (!_autoMirrorEnabled || _autoMirrorSuppressedByUser || _autoMirrorStartPosted ||
            !device.Ready || HasCaptureSession || IsCapturing ||
            DateTime.UtcNow < _autoMirrorNextAttemptUtc)
            return;

        _autoMirrorStartPosted = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) { _autoMirrorStartPosted = false; return; }
        dispatcher.BeginInvoke(new Action(() =>
        {
            _autoMirrorStartPosted = false;
            if (_disposed || !_autoMirrorEnabled || _autoMirrorSuppressedByUser) return;
            if (SelectedDevice is not { IsWireless: false, IsMediaCast: false, Ready: true } selected ||
                !DeviceViewModel.UdidEquals(selected.Udid, _autoMirrorUdid) ||
                !StartCommand.CanExecute(null))
                return;
            AddDiagnosticLog(AppLog.Event("auto_mirror_start",
                ("device", AppLog.Device(selected.Udid)),
                ("attempt", _autoMirrorFailures)));
            _ = StartAsync();
        }));
    }

    // Flags default to on; only an explicit false in the file turns one off.
    private static bool LoadAutoMirrorFlag(string name)
    {
        try
        {
            if (!File.Exists(AutoMirrorSettingsPath)) return true;
            using var document = JsonDocument.Parse(File.ReadAllText(AutoMirrorSettingsPath));
            return !document.RootElement.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.False;
        }
        catch { return true; }
    }

    private void SaveAutoMirrorSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoMirrorSettingsPath)!);
            File.WriteAllText(AutoMirrorSettingsPath, JsonSerializer.Serialize(
                new { enabled = _autoMirrorEnabled, camera = _autoCameraEnabled,
                    obsWindow = _autoObsWindowEnabled }));
        }
        catch { }
    }
}
