using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IPhoneMirror.UI.Animations;

// Short, replaceable transitions for individual surfaces. Never animate layout or scale text.
public static class SurfaceMotion
{
    public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached(
        "Lift", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false, LiftChanged));
    private static readonly DependencyProperty LiftShiftProperty = DependencyProperty.RegisterAttached(
        "LiftShift", typeof(TranslateTransform), typeof(SurfaceMotion));
    public static void SetLift(DependencyObject o, bool v) => o.SetValue(LiftProperty, v);
    public static bool GetLift(DependencyObject o) => (bool)o.GetValue(LiftProperty);
    private static void LiftChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;
        element.MouseEnter -= Hover; element.MouseLeave -= Hover;
        element.GotKeyboardFocus -= Focus; element.LostKeyboardFocus -= Focus;
        element.IsEnabledChanged -= Enable; element.Unloaded -= LiftUnloaded;
        ResetLift(element);
        if (e.NewValue is not true) return;
        element.MouseEnter += Hover; element.MouseLeave += Hover;
        element.GotKeyboardFocus += Focus; element.LostKeyboardFocus += Focus;
        element.IsEnabledChanged += Enable; element.Unloaded += LiftUnloaded;
    }
    private static void Hover(object sender, System.Windows.Input.MouseEventArgs e) => UpdateLift((FrameworkElement)sender);
    private static void Focus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => UpdateLift((FrameworkElement)sender);
    private static void Enable(object sender, DependencyPropertyChangedEventArgs e) => UpdateLift((FrameworkElement)sender);
    private static void LiftUnloaded(object sender, RoutedEventArgs e) => ResetLift((FrameworkElement)sender);
    private static void ResetLift(FrameworkElement element)
    {
        if (element.GetValue(LiftShiftProperty) is TranslateTransform shift)
        { shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = 0; }
    }
    private static void UpdateLift(FrameworkElement element)
    {
        if (!element.IsLoaded || !SystemParameters.ClientAreaAnimation) { ResetLift(element); return; }
        if (element.GetValue(LiftShiftProperty) is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            var group = new TransformGroup();
            if (element.RenderTransform is { } existing && existing != Transform.Identity) group.Children.Add(existing);
            group.Children.Add(shift); element.RenderTransform = group; element.SetValue(LiftShiftProperty, shift);
        }
        var target = element.IsEnabled && (element.IsMouseOver || element.IsKeyboardFocusWithin) ? -2d : 0d;
        var current = shift.Y;
        shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = target;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SurfaceMotion), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(SurfaceMotion), new PropertyMetadata(0d));
    public static readonly DependencyProperty RefreshKeyProperty = DependencyProperty.RegisterAttached(
        "RefreshKey", typeof(object), typeof(SurfaceMotion), new PropertyMetadata(null, Refresh));
    private static readonly DependencyProperty ShiftProperty = DependencyProperty.RegisterAttached(
        "Shift", typeof(TranslateTransform), typeof(SurfaceMotion));
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetDelay(DependencyObject o, double v) => o.SetValue(DelayProperty, v);
    public static double GetDelay(DependencyObject o) => (double)o.GetValue(DelayProperty);
    public static void SetRefreshKey(DependencyObject o, object v) => o.SetValue(RefreshKeyProperty, v);
    public static object GetRefreshKey(DependencyObject o) => o.GetValue(RefreshKeyProperty);
    private static void Changed(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement element) return;
        element.Loaded -= Loaded; element.Unloaded -= Unloaded; element.IsVisibleChanged -= Visible;
        Stop(element);
        if (e.NewValue is not true) return;
        element.Loaded += Loaded; element.Unloaded += Unloaded; element.IsVisibleChanged += Visible;
    }
    private static void Refresh(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        // Object-valued keys can be reboxed by bindings during a data refresh.
        // Only a different identity should replay the entrance animation.
        if (!Equals(e.OldValue, e.NewValue) && o is FrameworkElement element && GetEnabled(element)) Play(element);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Play((FrameworkElement)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((FrameworkElement)sender);
    private static void Visible(object sender, DependencyPropertyChangedEventArgs e)
    { if (e.NewValue is true) Play((FrameworkElement)sender); else Stop((FrameworkElement)sender); }
    private static void Stop(FrameworkElement element)
    {
        if (element.GetValue(ShiftProperty) is not TranslateTransform shift) return;
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);
    }
    private static void Play(FrameworkElement element)
    {
        Stop(element);
        if (!element.IsLoaded || !element.IsVisible || !SystemParameters.ClientAreaAnimation) return;
        if (element.GetValue(ShiftProperty) is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            var group = new TransformGroup();
            if (element.RenderTransform is { } existing && existing != Transform.Identity) group.Children.Add(existing);
            group.Children.Add(shift); element.RenderTransform = group; element.SetValue(ShiftProperty, shift);
        }
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(GetDelay(element), 0, 180));
        var duration = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Keyframes hold the entrance pose throughout the stagger, avoiding a one-frame flash.
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, delay));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame((double)element.GetAnimationBaseValue(UIElement.OpacityProperty), delay + duration, ease));
        var move = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        move.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, TimeSpan.Zero));
        move.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, delay));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(0, delay + duration, ease));
        Timeline.SetDesiredFrameRate(fade, 60); Timeline.SetDesiredFrameRate(move, 60);
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        shift.BeginAnimation(TranslateTransform.YProperty, move, HandoffBehavior.SnapshotAndReplace);
    }
}
