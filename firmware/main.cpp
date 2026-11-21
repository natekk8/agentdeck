#include <Arduino.h>
#include <ArduinoJson.h>
#include <M5Unified.h>
#include <Preferences.h>
#include <WiFi.h>

#include <esp_ota_ops.h>

#include <vector>
#include "deck_fonts.h"
#include "deck_link.h"
#include <algorithm>
#include <atomic>

namespace {
constexpr int Width = 240;
constexpr int Height = 135;

constexpr size_t FrameLimit = 16384;
constexpr uint16_t Background = 0x0021;
constexpr uint16_t Panel = 0x08A4;
constexpr uint16_t Focus = 0x21ac;
constexpr uint16_t Outline = 0x1908;
constexpr uint16_t Text = 0xF7BF;
constexpr uint16_t Muted = 0xADDA;
constexpr uint16_t Mint = 0x6739;
constexpr uint16_t Yellow = 0xFE2D;
constexpr uint16_t Pink = 0xFC93;
constexpr uint16_t Blue = 0x8DDF;
constexpr char Frame[] = "AD1 ";

enum class Page { Home, Projects, Models, Limits, Prompts, Confirm,
                  Sessions, Job, Stop, Settings, Pair, Cooldown, Wifi, WifiScan, Keys, Hosts,
                  WakeHosts, ShutdownHosts, Shutdown, Effort, Accounts, Language, Question };

struct Row {
    String id;
    String label;
    String detail;
    String state;
    bool git;
    int percent;
    String desc;
};

struct View {
    explicit View(Page page) : page(page) {}
    Page page;
    int cursor = 0;
    int first = 0;
    int offset = 0;
    int pageSize = 8;
    int total = 0;
    std::vector<Row> rows;
};

struct WifiNetwork {
    String name;
    int32_t rssi;
    bool secured;
};

struct WifiProfile {
    String name;
    String password;
};

bool langPl = false;

inline const char* tr(const char* en, const char* pl) {
    return langPl ? pl : en;
}

View view{Page::Home};
std::vector<View> history;
std::vector<WifiNetwork> scannedNetworks;
std::vector<WifiProfile> knownNetworks;
String provider, project, projectLabel, model, modelLabel, effort, effortLabel, prompt, promptLabel, promptDesc, jobId;
String accountId;
String questionId, questionText, seenQuestionId;
std::vector<String> questionOptions;
int answerChoice = -1;
String jobState, jobDetail, jobPreview, jobLabel, errorText;
bool statusMint = false;
String jobOutput;
String pinnedProject, powerHost, powerLabel;
String pendingId, pendingBody, pendingOp, source;
String idempotentBody, idempotentOp, pendingRoute, idempotentRoute;
String serialBuffer;
uint32_t pendingAt = 0, nextPoll = 0, lastAnswer = 0, elapsedAt = 0;
uint32_t cooldownAt = 0, displayAt = 0, lastDraw = 0, wakeAt = 0, usbAt = 0;
uint32_t wifiAttemptAt = 0, wifiLostAt = 0;
uint32_t nextAutoScan = 0;
String savedSsid, savedPassword, wifiTarget, wifiPassword, wifiStatus, pairDisplay;
String wifiPhase = "idle", wifiAction, queuedSsid, queuedPassword, wifiFailure;
uint32_t scanRevision = 0, queuedWifiAt = 0, publishedAt = 0;
std::atomic<uint8_t> wifiReason{0};
bool wifiStarting = false, scanning = false, wifiSetupOffered = false, wifiWasOnline = false;
bool wifiSecure = true;
bool wifiFromPc = false, wifiRestoring = false;
bool autoScanning = false;
bool preferLan = false;
int keyboard = 0;
const char* Keys[] = {"abcdefghijklmnopqrstuvwxyz", "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
                      "0123456789", " !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"};
int elapsed = 0, cooldown = -1, active = 0;
int batteryPct = -1;
bool batteryCharging = false;
uint32_t batteryAt = 0;
bool powered = false, powerAllowed = false, powerSent = false;
bool jobGit = false, demo = false;
String sessionMode = "read";
bool dirty = true, dim = false, sentLan = false, wakeRequested = false, serialOverflow = false;
bool listLoading = false;
bool powerGoHome = false;
int outputOffset = 0;
int outputPage = 0, outputLoaded = -1;
bool outputMore = false, outputTail = false;
LGFX_Sprite canvas(&M5.Display);
uint32_t motionAt = 0;
Page motionPage = Page::Home;
int motionCursor = -1;

void loadLanguage() {
    Preferences prefs;
    if (prefs.begin("agentdeck", true)) {
        langPl = prefs.getString("lang", "en") == "pl";
        prefs.end();
    }
}

void saveLanguage(bool pl) {
    langPl = pl;
    Preferences prefs;
    if (prefs.begin("agentdeck", false)) {
        prefs.putString("lang", pl ? "pl" : "en");
        prefs.end();
    }
    dirty = true;
}

const char* operation(Page page) {
    switch (page) {
        case Page::Projects: return "projects";
        case Page::Models: return "models";
        case Page::Effort: return "efforts";
        case Page::Limits: return "limits";
        case Page::Prompts: return "prompts";
        case Page::Sessions: return "sessions";
        case Page::Job: return "job";
        case Page::Accounts: return "accounts";
        default: return "status";
    }
}

void sendFrame(const String& frame) {
    if (!Serial) return;
    String message;
    message.reserve(frame.length() + sizeof(Frame) + 1);
    message += Frame;
    message += frame;
    message += '\n';
    Serial.write(reinterpret_cast<const uint8_t*>(message.c_str()), message.length());
}

bool isMutation(const String& op) {
    return op == "start" || op == "stop" || op == "power" || op == "retry" ||
           op == "create_project" || op == "switch_account" || op == "answer";
}

bool usbOnline() {
    return usbAt && millis() - usbAt < 1600;
}

void transmit() {
    pendingAt = millis();
    sentLan = false;
    if (pendingRoute != "LAN") sendFrame(pendingBody);
}

void request(const char* op, bool armed = false) {
    JsonDocument doc;
    pendingId = decknet::newId();
    doc["id"] = pendingId;
    doc["op"] = op;
    doc["provider"] = provider;
    doc["lang"] = langPl ? "pl" : "en";
    if (view.offset > 0) doc["offset"] = view.offset;
    if (view.page == Page::Prompts) doc["project"] = project;
    if (view.page == Page::Effort) doc["model"] = model;
    if (jobId.length()) doc["job"] = jobId;
    if (!strcmp(op, "job") && outputLoaded != outputPage)
        doc["output_page"] = outputPage;
    if (!strcmp(op, "start")) {
        doc["project"] = project;
        doc["model"] = model;
        doc["effort"] = effort;
        doc["prompt"] = prompt;
        doc["mode"] = sessionMode;
    }
    if (!strcmp(op, "answer")) {
        doc["choice"] = answerChoice;
    }
    if (!strcmp(op, "switch_account")) {
        doc["account"] = accountId;
    }
    if (!strcmp(op, "power")) {
        doc["armed"] = armed;
        if (armed) doc["confirm"] = "manual";
    }
    pendingBody = "";
    serializeJson(doc, pendingBody);
    pendingOp = op;
    pendingRoute = preferLan && decknet::selected().length() ? "LAN" :
                   usbOnline() ? "USB" : (isMutation(pendingOp) ?
                   (decknet::selected().length() ? "LAN" : "USB") : "");
    if (isMutation(pendingOp)) {
        idempotentBody = pendingBody;
        idempotentOp = pendingOp;
        idempotentRoute = pendingRoute;
    }
    transmit();
}

void statusNow() {
    pendingId = "";
    decknet::cancelRequest();
    nextPoll = 0;
    dirty = true;
}

void open(Page page) {
    history.push_back(view);
    view = View{page};
    errorText = "";
    statusMint = false;
    listLoading = true;
    statusNow();
}

void home() {
    history.clear();
    view = View{Page::Home};
    errorText = "";
    statusMint = false;
    pinnedProject = "";
    statusNow();
}

void back() {
    if (pendingId.length() && idempotentBody.length() && pendingOp == idempotentOp) return;
    if (view.page == Page::Pair) decknet::closePairing();
    if (view.page == Page::Keys) wifiPassword = "";
    if (history.empty()) {
        home();
        return;
    }
    view = history.back();
    history.pop_back();
    errorText = "";
    statusMint = false;
    statusNow();
}

void loadWifi() {
    Preferences prefs;
    if (prefs.begin("agentdeck", true)) {
        JsonDocument stored;
        if (!deserializeJson(stored, prefs.getString("wifi2"))) {
            savedSsid = stored["ssid"] | "";
            savedPassword = stored["password"] | "";
            for (JsonObjectConst item : stored["known"].as<JsonArrayConst>()) {
                const String name = item["ssid"] | "";
                const String password = item["password"] | "";
                if (name.length() && name.length() <= 32 && password.length() <= 63 && knownNetworks.size() < 6)
                    knownNetworks.push_back({name, password});
            }
            if (knownNetworks.empty() && savedSsid.length()) knownNetworks.push_back({savedSsid, savedPassword});
        }
        prefs.end();
    }
}

void beginWifi(const String& ssid, const String& password) {
    wifiTarget = ssid;
    wifiPassword = password;
    wifiStatus = tr("Connecting...", "Łączenie…");
    wifiPhase = "connecting";
    wifiReason = 0;
    wifiStarting = true;
    wifiAttemptAt = millis();
    WiFi.setAutoReconnect(false);
    WiFi.disconnect();
    WiFi.begin(wifiTarget.c_str(), wifiPassword.length() ? wifiPassword.c_str() : nullptr);
    dirty = true;
}

void saveWifi() {
    if (wifiTarget == savedSsid && wifiPassword == savedPassword) return;
    Preferences prefs;
    JsonDocument doc;
    doc["ssid"] = wifiTarget;
    doc["password"] = wifiPassword;
    auto old = knownNetworks;
    knownNetworks.clear();
    knownNetworks.push_back({wifiTarget, wifiPassword});
    for (const auto& network : old)
        if (network.name != wifiTarget && knownNetworks.size() < 6) knownNetworks.push_back(network);
    for (const auto& network : knownNetworks) {
        auto item = doc["known"].add<JsonObject>();
        item["ssid"] = network.name;
        item["password"] = network.password;
    }
    String stored;
    serializeJson(doc, stored);
    if (!prefs.begin("agentdeck", false)) {
        wifiStatus = tr("Connected. Not saved.", "Połączono. Nie zapisano.");
        wifiPhase = "unsaved";
        knownNetworks.swap(old);
        return;
    }
    const bool saved = prefs.putString("wifi2", stored) == stored.length();
    prefs.end();
    if (!saved) {
        wifiStatus = tr("Connected. Not saved.", "Połączono. Nie zapisano.");
        wifiPhase = "unsaved";
        knownNetworks.swap(old);
        return;
    }
    savedSsid = wifiTarget;
    savedPassword = wifiPassword;
}

void forgetWifi() {
    Preferences prefs;
    if (!prefs.begin("agentdeck", false)) return;
    const bool cleared = !prefs.isKey("wifi2") || prefs.remove("wifi2");
    prefs.end();
    if (!cleared) return;
    savedSsid = savedPassword = wifiTarget = wifiPassword = "";
    knownNetworks.clear();
    queuedSsid = queuedPassword = wifiAction = "";
    queuedWifiAt = 0;
    wifiStatus = tr("Select network", "Wybierz sieć");
    wifiPhase = "idle";
    wifiStarting = false;
    wifiWasOnline = false;
    WiFi.setAutoReconnect(false);
    WiFi.disconnect();
    dirty = true;
}

void startWifiScan() {
    if (scanning) return;
    scanning = true;
    WiFi.scanDelete();
    WiFi.scanNetworks(true);
}

void scanWifi() {
    if (view.page != Page::WifiScan) open(Page::WifiScan);
    view.cursor = view.first = 0;
    view.rows.clear();
    autoScanning = false;
    startWifiScan();
}

void collectNetworks() {
    if (!scanning) return;
    const int count = WiFi.scanComplete();
    if (count == WIFI_SCAN_RUNNING) return;
    scanning = false;
    scannedNetworks.clear();
    for (int i = 0; i < count && scannedNetworks.size() < 24; ++i) {
        const String name = WiFi.SSID(i);
        if (!name.length()) continue;
        const int32_t rssi = WiFi.RSSI(i);
        auto found = std::find_if(scannedNetworks.begin(), scannedNetworks.end(),
                                  [&](const WifiNetwork& item) { return item.name == name; });
        if (found == scannedNetworks.end()) {
            scannedNetworks.push_back({name, rssi, WiFi.encryptionType(i) != WIFI_AUTH_OPEN});
        } else if (rssi > found->rssi) {
            found->rssi = rssi;
            found->secured = WiFi.encryptionType(i) != WIFI_AUTH_OPEN;
        }
    }
    std::sort(scannedNetworks.begin(), scannedNetworks.end(),
              [](const WifiNetwork& a, const WifiNetwork& b) { return a.rssi > b.rssi; });
    ++scanRevision;
    if (view.page == Page::WifiScan) {
        for (const auto& item : scannedNetworks)
            view.rows.push_back({item.name, item.name, item.secured ? tr("Password", "Hasło") : tr("Open", "Otwarta"), "", item.secured});
        view.rows.push_back({"@scan", tr("Scan again", "Skanuj ponownie"), "", "", false});
    }
    WiFi.scanDelete();
    dirty = true;
    if (autoScanning) {
        autoScanning = false;
        for (const auto& detected : scannedNetworks) {
            for (const auto& known : knownNetworks) {
                if (detected.name == known.name && detected.secured == (known.password.length() > 0)) {
                    wifiSecure = detected.secured;
                    beginWifi(known.name, known.password);
                    return;
                }
            }
        }
    }
}

void tickWifi() {
    collectNetworks();
    const uint32_t now = millis();
    if (queuedWifiAt && int32_t(now - queuedWifiAt) >= 0) {
        queuedWifiAt = 0;
        wifiFromPc = true;
        wifiRestoring = false;
        wifiSecure = queuedPassword.length();
        beginWifi(queuedSsid, queuedPassword);
        queuedSsid = queuedPassword = "";
    }
    const bool online = WiFi.status() == WL_CONNECTED;
    if (online) {
        if (wifiStarting && WiFi.SSID() == wifiTarget) {
            wifiStarting = false;
            wifiPhase = wifiRestoring ? "failed" : "connected";
            wifiStatus = wifiRestoring ? wifiFailure : tr("Connected", "Połączono");
            if (!wifiRestoring) saveWifi();
            wifiPassword = "";
            wifiFromPc = wifiRestoring = false;
            WiFi.setAutoReconnect(true);
            dirty = true;
        }
        if (!wifiWasOnline) dirty = true;
        wifiWasOnline = true;
        wifiLostAt = 0;
        return;
    }
    if (wifiWasOnline && !wifiLostAt) {
        wifiLostAt = now;
        wifiStatus = tr("Reconnecting...", "Ponowne łączenie…");
        dirty = true;
    }
    if (wifiStarting && now - wifiAttemptAt >= 11000) {
        wifiStarting = false;
        const int reason = wifiReason.load();
        wifiStatus = reason == WIFI_REASON_NO_AP_FOUND ? tr("Network unavailable", "Sieć niedostępna") :
                     wifiSecure ? tr("Check password or network", "Sprawdź hasło lub sieć") :
                     tr("Connection failed", "Nie udało się połączyć");
        wifiPhase = "failed";
        if (wifiFromPc && !wifiRestoring && savedSsid.length() &&
            (savedSsid != wifiTarget || savedPassword != wifiPassword)) {
            wifiFailure = wifiStatus;
            wifiRestoring = true;
            wifiSecure = savedPassword.length();
            beginWifi(savedSsid, savedPassword);
            return;
        }
        wifiFromPc = wifiRestoring = false;
        wifiPassword = "";
        WiFi.disconnect();
        dirty = true;
    }
    if (wifiLostAt && now - wifiLostAt >= 12000) {
        wifiWasOnline = false;
        wifiLostAt = 0;
        wifiSetupOffered = false;
        wifiStatus = tr("Network unavailable", "Sieć niedostępna");
        WiFi.setAutoReconnect(false);
        dirty = true;
    }
    if (!wifiStarting && !wifiLostAt && !scanning && !knownNetworks.empty() &&
        view.page != Page::WifiScan && view.page != Page::Keys && int32_t(now - nextAutoScan) >= 0) {
        nextAutoScan = now + 18000;
        autoScanning = true;
        startWifiScan();
        return;
    }
    if (!wifiStarting && !usbOnline() && !wifiSetupOffered && view.page == Page::Home && now > 3500) {
        wifiSetupOffered = true;
        open(Page::Wifi);
    }
}

void showHosts() {
    open(Page::Hosts);
    view.rows.push_back({"@", tr("+ Pair computer", "+ Sparuj komputer"), "", "", false});
    view.rows.push_back({"@usb", tr("USB cable", "Przewód USB"), "", "", false});
    const String selected = decknet::selected();
    for (const auto& p : decknet::hosts()) {
        view.rows.push_back({p.id, p.label, p.id == selected ? tr("Active", "Aktywny") : "", "", false});
    }
    if (selected.length()) view.rows.push_back({"@forget", tr("Remove active PC", "Usuń aktywny komputer"), "", "", false});
}

bool listPage() {
    return view.page == Page::Projects || view.page == Page::Models || view.page == Page::Limits ||
        view.page == Page::Effort || view.page == Page::Prompts || view.page == Page::Sessions ||
        view.page == Page::Accounts;
}

bool localList() {
    return view.page == Page::WifiScan || view.page == Page::Hosts ||
        view.page == Page::WakeHosts || view.page == Page::ShutdownHosts;
}

void showPowerHosts(Page page) {
    open(page);
    if (page == Page::ShutdownHosts && usbOnline())
        view.rows.push_back({"@usb", tr("PC via USB", "Komputer przez USB"), "", "", false});
    for (const auto& pc : decknet::hosts())
        view.rows.push_back({pc.id, pc.label, "", "", false});
}

void returnToWifi() {
    while (view.page != Page::Wifi && !history.empty()) {
        view = history.back();
        history.pop_back();
    }
    if (view.page != Page::Wifi) open(Page::Wifi);
    statusNow();
}

void applyStatus(JsonObjectConst doc) {
    active = doc["active"] | active;
    powered = doc["armed"] | powered;
    powerAllowed = doc["power_allowed"] | powerAllowed;
    powerSent = doc["power_sent"] | powerSent;
    demo = doc["demo"] | demo;
    if (doc["armed"].is<bool>()) {
        cooldown = doc["cooldown"].isNull() ? -1 : doc["cooldown"].as<int>();
        cooldownAt = millis();
    }
    if (cooldown >= 0 && (view.page == Page::Home || view.page == Page::Job ||
                         view.page == Page::Sessions || view.page == Page::Settings ||
                         view.page == Page::Shutdown)) open(Page::Cooldown);
}

void applyJob(JsonObjectConst job) {
    if (job.isNull()) return;
    if (job["id"].as<String>() != jobId) {
        outputOffset = outputPage = 0;
        outputLoaded = -1;
        outputMore = outputTail = false;
        jobOutput = "";
        seenQuestionId = "";
    }
    jobId = job["id"].as<String>();
    jobState = job["state"].as<String>();
    jobLabel = job["label"].as<String>();
    jobDetail = job["detail"].as<String>();
    jobPreview = job["preview"].as<String>();
    elapsed = job["elapsed"] | 0;
    elapsedAt = millis();
    questionId = questionText = "";
    questionOptions.clear();
    const auto q = job["question"].as<JsonObjectConst>();
    if (!q.isNull() && jobState == "waiting_input") {
        questionId = q["id"] | "";
        questionText = q["question"] | "";
        for (JsonObjectConst opt : q["options"].as<JsonArrayConst>()) {
            const String lbl = opt["label"] | "";
            if (lbl.length() && questionOptions.size() < 4) questionOptions.push_back(lbl);
        }
        if (questionId.length() && questionId != seenQuestionId && !questionOptions.empty() && view.page == Page::Job) {
            seenQuestionId = questionId;
            open(Page::Question);
        }
    }
}

String resetCaption(long long seconds, long long now) {
    if (seconds <= 0 || now <= 0) return "";
    const long long minutes = (max(0LL, seconds - now) + 59) / 60;
    const String prefix = tr("Reset in ", "Reset za ");
    if (minutes >= 1440) return prefix + String(static_cast<int>(minutes / 1440)) + " d";
    if (minutes >= 60) return prefix + String(static_cast<int>(minutes / 60)) + " h";
    return prefix + String(static_cast<int>(minutes)) + " min";
}

String limitWindow(String window) {
    if (window == "weekly" || window == "7d") return tr("Weekly", "Tygodniowy");
    if (window == "daily" || window == "1d") return tr("Daily", "Dzienny");
    if (window == "monthly") return tr("Monthly", "Miesięczny");
    if (window == "primary") return tr("Primary", "Podstawowy");
    if (window == "secondary") return tr("Secondary", "Dodatkowy");
    if (window.length() > 1 && isDigit(window[0])) {
        if (window.endsWith("h")) return window.substring(0, window.length() - 1) + " h";
        if (window.endsWith("m")) return window.substring(0, window.length() - 1) + " min";
        if (window.endsWith("d")) return window.substring(0, window.length() - 1) + " d";
    }
    return window;
}

void applyListing(JsonObjectConst doc, const String& op) {
    const int previousCursor = view.cursor;
    const String selection = previousCursor >= 0 && previousCursor < int(view.rows.size())
        ? view.rows[previousCursor].id : "";
    view.rows.clear();
    const int offset = doc["offset"] | 0;
    if (offset > 0) view.rows.push_back({"<", tr("Previous page", "Poprzednia strona"), "", "", false});
    if (op == "projects" && offset == 0) {
        view.rows.push_back({"@new", tr("+ New Project", "+ Nowy projekt"), tr("Random name", "Losowa nazwa"), "", false});
    }
    const long long now = doc["now"].as<long long>();
    auto items = doc["items"].as<JsonArrayConst>();
    for (JsonObjectConst item : items) {
        Row row{item["id"] | "", item["label"] | "", item["detail"] | "",
                item["state"] | "", item["git"] | false, 0, item["description"] | ""};
        if (op == "limits") {
            row.label = item["group"] | row.label;
            row.detail = limitWindow(item["window"] | "");
            row.state = resetCaption(item["reset"].as<long long>(), now);
            row.percent = item["remaining"] | 0;
        }
        view.rows.push_back(row);
    }
    view.total = doc["total"] | 0;
    view.offset = offset;
    view.pageSize = doc["page_size"] | 8;
    if (offset + int(items.size()) < view.total) {
        view.rows.push_back({">", tr("Next page", "Następna strona"), "", "", false});
    }
    if (view.page == Page::Models) {
        view.rows.push_back({"@", tr("Show limits", "Pokaż limity"), "", "", false});
        view.rows.push_back({"@acc", tr("Switch account", "Zmień konto"), "", "", false});
    }
    listLoading = doc["loading"] | false;
    view.cursor = constrain(previousCursor, 0, max(0, int(view.rows.size()) - 1));
    if (selection.length()) {
        for (int i = 0; i < int(view.rows.size()); ++i) {
            if (selection == view.rows[i].id) {
                view.cursor = i;
                break;
            }
        }
    }
}

void accept(const String& body, const char* transport) {
    JsonDocument doc;
    if (deserializeJson(doc, body)) return;
    if (!pendingId.length() || doc["id"].as<String>() != pendingId) return;
    if (pendingRoute.length() && pendingRoute != transport &&
        !(pendingRoute == "LAN" && !strcmp(transport, "Internet"))) return;
    const String op = pendingOp;
    pendingId = "";
    decknet::cancelRequest();
    if (op == idempotentOp) {
        idempotentBody = "";
        idempotentOp = "";
        idempotentRoute = "";
    }
    lastAnswer = millis();
    if (!strcmp(transport, "USB")) usbAt = lastAnswer;
    source = transport;
    if (wakeRequested) {
        wakeRequested = false;
        dirty = true;
    }
    if (!doc["ok"]) {
        if (op == "power") powerGoHome = false;
        errorText = doc["error"] | tr("Bridge error", "Błąd mostka");
        statusMint = false;
        dirty = true;
        return;
    }
    errorText = doc["error"] | "";
    statusMint = false;
    applyStatus(doc.as<JsonObjectConst>());
    if (op == "start" || op == "retry") {
        applyJob(doc["job"].as<JsonObjectConst>());
        home();
        open(Page::Sessions);
        open(Page::Job);
    } else if (op == "create_project") {
        const auto created = doc["project"].as<JsonObjectConst>();
        if (!created.isNull()) {
            project = created["id"] | "";
            projectLabel = created["label"] | "";
            jobGit = created["git"] | false;
            open(Page::Prompts);
            errorText = projectLabel;
            statusMint = true;
        }
    } else if (op == "switch_account") {
        back();
        errorText = tr("Account switched", "Zmieniono konto");
        statusMint = true;
    } else if (op == "answer") {
        applyJob(doc["job"].as<JsonObjectConst>());
        if (view.page == Page::Question) back();
        errorText = tr("Answer sent", "Wysłano odpowiedź");
        statusMint = true;
    } else if (op == "stop") {
        applyJob(doc["job"].as<JsonObjectConst>());
        if (view.page == Page::Stop) back();
    } else if (op == "power" && powerGoHome) {
        powerGoHome = false;
        home();
    } else if (op == "job") {
        applyJob(doc["job"].as<JsonObjectConst>());
        const auto output = doc["output"].as<JsonObjectConst>();
        if (!output.isNull() && output["page"].as<int>() == outputPage) {
            jobOutput = output["text"] | "";
            outputLoaded = outputPage;
            outputMore = output["more"] | false;
            outputOffset = outputTail ? max(0, int(jobOutput.length()) - 22) : 0;
            outputTail = false;
        }
    } else if (!doc["items"].isNull() && op == operation(view.page)) {
        applyListing(doc.as<JsonObjectConst>(), op);
    }
    dirty = true;
}

const char* title();

void deviceState(JsonObject result) {
    const bool online = WiFi.status() == WL_CONNECTED;
    result["device"] = decknet::deviceId();
    result["firmware"] = "0.6.0";
    result["lang"] = langPl ? "pl" : "en";
    result["battery"] = batteryPct >= 0 ? batteryPct : constrain(int(M5.Power.getBatteryLevel()), 0, 100);
    result["charging"] = batteryCharging;
    result["screen"] = title();
    result["transport"] = source;
    result["wifi_connected"] = online;
    result["wifi_phase"] = wifiPhase;
    result["wifi_message"] = wifiStatus;
    result["wifi_action"] = wifiAction;
    result["ssid"] = online ? WiFi.SSID() : "";
    result["address"] = online ? WiFi.localIP().toString() : "";
    result["scanning"] = scanning;
    result["scan_revision"] = scanRevision;
    if (view.page == Page::Models || view.page == Page::Effort || view.page == Page::Projects ||
        view.page == Page::Prompts || view.page == Page::Confirm || view.page == Page::Accounts) {
        result["provider"] = provider;
    }
    if (((view.page == Page::Models || view.page == Page::Effort) && pinnedProject.length()) ||
        view.page == Page::Prompts || view.page == Page::Confirm) {
        result["project"] = project;
        result["project_name"] = projectLabel;
    }
    if (view.page == Page::Job) {
        result["session"] = jobLabel;
        result["session_state"] = jobState;
    }
}

String control(JsonObjectConst request, bool wireless) {
    JsonDocument result;
    result["id"] = request["id"];
    result["ok"] = true;
    const String action = request["action"] | "";
    String error;
    if (action == "relay_config") {
        const String host = request["host"] | "";
        const String endpoint = request["endpoint"] | "";
        if ((wireless && host != decknet::selected()) || !decknet::configureRelay(host, endpoint))
            error = tr("Pair Stick with this PC first.", "Najpierw sparuj Stick z tym PC.");
    } else if (action == "set_language") {
        const String next = request["lang"] | "";
        if (next != "en" && next != "pl") {
            error = tr("Invalid language.", "Nieprawidłowy język.");
        } else {
            saveLanguage(next == "pl");
        }
    } else if (action == "status") {
        deviceState(result["snapshot"].to<JsonObject>());
    } else if (action == "wifi_scan") {
        if (wifiStarting || queuedWifiAt) error = tr("Wait for Wi-Fi change.", "Zaczekaj na zmianę Wi-Fi.");
        else {
            startWifiScan();
            result["target_revision"] = scanRevision + 1;
        }
    } else if (action == "wifi_list") {
        result["scan_revision"] = scanRevision;
        auto list = result["networks"].to<JsonArray>();
        for (const auto& network : scannedNetworks) {
            auto entry = list.add<JsonObject>();
            entry["ssid"] = network.name;
            entry["rssi"] = network.rssi;
            entry["secured"] = network.secured;
        }
    } else if (action == "wifi_connect") {
        const String ssid = request["ssid"] | "";
        const String password = request["password"] | "";
        const auto found = std::find_if(scannedNetworks.begin(), scannedNetworks.end(),
                                      [&](const WifiNetwork& item) { return item.name == ssid; });
        if (queuedWifiAt || wifiStarting || scanning) error = tr("Wait for scan or connect.", "Zaczekaj na skanowanie lub łączenie.");
        else if (found == scannedNetworks.end()) error = tr("Scan and select a network first.", "Najpierw przeskanuj i wybierz sieć.");
        else if (found->secured ? password.length() < 8 || password.length() > 63 : password.length() != 0)
            error = tr("Check Wi-Fi password length.", "Sprawdź długość hasła do sieci.");
        else {
            wifiAction = request["id"] | "";
            queuedSsid = ssid;
            queuedPassword = password;
            wifiPhase = "connecting";
            wifiStatus = tr("Connecting...", "Łączenie…");
            queuedWifiAt = millis() + (wireless ? 1050 : 350);
            result["wifi_action"] = wifiAction;
            dirty = true;
        }
    } else if (action == "open_project") {
        const String target = request["project"] | "";
        const String name = request["label"] | "";
        const String service = request["provider"] | "";
        if (pendingId.length() && isMutation(pendingOp)) error = tr("Stick is busy with a session.", "Stick czeka na akcję sesji.");
        else if (target.length() != 16 || !name.length() || (service != "codex" && service != "agy"))
            error = tr("Select project and provider.", "Wybierz projekt i dostawcę.");
        else {
            preferLan = wireless;
            provider = service;
            project = target;
            projectLabel = name;
            jobGit = request["git"] | false;
            if (view.page == Page::Pair) decknet::closePairing();
            home();
            pinnedProject = target;
            open(Page::Models);
        }
    } else if (action == "show_pairing") {
        if (wireless || (pendingId.length() && isMutation(pendingOp))) {
            error = tr("Open pairing on Stick.", "Otwórz parowanie na Sticku.");
        } else {
            if (view.page != Page::Pair) open(Page::Pair);
            pairDisplay = decknet::openPairing();
            dirty = true;
        }
    } else {
        error = tr("Unknown Stick action.", "Stick nie zna tej akcji.");
    }
    if (error.length()) {
        result["ok"] = false;
        result["error"] = error;
    }
    String answer;
    serializeJson(result, answer);
    return answer;
}

void syncDeviceState() {
    const String command = decknet::takeControl();
    if (command.length()) {
        JsonDocument request;
        if (!deserializeJson(request, command)) {
            decknet::completeControl(control(request.as<JsonObjectConst>(), true));
        }
    }
    const uint32_t now = millis();
    if (now - publishedAt >= 360) {
        publishedAt = now;
        JsonDocument state;
        deviceState(state.to<JsonObject>());
        String body;
        serializeJson(state, body);
        decknet::publish(body);
    }
}

void serialInput(const String& line) {
    if (!line.startsWith(Frame)) return;
    const String body = line.substring(4);
    JsonDocument doc;
    if (deserializeJson(doc, body)) return;
    const String op = doc["op"].as<String>();
    const String ident = doc["id"].as<String>();
    if (op == "probe") {
        JsonDocument info;
        const esp_partition_t* running = esp_ota_get_running_partition();
        info["ok"] = true;
        info["id"] = ident;
        info["firmware"] = "AgentDeck 0.6.0";
        info["lang"] = langPl ? "pl" : "en";
        info["battery"] = batteryPct >= 0 ? batteryPct : constrain(int(M5.Power.getBatteryLevel()), 0, 100);
        info["charging"] = batteryCharging;
        info["rotation"] = M5.Display.getRotation();
        info["width"] = M5.Display.width();
        info["height"] = M5.Display.height();
        info["partition"] = running ? running->label : "";
        info["page"] = int(view.page);
        info["transport"] = source;
        info["free_heap"] = ESP.getFreeHeap();
        info["wifi_connected"] = WiFi.status() == WL_CONNECTED;
        info["wifi_saved"] = savedSsid.length() > 0;
        info["hosts"] = decknet::hosts().size();
        if (info["wifi_connected"] == true) info["wifi_ip"] = WiFi.localIP().toString();
        if (doc["row"].is<int>()) {
            const int y = doc["row"].as<int>();
            if (y >= 0 && y < Height) {
                char line[Width * 4 + 1];
                for (int x = 0; x < Width; ++x) {
                    snprintf(line + x * 4, 5, "%04x", canvas.readPixel(x, y));
                }
                info["row"] = y;
                info["rgb565"] = line;
            }
        }
        String answer;
        serializeJson(info, answer);
        sendFrame(answer);
    } else if (op == "device_control" && ident.length() == 24) {
        sendFrame(control(doc.as<JsonObjectConst>(), false));
    } else if (op == "pair_pc") {
        JsonDocument info;
        info["ok"] = ident.length() == 24 && decknet::pairUsb(doc.as<JsonObjectConst>());
        info["id"] = ident;
        info["device"] = decknet::deviceId();
        if (!info["ok"].as<bool>()) info["error"] = tr("Invalid or expired pairing code.", "Kod ze Sticka jest nieprawidłowy lub wygasł.");
        if (WiFi.status() == WL_CONNECTED) info["address"] = WiFi.localIP().toString();
        String answer;
        serializeJson(info, answer);
        sendFrame(answer);
        if (info["ok"] == true) {
            errorText = tr("PC paired", "Sparowano komputer");
            statusMint = true;
            usbAt = millis();
            preferLan = false;
            dirty = true;
        }
    } else {
        accept(body, "USB");
    }
}

void readSerial() {
    for (int i = 0; i < 4096 && Serial.available(); ++i) {
        char c = static_cast<char>(Serial.read());
        if (c == '\n') {
            if (!serialOverflow) {
                serialBuffer.trim();
                serialInput(serialBuffer);
            }
            serialBuffer = "";
            serialOverflow = false;
        } else if (!serialOverflow) {
            if (serialBuffer.length() < FrameLimit) serialBuffer += c;
            else serialOverflow = true;
        }
    }
}

String stateName(const String& state) {
    if (state == "finished") return tr("Completed", "Ukończono");
    if (state == "queued") return tr("Queued", "W kolejce");
    if (state == "running") return tr("Running", "Pracuje");
    if (state == "waiting_input") return tr("Question", "Pytanie");
    if (state == "stopping") return tr("Stopping", "Zatrzymywanie");
    if (state == "cancelled") return tr("Cancelled", "Przerwano");
    if (state == "limit") return tr("Limit", "Limit");
    if (state == "orphaned") return tr("Bridge restarted", "Mostek zrestartowany");
    if (state == "error") return tr("Error", "Błąd");
    if (state == "active") return tr("Active", "Aktywne");
    return state;
}

uint16_t stateColor(const String& state) {
    if (state == "finished" || state == "active") return Mint;
    if (state == "running") return Blue;
    if (state == "error" || state == "orphaned") return Pink;
    return Yellow;
}

const char* title() {
    switch (view.page) {
        case Page::Home: return "AgentDeck";
        case Page::Projects: return tr("Projects", "Projekty");
        case Page::Models: return tr("Models", "Modele");
        case Page::Effort: return tr("Reasoning", "Poziom myślenia");
        case Page::Limits: return tr("Limits", "Limity konta");
        case Page::Prompts: return tr("Prompts", "Prompty");
        case Page::Confirm: return tr("Choose mode", "Wybierz tryb");
        case Page::Sessions: return tr("Sessions", "Sesje");
        case Page::Job: return tr("Task", "Zadanie");
        case Page::Question: return tr("Question", "Pytanie");
        case Page::Stop: return tr("Stop?", "Zatrzymać?");
        case Page::Settings: return tr("Settings", "Ustawienia");
        case Page::Pair: return tr("Pair PC", "Parowanie PC");
        case Page::Cooldown: return tr("Power", "Zasilanie");
        case Page::Wifi: return "Wi-Fi";
        case Page::WifiScan: return tr("Select network", "Wybierz sieć");
        case Page::Keys: return tr("Wi-Fi Password", "Hasło Wi-Fi");
        case Page::Hosts: return tr("Computers", "Komputery");
        case Page::WakeHosts: return tr("Wake PC", "Obudź komputer");
        case Page::ShutdownHosts: return tr("Shut down PC", "Wyłącz komputer");
        case Page::Shutdown: return tr("Confirm shutdown", "Potwierdź wyłączenie");
        case Page::Accounts: return tr("Accounts", "Konta");
        case Page::Language: return tr("Language", "Język");
    }
    return "";
}

String trimWidth(String name, int width) {
    if (canvas.textWidth(name.c_str()) <= width) return name;
    while (name.length()) {
        unsigned int end = name.length() - 1;
        while (end && (static_cast<uint8_t>(name[end]) & 0xc0) == 0x80) --end;
        name.remove(end);
        if (canvas.textWidth((name + "...").c_str()) <= width) return name + "...";
    }
    return "";
}

void text(const String& text, int x, int y, uint16_t color, int width = 224) {
    canvas.setTextColor(color);
    canvas.drawString(trimWidth(text, width).c_str(), x, y);
}

void drawWrapped2(const String& str, int x, int y, uint16_t color, int width = 220) {
    if (canvas.textWidth(str.c_str()) <= width) {
        text(str, x, y, color, width);
        return;
    }
    int cut = str.length();
    for (int i = str.length() - 1; i > 0; --i) {
        if ((static_cast<uint8_t>(str[i]) & 0xc0) == 0x80) continue;
        if (str[i] == ' ' && canvas.textWidth(str.substring(0, i).c_str()) <= width) {
            cut = i;
            break;
        }
    }
    if (cut == int(str.length())) {
        for (int i = str.length() - 1; i > 0; --i) {
            if ((static_cast<uint8_t>(str[i]) & 0xc0) == 0x80) continue;
            if (canvas.textWidth(str.substring(0, i).c_str()) <= width) {
                cut = i;
                break;
            }
        }
    }
    String first = str.substring(0, cut);
    String second = str.substring(cut);
    second.trim();
    text(first, x, y, color, width);
    if (second.length()) text(second, x, y + 13, color, width);
}

void row(const String& label, int y, bool selected, const String& badge = "", uint16_t color = Muted) {
    canvas.fillRoundRect(8, y, 224, 17, 4, selected ? Focus : Panel);
    if (selected) {
        const uint32_t age = std::min<uint32_t>(150, millis() - motionAt);
        const int length = 3 + age * 10 / 150;
        canvas.fillRoundRect(9, y + (17 - length) / 2, 2, length, 1, Mint);
    }
    canvas.setFont(&DeckText);
    const int badgeWidth = badge.length() ? min(112, canvas.textWidth(badge.c_str())) : 0;
    text(label, 15, y + 2, Text, 209 - (badgeWidth ? badgeWidth + 6 : 0));
    if (badgeWidth) {
        canvas.fillRoundRect(221 - badgeWidth, y + 2, badgeWidth + 9, 13, 4, Background);
        text(badge, 225 - badgeWidth, y + 2, color, badgeWidth);
    }
}

void limitRow(const Row& item, int y, bool selected) {
    if (item.id == "<" || item.id == ">") {
        row(item.label, y + 8, selected);
        return;
    }
    canvas.fillRoundRect(8, y, 224, 35, 5, selected ? Focus : Panel);
    if (selected) canvas.fillRoundRect(9, y + 8, 2, 19, 1, Mint);
    const String percent = String(item.percent) + "%";
    const int percentWidth = canvas.textWidth(percent.c_str());
    text(item.label, 16, y + 3, Text, 207 - percentWidth);
    text(percent, 224 - percentWidth, y + 3, item.percent > 10 ? Mint : Yellow, percentWidth + 1);
    const String caption = item.detail.length() && item.state.length() ?
                           item.detail + "  /  " + item.state : item.detail + item.state;
    text(caption, 16, y + 18, Muted, 204);
}

void draw() {
    canvas.fillSprite(Background);
    canvas.fillRect(0, 0, Width, 23, Panel);
    canvas.setTextDatum(TL_DATUM);
    canvas.fillRoundRect(8, 5, 3, 13, 1, Mint);
    canvas.setFont(&DeckText);
    const bool online = lastAnswer && millis() - lastAnswer < 6500;
    const String connection = online ? (demo ? "DEMO" : source) : wakeRequested ? tr("Waking", "Budzenie") : "Offline";
    const int badge = canvas.textWidth(connection.c_str()) + 17;
    int rightBound = 232 - badge;
    canvas.fillRoundRect(rightBound, 4, badge, 15, 7, Background);
    canvas.fillCircle(rightBound + 7, 11, 2, online ? Mint : Yellow);
    text(connection, rightBound + 13, 5, online ? Mint : Yellow);
    if (batteryPct >= 0) {
        const String batStr = (batteryCharging ? String("+") : String("")) + String(batteryPct) + "%";
        const int batW = canvas.textWidth(batStr.c_str()) + 10;
        const int batX = rightBound - batW - 4;
        const uint16_t batColor = batteryCharging || batteryPct >= 45 ? Mint : batteryPct >= 20 ? Yellow : Pink;
        canvas.fillRoundRect(batX, 4, batW, 15, 7, Background);
        text(batStr, batX + 5, 5, batColor, batW - 6);
        rightBound = batX - 4;
    }
    canvas.setFont(&DeckHeading);
    text(title(), 15, 4, Text, max(48, rightBound - 16));
    canvas.setFont(&DeckText);
    canvas.drawFastHLine(8, 23, 224, Outline);
    canvas.fillRect(0, 110, Width, 25, Panel);
    canvas.drawFastHLine(8, 109, 224, Outline);
    if ((scanning || wifiStarting || queuedWifiAt || wakeRequested) && !dim) {
        const int offset = (millis() / 12) % 224;
        canvas.drawFastHLine(8 + offset, 109, min(29, 224 - offset), Blue);
    }
    text(view.page == Page::Job ? tr("B/A scroll out", "B/A przewiń wynik") : tr("B/A scroll", "B/A przewiń"), 8, 116, Muted);
    text(tr("A OK    B back", "A OK    B wróć"), 148, 116, Text);
    if (view.page == Page::Home) {
        row("Antigravity", 27, view.cursor == 0, "", Blue);
        row("Codex", 46, view.cursor == 1, "", Mint);
        row(active ? String(tr("Sessions", "Sesje")) + " (" + String(active) + ")" : tr("Sessions", "Sesje"), 65, view.cursor == 2);
        row(tr("Settings", "Ustawienia"), 84, view.cursor == 3);
    } else if (listPage() || localList()) {
        const bool limits = view.page == Page::Limits;
        const int visible = limits ? 2 : 4;
        const int count = int(view.rows.size());
        const int focused = constrain(view.cursor, 0, max(0, count - 1));
        view.first = constrain(view.first, max(0, focused - visible + 1), focused);
        for (int i = 0; i < visible && i + view.first < count; ++i) {
            const Row& item = view.rows[i + view.first];
            const bool hasState = (view.page == Page::Sessions || view.page == Page::Accounts) && item.state.length();
            const bool selected = i + view.first == view.cursor;
            const uint16_t badgeColor = item.id == "@new" ? Mint :
                item.id == "@" ? Yellow :
                item.id == "@acc" ? Blue :
                hasState ? stateColor(item.state) : Muted;
            if (limits) limitRow(item, 28 + i * 40, selected);
            else row(item.label, 27 + i * 19, selected, hasState ? stateName(item.state) : item.detail, badgeColor);
        }
        if (count == 0) {
            const String empty = view.page == Page::WifiScan ? tr("Scanning Wi-Fi...", "Skanowanie Wi-Fi…") :
                listLoading && !localList() ? tr("Loading...", "Pobieranie…") :
                view.page == Page::Prompts ? tr("No prompts in project", "Brak promptów w projekcie") :
                view.page == Page::Accounts ? tr("No saved accounts", "Brak zapisanych kont") :
                view.page == Page::Limits ? tr("No limits from provider", "Dostawca nie podał limitów") :
                tr("No items", "Brak pozycji");
            text(empty, 12, 42, Muted);
            if ((view.page == Page::Prompts || view.page == Page::Accounts) && !listLoading)
                text(tr("Add items in Windows app", "Dodaj w aplikacji Windows"), 12, 62, Text);
        }
    } else if (view.page == Page::Confirm) {
        text(promptLabel, 10, 26, Text);
        text(promptDesc.length() ? promptDesc : (effortLabel + " / " + modelLabel), 10, 40, Muted);
        row(tr("Read-only", "Tylko odczyt"), 55, view.cursor == 0, "Read", Blue);
        row(tr("Edit", "Edycja"), 73, view.cursor == 1, "Write", Mint);
        row(tr("Plan & Execute", "Planuj i wykonaj"), 91, view.cursor == 2, "Auto", Yellow);
    } else if (view.page == Page::Question) {
        drawWrapped2(questionText, 10, 25, Text, 220);
        const int count = int(questionOptions.size());
        const int visible = 3;
        view.first = constrain(view.first, max(0, view.cursor - visible + 1), view.cursor);
        for (int i = 0; i < visible && i + view.first < count; ++i) {
            const int idx = i + view.first;
            row(questionOptions[idx], 53 + i * 19, idx == view.cursor);
        }
    } else if (view.page == Page::Job) {
        text(jobLabel, 10, 27, Text);
        const int secs = elapsed + (jobState == "running" ? int((millis() - elapsedAt) / 1000) : 0);
        char timeText[16];
        snprintf(timeText, sizeof(timeText), "%02d:%02d", secs / 60, secs % 60);
        canvas.setFont(&DeckHeading);
        text(stateName(jobState) + "  " + timeText, 10, 45, stateColor(jobState));
        if (jobState == "running") {
            const int p = (millis() / 250) % 4;
            for (int i = 0; i < 4; ++i) canvas.fillCircle(208 + 6 * i, 53, 1, i == p ? Blue : Focus);
        }
        canvas.setFont(&DeckText);
        const String output = jobState == "finished" && outputLoaded == outputPage ? jobOutput :
                              jobState == "error" || jobState == "limit" ? jobDetail :
                              jobPreview.length() ? jobPreview : jobDetail;
        int offset = min(outputOffset, int(output.length()));
        while (offset && offset < int(output.length()) && (output[offset] & 0xc0) == 0x80) --offset;
        text(output.substring(offset), 10, 68, Muted);
        if (jobState == "waiting_input") row(tr("Answer question", "Odpowiedz na pytanie"), 85, true, "A/B/C", Yellow);
        else if (jobState == "queued" || jobState == "running") row(tr("Stop", "Zatrzymaj"), 85, true);
        else if (jobState != "stopping") row(tr("Retry as new chat", "Ponów jako nowy czat"), 85, true);
    } else if (view.page == Page::Stop) {
        text(jobLabel, 10, 28, Muted);
        row(tr("Back", "Wróć"), 48, view.cursor == 0);
        row(tr("Stop session", "Zatrzymaj sesję"), 67, view.cursor == 1);
    } else if (view.page == Page::Settings) {
        const Row items[] = {
            {"wifi", "Wi-Fi", WiFi.status() == WL_CONNECTED ? WiFi.SSID() : tr("Disconnected", "Rozłączono"), "", false},
            {"hosts", tr("Computers", "Komputery"), "", "", false},
            {"lang", tr("Language", "Język"), langPl ? "Polski" : "English", "", false},
            {"wake", "Wake on LAN", "", "", false},
            {"off", tr("Shut down PC", "Wyłącz PC"), "", "", false},
        };
        const int visible = 4;
        view.first = constrain(view.first, max(0, view.cursor - visible + 1), view.cursor);
        for (int i = 0; i < visible && i + view.first < 5; ++i) {
            const Row& item = items[i + view.first];
            row(item.label, 27 + i * 19, i + view.first == view.cursor, item.detail);
        }
    } else if (view.page == Page::Language) {
        row("English", 27, view.cursor == 0, !langPl ? tr("Active", "Aktywny") : "", !langPl ? Mint : Muted);
        row("Polski", 46, view.cursor == 1, langPl ? tr("Active", "Aktywny") : "", langPl ? Mint : Muted);
    } else if (view.page == Page::Shutdown) {
        text(powerLabel, 10, 30, Text);
        text(tr("30 second countdown", "Odliczanie trwa 30 sekund"), 10, 49, Muted);
        row(tr("Back", "Wróć"), 65, view.cursor == 0);
        row(tr("Start shutdown", "Rozpocznij wyłączanie"), 84, view.cursor == 1, "", Yellow);
    } else if (view.page == Page::Pair) {
        if (pairDisplay.length()) {
            text(tr("Enter code in PC app", "Wpisz kod w aplikacji PC"), 10, 27, Muted);
            canvas.fillRoundRect(8, 43, 224, 23, 5, Panel);
            canvas.setFont(&DeckHeading);
            const String pretty = pairDisplay.substring(0, 4) + "-" + pairDisplay.substring(4, 8) +
                "-" + pairDisplay.substring(8, 12) + "-" + pairDisplay.substring(12);
            text(pretty, 16, 44, Mint);
            canvas.setFont(&DeckText);
        } else {
            text(tr("Code expired or used", "Kod wygasł lub został użyty"), 10, 34, Muted);
        }
        const String detail = WiFi.status() == WL_CONNECTED ? String(tr("USB or Wi-Fi / ", "USB lub Wi-Fi / ")) + WiFi.localIP().toString()
                                                          : tr("Code works via USB", "Kod działa przez USB");
        text(detail, 10, 68, Muted);
        const int progress = decknet::pairingRemaining() * 224 / 120000;
        if (progress) canvas.drawFastHLine(8, 83, progress, Mint);
        row(tr("New code", "Nowy kod"), 86, true);
    } else if (view.page == Page::Wifi) {
        row(tr("Scan networks", "Skanuj sieci"), 27, view.cursor == 0);
        row(tr("Change password", "Zmień hasło"), 46, view.cursor == 1);
        row(tr("Forget networks", "Zapomnij sieci"), 65, view.cursor == 2);
        const String network = WiFi.status() == WL_CONNECTED ? WiFi.SSID() : wifiTarget;
        const String line = wifiStatus.length() ? wifiStatus : tr("Select network", "Wybierz sieć");
        text(network.length() ? line + " / " + network : line, 10, 91, Muted);
    } else if (view.page == Page::Keys) {
        String masked;
        for (size_t i = 0; i < min(wifiPassword.length(), size_t(19)); ++i) masked += '*';
        text(wifiTarget, 8, 26, Muted, 116);
        text(masked.length() ? masked : tr("Enter password", "Wpisz hasło"), 130, 26, Text, 102);
        const char* actions[] = {tr("Join", "Połącz"), tr("Del", "Usuń"), tr("Set", "Zestaw")};
        for (int i = 0; i < 3; ++i) {
            const int x = 8 + i * 75;
            if (view.cursor == i) canvas.fillRoundRect(x, 43, 69, 15, 3, Focus);
            text(actions[i], x + 3, 44, view.cursor == i ? Mint : Text);
        }
        for (int i = 0; i < int(strlen(Keys[keyboard])); ++i) {
            const int x = 8 + (i % 11) * 20;
            const int y = 62 + (i / 11) * 15;
            if (view.cursor == i + 3) canvas.fillRoundRect(x, y, 18, 14, 2, Focus);
            const char c = Keys[keyboard][i];
            text(c == ' ' ? "_" : String(c), x + 3, y, view.cursor == i + 3 ? Mint : Text);
        }
    } else if (view.page == Page::Cooldown) {
        canvas.setFont(&DeckHeading);
        if (powerSent) {
            text(tr("Command sent", "Wysłano polecenie"), 10, 34, Mint);
            canvas.setFont(&DeckText);
            text(tr("Waiting for Windows", "Oczekiwanie na Windows"), 10, 61, Muted);
        } else if (cooldown >= 0) {
            const int seconds = max(0, cooldown - int((millis() - cooldownAt) / 1000));
            text(String(tr("Shutdown in 00:", "Wyłączenie za 00:")) + String(seconds < 10 ? "0" : "") + String(seconds), 10, 29, Yellow);
        } else {
            text(tr("Countdown finished", "Odliczanie zakończone"), 10, 29, Muted);
        }
        if (!powerSent) {
            row(tr("Keep PC on", "Zostaw PC włączony"), 56, view.cursor == 0);
            row(tr("New prompt", "Nowy prompt"), 77, view.cursor == 1);
        }
    }
    if (errorText.length()) {
        canvas.fillRect(7, 111, 227, 22, Background);
        canvas.setFont(&DeckText);
        text(errorText, 8, 117, statusMint ? Mint : Pink);
    }
    static uint32_t previous[7]{};
    static bool firstFrame = true;
    const auto* pixels = static_cast<const lgfx::swap565_t*>(canvas.getBuffer());
    constexpr int bands[] = {0, 24, 44, 64, 84, 106, 109, Height};
    M5.Display.startWrite();
    for (size_t i = 0; i < 7; ++i) {
        const int start = bands[i], height = bands[i + 1] - start;
        const auto* area = pixels + start * Width;
        const uint8_t* bytes = reinterpret_cast<const uint8_t*>(area);
        uint32_t hash = 2166136261u;
        for (int n = 0; n < height * Width * 2; ++n) hash = (hash ^ bytes[n]) * 16777619u;
        if (firstFrame || hash != previous[i]) M5.Display.pushImage(0, start, Width, height, area);
        previous[i] = hash;
    }
    M5.Display.endWrite();
    firstFrame = false;
}

int menuSize() {
    if (listPage() || localList()) return view.rows.size();
    if (view.page == Page::Question) return questionOptions.size();
    if (view.page == Page::Confirm || view.page == Page::Wifi) return 3;
    if (view.page == Page::Keys) return 3 + strlen(Keys[keyboard]);
    if (view.page == Page::Settings) return 5;
    if (view.page == Page::Home) return 4;
    if (view.page == Page::Stop || view.page == Page::Cooldown ||
        view.page == Page::Shutdown || view.page == Page::Language) return 2;
    return 1;
}

void startOrRetry(const char* op) {
    if (pendingId.length() && isMutation(pendingOp)) return;
    if (idempotentBody.length() && idempotentOp == op) {
        JsonDocument old;
        bool same = !deserializeJson(old, idempotentBody);
        if (same) {
            if (!strcmp(op, "start")) {
                same = old["project"] == project && old["model"] == model && old["prompt"] == prompt &&
                       old["effort"] == effort && old["mode"] == sessionMode;
            } else if (!strcmp(op, "retry")) {
                same = old["job"] == jobId;
            } else if (!strcmp(op, "create_project")) {
                same = old["provider"] == provider;
            } else if (!strcmp(op, "switch_account")) {
                same = old["provider"] == provider && old["account"] == accountId;
            } else if (!strcmp(op, "answer")) {
                same = old["job"] == jobId && old["choice"] == answerChoice;
            } else {
                same = false;
            }
        }
        if (same) {
            pendingId = old["id"].as<String>();
            pendingBody = idempotentBody;
            pendingOp = idempotentOp;
            pendingRoute = idempotentRoute;
            transmit();
            return;
        }
    }
    request(op);
}

void select() {
    if (view.page == Page::Home) {
        if (view.cursor == 0 || view.cursor == 1) {
            provider = view.cursor == 0 ? "agy" : "codex";
            if (!lastAnswer || millis() - lastAnswer > 1600) {
                wakeRequested = true;
                wakeAt = 0;
                pendingAt = millis();
            }
            open(Page::Models);
        } else open(view.cursor == 2 ? Page::Sessions : Page::Settings);
    } else if (listPage()) {
        if (view.cursor >= int(view.rows.size())) return;
        const Row item = view.rows[view.cursor];
        if (item.id == ">" || item.id == "<") {
            view.offset = max(0, view.offset + (item.id == ">" ? view.pageSize : -view.pageSize));
            view.cursor = view.first = 0;
            view.rows.clear();
            statusNow();
        } else if (view.page == Page::Projects) {
            if (item.id == "@new") {
                startOrRetry("create_project");
            } else {
                project = item.id;
                projectLabel = item.label;
                jobGit = item.git;
                open(Page::Prompts);
            }
        } else if (view.page == Page::Models) {
            if (item.id == "@") open(Page::Limits);
            else if (item.id == "@acc") open(Page::Accounts);
            else {
                model = item.id;
                modelLabel = item.label;
                open(Page::Effort);
            }
        } else if (view.page == Page::Accounts) {
            if (provider == "agy") {
                errorText = tr("Switch in Antigravity on PC", "Zmień w Antigravity na PC");
                statusMint = false;
            } else if (item.id.length()) {
                accountId = item.id;
                startOrRetry("switch_account");
            }
        } else if (view.page == Page::Effort) {
            effort = item.id;
            effortLabel = item.label;
            if (pinnedProject.length()) open(Page::Prompts);
            else open(Page::Projects);
        } else if (view.page == Page::Prompts) {
            prompt = item.id;
            promptLabel = item.label;
            promptDesc = item.desc;
            open(Page::Confirm);
        } else if (view.page == Page::Sessions) {
            if (item.id == "@") home();
            else {
                jobId = item.id;
                jobLabel = item.label;
                jobState = item.state;
                jobPreview = jobDetail = "";
                jobOutput = "";
                seenQuestionId = "";
                outputOffset = outputPage = 0;
                outputLoaded = -1;
                outputMore = outputTail = false;
                elapsed = 0;
                elapsedAt = millis();
                open(Page::Job);
            }
        }
    } else if (view.page == Page::Confirm) {
        sessionMode = view.cursor == 0 ? "read" : view.cursor == 1 ? "write" : "plan_execute";
        startOrRetry("start");
    } else if (view.page == Page::Question) {
        if (view.cursor >= 0 && view.cursor < int(questionOptions.size())) {
            answerChoice = view.cursor;
            startOrRetry("answer");
        }
    } else if (view.page == Page::Job) {
        if (jobState == "waiting_input" && !questionOptions.empty()) open(Page::Question);
        else if (jobState == "queued" || jobState == "running") open(Page::Stop);
        else if (jobState.length() && jobState != "stopping") startOrRetry("retry");
    } else if (view.page == Page::Stop) {
        if (view.cursor == 0) back();
        else startOrRetry("stop");
    } else if (view.page == Page::Settings) {
        if (view.cursor == 0) {
            open(Page::Wifi);
        } else if (view.cursor == 1) {
            showHosts();
        } else if (view.cursor == 2) {
            open(Page::Language);
            view.cursor = langPl ? 1 : 0;
        } else {
            showPowerHosts(view.cursor == 3 ? Page::WakeHosts : Page::ShutdownHosts);
        }
    } else if (view.page == Page::Language) {
        saveLanguage(view.cursor == 1);
        back();
        request("sync_settings");
        errorText = tr("Language updated", "Zapisano język");
        statusMint = true;
    } else if (view.page == Page::WakeHosts && view.cursor < int(view.rows.size())) {
        if (WiFi.status() == WL_CONNECTED && decknet::wake(view.rows[view.cursor].id)) {
            errorText = tr("Wake on LAN packet sent", "Wysłano pakiet Wake on LAN");
            statusMint = true;
        } else {
            errorText = tr("No Wi-Fi or MAC address", "Brak Wi-Fi lub adresu MAC");
            statusMint = false;
        }
    } else if (view.page == Page::ShutdownHosts && view.cursor < int(view.rows.size())) {
        powerHost = view.rows[view.cursor].id;
        powerLabel = view.rows[view.cursor].label;
        open(Page::Shutdown);
    } else if (view.page == Page::Shutdown) {
        if (view.cursor == 0) back();
        else if (!pendingId.length() || !isMutation(pendingOp)) {
            const bool wired = powerHost == "@usb";
            if (wired ? usbOnline() : decknet::selectHost(powerHost)) {
                preferLan = !wired;
                statusNow();
                request("power", true);
            } else {
                errorText = tr("PC unavailable", "Komputer jest niedostępny");
                statusMint = false;
            }
        }
    } else if (view.page == Page::Pair) {
        pairDisplay = decknet::openPairing();
    } else if (view.page == Page::Wifi) {
        if (view.cursor == 0) scanWifi();
        else if (view.cursor == 1) {
            if (WiFi.status() == WL_CONNECTED) wifiTarget = WiFi.SSID();
            if (!wifiTarget.length()) wifiTarget = savedSsid;
            if (wifiTarget.length()) {
                wifiPassword = "";
                keyboard = 0;
                wifiSecure = true;
                open(Page::Keys);
                view.cursor = 3;
            } else scanWifi();
        } else forgetWifi();
    } else if (view.page == Page::WifiScan && view.cursor < int(view.rows.size())) {
        const Row network = view.rows[view.cursor];
        if (network.id == "@scan") scanWifi();
        else if (!network.git) {
            wifiSecure = false;
            returnToWifi();
            beginWifi(network.id, "");
        } else {
            wifiTarget = network.id;
            wifiPassword = "";
            keyboard = 0;
            wifiSecure = true;
            open(Page::Keys);
            view.cursor = 3;
        }
    } else if (view.page == Page::Keys) {
        if (view.cursor == 0) {
            if (wifiPassword.length() >= 8) {
                const String entry = wifiPassword;
                const String ssid = wifiTarget;
                returnToWifi();
                beginWifi(ssid, entry);
            } else {
                errorText = tr("Min 8 characters", "Hasło ma co najmniej 8 znaków");
                statusMint = false;
            }
        } else if (view.cursor == 1) {
            if (wifiPassword.length()) wifiPassword.remove(wifiPassword.length() - 1);
        } else if (view.cursor == 2) {
            keyboard = (keyboard + 1) % 4;
            view.cursor = 3;
        } else if (wifiPassword.length() < 63) {
            wifiPassword += Keys[keyboard][view.cursor - 3];
        }
    } else if (view.page == Page::Hosts && view.cursor < int(view.rows.size())) {
        const Row item = view.rows[view.cursor];
        if (item.id == "@") {
            open(Page::Pair);
            pairDisplay = decknet::openPairing();
        } else if (item.id == "@forget") {
            if (decknet::forgetHost(decknet::selected())) {
                back();
                showHosts();
            }
        } else if (item.id == "@usb" || decknet::selectHost(item.id)) {
            preferLan = item.id != "@usb";
            lastAnswer = usbAt = 0;
            source = "";
            idempotentBody = idempotentOp = idempotentRoute = "";
            home();
        }
    } else if (view.page == Page::Cooldown) {
        if (!powerSent && (!pendingId.length() || pendingOp == "status")) {
            powerGoHome = view.cursor == 1;
            request("power", false);
        }
    }
    dirty = true;
}

void processButtons() {
    static bool longA = false, longB = false;
    M5.update();
    if (M5.BtnA.wasPressed()) longA = false;
    if (M5.BtnB.wasPressed()) longB = false;
    const bool longNowA = M5.BtnA.pressedFor(500) && !longA;
    const bool longNowB = M5.BtnB.pressedFor(500) && !longB;
    if (longNowA) longA = true;
    if (longNowB) longB = true;
    const bool next = M5.BtnA.wasReleased() && !longA;
    const bool prev = M5.BtnB.wasReleased() && !longB;
    if (!(longNowA || longNowB || next || prev)) return;
    displayAt = millis();
    if (dim) {
        dim = false;
        M5.Display.setBrightness(92);
        return;
    }
    errorText = "";
    statusMint = false;
    if (longNowA) select();
    else if (longNowB) back();
    else if (view.page == Page::Job) {
        const bool full = jobState == "finished" && outputLoaded == outputPage;
        const int limit = max(0, int(full ? jobOutput.length() :
            jobState == "error" || jobState == "limit" || !jobPreview.length() ? jobDetail.length() :
            jobPreview.length()) - 22);
        if (full && ((next && outputMore && outputOffset >= limit) ||
            (prev && outputPage > 0 && outputOffset == 0))) {
            outputPage += next ? 1 : -1;
            outputTail = prev;
            statusNow();
        } else outputOffset = next ? min(outputOffset + 24, limit) : max(0, outputOffset - 24);
    } else if (const int size = menuSize()) view.cursor = (view.cursor + (next ? 1 : size - 1)) % size;
    dirty = true;
}

void syncBridge() {
    bool remote = false;
    const String answer = decknet::answer(remote);
    if (answer.length()) accept(answer, remote ? "Internet" : "LAN");
    const uint32_t now = millis();
    if (wakeRequested) {
        if (!wakeAt && now - pendingAt >= 400) {
            if (!lastAnswer || now - lastAnswer > 1600) decknet::wake();
            wakeAt = now;
        } else if (wakeAt && now - wakeAt >= 45000) {
            wakeRequested = false;
            errorText = tr("Bridge not responding", "Brak odpowiedzi mostka");
            statusMint = false;
            dirty = true;
        }
    }
    if (pendingId.length()) {
        if (!sentLan && pendingRoute != "USB" && now - pendingAt >= 165 &&
            WiFi.status() == WL_CONNECTED && decknet::selected().length()) {
            decknet::request(pendingBody);
            sentLan = true;
        }
        if (now - pendingAt > (sentLan ? 7100u : 850u)) {
            if (isMutation(pendingOp)) {
                errorText = tr("No confirmation. Retry", "Brak potwierdzenia. Ponów");
                statusMint = false;
            }
            pendingId = "";
            decknet::cancelRequest();
            dirty = true;
        }
        return;
    }
    if (view.page == Page::Cooldown && cooldown < 0 && !powered && !powerSent) {
        home();
        return;
    }
    const uint32_t interval = (view.page == Page::Sessions && active > 0) ? 1200 :
                              (listPage() && !listLoading ? 4000 : 850);
    if (nextPoll && now - nextPoll < interval) return;
    nextPoll = now;
    request(operation(view.page));
}
} // namespace

void sampleBattery(bool force = false) {
    const uint32_t now = millis();
    if (!force && batteryAt && now - batteryAt < 5000) return;
    batteryAt = now;
    const int level = constrain(int(M5.Power.getBatteryLevel()), 0, 100);
    const bool chg = M5.Power.isCharging() != 0;
    if (level != batteryPct || chg != batteryCharging) {
        batteryPct = level;
        batteryCharging = chg;
        dirty = true;
    }
}

void setupDevice() {
    auto config = M5.config();
    config.internal_mic = false;
    config.internal_spk = false;
    M5.begin(config);
    sampleBattery(true);
    M5.Display.setRotation(3);
    M5.Display.setBrightness(92);
    canvas.setColorDepth(16);
    canvas.createSprite(Width, Height);
    Serial.setRxBufferSize(4096);
    Serial.setTxBufferSize(4096);
    Serial.begin(115200);
    Serial.setTxTimeoutMs(75);
    serialBuffer.reserve(4096);
    loadLanguage();
    decknet::begin();
    loadWifi();
    WiFi.persistent(false);
    WiFi.mode(WIFI_STA);
    WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
        if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) wifiReason = info.wifi_sta_disconnected.reason;
    });
    if (WiFi.status() != WL_CONNECTED && savedSsid.length()) {
        wifiSecure = savedPassword.length();
        beginWifi(savedSsid, savedPassword);
    }
    displayAt = millis();
    draw();
}

void setup() {
    setupDevice();
}

void loop() {
    sampleBattery();
    readSerial();
    processButtons();
    syncBridge();
    tickWifi();
    syncDeviceState();
    if (view.page == Page::Pair) {
        const String current = decknet::pairingCode();
        if (current != pairDisplay) {
            pairDisplay = current;
            dirty = true;
        }
    }
    const auto now = millis();
    if (motionPage != view.page || motionCursor != view.cursor) {
        motionPage = view.page;
        motionCursor = view.cursor;
        motionAt = now;
        dirty = true;
    }
    if (!dim && now - displayAt > 30000) {
        M5.Display.setBrightness(20);
        dim = true;
    }
    if (dirty || (!dim && now - motionAt <= 175 && now - lastDraw >= 30)
              || (!dim && (scanning || wifiStarting || queuedWifiAt || wakeRequested) && now - lastDraw >= 100)
              || (view.page == Page::Pair && pairDisplay.length() && !dim && now - lastDraw >= 450)
              || (view.page == Page::Job && jobState == "running" && !dim && now - lastDraw >= 250)
              || (view.page == Page::Cooldown && now - lastDraw >= 1000)) {
        draw();
        dirty = false;
        lastDraw = now;
    }
    delay(8);
}
