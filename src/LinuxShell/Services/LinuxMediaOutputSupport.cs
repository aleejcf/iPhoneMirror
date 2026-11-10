// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;

namespace IPhoneMirror.LinuxShell.Services;

internal static class LinuxPendingRecordingStore
{
    internal static string CreateStagingPath(string completedPath)
    {
        var directory = Path.GetDirectoryName(completedPath);
        var name = Path.GetFileNameWithoutExtension(completedPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Se requiere una ruta de grabación completa.",
                nameof(completedPath));
        return Path.Combine(directory, name + ".partial.mp4");
    }
}

internal static class LinuxMediaMessages
{
    internal static string Get(string key) => key switch
    {
        "FfmpegAudioInputTimeout" => "FFmpeg no abrió la entrada de audio a tiempo.",
        "FfmpegEncoderSizeUnsupported" => "El codificador H.264 no admite esta resolución.",
        "FfmpegEncoderUnavailable" => "El codificador H.264 requerido no está disponible en FFmpeg.",
        "FfmpegExitedFormat" => "FFmpeg terminó durante la salida con código {0}.",
        "FfmpegNoUsableEncoderFormat" => "No se encontró un codificador H.264 funcional en {0}.",
        "FfmpegNotFound" => "No se encontró FFmpeg. Instala el paquete ffmpeg.",
        "FfmpegProbeFailed" => "No se pudieron consultar las capacidades de FFmpeg.",
        "FfmpegProtocolUnavailable" => "El protocolo o muxer requerido no está disponible en FFmpeg.",
        "FfmpegStartFailed" => "No se pudo iniciar FFmpeg.",
        "FfmpegStartupExitedFormat" => "FFmpeg terminó al iniciar con código {0}.",
        "MediaOutputAlreadyRunning" => "Esta salida ya está activa.",
        "MediaOutputAudioInvalid" => "El paquete PCM recibido está vacío o incompleto.",
        "MediaOutputAudioStalled" => "La secuencia de audio dejó de avanzar.",
        "MediaOutputAudioUnsupported" => "El formato PCM del dispositivo no es compatible.",
        "MediaOutputDestinationRequired" => "Indica dónde guardar o enviar la salida.",
        "MediaOutputFrameInvalid" => "El cuadro NV12 recibido no coincide con el perfil de salida.",
        "MediaOutputFrameTimeout" => "La captura dejó de entregar cuadros de video.",
        "MediaOutputInvalidDimensions" => "La resolución y la frecuencia de cuadros no son válidas.",
        "MediaOutputRtmpAddressRequired" => "Indica una dirección RTMP válida.",
        "MediaOutputSrtAddressRequired" => "Indica una dirección SRT válida.",
        "MediaOutputWhipAddressRequired" => "Indica una dirección WHIP válida.",
        "MicrophoneAudioRequired" => "FFmpeg necesita un codificador AAC u Opus para mezclar el micrófono.",
        "MicrophoneCaptureStopped" => "PipeWire detuvo la captura del micrófono.",
        "MicrophoneStartFailed" => "No se pudo iniciar pw-record para capturar el micrófono.",
        "RecordingFinalizeTimeout" => "Se agotó el tiempo para finalizar el archivo MP4.",
        "StreamingStopTimeout" => "Se agotó el tiempo para detener la transmisión.",
        _ => key,
    };

    internal static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo("es-ES"), Get(key), arguments);
}

internal static class LinuxMediaOutputLog
{
    private static readonly ConcurrentDictionary<string, byte> Seen = new();

    internal static void Exception(string category, string name, Exception error,
        params (string Name, object? Value)[] details) =>
        Write(category, name, error, details);

    internal static void ExceptionOnce(string key, string category, string name,
        Exception error, params (string Name, object? Value)[] details)
    {
        if (Seen.TryAdd(key, 0)) Write(category, name, error, details);
    }

    internal static void Info(string category, string name,
        params (string Name, object? Value)[] details) =>
        Write(category, name, null, details);

    private static void Write(string category, string name, Exception? error,
        IReadOnlyList<(string Name, object? Value)> details)
    {
        var fields = string.Join(" ", details.Select(detail =>
            $"{detail.Name}={detail.Value}"));
        Console.Error.WriteLine($"media_output {category}/{name} " +
            (error is null ? string.Empty : $"error={error.Message} ") + fields);
    }
}

// FFmpeg consumes PCM from a private FIFO. Opening it non-blocking lets the
// caller observe cancellation or an FFmpeg startup failure without leaving a
// thread blocked forever in open(2).
internal sealed class LinuxAudioFifo : IDisposable
{
    private const int OpenWrite = 1;
    private const int OpenNonBlocking = 0x800;
    private const int SetFileStatusFlags = 4;
    private const uint OwnerReadWrite = 0x180;
    private const int ErrorNoEntry = 2;
    private const int ErrorNoReader = 6;
    private bool _disposed;

    internal string Path { get; }

    internal LinuxAudioFifo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"iphone-mirror-audio-{Environment.ProcessId}-{Guid.NewGuid():N}.fifo");
        if (MakeFifo(Path, OwnerReadWrite) != 0)
            throw new IOException($"No se pudo crear la entrada PCM: errno {Marshal.GetLastPInvokeError()}.");
    }

    internal async Task<FileStream> OpenWriterAsync(Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"FFmpeg terminó con código {process.ExitCode} antes de abrir el audio.");

            var descriptor = Open(Path, OpenWrite | OpenNonBlocking);
            if (descriptor >= 0)
            {
                if (Fcntl(descriptor, SetFileStatusFlags, 0) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    Close(descriptor);
                    throw new IOException($"No se pudo configurar la entrada PCM: errno {error}.");
                }
                return new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true),
                    FileAccess.Write, 64 * 1024, isAsync: false);
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno is not (ErrorNoEntry or ErrorNoReader))
                throw new IOException($"No se pudo abrir la entrada PCM: errno {errno}.");
            await Task.Delay(20, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int value);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
