#pragma once

#include "Media/CoreMedia.h"
#include "Protocol/QuickTimePacket.h"

#include <chrono>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace iPhoneMirror::quicktime {

enum class SessionState {
    WaitingForPing,
    WaitingForAudioClock,
    Negotiating,
    Streaming,
    Stopping,
    Stopped,
    Error,
};

struct SessionOptions {
    // Valeria=false makes iOS honor DisplaySize. The protocol selects an
    // encoder tier from the requested long edge; an arbitrary huge square can
    // fall back to 1440p. 1206x2622 is the highest tier verified on both test
    // devices; broader 4K bounds make recent iOS choose a lower encoder tier.
    std::uint32_t requested_width{1206};
    std::uint32_t requested_height{2622};
    bool request_native_display_size{false};
    // The reverse-engineered HPD1 message has no verified frame-rate key.
    // Keep this preference alongside the negotiated dimensions so downstream
    // preview/recording sinks can cap presentation without destabilising the
    // private USB handshake.
    std::uint32_t target_fps{60};
    // Valeria is Apple's showroom/status-bar override (9:41, full battery).
    // QuickTime enables it for demonstrations, but a mirroring application
    // should preserve the phone's real status bar by default.
    bool demo_mode{false};
    bool request_audio{true};
    bool advertise_hevc_444{false};
};

struct SessionEvent {
    std::vector<std::vector<std::uint8_t>> outbound;
    std::optional<coremedia::SampleBuffer> video_sample;
    std::optional<coremedia::SampleBuffer> audio_sample;
    std::string warning;
    SessionState state{SessionState::WaitingForPing};
};

class SessionProtocol {
public:
    explicit SessionProtocol(SessionOptions options = {});

    [[nodiscard]] SessionEvent process(const Packet& packet);
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> stop_messages();
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> complete_stop_messages();
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> begin_display_reconfigure(
        std::uint32_t width, std::uint32_t height);
    [[nodiscard]] std::vector<std::vector<std::uint8_t>> complete_display_reconfigure();
    // The device only sends the next video frame after it receives a NEED, and
    // a NEED is normally sent per video sample. When iOS drops one (for example
    // while the game opens the microphone and the audio route changes) the
    // video stops for good while audio keeps flowing; re-sending it resumes.
    [[nodiscard]] std::optional<std::vector<std::uint8_t>> resend_need();
    // Soft audio restart (HPA0 then HPA1) without touching the video stream.
    [[nodiscard]] std::optional<std::vector<std::uint8_t>> stop_audio_stream();
    [[nodiscard]] std::optional<std::vector<std::uint8_t>> start_audio_stream();
    void set_request_audio(bool enabled) noexcept { options_.request_audio = enabled; }
    [[nodiscard]] bool audio_requested() const noexcept { return options_.request_audio; }
    void set_demo_mode(bool enabled) noexcept { options_.demo_mode = enabled; }
    void reset();

    [[nodiscard]] SessionState state() const noexcept { return state_; }
    [[nodiscard]] std::uint64_t video_frames() const noexcept { return video_frames_; }
    [[nodiscard]] std::uint64_t audio_packets() const noexcept { return audio_packets_; }
    [[nodiscard]] std::uint64_t need_sent() const noexcept { return need_sent_; }
    [[nodiscard]] std::uint64_t reply_received() const noexcept { return reply_received_; }
    [[nodiscard]] std::optional<std::chrono::steady_clock::time_point>
    last_video_sample_at() const noexcept { return last_video_sample_at_; }
    [[nodiscard]] std::optional<std::chrono::steady_clock::time_point>
    last_audio_sample_at() const noexcept { return last_audio_sample_at_; }
    [[nodiscard]] const std::optional<coremedia::FormatDescription>& video_format() const noexcept { return video_format_; }
    [[nodiscard]] const std::optional<coremedia::FormatDescription>& audio_format() const noexcept { return audio_format_; }
    [[nodiscard]] const std::optional<coremedia::AudioStreamBasicDescription>& negotiated_audio() const noexcept { return negotiated_audio_; }

private:
    SessionOptions options_;
    SessionState state_{SessionState::WaitingForPing};
    std::chrono::steady_clock::time_point epoch_{std::chrono::steady_clock::now()};
    std::uint64_t device_audio_clock_{};
    std::uint64_t local_audio_clock_{};
    std::uint64_t device_video_clock_{};
    std::uint64_t local_video_clock_{};
    std::uint64_t local_host_clock_{};
    std::uint64_t video_frames_{};
    std::uint64_t audio_packets_{};
    // Stream-flow diagnostics: the mirror currently emits NEED only per video
    // sample and ignores RPLY entirely. These counters let a single idle-screen
    // capture confirm how the device stall correlates with NEED/RPLY traffic.
    std::uint64_t need_sent_{};
    std::uint64_t reply_received_{};
    // Bounded log of every packet seen before the first video sample, so a
    // stalled negotiation shows which SYNC step the device never sent.
    std::uint32_t negotiation_packets_logged_{};
    std::optional<std::chrono::steady_clock::time_point> last_video_sample_at_{};
    std::optional<std::chrono::steady_clock::time_point> last_audio_sample_at_{};
    std::optional<coremedia::FormatDescription> video_format_;
    std::optional<coremedia::FormatDescription> audio_format_;
    std::optional<coremedia::AudioStreamBasicDescription> negotiated_audio_;
};

} // namespace iPhoneMirror::quicktime
