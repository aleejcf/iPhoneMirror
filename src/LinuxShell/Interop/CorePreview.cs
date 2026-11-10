// SPDX-License-Identifier: GPL-3.0-only
//
// P/Invoke for the preview C ABI in iPhoneMirror.Core.
//
// The struct mirrors iPhoneMirror::LinuxPreviewSurface field for field, and
// CheckAbi compares sizes before anything is called. That check exists because
// the S3 spike shipped a managed struct 144 bytes smaller than its native
// counterpart and the native side wrote past the end of it without complaint.

using System.Runtime.InteropServices;

namespace IPhoneMirror.LinuxShell.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct AudioPacketInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public ulong Sequence;
    public uint SampleRate;
    public ushort Channels;
    public ushort BitsPerSample;
}

internal sealed record AudioPacket(ulong Sequence, uint SampleRate,
    ushort Channels, ushort BitsPerSample, byte[] Pcm);

[StructLayout(LayoutKind.Sequential)]
internal struct CaptureOptions
{
    public uint StructSize;
    public uint ApiVersion;
    public uint RequestedWidth;
    public uint RequestedHeight;
    public uint TargetFps;
    public int PlayAudio;
    public float AudioVolume;
    public uint Reserved0;
    public uint Reserved1;
    public uint ProjectionMode;
    public uint DecoderPreference;
    public uint ColorOutputPreference;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PreviewSurface
{
    public int Valid;
    public int MemoryFd;
    public int RenderCompletedFd;
    public int AvailableFd;
    public uint Width;
    public uint Height;
    public uint VkFormat;
    public uint Reserved;
    public ulong AllocationSize;
    public ulong AllocationOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VideoFrameInfo
{
    public uint StructSize;
    public uint ApiVersion;
    public uint Width;
    public uint Height;
    public uint Stride;
    public uint PixelFormat;
    public long Timestamp100Ns;
}

internal static partial class CorePreview
{
    private const string Library = "iPhoneMirror.Core";

    [LibraryImport(Library, EntryPoint = "im_linux_preview_abi_size")]
    internal static partial uint AbiSize();

    // deviceUuid must be the compositor's 16-byte Vulkan device UUID, or null.
    // Not optional on a multi-GPU machine: an image exported from one physical
    // device cannot be imported by a compositor on another, and Avalonia only
    // notices when the first frame is presented.
    [LibraryImport(Library, EntryPoint = "im_linux_preview_open")]
    internal static unsafe partial int Open(uint width, uint height,
        byte* deviceUuid);

    [LibraryImport(Library, EntryPoint = "im_linux_preview_close")]
    internal static partial void Close();

    [LibraryImport(Library, EntryPoint = "im_linux_preview_describe")]
    internal static partial int Describe(out PreviewSurface surface);

    [LibraryImport(Library, EntryPoint = "im_linux_preview_present_nv12")]
    internal static unsafe partial int PresentNv12(byte* data, ulong size,
        uint width, uint height);

    [LibraryImport(Library, EntryPoint = "im_linux_preview_present_latest")]
    internal static partial int PresentLatest();

    [LibraryImport(Library, EntryPoint = "im_linux_preview_latest_geometry")]
    internal static partial int LatestGeometry(out uint width, out uint height);

    [LibraryImport(Library, EntryPoint = "im_linux_capture_copy_next_audio_packet")]
    private static partial int CopyNextAudioPacketNative(ulong afterSequence,
        ref AudioPacketInfo info, IntPtr buffer, ref uint bufferSize);

    internal static AudioPacket? CopyNextAudioPacket(ulong afterSequence)
    {
        var info = new AudioPacketInfo
        {
            StructSize = (uint)Marshal.SizeOf<AudioPacketInfo>(),
        };
        uint size = 0;
        var result = CopyNextAudioPacketNative(afterSequence, ref info,
            IntPtr.Zero, ref size);
        if (result == -7) return null;
        if (result != -3 || size == 0 || size > 4U * 1024U * 1024U)
            throw new InvalidOperationException(
                $"No se pudo consultar el paquete de audio: {result}");

        var nativeBuffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var actualSize = size;
            result = CopyNextAudioPacketNative(afterSequence, ref info,
                nativeBuffer, ref actualSize);
            if (result != 0 || actualSize != size)
                throw new InvalidOperationException(
                    $"No se pudo copiar el paquete de audio: {result}");
            var pcm = new byte[checked((int)actualSize)];
            Marshal.Copy(nativeBuffer, pcm, 0, pcm.Length);
            return new AudioPacket(info.Sequence, info.SampleRate,
                info.Channels, info.BitsPerSample, pcm);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    [LibraryImport(Library, EntryPoint = "im_linux_preview_copy_latest_nv12")]
    private static partial int CopyLatestNv12Native(uint outputWidth,
        uint outputHeight, ref VideoFrameInfo info, IntPtr buffer,
        ref uint bufferSize);

    internal static long? LatestNv12Timestamp(uint width, uint height)
    {
        var info = new VideoFrameInfo
        {
            StructSize = (uint)Marshal.SizeOf<VideoFrameInfo>(),
        };
        uint capacity = 0;
        var result = CopyLatestNv12Native(width, height, ref info, IntPtr.Zero,
            ref capacity);
        if (result == -6) return null;
        if (result != -3 || capacity == 0 || capacity > 256U * 1024U * 1024U)
            throw new InvalidOperationException(
                $"No se pudo consultar el cuadro NV12: {result}");
        return info.Timestamp100Ns;
    }

    internal static byte[]? CopyLatestNv12Frame(uint width, uint height,
        out long timestamp100Ns)
    {
        timestamp100Ns = 0;
        var info = new VideoFrameInfo
        {
            StructSize = (uint)Marshal.SizeOf<VideoFrameInfo>(),
        };
        uint capacity = 0;
        var result = CopyLatestNv12Native(width, height, ref info, IntPtr.Zero,
            ref capacity);
        if (result == -6) return null;
        if (result != -3 || capacity == 0 || capacity > 256U * 1024U * 1024U)
            throw new InvalidOperationException(
                $"No se pudo obtener el cuadro NV12: {result}");

        var nativeBuffer = Marshal.AllocHGlobal(checked((int)capacity));
        try
        {
            var actualSize = capacity;
            result = CopyLatestNv12Native(width, height, ref info, nativeBuffer,
                ref actualSize);
            if (result != 0 || actualSize != capacity)
                throw new InvalidOperationException(
                    $"No se pudo copiar el cuadro NV12: {result}");
            var pixels = new byte[checked((int)actualSize)];
            Marshal.Copy(nativeBuffer, pixels, 0, pixels.Length);
            timestamp100Ns = info.Timestamp100Ns;
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuffer);
        }
    }

    [LibraryImport(Library, EntryPoint = "im_copy_latest_video_frame")]
    private static unsafe partial int CopyLatestFrameNative(ref VideoFrameInfo info,
        byte* buffer, ref uint bufferSize);

    internal static (byte[] Pixels, uint Width, uint Height) CopyLatestFrame()
    {
        // Rotation can replace the source frame between the size query and the
        // copy. Retry if the second call reports that its new frame needs a
        // larger buffer; never expose a partially copied image.
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            var info = new VideoFrameInfo
            {
                StructSize = (uint)Marshal.SizeOf<VideoFrameInfo>(),
            };
            uint capacity = 0;
            int result;
            unsafe
            {
                result = CopyLatestFrameNative(ref info, null, ref capacity);
            }
            if (result != -3 || capacity == 0 || capacity > 256U * 1024U * 1024U)
                throw new InvalidOperationException(
                    $"No se pudo obtener el tamaño del cuadro: {result}");

            var nativeBuffer = Marshal.AllocHGlobal(checked((int)capacity));
            try
            {
                var actualSize = capacity;
                unsafe
                {
                    result = CopyLatestFrameNative(ref info,
                        (byte*)nativeBuffer, ref actualSize);
                }
                if (result == -3 && actualSize > capacity) continue;
                if (result != 0)
                    throw new InvalidOperationException(
                        $"No se pudo copiar el cuadro: {result}");
                var pixels = new byte[checked((int)actualSize)];
                Marshal.Copy(nativeBuffer, pixels, 0, pixels.Length);
                return (pixels, info.Width, info.Height);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
        }
        throw new InvalidOperationException(
            "La resolución cambió repetidamente; vuelve a intentar la captura.");
    }

    [LibraryImport(Library, EntryPoint = "im_initialize")]
    internal static partial int Initialize();

    // IntPtr, not string: see NativeWide. wchar_t is 32 bits here and .NET's
    // wide marshalling is UTF-16, so a marshalled string arrives truncated.
    [LibraryImport(Library, EntryPoint = "im_start_capture_with_options")]
    private static partial int StartCaptureWithOptionsNative(IntPtr serial,
        ref CaptureOptions options);

    internal static int StartCapture(string serial, int playAudio,
        uint projectionMode)
    {
        var buffer = NativeWide.Allocate(serial);
        try
        {
            var options = new CaptureOptions
            {
                StructSize = (uint)Marshal.SizeOf<CaptureOptions>(),
                ApiVersion = 18,
                TargetFps = 60,
                PlayAudio = playAudio,
                AudioVolume = 1.0f,
                ProjectionMode = projectionMode,
                ColorOutputPreference = 1,
            };
            return StartCaptureWithOptionsNative(buffer, ref options);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport(Library, EntryPoint = "im_stop_capture")]
    internal static partial int StopCapture();

    [LibraryImport(Library, EntryPoint = "im_last_error")]
    private static partial IntPtr LastErrorPointer();

    internal static string LastError() => NativeWide.Read(LastErrorPointer());

    // Throws rather than returning a flag: a layout mismatch has no safe
    // continuation, and the numbers belong in the message.
    internal static void CheckAbi()
    {
        var library = AbiSize();
        var managed = (uint)Marshal.SizeOf<PreviewSurface>();
        if (library != managed)
        {
            throw new InvalidOperationException(
                $"PreviewSurface is {managed} bytes here and {library} bytes in "
                + "iPhoneMirror.Core; the layouts have diverged");
        }
    }
}
