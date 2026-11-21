#include "deck_link.h"

#include <algorithm>
#include <Preferences.h>
#include <WiFi.h>
#include <esp_system.h>
#include <lwip/sockets.h>
#include <mbedtls/gcm.h>
#include <mbedtls/md.h>
#include <mbedtls/sha256.h>

namespace decknet {
namespace {
constexpr uint16_t Port = 8766;
constexpr size_t MaxPacket = 4096;
constexpr uint8_t Discovery[] = {'A','D','2','?'};
constexpr uint8_t DiscoveryReply[] = {'A','D','2','!'};
constexpr uint8_t Pair[] = {'A','D','2','P'};
constexpr uint8_t Pull[] = {'A','D','2','Q'};
constexpr uint8_t Reply[] = {'A','D','2','R'};

struct Peer {
    String id, label, key, mac, relay;
};
SemaphoreHandle_t guard;
std::vector<Peer> peers;
uint8_t device[12]{};
String chosen, code, pending, pendingId, received;
String snapshot, controlTicket, controlId, controlBody, controlReply;
bool controlExecuting = false;
uint32_t codeAt = 0;
uint32_t lastPairAttempt = 0;
uint32_t lastLocal = 0, lastRemote = 0;
bool receivedRemote = false;

class Lock {
public:
    Lock() { xSemaphoreTake(guard, portMAX_DELAY); }
    ~Lock() { xSemaphoreGive(guard); }
};

String toHex(const uint8_t* bytes, size_t length) {
    const char digits[] = "0123456789abcdef";
    String result;
    result.reserve(length * 2);
    for (size_t i = 0; i < length; ++i) {
        result += digits[bytes[i] >> 4];
        result += digits[bytes[i] & 15];
    }
    return result;
}

bool fromHex(const String& text, uint8_t* bytes, size_t length) {
    if (text.length() != length * 2) return false;
    for (size_t i = 0; i < length; ++i) {
        uint8_t values[2]{};
        for (size_t j = 0; j < 2; ++j) {
            const char c = text[2*i+j];
            if (c >= '0' && c <= '9') values[j] = c - '0';
            else if (c >= 'a' && c <= 'f') values[j] = c - 'a' + 10;
            else return false;
        }
        bytes[i] = values[0] * 16 + values[1];
    }
    return true;
}

Peer* peer(const String& id) {
    for (auto& item : peers) if (item.id == id) return &item;
    return nullptr;
}

bool validPeer(const Peer& item) {
    uint8_t raw[32]{};
    const bool valid = item.label.length() && item.label.length() <= 120 &&
        fromHex(item.id, raw, 12) && fromHex(item.mac, raw, 6) && fromHex(item.key, raw, 32);
    memset(raw, 0, sizeof(raw));
    return valid;
}

bool save(const std::vector<Peer>& values, const String& active) {
    JsonDocument doc;
    doc["selected"] = active;
    auto array = doc["hosts"].to<JsonArray>();
    for (const auto& item : values) {
        auto entry = array.add<JsonObject>();
        entry["host"] = item.id;
        entry["label"] = item.label;
        entry["key"] = item.key;
        entry["mac"] = item.mac;
        if (item.relay.length()) entry["relay"] = item.relay;
    }
    String body;
    serializeJson(doc, body);
    Preferences prefs;
    if (!prefs.begin("agentdeck", false)) return false;
    const bool ok = prefs.putString("hosts2", body) == body.length();
    prefs.end();
    return ok;
}

void load() {
    Preferences prefs;
    if (!prefs.begin("agentdeck", false)) return;
    if (prefs.isKey("wifi")) prefs.remove("wifi");
    const String stored = prefs.getString("hosts2");
    prefs.end();
    JsonDocument doc;
    if (deserializeJson(doc, stored)) return;
    for (JsonObjectConst entry : doc["hosts"].as<JsonArrayConst>()) {
        const Peer item{entry["host"] | "", entry["label"] | "", entry["key"] | "",
                        entry["mac"] | "", entry["relay"] | ""};
        if (validPeer(item) && peers.size() < 8) peers.push_back(item);
    }
    const String desired = doc["selected"] | "";
    if (peer(desired)) chosen = desired;
}

bool registerPeer(JsonObjectConst request) {
    Peer item{request["host"] | "", request["label"] | "", request["key"] | "", request["mac"] | "", ""};
    if (!validPeer(item)) return false;
    auto updated = peers;
    auto found = std::find_if(updated.begin(), updated.end(), [&](const Peer& p) { return p.id == item.id; });
    if (found == updated.end()) {
        if (updated.size() >= 8) return false;
        updated.push_back(item);
    } else {
        *found = item;
    }
    if (!save(updated, item.id)) return false;
    peers = std::move(updated);
    chosen = item.id;
    pending = pendingId = received = "";
    code = "";
    return true;
}

bool activeCode() {
    if (code.length() && millis() - codeAt >= 120000) code = "";
    return code.length();
}

void resetControl() {
    lastLocal = lastRemote = 0;
    controlTicket = newId();
    controlId = controlBody = controlReply = "";
    controlExecuting = false;
}

void udpSend(int socketFd, const sockaddr_in& address, const uint8_t* bytes, size_t count) {
    lwip_sendto(socketFd, bytes, count, 0, reinterpret_cast<const sockaddr*>(&address), sizeof(address));
}

void derive(const uint8_t* secret, const char* label, uint8_t* key) {
    const auto* info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    mbedtls_md_hmac(info, secret, 32, reinterpret_cast<const uint8_t*>(label), strlen(label), key);
}

bool decrypt(const uint8_t* packet, const uint8_t* secret, size_t header, size_t size, JsonDocument& result) {
    const size_t start = header + 12;
    if (size <= start + 16) return false;
    const size_t length = size - start - 16;
    std::vector<uint8_t> plain(length);
    uint8_t key[32];
    derive(secret, "AgentDeck/pc-to-stick/v2", key);
    mbedtls_gcm_context ctx;
    mbedtls_gcm_init(&ctx);
    int rc = mbedtls_gcm_setkey(&ctx, MBEDTLS_CIPHER_ID_AES, key, 256);
    memset(key, 0, sizeof(key));
    if (!rc) rc = mbedtls_gcm_auth_decrypt(&ctx, length, packet + header, 12,
        packet, header, packet + start + length, 16, packet + start, plain.data());
    mbedtls_gcm_free(&ctx);
    return !rc && !deserializeJson(result, plain.data(), length) && result.is<JsonObject>();
}

std::vector<uint8_t> respond(const uint8_t* secret, const uint8_t* nonce, const JsonDocument& result) {
    String text;
    serializeJson(result, text);
    if (text.length() + 32 > MaxPacket) return {};
    std::vector<uint8_t> outgoing(text.length() + 32);
    memcpy(outgoing.data(), Reply, 4);
    esp_fill_random(outgoing.data() + 4, 12);
    uint8_t key[32];
    derive(secret, "AgentDeck/stick-to-pc/v2", key);
    mbedtls_gcm_context ctx;
    mbedtls_gcm_init(&ctx);
    int rc = mbedtls_gcm_setkey(&ctx, MBEDTLS_CIPHER_ID_AES, key, 256);
    memset(key, 0, sizeof(key));
    if (!rc) rc = mbedtls_gcm_crypt_and_tag(&ctx, MBEDTLS_GCM_ENCRYPT, text.length(),
        outgoing.data() + 4, 12, nonce, 12, reinterpret_cast<const uint8_t*>(text.c_str()),
        outgoing.data() + 16, 16, outgoing.data() + 16 + text.length());
    mbedtls_gcm_free(&ctx);
    if (rc) return {};
    return outgoing;
}

std::vector<uint8_t> handle(const uint8_t* packet, size_t size, bool remote) {
    if (!remote && size == 16 && !memcmp(packet, Discovery, 4)) {
        uint8_t reply[29];
        memcpy(reply, DiscoveryReply, 4);
        memcpy(reply + 4, packet + 4, 12);
        memcpy(reply + 16, device, 12);
        {
            Lock lock;
            reply[28] = activeCode() ? 1 : 0;
        }
        return {reply, reply + sizeof(reply)};
    }
    if (size < 32) return {};
    JsonDocument request, response;
    size_t header = 0;
    uint8_t secret[32]{};
    if (!remote && !memcmp(packet, Pair, 4)) {
        Lock lock;
        if (!activeCode() || millis() - lastPairAttempt < 150) return {};
        lastPairAttempt = millis();
        const char prefix[] = "AgentDeck pairing v2";
        uint8_t material[sizeof(prefix) + 16]{};
        memcpy(material, prefix, sizeof(prefix));
        memcpy(material + sizeof(prefix), code.c_str(), 16);
        if (mbedtls_sha256_ret(material, sizeof(material), secret, 0)) return {};
        memset(material, 0, sizeof(material));
        header = 4;
        if (decrypt(packet, secret, header, size, request) && request["device"] == deviceId()) {
            const bool paired = registerPeer(request.as<JsonObjectConst>());
            if (paired) resetControl();
            response["ok"] = paired;
            response["device"] = deviceId();
        }
    } else if (size >= 44 && !memcmp(packet, Pull, 4)) {
        Lock lock;
        const String id = toHex(packet + 4, 12);
        Peer* current = peer(id);
        if (!current || !fromHex(current->key, secret, sizeof(secret))) return {};
        header = 16;
        if (decrypt(packet, secret, header, size, request)) {
            response["device"] = deviceId();
            response["selected"] = id == chosen;
            if (id == chosen) {
                (remote ? lastRemote : lastLocal) = millis();
                const JsonObjectConst result = request["answer"].as<JsonObjectConst>();
                if (pendingId.length() && result["id"] == pendingId) {
                    received = "";
                    serializeJson(result, received);
                    receivedRemote = remote;
                    pending = pendingId = "";
                }
                if (pending.length()) response["request"] = serialized(pending);
                if (snapshot.length()) response["snapshot"] = serialized(snapshot);
                const JsonObjectConst control = request["control"].as<JsonObjectConst>();
                const String commandId = control["id"] | "";
                uint8_t rawId[12];
                if (commandId != controlId && fromHex(commandId, rawId, 12) &&
                    controlBody.isEmpty() && !controlExecuting && control["ticket"] == controlTicket) {
                    controlTicket = newId();
                    controlId = commandId;
                    controlReply = "";
                    serializeJson(control, controlBody);
                }
                response["ticket"] = controlTicket;
                if (controlReply.length()) response["control_result"] = serialized(controlReply);
                String mac = request["mac"] | "";
                uint8_t parsed[6]{};
                if (!remote && mac != current->mac && fromHex(mac, parsed, 6) && mac != "000000000000") {
                    current->mac = mac;
                    save(peers, chosen);
                }
            }
        }
    }
    auto result = header && !response.isNull() ? respond(secret, packet + header, response) :
                                                std::vector<uint8_t>{};
    memset(secret, 0, sizeof(secret));
    return result;
}

void listen(void*) {
    int socketFd = -1;
    std::vector<uint8_t> packet(MaxPacket + 1);
    while (true) {
        if (WiFi.status() != WL_CONNECTED) {
            if (socketFd >= 0) lwip_close(socketFd);
            socketFd = -1;
            vTaskDelay(pdMS_TO_TICKS(120));
            continue;
        }
        if (socketFd < 0) {
            socketFd = lwip_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
            sockaddr_in local{};
            local.sin_family = AF_INET;
            local.sin_port = htons(Port);
            local.sin_addr.s_addr = INADDR_ANY;
            if (socketFd >= 0 && lwip_bind(socketFd, reinterpret_cast<sockaddr*>(&local), sizeof(local))) {
                lwip_close(socketFd);
                socketFd = -1;
            }
        }
        if (socketFd >= 0) {
            sockaddr_in source{};
            socklen_t sourceSize = sizeof(source);
            const int size = lwip_recvfrom(socketFd, packet.data(), packet.size(), MSG_DONTWAIT,
                reinterpret_cast<sockaddr*>(&source), &sourceSize);
            if (size > 0 && size <= int(MaxPacket)) {
                auto result = handle(packet.data(), size, false);
                if (!result.empty()) udpSend(socketFd, source, result.data(), result.size());
            }
        }
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

int relayExchange(const String& url, const uint8_t* authKey, const std::vector<uint8_t>& outgoing,
                  std::vector<uint8_t>& incoming) {
    const int pathAt = url.indexOf('/', 7);
    if (!url.startsWith("http://") || pathAt < 0) return -1;
    const String authority = url.substring(7, pathAt);
    const int split = authority.indexOf(':');
    const String domain = split < 0 ? authority : authority.substring(0, split);
    const int port = split < 0 ? 80 : authority.substring(split + 1).toInt();
    WiFiClient client;
    client.setTimeout(2);
    if (!port || !client.connect(domain.c_str(), port, 1500)) return -1;
    uint8_t nonce[12], mac[32];
    esp_fill_random(nonce, sizeof(nonce));
    mbedtls_md_context_t hash;
    mbedtls_md_init(&hash);
    int error = mbedtls_md_setup(&hash, mbedtls_md_info_from_type(MBEDTLS_MD_SHA256), 1);
    if (!error) error = mbedtls_md_hmac_starts(&hash, authKey, 32);
    if (!error) error = mbedtls_md_hmac_update(&hash, reinterpret_cast<const uint8_t*>("stick"), 5);
    if (!error) error = mbedtls_md_hmac_update(&hash, nonce, sizeof(nonce));
    if (!error && !outgoing.empty()) error = mbedtls_md_hmac_update(&hash, outgoing.data(), outgoing.size());
    if (!error) error = mbedtls_md_hmac_finish(&hash, mac);
    mbedtls_md_free(&hash);
    if (error) return -1;
    const String header = "POST " + url.substring(pathAt) + " HTTP/1.0\r\nHost: " + authority +
        "\r\nX-AgentDeck-Nonce: " + toHex(nonce, sizeof(nonce)) +
        "\r\nX-AgentDeck-Auth: " + toHex(mac, sizeof(mac)) +
        "\r\nConnection: close\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
        String(outgoing.size()) + "\r\n\r\n";
    if (client.print(header) != header.length() ||
        (!outgoing.empty() && client.write(outgoing.data(), outgoing.size()) != outgoing.size())) return -1;
    String line = client.readStringUntil('\n');
    if (!line.startsWith("HTTP/1.0 200 ") && !line.startsWith("HTTP/1.1 200 ")) return -1;
    int length = -1;
    size_t headerSize = line.length();
    while (headerSize < 4096) {
        line = client.readStringUntil('\n');
        headerSize += line.length();
        if (!line.length()) return -1;
        if (line == "\r") {
            if (length < 0 || length > int(MaxPacket)) return -1;
            return client.readBytes(incoming.data(), length) == size_t(length) ? length : -1;
        }
        line.toLowerCase();
        if (line.startsWith("content-length:")) length = line.substring(15).toInt();
    }
    return -1;
}

void relayLoop(void*) {
    String previousUrl;
    std::vector<uint8_t> incoming(MaxPacket + 1), outgoing;
    uint8_t previousNonce[12]{};
    while (true) {
        String url;
        uint8_t authKey[32]{};
        bool waiting = false;
        if (WiFi.status() == WL_CONNECTED) {
            Lock lock;
            const Peer* pc = peer(chosen);
            waiting = pendingId.length();
            if (pc && pc->relay.length() && (!lastLocal || millis() - lastLocal > 2800)) {
                uint8_t key[32], channel[32];
                if (fromHex(pc->key, key, sizeof(key))) {
                    derive(key, "AgentDeck/remote-mailbox/v1", channel);
                    derive(key, "AgentDeck/remote-auth/v1", authKey);
                    url = pc->relay + "/v1/" + toHex(channel, sizeof(channel)) + "/stick";
                }
                memset(key, 0, sizeof(key));
            }
        }
        if (url != previousUrl) {
            outgoing.clear();
            memset(previousNonce, 0, sizeof(previousNonce));
            previousUrl = url;
        }
        if (url.length()) {
            const int count = relayExchange(url, authKey, outgoing, incoming);
            if (count >= 44 && count <= int(MaxPacket) && !memcmp(incoming.data(), Pull, 4) &&
                memcmp(previousNonce, incoming.data() + 16, sizeof(previousNonce))) {
                auto response = handle(incoming.data(), count, true);
                if (!response.empty()) {
                    memcpy(previousNonce, incoming.data() + 16, sizeof(previousNonce));
                    outgoing.assign(previousNonce, previousNonce + sizeof(previousNonce));
                    outgoing.insert(outgoing.end(), response.begin(), response.end());
                }
            }
        }
        memset(authKey, 0, sizeof(authKey));
        vTaskDelay(pdMS_TO_TICKS(waiting && url.length() ? 580 : 1300));
    }
}
} // namespace

void begin() {
    guard = xSemaphoreCreateMutex();
    const uint64_t mac = ESP.getEfuseMac();
    uint8_t hash[32];
    mbedtls_sha256_ret(reinterpret_cast<const uint8_t*>(&mac), sizeof(mac), hash, 0);
    memcpy(device, hash, sizeof(device));
    resetControl();
    load();
    xTaskCreate(listen, "deck-link", 10000, nullptr, 1, nullptr);
    xTaskCreate(relayLoop, "deck-relay", 11000, nullptr, 1, nullptr);
}

String deviceId() { return toHex(device, sizeof(device)); }

String newId() {
    uint8_t random[12];
    esp_fill_random(random, sizeof(random));
    return toHex(random, sizeof(random));
}

bool pairUsb(JsonObjectConst request) {
    Lock lock;
    const String supplied = request["code"] | "";
    if (supplied.length() && (!activeCode() || supplied != code)) return false;
    if (!registerPeer(request)) return false;
    resetControl();
    return true;
}

String openPairing() {
    Lock lock;
    const char alphabet[] = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    uint8_t random[16];
    esp_fill_random(random, sizeof(random));
    code = "";
    for (uint8_t n : random) code += alphabet[n & 31];
    codeAt = millis();
    return code;
}

String pairingCode() {
    Lock lock;
    return activeCode() ? code : "";
}

uint32_t pairingRemaining() {
    Lock lock;
    return activeCode() ? 120000 - (millis() - codeAt) : 0;
}

void closePairing() {
    Lock lock;
    code = "";
}

std::vector<Host> hosts() {
    Lock lock;
    std::vector<Host> items;
    for (const auto& p : peers) items.push_back({p.id, p.label});
    return items;
}

String selected() {
    Lock lock;
    return chosen;
}

bool selectHost(const String& host) {
    Lock lock;
    if (!peer(host) || !save(peers, host)) return false;
    chosen = host;
    pending = pendingId = received = "";
    resetControl();
    return true;
}

bool forgetHost(const String& host) {
    Lock lock;
    auto updated = peers;
    updated.erase(std::remove_if(updated.begin(), updated.end(),
                                 [&](const Peer& item) { return item.id == host; }), updated.end());
    const String active = chosen == host ? (updated.empty() ? String() : updated[0].id) : chosen;
    if (!save(updated, active)) return false;
    peers = std::move(updated);
    chosen = active;
    pending = pendingId = received = "";
    resetControl();
    return true;
}

void request(const String& body) {
    JsonDocument doc;
    if (deserializeJson(doc, body)) return;
    Lock lock;
    pendingId = doc["id"] | "";
    pending = body;
}

void cancelRequest() {
    Lock lock;
    pending = pendingId = received = "";
}

String answer(bool& remote) {
    Lock lock;
    remote = receivedRemote;
    String content = received;
    received = "";
    return content;
}

bool configureRelay(const String& host, const String& endpoint) {
    Lock lock;
    Peer* pc = peer(host);
    if (!pc || endpoint.length() > 180 ||
        (endpoint.length() && !endpoint.startsWith("http://"))) return false;
    auto previous = pc->relay;
    pc->relay = endpoint;
    if (save(peers, chosen)) return true;
    pc->relay = previous;
    return false;
}

void publish(const String& state) {
    Lock lock;
    snapshot = state;
}

String takeControl() {
    Lock lock;
    const String command = controlBody;
    if (command.length()) {
        controlExecuting = true;
        controlBody = "";
    }
    return command;
}

void completeControl(const String& result) {
    JsonDocument doc;
    if (deserializeJson(doc, result)) return;
    Lock lock;
    if (doc["id"] == controlId) {
        controlReply = result;
        controlExecuting = false;
    }
}

bool wake(const String& host) {
    if (WiFi.status() != WL_CONNECTED) return false;
    uint8_t mac[6];
    {
        Lock lock;
        const Peer* item = peer(host.length() ? host : chosen);
        if (!item || item->mac == "000000000000" || !fromHex(item->mac, mac, 6)) return false;
    }
    const IPAddress local = WiFi.localIP(), mask = WiFi.subnetMask();
    IPAddress broadcast;
    for (int i = 0; i < 4; ++i) broadcast[i] = local[i] | ~mask[i];
    uint8_t bytes[102];
    memset(bytes, 255, 6);
    for (auto i = 0; i < 16; ++i) memcpy(bytes + 6 + i * 6, mac, 6);
    const int socketFd = lwip_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socketFd < 0) return false;
    const int enabled = 1;
    lwip_setsockopt(socketFd, SOL_SOCKET, SO_BROADCAST, &enabled, sizeof(enabled));
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(9);
    address.sin_addr.s_addr = static_cast<uint32_t>(broadcast);
    const bool sent = lwip_sendto(socketFd, bytes, sizeof(bytes), 0,
                                 reinterpret_cast<sockaddr*>(&address), sizeof(address)) == sizeof(bytes);
    lwip_close(socketFd);
    return sent;
}
} // namespace decknet
