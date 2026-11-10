// SPDX-License-Identifier: GPL-3.0-only
//
// Implementation of the Linux preview C ABI. Thin on purpose: it owns one
// renderer and translates the C++ seam into the flat struct the managed side
// mirrors. All of the interesting work is in Media/LinuxPlaceboRenderer.cpp.

#include "iPhoneMirror/LinuxPreviewApi.h"

#include "LinuxCaptureBridge.h"
#include "Media/LinuxPlaceboRenderer.h"
#include "Media/VideoFormats.h"
#include "Logging.h"
#include "Text/Utf.h"

#include <cstring>
#include <format>
#include <memory>
#include <mutex>
#include <span>
#include <string>

namespace {

std::mutex preview_mutex;
std::unique_ptr<iPhoneMirror::media::ILinuxPreviewRenderer> renderer;

// The shared C ABI keeps its own last-error string; this one is local so a
// preview failure cannot clobber a capture diagnostic the caller has not read.
std::string preview_error;

void set_error(std::string message) {
    preview_error = std::move(message);
    iPhoneMirror::logging::write(std::format("linux_preview {}", preview_error));
}

} // namespace

std::uint32_t IM_CALL im_linux_preview_abi_size() {
    return static_cast<std::uint32_t>(sizeof(iPhoneMirror::LinuxPreviewSurface));
}

std::int32_t IM_CALL im_linux_preview_open(std::uint32_t width,
    std::uint32_t height, const std::uint8_t* device_uuid) {
    std::scoped_lock lock(preview_mutex);
    try {
        renderer = iPhoneMirror::media::make_placebo_preview_renderer(width,
            height, device_uuid);
        preview_error.clear();
        return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
    } catch (const std::exception& error) {
        renderer.reset();
        set_error(error.what());
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
}

void IM_CALL im_linux_preview_close() {
    std::scoped_lock lock(preview_mutex);
    renderer.reset();
}

std::int32_t IM_CALL im_linux_preview_describe(
    iPhoneMirror::LinuxPreviewSurface* surface) {
    if (surface == nullptr)
        return static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    std::scoped_lock lock(preview_mutex);
    if (!renderer) {
        set_error("no preview is open");
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
    const auto exported = renderer->exported_surface();
    *surface = iPhoneMirror::LinuxPreviewSurface{
        .valid = exported.valid ? 1 : 0,
        .memory_fd = exported.memory_fd,
        .render_completed_fd = exported.render_completed_fd,
        .available_fd = exported.available_fd,
        .width = exported.width,
        .height = exported.height,
        .vk_format = exported.vk_format,
        .reserved = 0,
        .allocation_size = exported.allocation_size,
        .allocation_offset = exported.allocation_offset,
    };
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_linux_preview_present_nv12(const std::uint8_t* data,
    std::uint64_t size, std::uint32_t width, std::uint32_t height) {
    if (data == nullptr || width == 0 || height == 0)
        return static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    const auto required = static_cast<std::uint64_t>(width) * height * 3U / 2U;
    if (size < required) {
        set_error(std::format("{} bytes is short of the {} an {}x{} NV12 frame "
                              "needs", size, required, width, height));
        return static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    }
    std::scoped_lock lock(preview_mutex);
    if (!renderer) {
        set_error("no preview is open");
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }

    iPhoneMirror::media::DecodedFrame frame;
    frame.width = width;
    frame.height = height;
    frame.stride = static_cast<std::int32_t>(width);
    frame.pixel_format = iPhoneMirror::media::PixelFormat::Nv12;
    // The same defaults the decoder applies to an unspecified stream, so a
    // caller feeding raw bytes gets the colour treatment a decoded frame gets.
    frame.color.primaries = iPhoneMirror::coremedia::ColorPrimaries::Bt709;
    frame.color.transfer = iPhoneMirror::coremedia::TransferFunction::Bt709;
    frame.color.matrix = height >= 720
        ? iPhoneMirror::coremedia::MatrixCoefficients::Bt709
        : iPhoneMirror::coremedia::MatrixCoefficients::Bt601;
    frame.color.range = iPhoneMirror::coremedia::ColorRange::Limited;
    frame.nv12.assign(data, data + required);

    if (!renderer->present(frame)) {
        set_error(std::string(renderer->last_error()));
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
    preview_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_linux_preview_present_latest() {
    const auto frame = iPhoneMirror::linux_bridge::latest_render_frame();
    if (!frame) {
        // Not an error: the handshake and the first keyframe both take time, and
        // a caller polling on a timer would otherwise log a failure per tick.
        return static_cast<std::int32_t>(iPhoneMirror::Result::DeviceNotFound);
    }
    std::scoped_lock lock(preview_mutex);
    if (!renderer) {
        set_error("no preview is open");
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
    // The mailbox hands out an immutable frame, so presenting it needs no copy
    // and no lock beyond the one already held.
    if (!renderer->present(*frame)) {
        set_error(std::string(renderer->last_error()));
        return static_cast<std::int32_t>(
            iPhoneMirror::Result::CaptureBackendUnavailable);
    }
    preview_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_linux_preview_latest_geometry(
    std::uint32_t* width, std::uint32_t* height) {
    if (width == nullptr || height == nullptr)
        return static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    const auto frame = iPhoneMirror::linux_bridge::latest_render_frame();
    if (!frame)
        return static_cast<std::int32_t>(iPhoneMirror::Result::DeviceNotFound);
    *width = frame->width;
    *height = frame->height;
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}

std::int32_t IM_CALL im_linux_preview_copy_latest_nv12(
    std::uint32_t output_width, std::uint32_t output_height,
    iPhoneMirror::VideoFrameInfo* info, std::uint8_t* buffer,
    std::uint32_t* buffer_size) {
    if (!info || info->struct_size != sizeof(iPhoneMirror::VideoFrameInfo) ||
        !buffer_size || output_width < 16 || output_height < 16 ||
        output_width > 8192 || output_height > 8192 ||
        (output_width & 1U) != 0 || (output_height & 1U) != 0) {
        return static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    }
    const auto frame = iPhoneMirror::linux_bridge::latest_render_frame();
    if (!frame)
        return static_cast<std::int32_t>(iPhoneMirror::Result::DeviceNotFound);
    const auto required = iPhoneMirror::media::detail::checked_nv12_buffer_size(
        output_width, output_height);
    if (!required)
        return static_cast<std::int32_t>(iPhoneMirror::Result::InternalError);

    info->api_version = iPhoneMirror::ApiVersion;
    info->width = output_width;
    info->height = output_height;
    info->stride = output_width;
    info->pixel_format = 2;
    info->timestamp_100ns = frame->timestamp_100ns;
    const auto capacity = *buffer_size;
    *buffer_size = *required;
    if (!buffer || capacity < *required)
        return static_cast<std::int32_t>(iPhoneMirror::Result::BufferTooSmall);
    if (!iPhoneMirror::media::detail::copy_nv12_frame_letterboxed(*frame,
            std::span<std::uint8_t>(buffer, *required), output_width,
            output_height)) {
        set_error("the decoded frame could not be copied to the NV12 output canvas");
        return static_cast<std::int32_t>(iPhoneMirror::Result::ProtocolError);
    }
    preview_error.clear();
    return static_cast<std::int32_t>(iPhoneMirror::Result::Ok);
}
