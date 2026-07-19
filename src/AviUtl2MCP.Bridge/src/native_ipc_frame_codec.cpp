#include "aviutl2_mcp/native_ipc_frame_codec.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <initializer_list>
#include <limits>
#include <memory>
#include <stdexcept>

namespace aviutl2_mcp {
namespace {

void read_exact(
    byte_transport& transport,
    const std::span<std::uint8_t> buffer,
    const bool allow_clean_end = false) {
    std::size_t offset = 0U;
    while (offset < buffer.size()) {
        const auto read = transport.read_some(buffer.subspan(offset));
        if (read == 0U) {
            if (allow_clean_end && offset == 0U) {
                throw ipc_stream_closed();
            }
            throw std::runtime_error("IPC frame ended before the requested bytes were read");
        }
        if (read > buffer.size() - offset) {
            throw std::runtime_error("IPC transport returned an invalid read count");
        }
        offset += read;
    }
}

void write_all(byte_transport& transport, const std::span<const std::uint8_t> buffer) {
    std::size_t offset = 0U;
    while (offset < buffer.size()) {
        const auto written = transport.write_some(buffer.subspan(offset));
        if (written == 0U || written > buffer.size() - offset) {
            throw std::runtime_error("IPC transport returned an invalid write count");
        }
        offset += written;
    }
}

void check_nt_status(const NTSTATUS status, const char* message) {
    if (status < 0) {
        throw std::runtime_error(message);
    }
}

class algorithm_handle final {
public:
    algorithm_handle() {
        check_nt_status(
            BCryptOpenAlgorithmProvider(&handle_, BCRYPT_SHA256_ALGORITHM, nullptr, 0U),
            "BCryptOpenAlgorithmProvider failed");
    }

    ~algorithm_handle() {
        if (handle_ != nullptr) {
            BCryptCloseAlgorithmProvider(handle_, 0U);
        }
    }

    algorithm_handle(const algorithm_handle&) = delete;
    algorithm_handle& operator=(const algorithm_handle&) = delete;

    [[nodiscard]] BCRYPT_ALG_HANDLE get() const noexcept {
        return handle_;
    }

private:
    BCRYPT_ALG_HANDLE handle_ = nullptr;
};

class hash_handle final {
public:
    hash_handle(const BCRYPT_ALG_HANDLE algorithm, std::span<std::uint8_t> object) {
        check_nt_status(
            BCryptCreateHash(
                algorithm,
                &handle_,
                object.data(),
                static_cast<ULONG>(object.size()),
                nullptr,
                0U,
                0U),
            "BCryptCreateHash failed");
    }

    ~hash_handle() {
        if (handle_ != nullptr) {
            BCryptDestroyHash(handle_);
        }
    }

    hash_handle(const hash_handle&) = delete;
    hash_handle& operator=(const hash_handle&) = delete;

    [[nodiscard]] BCRYPT_HASH_HANDLE get() const noexcept {
        return handle_;
    }

private:
    BCRYPT_HASH_HANDLE handle_ = nullptr;
};

void append_hash(const BCRYPT_HASH_HANDLE hash, const std::span<const std::uint8_t> bytes) {
    if (bytes.empty()) {
        return;
    }
    if (bytes.size() > (std::numeric_limits<ULONG>::max)()) {
        throw std::length_error("hash input is too large");
    }
    check_nt_status(
        BCryptHashData(
            hash,
            const_cast<PUCHAR>(bytes.data()),
            static_cast<ULONG>(bytes.size()),
            0U),
        "BCryptHashData failed");
}

[[nodiscard]] std::string to_lower_hex(const std::span<const std::uint8_t> bytes) {
    constexpr char HEX[] = "0123456789abcdef";
    std::string result(bytes.size() * 2U, '0');
    for (std::size_t index = 0U; index < bytes.size(); ++index) {
        result[index * 2U] = HEX[bytes[index] >> 4U];
        result[index * 2U + 1U] = HEX[bytes[index] & 0x0fU];
    }
    return result;
}

[[nodiscard]] std::string calculate_hash(
    const std::initializer_list<std::span<const std::uint8_t>> chunks) {
    algorithm_handle algorithm;
    DWORD object_bytes = 0U;
    DWORD result_bytes = 0U;
    check_nt_status(
        BCryptGetProperty(
            algorithm.get(),
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&object_bytes),
            sizeof(object_bytes),
            &result_bytes,
            0U),
        "BCryptGetProperty object length failed");
    DWORD hash_bytes = 0U;
    check_nt_status(
        BCryptGetProperty(
            algorithm.get(),
            BCRYPT_HASH_LENGTH,
            reinterpret_cast<PUCHAR>(&hash_bytes),
            sizeof(hash_bytes),
            &result_bytes,
            0U),
        "BCryptGetProperty hash length failed");

    std::vector<std::uint8_t> object(object_bytes);
    std::vector<std::uint8_t> digest(hash_bytes);
    hash_handle hash(algorithm.get(), object);
    for (const auto chunk : chunks) {
        append_hash(hash.get(), chunk);
    }
    check_nt_status(
        BCryptFinishHash(hash.get(), digest.data(), static_cast<ULONG>(digest.size()), 0U),
        "BCryptFinishHash failed");
    return to_lower_hex(digest);
}

}  // namespace

ipc_stream_closed::ipc_stream_closed()
    : std::runtime_error("IPC client disconnected between frames") {}

void validate_utf8(const std::span<const std::uint8_t> bytes) {
    std::size_t index = 0U;
    while (index < bytes.size()) {
        const std::uint8_t first = bytes[index++];
        if (first <= 0x7fU) {
            continue;
        }

        std::uint32_t code_point = 0U;
        std::size_t continuation_count = 0U;
        std::uint32_t minimum = 0U;
        if (first >= 0xc2U && first <= 0xdfU) {
            code_point = first & 0x1fU;
            continuation_count = 1U;
            minimum = 0x80U;
        } else if (first >= 0xe0U && first <= 0xefU) {
            code_point = first & 0x0fU;
            continuation_count = 2U;
            minimum = 0x800U;
        } else if (first >= 0xf0U && first <= 0xf4U) {
            code_point = first & 0x07U;
            continuation_count = 3U;
            minimum = 0x10000U;
        } else {
            throw std::invalid_argument("IPC JSON payload is not valid UTF-8");
        }

        if (continuation_count > bytes.size() - index) {
            throw std::invalid_argument("IPC JSON payload is not valid UTF-8");
        }
        for (std::size_t continuation = 0U; continuation < continuation_count; ++continuation) {
            const std::uint8_t next = bytes[index++];
            if ((next & 0xc0U) != 0x80U) {
                throw std::invalid_argument("IPC JSON payload is not valid UTF-8");
            }
            code_point = (code_point << 6U) | (next & 0x3fU);
        }
        if (code_point < minimum || code_point > 0x10ffffU
            || (code_point >= 0xd800U && code_point <= 0xdfffU)) {
            throw std::invalid_argument("IPC JSON payload is not valid UTF-8");
        }
    }
}

std::string calculate_payload_hash(
    const frame_header& header,
    const std::span<const std::uint8_t> json,
    const std::span<const std::uint8_t> binary) {
    if (header.json_length != json.size() || header.binary_length != binary.size()) {
        throw std::invalid_argument("IPC header lengths do not match the frame body");
    }

    std::array<std::uint8_t, 6> prefix{};
    prefix[0] = 1U;
    prefix[1] = static_cast<std::uint8_t>(header.flags);
    for (std::size_t index = 0U; index < sizeof(header.json_length); ++index) {
        prefix[2U + index] = static_cast<std::uint8_t>(header.json_length >> (index * 8U));
    }
    std::array<std::uint8_t, 8> binary_length{};
    for (std::size_t index = 0U; index < sizeof(header.binary_length); ++index) {
        binary_length[index] = static_cast<std::uint8_t>(header.binary_length >> (index * 8U));
    }

    return calculate_hash({prefix, json, binary_length, binary});
}

std::string calculate_sha256(const std::span<const std::uint8_t> bytes) {
    return calculate_hash({bytes});
}

ipc_frame read_frame(byte_transport& transport) {
    std::array<std::uint8_t, IPC_HEADER_BYTES> header_bytes{};
    read_exact(transport, header_bytes, true);
    frame_header header = decode_header(header_bytes);
    std::vector<std::uint8_t> json(header.json_length);
    std::vector<std::uint8_t> binary(static_cast<std::size_t>(header.binary_length));
    read_exact(transport, json);
    read_exact(transport, binary);
    validate_utf8(json);
    const std::string payload_hash = calculate_payload_hash(header, json, binary);
    return ipc_frame{
        .header = header,
        .json = std::move(json),
        .binary = std::move(binary),
        .payload_hash = payload_hash,
    };
}

void write_frame(byte_transport& transport, const ipc_frame& frame) {
    if (frame.header.json_length != frame.json.size()
        || frame.header.binary_length != frame.binary.size()) {
        throw std::invalid_argument("IPC header lengths do not match the frame body");
    }
    validate_utf8(frame.json);
    const auto header_bytes = encode_header(frame.header);
    write_all(transport, header_bytes);
    write_all(transport, frame.json);
    write_all(transport, frame.binary);
}

}  // namespace aviutl2_mcp
