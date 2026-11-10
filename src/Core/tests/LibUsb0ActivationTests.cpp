#include "Transport/LibUsb0Transport.h"
#include "Logging.h"
#include <lusb0_usb.h>

#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <chrono>
#include <stop_token>
#include <stdexcept>

// Substitute every libusb0 call used by the actual open path. This executable
// never links libusb0.dll and cannot enumerate or change a real phone.
namespace {
usb_bus bus{};
struct usb_device phone{};
usb_config_descriptor cached{};
usb_interface group{};
usb_interface_descriptor iface{};
std::array<usb_endpoint_descriptor, 2> endpoints{};
constexpr std::array<unsigned char, 32> live_descriptor{
    9, USB_DT_CONFIG, 32, 0, 1, 5, 0, 0x80, 50,
    9, USB_DT_INTERFACE, 2, 0, 2, 0xff, 0x2a, 0, 0,
    7, USB_DT_ENDPOINT, 0x86, USB_ENDPOINT_TYPE_BULK, 0, 2, 0,
    7, USB_DT_ENDPOINT, 0x05, USB_ENDPOINT_TYPE_BULK, 0, 2, 0};
int configuration_reads{}, descriptor_reads{}, opens{}, closes{}, claims{}, sets{}, unexpected_calls{};
int ready_on_read{}, claim_result{};
bool unreadable_configuration{};
std::stop_source* cancel_on_probe{};

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

void reset(int ready_after = 4, bool cached_quicktime = true) {
    bus = {}; phone = {}; cached = {}; group = {}; iface = {}; endpoints = {};
    strcpy_s(bus.dirname, "fixture-bus");
    strcpy_s(phone.filename, "fixture-phone");
    bus.devices = &phone;
    phone.bus = &bus;
    phone.descriptor.idVendor = 0x05ac;
    phone.descriptor.idProduct = 0x12a8;
    phone.descriptor.iSerialNumber = 1;
    phone.descriptor.bNumConfigurations = 1;
    phone.config = &cached;
    cached.bConfigurationValue = 5;
    cached.bNumInterfaces = 1;
    cached.interface = &group;
    group.num_altsetting = 1;
    group.altsetting = &iface;
    iface.bInterfaceNumber = 2;
    iface.bInterfaceClass = 0xff;
    iface.bInterfaceSubClass = cached_quicktime ? 0x2a : 0xfd;
    iface.bNumEndpoints = 2;
    iface.endpoint = endpoints.data();
    endpoints[0].bEndpointAddress = 0x86;
    endpoints[1].bEndpointAddress = 0x05;
    for (auto& endpoint : endpoints) {
        endpoint.bmAttributes = USB_ENDPOINT_TYPE_BULK;
        endpoint.wMaxPacketSize = 512;
    }
    configuration_reads = descriptor_reads = opens = closes = claims = sets = unexpected_calls = 0;
    ready_on_read = ready_after;
    claim_result = 0;
    unreadable_configuration = false;
    cancel_on_probe = nullptr;
}

auto open(bool wait, std::stop_token stop = {}) {
    using namespace iPhoneMirror::transport;
    return LibUsb0Connection::open_quicktime(AppleUsbIdentity{
        .serial = "fixture", .expected_quicktime_configuration = 5},
        LibUsb0QuickTimeOpenOptions{.allow_configuration_initialization = true,
            .wait_for_activated_descriptor = wait, .stop_token = stop});
}
} // namespace

namespace iPhoneMirror::logging {
void write(std::string_view) noexcept {}
void write(Level, std::string_view, std::string_view) noexcept {}
} // namespace iPhoneMirror::logging

extern "C" {
void usb_init() {}
int usb_find_busses() { return 0; }
int usb_find_devices() { return 0; }
usb_bus* usb_get_busses() { return &bus; }
usb_dev_handle* usb_open(struct usb_device* value) {
    ++opens;
    if (value != &phone) ++unexpected_calls;
    return reinterpret_cast<usb_dev_handle*>(value);
}
int usb_close(usb_dev_handle*) { ++closes; return 0; }
int usb_get_string_simple(usb_dev_handle*, int, char* output, size_t capacity) {
    constexpr char serial[] = "fixture";
    if (capacity < sizeof(serial)) return -1;
    std::memcpy(output, serial, sizeof(serial));
    return sizeof(serial) - 1;
}
int usb_control_msg(usb_dev_handle*, int type, int request, int, int, char* output, int size, int) {
    if (type != 0x80 || request != USB_REQ_GET_CONFIGURATION || size != 1) {
        ++unexpected_calls; return -1;
    }
    ++configuration_reads;
    if (unreadable_configuration) return -1;
    *output = configuration_reads >= ready_on_read ? 5 : 3;
    return 1;
}
int usb_get_descriptor(usb_dev_handle*, unsigned char type, unsigned char, void* output, int size) {
    ++descriptor_reads;
    if (cancel_on_probe) cancel_on_probe->request_stop();
    if (type == USB_DT_DEVICE) {
        const auto count = (std::min)(size, static_cast<int>(sizeof(phone.descriptor)));
        std::memcpy(output, &phone.descriptor, count);
        return count;
    }
    if (type == USB_DT_CONFIG) {
        const auto count = (std::min)(size, static_cast<int>(live_descriptor.size()));
        std::memcpy(output, live_descriptor.data(), count);
        return count;
    }
    ++unexpected_calls; return -1;
}
int usb_claim_interface(usb_dev_handle*, int number) {
    ++claims;
    if (number != 2) ++unexpected_calls;
    return claim_result;
}
int usb_set_configuration(usb_dev_handle*, int value) {
    ++sets;
    if (value != 5 || configuration_reads < ready_on_read) ++unexpected_calls;
    claim_result = 0;
    return 0;
}
char* usb_strerror() { static char text[] = "invalid configuration 0"; return text; }
int usb_release_interface(usb_dev_handle*, int) { ++unexpected_calls; return -1; }
int usb_set_altinterface(usb_dev_handle*, int) { ++unexpected_calls; return -1; }
int usb_clear_halt(usb_dev_handle*, unsigned int) { ++unexpected_calls; return -1; }
int usb_bulk_read(usb_dev_handle*, int, char*, int, int) { ++unexpected_calls; return -1; }
int usb_bulk_write(usb_dev_handle*, int, char*, int, int) { ++unexpected_calls; return -1; }
}

int main() {
    try {
        // The reported failure: descriptor 5 is already cached, but the phone
        // returns active=3 twice and commits configuration 5 only after a probe.
        reset();
        { auto connection = open(true); }
        require(descriptor_reads >= 6 && claims == 1 && sets == 0 && opens == 1 && closes == 1,
            "wait for active=5 on the same handle before claiming the visible descriptor");

        reset(3, false);
        { auto connection = open(true); }
        require(descriptor_reads == 3 && claims == 1 && opens == 1 && closes == 1,
            "a newly published descriptor still supports the existing activation path");

        reset(1);
        { auto connection = open(true); }
        require(descriptor_reads == 0 && sets == 0 && claims == 1,
            "an already active QuickTime device is claimed without polling or reconfiguration");

        reset();
        bool rejected{};
        try { auto connection = open(false); }
        catch (const std::runtime_error&) { rejected = true; }
        require(rejected && descriptor_reads == 0 && claims == 0 && sets == 0 && closes == opens,
            "retained descriptors do not authorize waiting or SET_CONFIGURATION without activation");

        reset(4);
        claim_result = -22;
        { auto connection = open(true); }
        require(sets == 1 && claims == 2 && unexpected_calls == 0,
            "pipe-table initialization occurs once only after active configuration is verified");

        reset(999);
        std::stop_source stop;
        cancel_on_probe = &stop;
        rejected = false;
        try { auto connection = open(true, stop.get_token()); }
        catch (const std::runtime_error& error) {
            rejected = std::string_view(error.what()).find("cancelled") != std::string_view::npos;
        }
        require(rejected && claims == 0 && sets == 0 && closes == opens,
            "cancel a pending transition without claiming or reconfiguring its handle");

        reset(999);
        rejected = false;
        const auto started = std::chrono::steady_clock::now();
        try { auto connection = open(true); }
        catch (const std::runtime_error& error) {
            rejected = std::string_view(error.what()).find("not committed") != std::string_view::npos;
        }
        require(rejected && claims == 0 && sets == 0 && closes == opens &&
            std::chrono::steady_clock::now() - started < std::chrono::seconds(7),
            "a device that stays on configuration 3 times out without forced reconfiguration");
        require(unexpected_calls == 0, "no unplanned USB operations");
        std::puts("libusb0 activation tests passed.");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s\n", error.what());
        return 1;
    }
}
