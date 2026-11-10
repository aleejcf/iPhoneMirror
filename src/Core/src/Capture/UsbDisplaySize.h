#pragma once

#include <cstdint>
#include <string_view>
#include <utility>

namespace iPhoneMirror::capture {

struct NativeDisplaySize {
    std::uint32_t width;
    std::uint32_t height;
    bool model_known{true};
};

// Portrait panel pixels keyed by Apple's exact ProductType. The fallback is
// only a negotiation tier; it does not claim to be the unknown device's panel
// resolution.
inline NativeDisplaySize native_display_size(std::wstring_view product_type) noexcept {
    static constexpr std::pair<std::wstring_view, NativeDisplaySize> sizes[] = {
        {L"iPhone10,1", {750, 1334}}, {L"iPhone10,4", {750, 1334}},
        {L"iPhone10,2", {1080, 1920}}, {L"iPhone10,5", {1080, 1920}},
        {L"iPhone10,3", {1125, 2436}}, {L"iPhone10,6", {1125, 2436}},
        {L"iPhone12,1", {828, 1792}}, {L"iPhone12,3", {1125, 2436}},
        {L"iPhone12,5", {1242, 2688}}, {L"iPhone12,8", {750, 1334}},
        {L"iPhone13,1", {1080, 2340}}, {L"iPhone13,2", {1170, 2532}},
        {L"iPhone13,3", {1170, 2532}}, {L"iPhone13,4", {1284, 2778}},
        {L"iPhone14,4", {1080, 2340}}, {L"iPhone14,5", {1170, 2532}},
        {L"iPhone14,2", {1170, 2532}}, {L"iPhone14,3", {1284, 2778}},
        {L"iPhone14,6", {750, 1334}}, {L"iPhone14,7", {1170, 2532}},
        {L"iPhone14,8", {1284, 2778}}, {L"iPhone15,2", {1179, 2556}},
        {L"iPhone15,3", {1290, 2796}}, {L"iPhone15,4", {1179, 2556}},
        {L"iPhone15,5", {1290, 2796}}, {L"iPhone16,1", {1179, 2556}},
        {L"iPhone16,2", {1290, 2796}}, {L"iPhone17,3", {1179, 2556}},
        {L"iPhone17,4", {1290, 2796}}, {L"iPhone17,1", {1206, 2622}},
        {L"iPhone17,2", {1320, 2868}}, {L"iPhone17,5", {1170, 2532}},
        {L"iPhone18,3", {1206, 2622}},
        {L"iPad6,7", {2048, 2732}}, {L"iPad6,8", {2048, 2732}},
        {L"iPad7,1", {2048, 2732}}, {L"iPad7,2", {2048, 2732}},
        {L"iPad8,5", {2048, 2732}}, {L"iPad8,6", {2048, 2732}},
        {L"iPad8,7", {2048, 2732}}, {L"iPad8,8", {2048, 2732}},
        {L"iPad8,11", {2048, 2732}}, {L"iPad8,12", {2048, 2732}},
        {L"iPad13,8", {2048, 2732}}, {L"iPad13,9", {2048, 2732}},
        {L"iPad13,10", {2048, 2732}}, {L"iPad13,11", {2048, 2732}},
        {L"iPad14,5", {2048, 2732}}, {L"iPad14,6", {2048, 2732}},
        {L"iPad8,1", {1668, 2388}}, {L"iPad8,2", {1668, 2388}},
        {L"iPad8,3", {1668, 2388}}, {L"iPad8,4", {1668, 2388}},
        {L"iPad8,9", {1668, 2388}}, {L"iPad8,10", {1668, 2388}},
        {L"iPad13,4", {1668, 2388}}, {L"iPad13,5", {1668, 2388}},
        {L"iPad13,6", {1668, 2388}}, {L"iPad13,7", {1668, 2388}},
        {L"iPad14,3", {1668, 2388}}, {L"iPad14,4", {1668, 2388}},
        {L"iPad16,3", {1668, 2420}}, {L"iPad16,4", {1668, 2420}},
        {L"iPad16,5", {2064, 2752}}, {L"iPad16,6", {2064, 2752}},
        {L"iPad13,1", {1640, 2360}}, {L"iPad13,2", {1640, 2360}},
        {L"iPad13,16", {1640, 2360}}, {L"iPad13,17", {1640, 2360}},
        {L"iPad14,8", {1640, 2360}}, {L"iPad14,9", {1640, 2360}},
        {L"iPad15,3", {1640, 2360}}, {L"iPad15,4", {1640, 2360}},
        {L"iPad13,18", {1640, 2360}}, {L"iPad13,19", {1640, 2360}},
        {L"iPad15,7", {1640, 2360}}, {L"iPad15,8", {1640, 2360}},
        {L"iPad14,10", {2048, 2732}}, {L"iPad14,11", {2048, 2732}},
        {L"iPad15,5", {2048, 2732}}, {L"iPad15,6", {2048, 2732}},
        {L"iPad14,1", {1488, 2266}}, {L"iPad14,2", {1488, 2266}},
        {L"iPad16,1", {1488, 2266}}, {L"iPad16,2", {1488, 2266}},
        {L"iPad7,11", {1620, 2160}}, {L"iPad7,12", {1620, 2160}},
        {L"iPad11,6", {1620, 2160}}, {L"iPad11,7", {1620, 2160}},
        {L"iPad12,1", {1620, 2160}}, {L"iPad12,2", {1620, 2160}},
        {L"iPad11,3", {1668, 2224}}, {L"iPad11,4", {1668, 2224}},
        {L"iPad7,3", {1668, 2224}}, {L"iPad7,4", {1668, 2224}},
        {L"iPad11,1", {1536, 2048}}, {L"iPad11,2", {1536, 2048}},
    };
    for (const auto& [identifier, size] : sizes)
        if (identifier == product_type) return size;
    if (product_type.starts_with(L"iPad")) return {1640, 2360, false};
    return {1206, 2622, false};
}

} // namespace iPhoneMirror::capture
