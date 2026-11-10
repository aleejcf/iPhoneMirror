// SPDX-License-Identifier: GPL-3.0-only

#include "Media/VideoFormats.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <vector>

using iPhoneMirror::coremedia::ColorRange;
using iPhoneMirror::media::DecodedFrame;
using iPhoneMirror::media::PixelFormat;
using iPhoneMirror::media::detail::copy_nv12_frame_letterboxed;

namespace {
int failures{};

void check(bool condition, const char* message) {
    if (condition) return;
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
}

DecodedFrame frame(std::uint32_t width, std::uint32_t height,
    std::vector<std::uint8_t> bytes) {
    DecodedFrame result;
    result.width = width;
    result.height = height;
    result.stride = static_cast<std::int32_t>(width);
    result.pixel_format = PixelFormat::Nv12;
    result.color.range = ColorRange::Limited;
    result.nv12 = std::move(bytes);
    return result;
}
}

int main() {
    // Landscape source must stay landscape inside a fixed portrait canvas.
    const auto landscape = frame(4, 2, {
        10, 11, 12, 13,
        20, 21, 22, 23,
        101, 102, 103, 104,
    });
    std::vector<std::uint8_t> portrait_canvas(4U * 8U * 3U / 2U);
    check(copy_nv12_frame_letterboxed(landscape, portrait_canvas, 4, 8),
        "landscape NV12 copies into portrait output");
    std::vector<std::uint8_t> portrait_expected(portrait_canvas.size(), 16);
    std::fill(portrait_expected.begin() + 32, portrait_expected.end(), 128);
    std::copy_n(landscape.nv12.begin(), 4, portrait_expected.begin() + 8);
    std::copy_n(landscape.nv12.begin() + 4, 4, portrait_expected.begin() + 12);
    std::copy_n(landscape.nv12.begin() + 8, 4, portrait_expected.begin() + 36);
    check(portrait_canvas == portrait_expected,
        "landscape pixels and chroma stay centered without vertical crop");

    // Portrait source must stay portrait inside a fixed landscape canvas.
    const auto portrait = frame(2, 4, {
        30, 31,
        32, 33,
        34, 35,
        36, 37,
        111, 112,
        113, 114,
    });
    std::vector<std::uint8_t> landscape_canvas(8U * 4U * 3U / 2U);
    check(copy_nv12_frame_letterboxed(portrait, landscape_canvas, 8, 4),
        "portrait NV12 copies into landscape output");
    std::vector<std::uint8_t> landscape_expected(landscape_canvas.size(), 16);
    std::fill(landscape_expected.begin() + 32, landscape_expected.end(), 128);
    for (std::size_t row{}; row < 4; ++row)
        std::copy_n(portrait.nv12.begin() + static_cast<std::ptrdiff_t>(row * 2),
            2, landscape_expected.begin() +
                static_cast<std::ptrdiff_t>(row * 8 + 2));
    std::copy_n(portrait.nv12.begin() + 8, 2,
        landscape_expected.begin() + 34);
    std::copy_n(portrait.nv12.begin() + 10, 2,
        landscape_expected.begin() + 42);
    check(landscape_canvas == landscape_expected,
        "portrait pixels and chroma stay centered without horizontal crop");

    if (failures != 0) return 1;
    std::puts("PASS: NV12 orientation and aspect-preserving output");
    return 0;
}
