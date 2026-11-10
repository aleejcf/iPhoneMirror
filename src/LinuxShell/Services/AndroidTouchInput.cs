// SPDX-License-Identifier: GPL-3.0-only
// Maps gestures on the clean V4L2 preview into Android's current display space.

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;

namespace IPhoneMirror.LinuxShell.Services;

internal sealed class AndroidTouchInput(string serial, uint canvasWidth,
    uint canvasHeight) : IDisposable
{
    private sealed record DisplayGeometry(int Left, int Top, int Width, int Height);

    private static readonly Regex ViewportArray = new(
        @"displayId\s*=\s*0[^\r\n]*?logicalFrame\s*=\s*\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ViewportRect = new(
        @"displayId\s*=\s*0[^\r\n]*?logicalFrame\s*=\s*Rect\(\s*(-?\d+)\s*,\s*(-?\d+)\s*-\s*(-?\d+)\s*,\s*(-?\d+)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gestureGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly object _diagnosticGate = new();
    private DisplayGeometry? _geometry;
    private Task? _refreshTask;
    private string? _lastDiagnostic;
    private bool _disposed;

    internal void Start() => _refreshTask ??= Task.Run(RefreshGeometryLoopAsync);

    internal async Task SendGestureAsync(Point start, Point end, TimeSpan duration)
    {
        lock (_lifecycleGate)
        {
            if (_disposed || !_gestureGate.Wait(0)) return;
        }
        try
        {
            var geometry = Volatile.Read(ref _geometry);
            if (geometry is null)
            {
                geometry = await ReadDisplayGeometryAsync(_stop.Token);
                Volatile.Write(ref _geometry, geometry);
            }
            if (!TryMapPoint(start, geometry, clamp: false, out var deviceStart))
            {
                LogOnce("El gesto empezó fuera del área visible del teléfono.");
                return;
            }
            if (!TryMapPoint(end, geometry, clamp: true, out var deviceEnd)) return;

            var deltaX = deviceEnd.X - deviceStart.X;
            var deltaY = deviceEnd.Y - deviceStart.Y;
            var isTap = Math.Abs(deltaX) + Math.Abs(deltaY) < 12;
            var arguments = isTap
                ? new[] { "-s", serial, "shell", "input", "tap",
                    deviceStart.X.ToString(CultureInfo.InvariantCulture),
                    deviceStart.Y.ToString(CultureInfo.InvariantCulture) }
                : new[] { "-s", serial, "shell", "input", "swipe",
                    deviceStart.X.ToString(CultureInfo.InvariantCulture),
                    deviceStart.Y.ToString(CultureInfo.InvariantCulture),
                    deviceEnd.X.ToString(CultureInfo.InvariantCulture),
                    deviceEnd.Y.ToString(CultureInfo.InvariantCulture),
                    Math.Clamp((int)duration.TotalMilliseconds, 80, 2000)
                        .ToString(CultureInfo.InvariantCulture) };

            var result = await RunAdbAsync(arguments, _stop.Token);
            if (result.ExitCode != 0)
            {
                var detail = result.StandardError.Trim().Replace(serial,
                    "[dispositivo]", StringComparison.Ordinal);
                LogOnce(string.IsNullOrWhiteSpace(detail)
                    ? $"ADB terminó con código {result.ExitCode} al enviar el gesto."
                    : $"ADB no pudo enviar el gesto: {detail}");
            }
            else
            {
                lock (_diagnosticGate) _lastDiagnostic = null;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogOnce($"No se pudo enviar el gesto Android ({error.GetType().Name}): "
                + error.Message.Replace(serial, "[dispositivo]", StringComparison.Ordinal));
        }
        finally { _gestureGate.Release(); }
    }

    private async Task RefreshGeometryLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var geometry = await ReadDisplayGeometryAsync(_stop.Token);
                Volatile.Write(ref _geometry, geometry);
                lock (_diagnosticGate) _lastDiagnostic = null;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                LogOnce($"No se pudo leer la geometría de Android ({error.GetType().Name}).");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        }
    }

    private async Task<DisplayGeometry> ReadDisplayGeometryAsync(
        CancellationToken cancellationToken)
    {
        var result = await RunAdbAsync(new[] { "-s", serial, "shell", "dumpsys", "input" },
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("ADB no pudo consultar la pantalla Android.");

        foreach (var expression in new[] { ViewportArray, ViewportRect })
        {
            var match = expression.Match(result.StandardOutput);
            if (!match.Success) continue;
            var left = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var top = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var right = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var bottom = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            var width = right - left;
            var height = bottom - top;
            if (width is > 0 and <= 32768 && height is > 0 and <= 32768)
                return new DisplayGeometry(left, top, width, height);
        }
        throw new InvalidOperationException(
            "Android no informó el área lógica de su pantalla; no se envió el toque.");
    }

    private bool TryMapPoint(Point canvasPoint, DisplayGeometry geometry,
        bool clamp, out (int X, int Y) devicePoint)
    {
        devicePoint = default;
        var scale = Math.Min((double)canvasWidth / geometry.Width,
            (double)canvasHeight / geometry.Height);
        if (!double.IsFinite(scale) || scale <= 0) return false;
        var imageWidth = geometry.Width * scale;
        var imageHeight = geometry.Height * scale;
        var padX = (canvasWidth - imageWidth) / 2;
        var padY = (canvasHeight - imageHeight) / 2;
        var x = canvasPoint.X - padX;
        var y = canvasPoint.Y - padY;
        if (!clamp && (x < 0 || y < 0 || x >= imageWidth || y >= imageHeight))
            return false;
        var normalizedX = Math.Clamp(x / imageWidth, 0, 1);
        var normalizedY = Math.Clamp(y / imageHeight, 0, 1);
        devicePoint = (
            geometry.Left + (int)Math.Round(normalizedX * (geometry.Width - 1)),
            geometry.Top + (int)Math.Round(normalizedY * (geometry.Height - 1)));
        return true;
    }

    private static async Task<(int ExitCode, string StandardOutput,
        string StandardError)> RunAdbAsync(IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("adb")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("No se pudo iniciar ADB.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            throw new TimeoutException("ADB tardó demasiado en responder.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            throw;
        }
        return (process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private void LogOnce(string diagnostic)
    {
        lock (_diagnosticGate)
        {
            if (diagnostic == _lastDiagnostic) return;
            _lastDiagnostic = diagnostic;
        }
        Console.Error.WriteLine($"Android touch: {diagnostic}");
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
        }
        _ = Task.Run(DisposeResourcesAsync);
    }

    private async Task DisposeResourcesAsync()
    {
        try { if (_refreshTask is not null) await _refreshTask; }
        catch (OperationCanceledException) { }
        var releaseGate = false;
        try { releaseGate = await _gestureGate.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (ObjectDisposedException) { return; }
        if (!releaseGate) return;
        _gestureGate.Release();
        _gestureGate.Dispose();
        _stop.Dispose();
    }
}
