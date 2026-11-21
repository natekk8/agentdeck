#pragma once

#include <Arduino.h>
#include <ArduinoJson.h>
#include <vector>

namespace decknet {
struct Host {
    String id;
    String label;
};

void begin();
String deviceId();
String newId();
bool pairUsb(JsonObjectConst request);
String openPairing();
String pairingCode();
uint32_t pairingRemaining();
void closePairing();
std::vector<Host> hosts();
String selected();
bool selectHost(const String& host);
bool forgetHost(const String& host);
void request(const String& body);
void cancelRequest();
String answer(bool& remote);
bool configureRelay(const String& host, const String& endpoint);
void publish(const String& state);
String takeControl();
void completeControl(const String& result);
bool wake(const String& host = "");
}
