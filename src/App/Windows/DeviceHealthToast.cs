using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IPhoneMirror.App.Windows;

// Small, non-activating corner notice for battery/heat warnings. It never takes
// focus from the game or OBS and closes itself, unlike the modal error dialog.
internal sealed class DeviceHealthToast : Window
{
    private static DeviceHealthToast? _current;

    private DeviceHealthToast(string title, string body, bool critical)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var accent = critical ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0xFF, 0x8A, 0x1F);
        Content = new Border
        {
            Width = 340,
            Padding = new Thickness(16, 12, 16, 14),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x24)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(0, 0, 0, 3),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(accent), TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = body, FontSize = 13, Margin = new Thickness(0, 6, 0, 0),
                        Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
        MouseLeftButtonUp += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(critical ? 20 : 12) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    internal static void Show(string title, string body, bool critical)
    {
        _current?.Close();
        var toast = new DeviceHealthToast(title, body, critical);
        toast.Closed += (_, _) => { if (ReferenceEquals(_current, toast)) _current = null; };
        _current = toast;
        toast.Show();
    }
}
