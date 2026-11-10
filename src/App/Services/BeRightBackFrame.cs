using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPhoneMirror.App.Interop;

namespace IPhoneMirror.App.Services;

// Builds the BGRA "be right back" frame the virtual camera shows while the
// device feed is missing, so OBS/streams show a card instead of black. A user
// image at %LOCALAPPDATA%\iPhoneMirror\ya-regreso.png replaces the default card.
internal static class BeRightBackFrame
{
    internal static readonly string CustomImagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror", "ya-regreso.png");

    private static readonly object CacheLock = new();
    private static VideoFrame? _cached;
    private static DateTime _cachedImageStamp;

    internal static VideoFrame Get(uint width, uint height)
    {
        var stamp = File.Exists(CustomImagePath)
            ? File.GetLastWriteTimeUtc(CustomImagePath) : DateTime.MinValue;
        lock (CacheLock)
        {
            if (_cached is { } cached && cached.Width == width && cached.Height == height &&
                stamp == _cachedImageStamp)
                return cached;
        }
        // WPF rendering needs an STA thread; the camera pump runs on the pool.
        VideoFrame? frame = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { frame = Render(width, height); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            DiagnosticLogger.ExceptionOnce("be-right-back-render", "virtual_camera",
                "be_right_back_render_failed", failure);
            frame = Solid(width, height);
        }
        lock (CacheLock)
        {
            _cached = frame;
            _cachedImageStamp = stamp;
        }
        return frame!;
    }

    private static VideoFrame Render(uint width, uint height)
    {
        var w = (int)width;
        var h = (int)height;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, w, h);
            context.DrawRectangle(new LinearGradientBrush(
                Color.FromRgb(0x14, 0x10, 0x2A), Color.FromRgb(0x05, 0x05, 0x0C), 90), null, bounds);
            if (TryLoadCustomImage() is { } image)
            {
                // Uniform fit, centered.
                var scale = Math.Min(w / image.Width, h / image.Height);
                var size = new Size(image.Width * scale, image.Height * scale);
                context.DrawImage(image, new Rect(
                    new Point((w - size.Width) / 2, (h - size.Height) / 2), size));
            }
            else
            {
                var unit = Math.Min(w, h) / 100.0;
                var typeface = new Typeface(new FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var title = Text("YA REGRESO", typeface, unit * 11, Brushes.White);
                var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F));
                var subtitle = Text("Reconectando el iPhone…",
                    new Typeface("Segoe UI"), unit * 4.2, accent);
                var footer = Text("No te vayas, ahorita volvemos 🎮",
                    new Typeface("Segoe UI"), unit * 3.4,
                    new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD8)));
                var centerY = h / 2.0;
                context.DrawRoundedRectangle(accent, null, new Rect(
                    (w - unit * 30) / 2, centerY - title.Height / 2 - unit * 4,
                    unit * 30, unit * 1.2), unit * 0.6, unit * 0.6);
                context.DrawText(title, new Point((w - title.Width) / 2,
                    centerY - title.Height / 2));
                context.DrawText(subtitle, new Point((w - subtitle.Width) / 2,
                    centerY + title.Height / 2 + unit * 2));
                context.DrawText(footer, new Point((w - footer.Width) / 2,
                    h - footer.Height - unit * 8));
            }
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = w * 4;
        var pixels = new byte[stride * h];
        bitmap.CopyPixels(pixels, stride, 0);
        return new VideoFrame(width, height, (uint)stride, 0, pixels);
    }

    private static FormattedText Text(string value, Typeface typeface, double size, Brush brush) =>
        new(value, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, Math.Max(size, 8), brush, 1.0);

    private static BitmapSource? TryLoadCustomImage()
    {
        try
        {
            if (!File.Exists(CustomImagePath)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(CustomImagePath);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("be-right-back-image", "virtual_camera",
                "be_right_back_image_failed", error);
            return null;
        }
    }

    private static VideoFrame Solid(uint width, uint height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x2A;
            pixels[index + 1] = 0x10;
            pixels[index + 2] = 0x14;
            pixels[index + 3] = 0xFF;
        }
        return new VideoFrame(width, height, width * 4, 0, pixels);
    }
}
