#include <Wire.h>

constexpr uint8_t Address = 0x50;
constexpr size_t DataLength = 73;

bool writePattern(TwoWire& bus, int port) {
  bus.beginTransmission(Address);
  bus.write(0);
  bus.write(0x20);
  for (size_t i = 0; i < DataLength; ++i) {
    bus.write(uint8_t((i * 7 + 41 + port * 83) & 0xff));
  }
  const auto result = bus.endTransmission();
  if (result != 0) {
    Serial.printf("I2C%d_WRITE_ERROR=%u\n", port, result);
  }
  return result == 0;
}

bool readPattern(TwoWire& bus, int port) {
  bus.beginTransmission(Address);
  bus.write(0);
  bus.write(0x20);
  if (bus.endTransmission(false) != 0) {
    Serial.printf("I2C%d_START_ERROR\n", port);
    return false;
  }
  const size_t count = bus.requestFrom(Address, DataLength, true);
  if (count != DataLength) {
    Serial.printf("I2C%d_READ_COUNT=%u\n", port, unsigned(count));
    return false;
  }
  for (size_t i = 0; i < count; ++i) {
    const int sample = bus.read();
    if (sample != int((i * 7 + 41 + port * 83) & 0xff)) {
      Serial.printf("I2C%d_READ_MISMATCH=%u:%d\n", port, unsigned(i), sample);
      return false;
    }
  }
  Serial.printf("I2C%d_RESTART_OK=%u\n", port, unsigned(count));
  return true;
}

bool rejectsMissingDevice(TwoWire& bus, int port) {
  bus.beginTransmission(Address + 1);
  const auto result = bus.endTransmission();
  Serial.printf("I2C%d_MISSING=%u\n", port, result);
  return result == 2;
}

void setup() {
  Serial.begin(115200);
  Serial.println("I2C_PROBE_START");
  if (!Wire.begin(8, 9, 100000) || !Wire1.begin(15, 16, 400000)) {
    Serial.println("I2C_BEGIN_ERROR");
    return;
  }
  if (rejectsMissingDevice(Wire, 0) && rejectsMissingDevice(Wire1, 1) &&
      writePattern(Wire, 0) && writePattern(Wire1, 1) &&
      readPattern(Wire, 0) && readPattern(Wire1, 1)) {
    Serial.println("I2C_PROBE_OK");
  }
}

void loop() {
  delay(100);
}
