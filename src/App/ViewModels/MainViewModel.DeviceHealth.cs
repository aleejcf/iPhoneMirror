using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.ViewModels;

// Polls the selected USB iPhone's battery and temperature once a minute and
// warns before a live stream suffers: low battery while not charging, and a
// hot phone (iOS throttles the GPU, so the stream starts dropping frames).
internal sealed partial class MainViewModel
{
    private static readonly TimeSpan DeviceHealthInterval = TimeSpan.FromSeconds(60);
    private const int LowBatteryPercent = 20;
    private const int CriticalBatteryPercent = 10;
    private const double WarmTemperatureC = 40.0;
    private const double HotTemperatureC = 45.0;

    private string _deviceHealthText = string.Empty;
    private bool _deviceHealthBusy;
    private DateTime _deviceHealthNextUtc = DateTime.MinValue;
    private string? _deviceHealthUdid;
    private int _batteryWarningLevel;
    private int _heatWarningLevel;

    public string DeviceHealthText
    {
        get => _deviceHealthText;
        private set
        {
            if (Set(ref _deviceHealthText, value))
                OnPropertyChanged(nameof(DeviceHealthVisibility));
        }
    }

    public Visibility DeviceHealthVisibility =>
        string.IsNullOrEmpty(_deviceHealthText) ? Visibility.Collapsed : Visibility.Visible;

    private void DeviceHealthTick()
    {
        var device = SelectedDevice;
        if (device is null || device.IsWireless || device.IsMediaCast || !device.Ready)
        {
            if (_deviceHealthUdid is not null) ResetDeviceHealth();
            return;
        }
        if (!DeviceViewModel.UdidEquals(_deviceHealthUdid, device.Udid))
        {
            ResetDeviceHealth();
            _deviceHealthUdid = device.Udid;
        }
        if (_deviceHealthBusy || DateTime.UtcNow < _deviceHealthNextUtc) return;
        _deviceHealthBusy = true;
        _deviceHealthNextUtc = DateTime.UtcNow + DeviceHealthInterval;
        var udid = device.Udid;
        _ = Task.Run(async () =>
        {
            DeviceHealth? health = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                health = await DeviceHealthMonitor.ReadAsync(udid, timeout.Token);
            }
            catch (Exception error)
            {
                DiagnosticLogger.ExceptionOnce("device-health-read", "device_health",
                    "read_failed", error);
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) { _deviceHealthBusy = false; return; }
            await dispatcher.InvokeAsync(() =>
            {
                _deviceHealthBusy = false;
                if (_disposed || !DeviceViewModel.UdidEquals(_deviceHealthUdid, udid)) return;
                ApplyDeviceHealth(health);
            });
        });
    }

    private void ResetDeviceHealth()
    {
        _deviceHealthUdid = null;
        _deviceHealthNextUtc = DateTime.MinValue;
        _batteryWarningLevel = 0;
        _heatWarningLevel = 0;
        DeviceHealthText = string.Empty;
    }

    private void ApplyDeviceHealth(DeviceHealth? health)
    {
        if (health is null) return;
        var parts = new List<string>();
        if (health.BatteryPercent is { } battery)
            parts.Add(LocalizationService.Format(health.Charging == true
                ? "DeviceHealthBatteryChargingFormat" : "DeviceHealthBatteryFormat", battery));
        if (health.TemperatureC is { } temperature)
            parts.Add(LocalizationService.Format("DeviceHealthTemperatureFormat",
                temperature.ToString("0.0")));
        DeviceHealthText = string.Join("  ·  ", parts);
        AddDiagnosticLog(AppLog.Event("device_health",
            ("battery", health.BatteryPercent), ("charging", health.Charging),
            ("temperature_c", health.TemperatureC)));

        // Battery: warn once per level; re-arm after charging or recovering.
        var batteryLevel = health.BatteryPercent is { } percent && health.Charging != true
            ? percent <= CriticalBatteryPercent ? 2 : percent <= LowBatteryPercent ? 1 : 0
            : 0;
        if (batteryLevel > _batteryWarningLevel)
            ShowDeviceHealthNotice(
                LocalizationService.Get(batteryLevel == 2
                    ? "DeviceHealthBatteryCriticalTitle" : "DeviceHealthBatteryLowTitle"),
                LocalizationService.Format("DeviceHealthBatteryBodyFormat", health.BatteryPercent ?? 0),
                critical: batteryLevel == 2);
        _batteryWarningLevel = batteryLevel;

        var heatLevel = health.TemperatureC is { } celsius
            ? celsius >= HotTemperatureC ? 2 : celsius >= WarmTemperatureC ? 1 : 0
            : 0;
        if (heatLevel > _heatWarningLevel)
            ShowDeviceHealthNotice(
                LocalizationService.Get(heatLevel == 2
                    ? "DeviceHealthHotTitle" : "DeviceHealthWarmTitle"),
                LocalizationService.Format(heatLevel == 2
                    ? "DeviceHealthHotBodyFormat" : "DeviceHealthWarmBodyFormat",
                    (health.TemperatureC ?? 0).ToString("0.0")),
                critical: heatLevel == 2);
        // Hysteresis: only re-arm the heat warning after cooling 2 °C below it.
        if (heatLevel > _heatWarningLevel ||
            health.TemperatureC is { } current && current < WarmTemperatureC - 2)
            _heatWarningLevel = heatLevel;
    }

    private void ShowDeviceHealthNotice(string title, string body, bool critical)
    {
        AddDiagnosticLog(AppLog.Event("device_health_warning", ("title", title)));
        DeviceHealthToast.Show(title, body, critical);
    }
}
