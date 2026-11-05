/*
 * esp32_chat_client.ino - the sensor node as a chat user (Telecommunication Systems
 * course project). It logs in to the C# chat server like any WPF client and posts
 * a Sensor message every 5 s; every chat window shows the latest reading.
 * If someone sends it the private message "status", it answers privately.
 *
 * Board: ESP32 dev board, Arduino-ESP32 core 3.x. Sensors as in esp32-network-node:
 * LM35 on GPIO34, HC-SR04 Trig GPIO5 / Echo GPIO18 (Echo through a divider), LDR on GPIO35.
 *
 * Wire format (the same as src/Chat.Protocol/FrameCodec.cs): a 4-byte big-endian
 * length, then UTF-8 JSON, e.g. {"type":"Sensor","temp":23.4,"dist":41.7,"light":63}
 */
#include <WiFi.h>
#include "secrets.h"

const int PIN_TEMP = 34, PIN_LIGHT = 35, PIN_TRIG = 5, PIN_ECHO = 18;
WiFiClient server;
unsigned long lastPost = 0;
static char rx[2048];

bool sendFrame(const char *json) {
  uint32_t n = strlen(json);
  uint8_t h[4] = { (uint8_t)(n >> 24), (uint8_t)(n >> 16), (uint8_t)(n >> 8), (uint8_t)n };
  return server.write(h, 4) == 4 && server.write((const uint8_t *)json, n) == n;
}

// reads one frame into rx; returns its length, 0 if none is waiting, -1 on error
int readFrame() {
  if (server.available() < 4) return 0;
  uint8_t h[4];
  server.readBytes(h, 4);
  uint32_t n = ((uint32_t)h[0] << 24) | ((uint32_t)h[1] << 16) | ((uint32_t)h[2] << 8) | h[3];
  if (n == 0 || n >= sizeof rx) return -1;                      // too big for this node: drop the connection
  if (server.readBytes(rx, n) != n) return -1;
  rx[n] = 0;
  return n;
}

bool login() {
  if (!server.connect(CHAT_SERVER, CHAT_PORT)) return false;
  char msg[160];
  snprintf(msg, sizeof msg, "{\"type\":\"Login\",\"user\":\"%s\",\"password\":\"%s\"}", CHAT_USER, CHAT_PASSWORD);
  sendFrame(msg);
  unsigned long t = millis();
  while (millis() - t < 5000) {
    int n = readFrame();
    if (n < 0) break;
    if (n > 0 && strstr(rx, "\"type\":\"LoginResult\"")) {
      bool ok = strstr(rx, "\"ok\":true") != nullptr;
      Serial.printf("login: %s\n", ok ? "ok" : rx);
      return ok;
    }
    delay(10);
  }
  server.stop();
  return false;
}

float tempC() { uint32_t mv = 0; for (int i = 0; i < 16; i++) mv += analogReadMilliVolts(PIN_TEMP); return mv / 16 / 10.0f; }
float distCm() {
  digitalWrite(PIN_TRIG, LOW); delayMicroseconds(2);
  digitalWrite(PIN_TRIG, HIGH); delayMicroseconds(10); digitalWrite(PIN_TRIG, LOW);
  unsigned long e = pulseIn(PIN_ECHO, HIGH, 25000UL);
  return e ? e * 0.0343f / 2 : -1;
}
int lightPct() { return analogRead(PIN_LIGHT) * 100 / 4095; }

void postReading() {
  char msg[128];
  float d = distCm();
  if (d < 0) snprintf(msg, sizeof msg, "{\"type\":\"Sensor\",\"temp\":%.1f,\"light\":%d}", tempC(), lightPct());
  else snprintf(msg, sizeof msg, "{\"type\":\"Sensor\",\"temp\":%.1f,\"dist\":%.1f,\"light\":%d}", tempC(), d, lightPct());
  if (!sendFrame(msg)) server.stop();
}

// answers a private "status" message; the sender's name is in "from":"..."
void handle(const char *json) {
  if (!strstr(json, "\"type\":\"Private\"") || !strstr(json, "\"text\":\"status\"")) return;
  const char *f = strstr(json, "\"from\":\"");
  if (!f) return;
  f += 8;
  char who[24]; int k = 0;
  while (*f && *f != '"' && k < 23) who[k++] = *f++;
  who[k] = 0;
  char msg[200];
  snprintf(msg, sizeof msg, "{\"type\":\"Private\",\"to\":\"%s\",\"text\":\"up %lu s, RSSI %d dBm, %.1f C\"}",
           who, millis() / 1000, WiFi.RSSI(), tempC());
  sendFrame(msg);
}

void setup() {
  Serial.begin(115200);
  pinMode(PIN_TRIG, OUTPUT); pinMode(PIN_ECHO, INPUT);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) { delay(250); Serial.print('.'); }
  Serial.printf("\nIP %s\n", WiFi.localIP().toString().c_str());
}

void loop() {
  if (!server.connected() && !login()) { delay(3000); return; }   // reconnect and log in again
  int n = readFrame();
  if (n < 0) { server.stop(); return; }
  if (n > 0) handle(rx);
  if (millis() - lastPost >= 5000) { lastPost = millis(); postReading(); }
  delay(5);
}
