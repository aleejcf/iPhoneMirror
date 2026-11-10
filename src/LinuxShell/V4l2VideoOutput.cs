// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;

namespace IPhoneMirror.LinuxShell;

// Feeds the latest decoded iOS frame to FFmpeg at 30 FPS. Core scales and
// letterboxes into a fixed NV12 canvas, so both the V4L2 format and FFmpeg input
// stay stable when the phone rotates.
internal sealed class V4l2VideoOutput : IAsyncDisposable
{
    private const int FramesPerSecond = 30;
    private readonly string _device;
    private readonly Func<uint, uint, byte[]?> _frameProvider;
    private readonly uint _canvasWidth;
    private readonly uint _canvasHeight;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _processLock = new();
    private Process? _activeEncoder;
    private Task? _pump;
    private bool _disposed;

    internal V4l2VideoOutput(string device, string profile,
        Func<uint, uint, byte[]?> frameProvider)
    {
        _device = device;
        _frameProvider = frameProvider;
        _canvasWidth = profile == "horizontal" ? 1920U : 1080U;
        _canvasHeight = profile == "horizontal" ? 1080U : 1920U;
    }

    internal event Action<string>? Failed;

    internal void Start()
    {
        if (_pump is not null) return;
        _pump = Task.Run(() => PumpAsync(_stop.Token));
    }

    internal void Stop()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        lock (_processLock)
        {
            try
            {
                if (_activeEncoder is { HasExited: false })
                    _activeEncoder.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        Process? encoder = null;
        Task<string>? standardError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pixels = _frameProvider(_canvasWidth, _canvasHeight);
                if (pixels is null)
                {
                    await Task.Delay(FramesPerSecondDelay(), cancellationToken);
                    continue;
                }
                if (encoder is null || encoder.HasExited)
                {
                    if (encoder is not null)
                    {
                        var previousEncoder = encoder;
                        encoder = null;
                        ForgetEncoder(previousEncoder);
                        await CloseEncoderAsync(previousEncoder, standardError,
                            cancellationToken, checkExitCode: true);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    encoder = StartEncoder(out standardError);
                    lock (_processLock) _activeEncoder = encoder;
                }

                await encoder.StandardInput.BaseStream.WriteAsync(pixels,
                    cancellationToken);
                await Task.Delay(FramesPerSecondDelay(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var details = exception.Message;
                if (encoder is not null)
                {
                    ForgetEncoder(encoder);
                    try
                    {
                        await CloseEncoderAsync(encoder, standardError,
                            cancellationToken, checkExitCode: false);
                    }
                    catch (Exception cleanupError)
                    {
                        details += $"; encoder cleanup: {cleanupError.Message}";
                    }
                    encoder = null;
                }
                if (standardError is not null)
                {
                    try
                    {
                        var error = (await standardError).Trim();
                        if (!string.IsNullOrEmpty(error)) details += $"; FFmpeg: {error}";
                    }
                    catch (InvalidOperationException) { }
                }
                Failed?.Invoke(details);
            }
        }
        finally
        {
            if (encoder is not null)
            {
                try
                {
                    await CloseEncoderAsync(encoder, standardError,
                        cancellationToken, checkExitCode: false);
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        Console.Error.WriteLine($"V4L2 cleanup: {exception.Message}");
                }
            }
        }
    }

    private Process StartEncoder(out Task<string> standardError)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "rawvideo", "-pixel_format", "nv12",
            "-video_size", $"{_canvasWidth}x{_canvasHeight}",
            "-framerate", FramesPerSecond.ToString(), "-i", "pipe:0",
            "-an", "-r", FramesPerSecond.ToString(),
            "-pix_fmt", "yuv420p", "-vcodec", "rawvideo", "-f", "v4l2",
            _device,
        })
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("No se pudo iniciar FFmpeg.");
        }
        standardError = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task CloseEncoderAsync(Process process,
        Task<string>? standardError, CancellationToken cancellationToken,
        bool checkExitCode)
    {
        try { process.StandardInput.Close(); }
        catch (InvalidOperationException) { }
        catch (IOException) { }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        var error = standardError is null ? string.Empty : (await standardError).Trim();
        var code = process.ExitCode;
        process.Dispose();
        if (checkExitCode && !cancellationToken.IsCancellationRequested && code != 0)
            throw new InvalidOperationException(
                string.IsNullOrEmpty(error) ? $"FFmpeg terminó con código {code}." : error);
    }

    private static TimeSpan FramesPerSecondDelay() =>
        TimeSpan.FromMilliseconds(1000.0 / FramesPerSecond);

    private void ForgetEncoder(Process process)
    {
        lock (_processLock)
        {
            if (ReferenceEquals(_activeEncoder, process)) _activeEncoder = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        if (_pump is not null)
        {
            try { await _pump; }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
