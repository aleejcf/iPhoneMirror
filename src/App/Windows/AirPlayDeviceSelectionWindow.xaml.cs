using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Windows;

public partial class AirPlayDeviceSelectionWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private readonly bool _previewOnly;
    private readonly ObservableCollection<DeviceViewModel> _source;
    private readonly HashSet<DeviceViewModel> _observed = [];
    private DeviceViewModel? _selectedDevice;
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public DeviceViewModel? SelectedDevice { get => _selectedDevice; set { _selectedDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanConfirm)); } }
    public bool CanConfirm => SelectedDevice is { Ready: true } device && Devices.Contains(device);
    internal AirPlayDeviceSelectionWindow(Window owner, ObservableCollection<DeviceViewModel> devices, bool previewOnly = false)
    {
        _previewOnly = previewOnly; Owner = owner; _source = devices;
        DataContext = this; InitializeComponent();
        _source.CollectionChanged += Changed;
        RefreshDevices();
        Closed += (_, _) =>
        {
            _source.CollectionChanged -= Changed;
            foreach (var device in _observed) device.PropertyChanged -= DeviceChanged;
            _observed.Clear();
        };
    }
    internal static DeviceViewModel? Show(Window owner, ObservableCollection<DeviceViewModel> devices)
    { var window = new AirPlayDeviceSelectionWindow(owner, devices); return window.ShowDialog() == true ? window.SelectedDevice : null; }
    internal static void ShowDeveloperPreview(Window owner) =>
        new AirPlayDeviceSelectionWindow(owner, [], previewOnly: true).Show();
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDevices();
    private void DeviceChanged(object? sender, PropertyChangedEventArgs e) => RefreshDevices();
    private void RefreshDevices()
    {
        foreach (var old in _observed.Where(d => !_source.Contains(d)).ToArray())
        { old.PropertyChanged -= DeviceChanged; _observed.Remove(old); }
        foreach (var device in _source.Where(d => !_observed.Contains(d)))
        { _observed.Add(device); device.PropertyChanged += DeviceChanged; }
        var available = _source.Where(d => d.IsWireless && !d.IsMediaCast && d.Ready).ToArray();
        foreach (var old in Devices.Where(d => !available.Contains(d)).ToArray()) Devices.Remove(old);
        foreach (var device in available.Where(d => !Devices.Contains(d))) Devices.Add(device);
        if (SelectedDevice is not null && !Devices.Contains(SelectedDevice)) SelectedDevice = null;
        OnPropertyChanged(nameof(CanConfirm));
    }
    private void ConfirmClick(object sender, RoutedEventArgs e) { if (CanConfirm) DialogResult = true; }
    private void CancelClick(object sender, RoutedEventArgs e)
    { if (_previewOnly) Close(); else DialogResult = false; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
