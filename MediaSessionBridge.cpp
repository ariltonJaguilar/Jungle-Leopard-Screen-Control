#include <windows.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Media.Control.h>
#include <winrt/Windows.Storage.Streams.h>

#include <chrono>
#include <cstdint>
#include <deque>
#include <map>
#include <string>
#include <string_view>
#include <vector>

using namespace winrt;
using namespace Windows::Media::Control;
using namespace Windows::Storage::Streams;

namespace
{
    std::string ToUtf8(std::wstring_view value)
    {
        if (value.empty()) return {};
        int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
        if (size <= 0) return {};
        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    std::string Escape(std::string_view value)
    {
        std::string result;
        result.reserve(value.size() + 8);
        for (unsigned char c : value)
        {
            switch (c)
            {
            case '"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (c < 0x20) result += ' ';
                else result += static_cast<char>(c);
            }
        }
        return result;
    }

    std::string Base64(const std::vector<uint8_t>& bytes)
    {
        static constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        std::string result;
        result.reserve(((bytes.size() + 2) / 3) * 4);
        for (size_t i = 0; i < bytes.size(); i += 3)
        {
            uint32_t value = static_cast<uint32_t>(bytes[i]) << 16;
            if (i + 1 < bytes.size()) value |= static_cast<uint32_t>(bytes[i + 1]) << 8;
            if (i + 2 < bytes.size()) value |= bytes[i + 2];
            result.push_back(alphabet[(value >> 18) & 0x3f]);
            result.push_back(alphabet[(value >> 12) & 0x3f]);
            result.push_back(i + 1 < bytes.size() ? alphabet[(value >> 6) & 0x3f] : '=');
            result.push_back(i + 2 < bytes.size() ? alphabet[value & 0x3f] : '=');
        }
        return result;
    }

    std::string ReadThumbnail(const GlobalSystemMediaTransportControlsSessionMediaProperties& properties)
    {
        try
        {
            auto reference = properties.Thumbnail();
            if (!reference) return {};
            auto stream = reference.OpenReadAsync().get();
            uint64_t size = stream.Size();
            constexpr uint64_t maxBytes = 2 * 1024 * 1024;
            if (size == 0 || size > maxBytes) return {};

            DataReader reader(stream.GetInputStreamAt(0));
            reader.LoadAsync(static_cast<uint32_t>(size)).get();
            std::vector<uint8_t> bytes(static_cast<size_t>(size));
            reader.ReadBytes(array_view<uint8_t>(bytes));
            reader.Close();
            stream.Close();
            return Base64(bytes);
        }
        catch (...) { return {}; }
    }

    std::string CachedThumbnail(const std::string& key,
        const GlobalSystemMediaTransportControlsSessionMediaProperties& properties)
    {
        struct Entry
        {
            std::string image;
            std::chrono::steady_clock::time_point retryAfter;
        };
        static std::map<std::string, Entry> cache;
        static std::deque<std::string> insertionOrder;
        auto cached = cache.find(key);
        if (cached != cache.end()
            && (!cached->second.image.empty() || std::chrono::steady_clock::now() < cached->second.retryAfter))
            return cached->second.image;

        std::string image = ReadThumbnail(properties);
        auto retryAfter = image.empty()
            ? std::chrono::steady_clock::now() + std::chrono::seconds(5)
            : std::chrono::steady_clock::time_point{};
        if (cached == cache.end())
        {
            cache.emplace(key, Entry{ image, retryAfter });
            insertionOrder.push_back(key);
        }
        else
        {
            cached->second = Entry{ image, retryAfter };
        }
        while (insertionOrder.size() > 16)
        {
            cache.erase(insertionOrder.front());
            insertionOrder.pop_front();
        }
        return image;
    }

    std::string BuildSnapshot(const GlobalSystemMediaTransportControlsSession& session)
    {
        auto properties = session.TryGetMediaPropertiesAsync().get();
        auto timeline = session.GetTimelineProperties();
        auto status = session.GetPlaybackInfo().PlaybackStatus();
        auto appId = ToUtf8(session.SourceAppUserModelId());
        auto title = ToUtf8(properties.Title());
        auto artist = ToUtf8(properties.Artist());
        auto album = ToUtf8(properties.AlbumTitle());
        std::string cacheKey = appId + "\x1f" + title + "\x1f" + artist + "\x1f" + album;
        std::string cover = CachedThumbnail(cacheKey, properties);
        long long positionMs = std::chrono::duration_cast<std::chrono::milliseconds>(timeline.Position()).count();
        long long endMs = std::chrono::duration_cast<std::chrono::milliseconds>(timeline.EndTime()).count();
        bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
        return "{\"appId\":\"" + Escape(appId) + "\",\"title\":\"" + Escape(title)
            + "\",\"artist\":\"" + Escape(artist) + "\",\"album\":\"" + Escape(album)
            + "\",\"coverBase64\":\"" + cover
            + "\",\"positionMs\":" + std::to_string(positionMs)
            + ",\"durationMs\":" + std::to_string(endMs)
            + ",\"playing\":" + (playing ? "true" : "false") + "}";
    }
}

extern "C" __declspec(dllexport) int __cdecl GetMediaSessionSnapshot(char* buffer, int capacity) noexcept
{
    if (!buffer || capacity <= 0) return -1;
    buffer[0] = '\0';
    try
    {
        thread_local bool apartmentInitialized = false;
        if (!apartmentInitialized)
        {
            init_apartment(apartment_type::multi_threaded);
            apartmentInitialized = true;
        }
        auto manager = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().get();
        auto current = manager.GetCurrentSession();
        std::string json;
        if (current)
        {
            try { json = BuildSnapshot(current); }
            catch (...) { json.clear(); }
        }

        std::string fallback = std::move(json);
        json.clear();
        auto sessions = manager.GetSessions();
        for (uint32_t index = 0; index < sessions.Size(); ++index)
        {
            auto session = sessions.GetAt(index);
            if (!session) continue;
            try
            {
                auto candidate = BuildSnapshot(session);
                if (candidate.find("\"playing\":true") != std::string::npos)
                {
                    json = std::move(candidate);
                    break;
                }
                if (fallback.empty()) fallback = std::move(candidate);
            }
            catch (...) { }
        }

        if (json.empty()) json = std::move(fallback);
        if (json.empty()) json = "{}";
        if (json.size() + 1 > static_cast<size_t>(capacity)) return -2;
        memcpy(buffer, json.data(), json.size());
        buffer[json.size()] = '\0';
        return static_cast<int>(json.size());
    }
    catch (...)
    {
        return -3;
    }
}
