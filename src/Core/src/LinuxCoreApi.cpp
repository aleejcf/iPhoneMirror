// SPDX-License-Identifier: GPL-3.0-only
//
// Linux implementation of the Core C ABI. Written as a new file rather than by
// editing CoreApi.cpp, which is built entirely around Windows session,
// preview-HWND, virtual-camera and AirPlay plumbing.
//
// What is real here: initialization, logging, the environment report and device
// enumeration. That is the WP3 scope and it is what the Linux shell needs to
// show a device list and explain why capture is or is not possible.
//
// Everything else returns an explicit failure with a message naming the work
// package that implements it. The rule is that no export may report success
// without having done the thing: a caller that gets Ok from im_start_capture
// would wait forever for frames.

#include "iPhoneMirror/CoreApi.h"
#include "iPhoneMirror/LinuxPreviewApi.h"

#include "Capture/CaptureSession.h"
#include "Device/DeviceManager.h"
#include "LinuxCaptureBridge.h"
#include "Logging.h"
#include "Media/VideoFormats.h"
#include "Text/Utf.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>

namespace {

std::mutex state_mutex;
bool initialized{};
std::mutex capture_mutex;
std::unique_ptr<iPhoneMirror::capture::CaptureSession> capture_session;
iPhoneMirror::capture::CapturePreferences capture_preferences;
std::wstring last_error;
iPhoneMirror::device::DeviceManager device_manager;

std::int32_t fail(iPhoneMirror::Result result, std::wstring message) {
    last_error = std::move(message);
    return static_cast<std::int32_t>(result);
}

// Reports a Linux export that has no implementation yet. The message names the
// work package so a caller sees why rather than a bare error code.
std::int32_t not_implemented(std::wstring_view feature, std::wstring_view work_package) {
    last_error = std::wstring(feature) + L" 在 Linux 版尚未实现（" +
        std::wstring(work_package) + L"）";
    return static_cast<std::int32_t>(
        iPhoneMirror::Result::CaptureBackendUnavailable);
}

double normalized_component(const std::uint8_t* plane, std::size_t index,
    iPhoneMirror::media::PixelFormat format) {
    if (format == iPhoneMirror::media::PixelFormat::P010) {
        std::uint16_t word{};
        std::memcpy(&word, plane + index * 2U, sizeof(word));
        return static_cast<double>(word >> 6U) / 1023.0;
    }
    return static_cast<double>(plane[index]) / 255.0;
}

bool decoded_frame_to_bgra(const iPhoneMirror::media::DecodedFrame& source,
    std::uint8_t* output) {
    auto frame = source;
    if (frame.nv12.empty() && frame.gpu_frame &&
        !iPhoneMirror::media::detail::materialize_gpu_frame(frame)) return false;
    if (!output || frame.width == 0 || frame.height == 0 || frame.nv12.empty())
        return false;

    const auto stride = static_cast<std::size_t>(std::abs(frame.stride));
    const bool ten_bit = frame.pixel_format == iPhoneMirror::media::PixelFormat::P010;
    const auto component_bytes = ten_bit ? 2U : 1U;
    const auto luma_row_bytes = static_cast<std::size_t>(frame.width) * component_bytes;
    const auto chroma_row_bytes =
        static_cast<std::size_t>((frame.width + 1U) / 2U) * 2U * component_bytes;
    const auto luma_bytes = stride * frame.height;
    const auto chroma_rows = (static_cast<std::size_t>(frame.height) + 1U) / 2U;
    const auto required = luma_bytes + stride * chroma_rows;
    if (stride < std::max(luma_row_bytes, chroma_row_bytes) ||
        frame.nv12.size() < required) return false;

    const auto* luma = frame.nv12.data();
    const auto* chroma = luma + luma_bytes;
    const auto conversion = iPhoneMirror::media::detail::yuv_conversion_parameters(
        frame.pixel_format, frame.color.range, frame.color.matrix);
    for (std::uint32_t y = 0; y < frame.height; ++y) {
        const auto* y_row = luma + static_cast<std::size_t>(y) * stride;
        const auto* uv_row = chroma + static_cast<std::size_t>(y / 2U) * stride;
        auto* out = output + static_cast<std::size_t>(y) * frame.width * 4U;
        for (std::uint32_t x = 0; x < frame.width; ++x) {
            const auto uv = static_cast<std::size_t>(x / 2U) * 2U;
            const auto rgb = iPhoneMirror::media::detail::convert_yuv_to_sdr(
                normalized_component(y_row, x, frame.pixel_format),
                normalized_component(uv_row, uv, frame.pixel_format),
                normalized_component(uv_row, uv + 1U, frame.pixel_format),
                frame.color, conversion);
            const auto red = static_cast<std::uint8_t>(std::lround(rgb.red * 255.0));
            const auto green = static_cast<std::uint8_t>(std::lround(rgb.green * 255.0));
            const auto blue = static_cast<std::uint8_t>(std::lround(rgb.blue * 255.0));
            const auto offset = static_cast<std::size_t>(x) * 4U;
            out[offset] = blue;
            out[offset + 1U] = green;
            out[offset + 2U] = red;
            out[offset + 3U] = 255;
        }
    }
    return true;
}

template <std::size_t Capacity>
void copy_text(wchar_t (&destination)[Capacity], std::wstring_view source) {
    const auto length = std::min(source.size(), Capacity - 1);
    std::copy_n(source.begin(), length, destination);
    destination[length] = L'\0';
}

std::int32_t refresh_devices_locked(iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count, bool refresh_metadata) {
    if (!count) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"count 不能为空");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    try {
        const auto records = device_manager.refresh(refresh_metadata);
        const auto capacity = *count;
        *count = static_cast<std::uint32_t>(records.size());
        if (!devices) {
            last_error.clear();
            return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
        }
        if (capacity < records.size()) {
            return fail(iPhoneMirror::Result::BufferTooSmall,
                L"DeviceInfo 缓冲区容量不足");
        }
        for (std::size_t index = 0; index < records.size(); ++index) {
            const auto& record = records[index];
            auto& info = devices[index];
            info.struct_size = sizeof(iPhoneMirror::DeviceInfo);
            info.api_version = iPhoneMirror::ApiVersion;
            info.device_id = record.device_id;
            info.mux_port = record.mux_port;
            info.state = record.state;
            info.usb_connected = record.usb_connected ? 1 : 0;
            info.pair_record_present = record.pair_record_present ? 1 : 0;
            info.lockdown_accessible = record.lockdown_accessible ? 1 : 0;
            copy_text(info.udid, record.udid);
            copy_text(info.name, record.name);
            copy_text(info.product_type, record.product_type);
            copy_text(info.os_version, record.os_version);
            copy_text(info.connection_type, record.connection_type);
            copy_text(info.status, record.status);
        }
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        return fail(iPhoneMirror::Result::InternalError,
            L"枚举设备失败：" + iPhoneMirror::text::utf8_to_wide(error.what()));
    } catch (...) {
        return fail(iPhoneMirror::Result::InternalError, L"枚举设备时发生异常");
    }
}

} // namespace

std::int32_t IM_CALL im_initialize() {
    std::scoped_lock lock(state_mutex);
    iPhoneMirror::logging::initialize();
    initialized = true;
    last_error.clear();
    iPhoneMirror::logging::write("core initialize platform=linux");
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

void IM_CALL im_shutdown() {
    std::scoped_lock lock(state_mutex);
    if (!initialized) return;
    initialized = false;
    iPhoneMirror::logging::write("core shutdown platform=linux");
    iPhoneMirror::logging::shutdown();
}

std::uint32_t IM_CALL im_api_version() { return iPhoneMirror::ApiVersion; }

std::int32_t IM_CALL im_log_message(const wchar_t* message) {
    if (!message || !*message) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"日志消息不能为空");
    }
    constexpr std::size_t MaxLogMessage = 4096;
    std::wstring_view text(message);
    if (text.size() > MaxLogMessage) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"日志消息过长");
    }
    iPhoneMirror::logging::write(iPhoneMirror::text::wide_to_utf8(text));
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_environment(iPhoneMirror::EnvironmentInfo* environment) {
    if (!environment ||
        environment->struct_size != sizeof(iPhoneMirror::EnvironmentInfo)) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"EnvironmentInfo 结构版本不匹配");
    }
    std::scoped_lock lock(state_mutex);
    if (!initialized) {
        return fail(iPhoneMirror::Result::NotInitialized, L"核心尚未初始化");
    }
    try {
        const auto info = device_manager.environment();
        environment->api_version = iPhoneMirror::ApiVersion;
        environment->apple_mobile_device_service_installed = info.service_installed;
        environment->apple_mobile_device_service_running = info.service_running;
        environment->standard_usbmux_available = info.standard_mux;
        environment->capture_usbmux_available = info.capture_mux;
        environment->physical_apple_usb_devices = info.physical_device_count;
        copy_text(environment->diagnostic, info.diagnostic);
        environment->libusb_runtime_available = info.libusb_runtime;
        environment->usbdk_backend_available = info.usbdk_backend;
        environment->libusb_apple_devices = info.libusb_apple_devices;
        copy_text(environment->libusb_version, info.libusb_version);
        environment->usbdk_backend_known = info.usbdk_backend_known;
        environment->libusb_apple_devices_known = info.libusb_apple_devices_known;
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (...) {
        return fail(iPhoneMirror::Result::InternalError, L"读取驱动环境时发生异常");
    }
}

std::int32_t IM_CALL im_refresh_devices(iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count) {
    return refresh_devices_locked(devices, count, false);
}

std::int32_t IM_CALL im_refresh_devices_ex(iPhoneMirror::DeviceInfo* devices,
    std::uint32_t* count, std::int32_t refresh_metadata) {
    return refresh_devices_locked(devices, count, refresh_metadata != 0);
}

const wchar_t* IM_CALL im_last_error() { return last_error.c_str(); }

// Wired USB capture. The shared capture state machine, the libusb-1.0 transport,
// the FFmpeg decoder and the PipeWire renderer all exist on Linux now, so this
// runs the real thing. use_usbdk is false unconditionally: UsbDk is a Windows
// backend and CaptureSession only ever selects LibUsb1 here.
//
// A capable usbmuxd is a precondition rather than something this code arranges.
// With one, the device is already in the capture configuration and claiming the
// AV interface takes no contention; with usbmuxd 1.1.1 the daemon clamps its
// configuration choice to 4, abandons the device, and iOS never opens a session.
// See docs/LINUX_PORT.md.
std::int32_t IM_CALL start_capture_locked(const wchar_t* serial,
    const iPhoneMirror::capture::CapturePreferences& preferences) {
    if (serial == nullptr || *serial == L'\0')
        return fail(iPhoneMirror::Result::InvalidArgument, L"serial 不能为空");
    if (capture_session) {
        return fail(iPhoneMirror::Result::SessionAlreadyExists,
            L"已有采集会话在运行");
    }
    try {
        auto session = std::make_unique<iPhoneMirror::capture::CaptureSession>(
            iPhoneMirror::text::wide_to_utf8(serial), preferences);
        session->start(false);
        capture_session = std::move(session);
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        capture_session.reset();
        last_error = iPhoneMirror::text::utf8_to_wide(error.what());
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
}

std::int32_t IM_CALL im_start_capture(const wchar_t* serial) {
    std::scoped_lock lock(capture_mutex);
    return start_capture_locked(serial, capture_preferences);
}

std::int32_t IM_CALL im_start_capture_ex(const wchar_t* serial,
    std::int32_t play_audio) {
    std::scoped_lock lock(capture_mutex);
    auto preferences = capture_preferences;
    preferences.play_audio = play_audio != 0;
    return start_capture_locked(serial, preferences);
}

std::int32_t IM_CALL im_start_capture_with_options(const wchar_t* serial,
    const iPhoneMirror::CaptureOptions* options) {
    if (!serial || !*serial || !options ||
        options->struct_size != sizeof(iPhoneMirror::CaptureOptions) ||
        (options->requested_width == 0) != (options->requested_height == 0) ||
        (options->requested_width != 0 &&
            (options->requested_width < 16 || options->requested_height < 16 ||
             options->requested_width > 8192 || options->requested_height > 8192)) ||
        options->target_fps > 120 || !std::isfinite(options->audio_volume) ||
        options->audio_volume < 0.0F || options->audio_volume > 1.0F ||
        (options->reserved[0] == 0) != (options->reserved[1] == 0) ||
        (options->reserved[0] != 0 &&
            (options->reserved[0] < 16 || options->reserved[1] < 16 ||
             options->reserved[0] > 8192 || options->reserved[1] > 8192)) ||
        options->reserved[2] > 2 || options->reserved[3] > 2 ||
        options->reserved[4] > 2) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"Los parámetros de CaptureOptions no son válidos");
    }

    const iPhoneMirror::capture::CapturePreferences preferences{
        .render_max_width = options->requested_width,
        .render_max_height = options->requested_height,
        .target_fps = options->target_fps,
        .play_audio = options->play_audio != 0,
        .audio_volume = options->audio_volume,
        .usb_requested_width = options->reserved[0],
        .usb_requested_height = options->reserved[1],
        .usb_projection_mode = static_cast<iPhoneMirror::capture::UsbProjectionMode>(
            options->reserved[2]),
        .decoder_preference = static_cast<iPhoneMirror::media::DecoderPreference>(
            options->reserved[3]),
        .color_output_preference = static_cast<iPhoneMirror::media::ColorOutputPreference>(
            options->reserved[4]),
    };
    std::scoped_lock lock(capture_mutex);
    capture_preferences = preferences;
    return start_capture_locked(serial, preferences);
}

std::int32_t IM_CALL im_stop_capture() {
    std::scoped_lock lock(capture_mutex);
    if (!capture_session) {
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    }
    capture_session->stop();
    capture_session.reset();
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_capture_status(iPhoneMirror::CaptureStatus* status) {
    if (!status || status->struct_size != sizeof(iPhoneMirror::CaptureStatus)) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"CaptureStatus 结构版本不匹配");
    }
    status->api_version = iPhoneMirror::ApiVersion;
    std::scoped_lock lock(capture_mutex);
    if (!capture_session) {
        // A well-formed idle status, not a failure: no session is running.
        status->state = iPhoneMirror::CaptureState::Idle;
        status->width = 0;
        status->height = 0;
        status->fps = 0.0;
        status->latency_ms = 0.0;
        status->video_frames = 0;
        status->audio_packets = 0;
        status->audio_sample_rate = 0;
        status->audio_channels = 0;
        status->failure_kind = iPhoneMirror::CaptureFailureKind::None;
        status->failure_stage = iPhoneMirror::CaptureFailureStage::None;
        status->error_code = 0;
        copy_text(status->message, L"Idle");
        last_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    }
    // Snapshot maps onto CaptureStatus one field at a time, so the shared state
    // machine's own reporting is what the caller sees.
    const auto snapshot = capture_session->snapshot();
    status->state = static_cast<iPhoneMirror::CaptureState>(snapshot.state);
    status->width = snapshot.width;
    status->height = snapshot.height;
    status->fps = snapshot.fps;
    status->latency_ms = snapshot.latency_ms;
    status->video_frames = snapshot.video_frames;
    status->audio_packets = snapshot.audio_packets;
    status->audio_sample_rate = snapshot.audio_sample_rate;
    status->audio_channels = snapshot.audio_channels;
    status->failure_kind =
        static_cast<iPhoneMirror::CaptureFailureKind>(snapshot.failure_kind);
    status->failure_stage =
        static_cast<iPhoneMirror::CaptureFailureStage>(snapshot.failure_stage);
    status->error_code = snapshot.error_code;
    copy_text(status->message, snapshot.message);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_get_latest_video_timestamp(std::int64_t* timestamp_100ns) {
    if (!timestamp_100ns) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"timestamp 不能为空");
    }
    std::scoped_lock lock(capture_mutex);
    *timestamp_100ns = capture_session
        ? capture_session->latest_frame_timestamp() : 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_copy_latest_video_frame(iPhoneMirror::VideoFrameInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::VideoFrameInfo) ||
        !buffer_size) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"VideoFrameInfo 结构版本不匹配");
    }
    std::shared_ptr<const iPhoneMirror::media::DecodedFrame> frame;
    {
        std::scoped_lock lock(capture_mutex);
        if (!capture_session)
            return fail(iPhoneMirror::Result::CaptureBackendUnavailable,
                L"当前没有投屏会话");
        frame = capture_session->latest_frame();
    }
    if (!frame) return fail(iPhoneMirror::Result::DeviceNotFound,
        L"正在等待首个解码视频帧");
    const auto required64 = static_cast<std::uint64_t>(frame->width) *
        frame->height * 4U;
    if (required64 == 0 || required64 > std::numeric_limits<std::uint32_t>::max())
        return fail(iPhoneMirror::Result::InternalError, L"视频帧尺寸过大");
    const auto required = static_cast<std::uint32_t>(required64);
    info->api_version = iPhoneMirror::ApiVersion;
    info->width = frame->width;
    info->height = frame->height;
    info->stride = frame->width * 4U;
    info->pixel_format = 1;
    info->timestamp_100ns = frame->timestamp_100ns;
    const auto capacity = *buffer_size;
    *buffer_size = required;
    if (!buffer || capacity < required)
        return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    if (!decoded_frame_to_bgra(*frame, buffer))
        return fail(iPhoneMirror::Result::ProtocolError,
            L"El formato del cuadro decodificado no se pudo convertir a BGRA");
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_linux_capture_copy_next_audio_packet(
    std::uint64_t after_sequence, iPhoneMirror::AudioPacketInfo* info,
    std::uint8_t* buffer, std::uint32_t* buffer_size) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::AudioPacketInfo) ||
        !buffer_size) {
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"AudioPacketInfo 结构版本不匹配");
    }
    std::shared_ptr<const iPhoneMirror::capture::AudioPacket> packet;
    {
        std::scoped_lock lock(capture_mutex);
        if (!capture_session)
            return fail(iPhoneMirror::Result::CaptureBackendUnavailable,
                L"当前没有投屏会话");
        packet = capture_session->next_audio_packet(after_sequence);
    }
    if (!packet) {
        last_error.clear();
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
    if (packet->pcm.size() > std::numeric_limits<std::uint32_t>::max())
        return fail(iPhoneMirror::Result::InternalError, L"音频数据包过大");

    info->api_version = iPhoneMirror::ApiVersion;
    info->sequence = packet->sequence;
    info->sample_rate = packet->sample_rate;
    info->channels = packet->channels;
    info->bits_per_sample = packet->bits_per_sample;
    const auto required = static_cast<std::uint32_t>(packet->pcm.size());
    const auto capacity = *buffer_size;
    *buffer_size = required;
    if (!buffer || capacity < required)
        return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    std::copy(packet->pcm.begin(), packet->pcm.end(), buffer);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_copy_latest_video_frame_scaled(
    iPhoneMirror::VideoFrameInfo*, std::uint8_t*, std::uint32_t*,
    std::uint32_t, std::uint32_t) {
    return not_implemented(L"视频帧拷贝", L"WP5");
}

// Preview hosting. Linux uses the exported-target model from decision D1
// (libplacebo renders into an exportable image that Avalonia imports), so the
// HWND-shaped entry points never gain a Linux implementation.
std::int32_t IM_CALL im_attach_preview_window(void*) {
    return not_implemented(L"HWND 预览宿主", L"Linux 改用导出面模型，见 D1");
}

void IM_CALL im_detach_preview_window() {}

std::int32_t IM_CALL im_force_preview_refresh() {
    return not_implemented(L"预览刷新", L"WP5");
}

std::int32_t IM_CALL im_set_preview_corner_profile(float, float) {
    return not_implemented(L"预览圆角裁剪", L"WP6");
}

std::int32_t IM_CALL im_set_video_preferences(std::uint32_t width,
    std::uint32_t height, std::uint32_t fps) {
    if ((width == 0) != (height == 0) || fps > 120 ||
        (width != 0 && (width < 16 || height < 16 || width > 8192 || height > 8192)))
        return fail(iPhoneMirror::Result::InvalidArgument, L"视频尺寸或帧率无效");
    std::scoped_lock lock(capture_mutex);
    if (capture_session && width != 0)
        return not_implemented(L"运行中调整视频尺寸", L"P5b");
    capture_preferences.render_max_width = width;
    capture_preferences.render_max_height = height;
    capture_preferences.target_fps = fps;
    if (capture_session) capture_session->set_target_fps(fps);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_image_adjustments(float, float, float, float) {
    return not_implemented(L"图像调节", L"WP5");
}

std::int32_t IM_CALL im_set_audio_enabled(std::int32_t enabled) {
    std::scoped_lock lock(capture_mutex);
    capture_preferences.play_audio = enabled != 0;
    if (capture_session) capture_session->set_audio_enabled(enabled != 0);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_set_audio_volume(float volume) {
    if (!std::isfinite(volume) || volume < 0.0F || volume > 1.0F)
        return fail(iPhoneMirror::Result::InvalidArgument,
            L"El volumen debe estar entre 0.0 y 1.0");
    std::scoped_lock lock(capture_mutex);
    capture_preferences.audio_volume = volume;
    if (capture_session) capture_session->set_audio_volume(volume);
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_is_libusb0_device_available(const wchar_t*,
    std::int32_t* available) {
    if (!available) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"available 不能为空");
    }
    // libusb0 is the Windows libusb-win32 filter backend. It does not exist on
    // Linux, so the answer is a definite "no", not a failure.
    *available = 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

// Multi-device sessions. The shared capture state machine is ready; these wait
// on the same decoder/renderer work as im_start_capture.
std::int32_t IM_CALL im_session_create(const wchar_t*,
    const iPhoneMirror::CaptureOptions*, iPhoneMirror::SessionHandle* handle) {
    if (handle) *handle = 0;
    return not_implemented(L"有线采集会话", L"WP4/WP5");
}

std::int32_t IM_CALL im_session_stop(iPhoneMirror::SessionHandle) {
    return not_implemented(L"有线采集会话", L"WP4/WP5");
}

void IM_CALL im_session_destroy(iPhoneMirror::SessionHandle) {}

std::int32_t IM_CALL im_session_get_status(iPhoneMirror::SessionHandle,
    iPhoneMirror::CaptureStatus*) {
    return not_implemented(L"会话状态查询", L"WP4/WP5");
}

std::int32_t IM_CALL im_session_get_video_output_status(
    iPhoneMirror::SessionHandle, void*, iPhoneMirror::VideoOutputStatus*) {
    return not_implemented(L"视频输出诊断", L"WP5");
}

std::int32_t IM_CALL im_session_attach_preview(iPhoneMirror::SessionHandle, void*) {
    return not_implemented(L"HWND 预览宿主", L"Linux 改用导出面模型，见 D1");
}

void IM_CALL im_session_detach_preview(iPhoneMirror::SessionHandle, void*) {}

std::int32_t IM_CALL im_session_set_video_preferences(iPhoneMirror::SessionHandle,
    std::uint32_t, std::uint32_t, std::uint32_t) {
    return not_implemented(L"视频渲染偏好", L"WP5");
}

std::int32_t IM_CALL im_session_set_image_adjustments(iPhoneMirror::SessionHandle,
    float, float, float, float) {
    return not_implemented(L"图像调节", L"WP5");
}

std::int32_t IM_CALL im_session_set_pipeline_preferences(
    iPhoneMirror::SessionHandle, std::uint32_t, std::uint32_t) {
    return not_implemented(L"解码/色彩策略", L"WP5");
}

std::int32_t IM_CALL im_session_set_audio_enabled(iPhoneMirror::SessionHandle,
    std::int32_t) {
    return not_implemented(L"音频开关", L"WP5");
}

std::int32_t IM_CALL im_session_set_audio_volume(iPhoneMirror::SessionHandle, float) {
    return not_implemented(L"音量设置", L"WP5");
}

std::int32_t IM_CALL im_session_set_corner_profile(iPhoneMirror::SessionHandle,
    float, float) {
    return not_implemented(L"预览圆角裁剪", L"WP6");
}

std::int32_t IM_CALL im_session_get_latest_video_timestamp(
    iPhoneMirror::SessionHandle, std::int64_t*) {
    return not_implemented(L"会话视频时间戳", L"WP5");
}

std::int32_t IM_CALL im_session_copy_latest_video_frame(iPhoneMirror::SessionHandle,
    iPhoneMirror::VideoFrameInfo*, std::uint8_t*, std::uint32_t*,
    std::uint32_t, std::uint32_t) {
    return not_implemented(L"视频帧拷贝", L"WP5");
}

std::int32_t IM_CALL im_session_copy_latest_video_frame_nv12(
    iPhoneMirror::SessionHandle, iPhoneMirror::VideoFrameInfo*, std::uint8_t*,
    std::uint32_t*, std::uint32_t, std::uint32_t) {
    return not_implemented(L"NV12 帧拷贝", L"WP5");
}

std::int32_t IM_CALL im_session_copy_next_audio_packet(iPhoneMirror::SessionHandle,
    std::uint64_t, iPhoneMirror::AudioPacketInfo*, std::uint8_t*, std::uint32_t*) {
    return not_implemented(L"PCM 包拷贝", L"WP5");
}

std::int32_t IM_CALL im_session_force_preview_refresh(iPhoneMirror::SessionHandle) {
    return not_implemented(L"预览刷新", L"WP5");
}

std::int32_t IM_CALL im_session_set_window_corner_profile(
    iPhoneMirror::SessionHandle, void*, float, float) {
    return not_implemented(L"预览圆角裁剪", L"WP6");
}

std::int32_t IM_CALL im_session_set_window_rotation(iPhoneMirror::SessionHandle,
    void*, std::int32_t) {
    return not_implemented(L"预览旋转", L"WP6");
}

// AirPlay receiver. The Linux receiver is built from UxPlay sources rather than
// the vendored AirPlayServer runtime, which is a separate work package.
std::int32_t IM_CALL im_wireless_receiver_start(const wchar_t*, const wchar_t*) {
    return not_implemented(L"AirPlay 接收端", L"WP6 之后的 P6");
}

std::int32_t IM_CALL im_wireless_receiver_start_ex(const wchar_t*, const wchar_t*,
    std::uint32_t, std::uint32_t, std::uint32_t) {
    return not_implemented(L"AirPlay 接收端", L"WP6 之后的 P6");
}

void IM_CALL im_wireless_receiver_stop() {}

std::int32_t IM_CALL im_wireless_receiver_get_status(std::int32_t* running,
    std::int32_t* ready) {
    if (running) *running = 0;
    if (ready) *ready = 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_refresh_wireless_devices(iPhoneMirror::DeviceInfo*,
    std::uint32_t* count) {
    // No receiver can be running, so an empty list is the correct answer.
    if (!count) {
        return fail(iPhoneMirror::Result::InvalidArgument, L"count 不能为空");
    }
    *count = 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_wireless_session_create(const wchar_t*,
    const iPhoneMirror::CaptureOptions*, iPhoneMirror::SessionHandle* handle) {
    if (handle) *handle = 0;
    return not_implemented(L"AirPlay 采集会话", L"WP6 之后的 P6");
}

std::int32_t IM_CALL im_media_cast_receiver_start(const wchar_t*, const wchar_t*) {
    return not_implemented(L"URL 视频接收端", L"WP6 之后的 P6");
}

void IM_CALL im_media_cast_receiver_stop() {}

std::int32_t IM_CALL im_media_cast_receiver_get_status(std::int32_t* running,
    std::int32_t* ready) {
    if (running) *running = 0;
    if (ready) *ready = 0;
    last_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_media_cast_get_request(iPhoneMirror::MediaCastRequest*) {
    return not_implemented(L"URL 视频请求", L"WP6 之后的 P6");
}

std::int32_t IM_CALL im_media_cast_set_playback_state(std::uint64_t, double,
    double, double) {
    return not_implemented(L"URL 视频播放状态", L"WP6 之后的 P6");
}

std::int32_t IM_CALL im_media_cast_request_stop() {
    return not_implemented(L"URL 视频停止", L"WP6 之后的 P6");
}

namespace iPhoneMirror::linux_bridge {

std::shared_ptr<const media::DecodedFrame> latest_render_frame() {
    std::scoped_lock lock(capture_mutex);
    return capture_session ? capture_session->next_render_frame() : nullptr;
}

} // namespace iPhoneMirror::linux_bridge
