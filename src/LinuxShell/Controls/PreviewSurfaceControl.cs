// SPDX-License-Identifier: GPL-3.0-only
//
// The preview control: imports the image Core exports and shows it.
//
// This is the Linux counterpart of attaching a D3D11 preview to an HWND. The
// window never draws the frame itself; it binds the exported Vulkan image and
// lets the compositor sample it, with a semaphore in each direction so it never
// samples a half-drawn one.

using System.Globalization;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using IPhoneMirror.LinuxShell.Interop;

namespace IPhoneMirror.LinuxShell.Controls;

internal sealed class PreviewSurfaceControl : Control
{
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private ICompositionImportedGpuImage? _importedImage;
    private ICompositionImportedGpuSemaphore? _renderCompleted;
    private ICompositionImportedGpuSemaphore? _available;
    private PreviewSurface _described;
    private byte[]? _deviceUuid;
    private bool _ready;
    private bool _tornDown;
    private IPointer? _gesturePointer;
    private Point _gestureStart;
    private long _gestureStartedAt;

    internal string Diagnostic { get; private set; } = "not attached yet";
    internal bool IsReady => _ready;
    internal event EventHandler? Ready;
    internal event Action<string>? InitializationFailed;
    internal event Action<Point, Point, TimeSpan>? PointerGestureCompleted;
    internal bool ForwardPointerGestures { get; set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = InitializeAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Teardown();
    }

    private async Task InitializeAsync()
    {
        _tornDown = false;
        var report = new StringBuilder();
        try
        {
            CorePreview.CheckAbi();
            report.Append("abi size: match").AppendLine();

            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual is null)
            {
                Fail(report, "the control has no composition visual");
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }
            _compositor = selfVisual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (_interop is null)
            {
                Fail(report, "this backend exposes no GPU interop");
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }

            const string ImageType =
                KnownPlatformGraphicsExternalImageHandleTypes
                    .VulkanOpaquePosixFileDescriptor;
            if (!_interop.SupportedImageHandleTypes.Contains(ImageType))
            {
                Fail(report, "the compositor will not accept an opaque POSIX "
                    + "file descriptor, so an exported Vulkan image cannot be bound");
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }
            var synchronization = _interop.GetSynchronizationCapabilities(ImageType);
            if ((synchronization &
                CompositionGpuImportedImageSynchronizationCapabilities.Semaphores) == 0)
            {
                Fail(report, "the compositor cannot synchronise an imported image "
                    + "with binary semaphores, which is the only mode Core drives");
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }

            _deviceUuid = _interop.DeviceUuid;
            if (!OpenPreview(report, _deviceUuid))
            {
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }

            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            FitVisual();
            ElementComposition.SetElementChildVisual(this, _visual);

            if (!ImportSurface(report))
            {
                InitializationFailed?.Invoke(Diagnostic);
                return;
            }

            _ready = true;
            Diagnostic = report.ToString();
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Fail(report, $"{exception.GetType().Name}: {exception.Message}");
            InitializationFailed?.Invoke(Diagnostic);
        }
    }

    private bool ImportSurface(StringBuilder report)
    {
        if (_interop is null) return false;
        const string ImageType = KnownPlatformGraphicsExternalImageHandleTypes
            .VulkanOpaquePosixFileDescriptor;
        var properties = new PlatformGraphicsExternalImageProperties
        {
            Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
            Width = (int)_described.Width,
            Height = (int)_described.Height,
            MemorySize = _described.AllocationSize,
            MemoryOffset = _described.AllocationOffset,
        };
        _importedImage = _interop.ImportImage(
            new PlatformHandle(_described.MemoryFd, ImageType), properties);
        _renderCompleted = _interop.ImportSemaphore(new PlatformHandle(
            _described.RenderCompletedFd,
            KnownPlatformGraphicsExternalSemaphoreHandleTypes
                .VulkanOpaquePosixFileDescriptor));
        _available = _interop.ImportSemaphore(new PlatformHandle(
            _described.AvailableFd,
            KnownPlatformGraphicsExternalSemaphoreHandleTypes
                .VulkanOpaquePosixFileDescriptor));
        report.Append("import image and semaphores: ok").AppendLine();
        _ready = true;
        FitVisual();
        return true;
    }

    // Recreate the exported image at the settled decoder geometry after a
    // rotation, so OBS receives the screen without padding from the old aspect.
    internal async Task<bool> SetFrameGeometryAsync(uint width, uint height)
    {
        // A previous surface recreation can fail transiently. Equal dimensions
        // do not mean that the compositor import is ready; allow the next stable
        // geometry tick to retry opening Core and importing the image/semaphores.
        if (_ready && width == TargetWidth && height == TargetHeight) return true;

        _ready = false;
        // First return the shared target to Core, then detach the old importer.
        // Destroying these resources in the opposite order can leave the GPU
        // waiting on a semaphore whose compositor has already been disposed.
        CorePreview.Close();
        var oldImage = _importedImage;
        var oldRenderCompleted = _renderCompleted;
        var oldAvailable = _available;
        _importedImage = null;
        _renderCompleted = null;
        _available = null;

        if (_compositor is not null)
        {
            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(this, _visual);
        }
        if (oldImage is not null) await oldImage.DisposeAsync();
        if (oldRenderCompleted is not null)
            await oldRenderCompleted.DisposeAsync();
        if (oldAvailable is not null) await oldAvailable.DisposeAsync();

        TargetWidth = width;
        TargetHeight = height;

        var report = new StringBuilder();
        if (!OpenPreview(report, _deviceUuid) || !ImportSurface(report))
        {
            Diagnostic = report.ToString();
            Console.Error.WriteLine(Diagnostic);
            return false;
        }
        Diagnostic = report.ToString();
        return true;
    }

    private unsafe bool OpenPreview(StringBuilder report, byte[]? deviceUuid)
    {
        report.Append(deviceUuid is null
            ? "compositor device uuid: unavailable"
            : $"compositor device uuid: {Convert.ToHexString(deviceUuid)}")
            .AppendLine();
        int status;
        if (deviceUuid is { Length: 16 })
        {
            fixed (byte* uuid = deviceUuid)
                status = CorePreview.Open(TargetWidth, TargetHeight, uuid);
        }
        else
        {
            status = CorePreview.Open(TargetWidth, TargetHeight, null);
        }
        if (status != 0)
        {
            Fail(report, $"im_linux_preview_open returned {status}");
            return false;
        }
        status = CorePreview.Describe(out _described);
        if (status != 0)
        {
            Fail(report, $"im_linux_preview_describe returned {status}");
            return false;
        }
        if (_described.Valid == 0)
        {
            Fail(report, "Core rendered but could not export the image on this "
                + "platform, so there is nothing to import");
            return false;
        }
        report.Append(string.Create(CultureInfo.InvariantCulture,
            $"surface: {_described.Width}x{_described.Height} vk_format="
            + $"{_described.VkFormat} size={_described.AllocationSize}"))
            .AppendLine();
        return true;
    }

    // Scales the visual instead of stretching the image: Core already
    // letterboxed into the target, so a second fit here would distort twice.
    private void FitVisual()
    {
        if (_visual is null || _described.Width == 0 || _described.Height == 0)
            return;
        var available = Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
            available = new Size(_described.Width, _described.Height);
        var scale = Math.Min(available.Width / _described.Width,
            available.Height / _described.Height);
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        _visual.Size = new Vector(_described.Width, _described.Height);
        _visual.Scale = new Vector3D(scale, scale, 1);
        _visual.Offset = new Vector3D(
            (available.Width - _described.Width * scale) / 2,
            (available.Height - _described.Height * scale) / 2, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        FitVisual();
        return result;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!ForwardPointerGestures ||
            (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
                e.Pointer.Type != PointerType.Touch) ||
            !TryMapToCanvas(e.GetPosition(this), clamp: false, out var canvasPoint))
            return;
        _gesturePointer = e.Pointer;
        _gestureStart = canvasPoint;
        _gestureStartedAt = Stopwatch.GetTimestamp();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ForwardPointerGestures || _gesturePointer != e.Pointer) return;
        var start = _gestureStart;
        var hasEnd = TryMapToCanvas(e.GetPosition(this), clamp: true, out var end);
        var duration = Stopwatch.GetElapsedTime(_gestureStartedAt);
        _gesturePointer = null;
        e.Pointer.Capture(null);
        if (!hasEnd) return;
        PointerGestureCompleted?.Invoke(start, end, duration);
        e.Handled = true;
    }

    private bool TryMapToCanvas(Point position, bool clamp, out Point canvasPoint)
    {
        canvasPoint = default;
        if (TargetWidth == 0 || TargetHeight == 0 ||
            Bounds.Width <= 0 || Bounds.Height <= 0) return false;
        var scale = Math.Min(Bounds.Width / TargetWidth,
            Bounds.Height / TargetHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            return false;
        var drawnWidth = TargetWidth * scale;
        var drawnHeight = TargetHeight * scale;
        var left = (Bounds.Width - drawnWidth) / 2;
        var top = (Bounds.Height - drawnHeight) / 2;
        var x = position.X - left;
        var y = position.Y - top;
        if (!clamp && (x < 0 || y < 0 || x >= drawnWidth || y >= drawnHeight))
            return false;
        canvasPoint = new Point(
            Math.Clamp(x / scale, 0, TargetWidth - 1),
            Math.Clamp(y / scale, 0, TargetHeight - 1));
        return true;
    }

    // Renders one NV12 frame through Core and hands the result to the compositor.
    internal async Task<bool> PresentAsync(byte[] frame, uint width, uint height)
    {
        // One message per condition: the first attempt showed a black window with
        // the status text unchanged, which is what a silent early return looks
        // like from the outside.
        if (!_ready) return NotReady("the control never became ready");
        if (_surface is null) return NotReady("no drawing surface");
        if (_importedImage is null) return NotReady("no imported image");
        if (_renderCompleted is null) return NotReady("no render-completed semaphore");
        if (_available is null) return NotReady("no available semaphore");
        int status;
        unsafe
        {
            fixed (byte* data = frame)
                status = CorePreview.PresentNv12(data, (ulong)frame.LongLength,
                    width, height);
        }
        if (status != 0)
        {
            Diagnostic = $"im_linux_preview_present_nv12 returned {status}";
            return false;
        }
        return await CommitAsync();
    }

    // Hands whatever Core last rendered to the compositor. Separate from
    // PresentAsync because the device path renders through the session's own
    // frame mailbox and only needs this half.
    internal async Task<bool> CommitAsync()
    {
        if (!_ready) return NotReady("the control never became ready");
        if (_surface is null) return NotReady("no drawing surface");
        if (_importedImage is null) return NotReady("no imported image");
        if (_renderCompleted is null) return NotReady("no render-completed semaphore");
        if (_available is null) return NotReady("no available semaphore");
        try
        {
            await _surface.UpdateWithSemaphoresAsync(_importedImage,
                _renderCompleted, _available);
        }
        catch (Exception exception)
        {
            Diagnostic = $"UpdateWithSemaphoresAsync: {exception.GetType().Name}: "
                + exception.Message;
            return false;
        }
        return true;
    }

    private bool NotReady(string reason)
    {
        Diagnostic = $"present skipped: {reason}";
        return false;
    }

    private void Fail(StringBuilder report, string reason)
    {
        report.Append(reason).AppendLine();
        Diagnostic = report.ToString();
        Teardown();
    }

    private void Teardown()
    {
        if (_tornDown) return;
        _tornDown = true;
        _ready = false;
        try
        {
            CorePreview.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"preview cleanup: {exception.Message}");
        }
        var image = _importedImage;
        var renderCompleted = _renderCompleted;
        var available = _available;
        _importedImage = null;
        _renderCompleted = null;
        _available = null;
        if (image is not null)
            _ = DisposeQuietlyAsync(image.DisposeAsync, "imported image");
        if (renderCompleted is not null)
            _ = DisposeQuietlyAsync(renderCompleted.DisposeAsync,
                "render-completed semaphore");
        if (available is not null)
            _ = DisposeQuietlyAsync(available.DisposeAsync,
                "available semaphore");
    }

    private static async Task DisposeQuietlyAsync(Func<ValueTask> dispose,
        string resource)
    {
        try
        {
            await dispose();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"preview cleanup ({resource}): "
                + exception.Message);
        }
    }

    // Defaults to the highest encoder tier verified on both test devices; the
    // shell overrides it to match the source when it is playing a file, which
    // keeps the acceptance run free of an unrelated letterbox.
    internal uint TargetWidth { get; set; } = 1206;
    internal uint TargetHeight { get; set; } = 2622;
}
