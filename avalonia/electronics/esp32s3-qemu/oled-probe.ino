#include <Wire.h>

constexpr uint8_t Address = 0x3c;
constexpr size_t Length = 73;

unsigned probe(TwoWire& bus, uint8_t address) {
  bus.beginTransmission(address);
  return bus.endTransmission();
}

bool draw(TwoWire& bus, uint8_t pattern) {
  const uint8_t init[] = {0x00, 0xae, 0x8d, 0x14, 0x20, 0x00, 0xa1, 0xc8, 0xaf};
  bus.beginTransmission(Address);
  bus.write(init, sizeof(init));
  if (bus.endTransmission() != 0) return false;

  bus.beginTransmission(Address);
  bus.write(uint8_t(0x40));
  for (size_t i = 0; i < Length; ++i) bus.write(uint8_t(pattern + i));
  return bus.endTransmission() == 0;
}

void setup() {
  Serial.begin(115200);
  Serial.println("OLED_BOOT");
  if (!Wire.begin(5, 6, 100000)) return;
  Serial.printf("WRONG_PINS=%u\n", probe(Wire, Address));
  Wire.end();
  if (!Wire.begin(8, 9, 100000) || !Wire1.begin(17, 18, 400000)) return;
  Serial.printf("NO_POWER=%u\n", probe(Wire1, Address + 1));
  Wire1.end();
  if (!Wire1.begin(15, 16, 400000)) return;
  Serial.printf("WRITE_ONLY=%u\n", unsigned(Wire.requestFrom(Address, size_t(1), true)));
  const bool first = draw(Wire, 11);
  const bool second = draw(Wire1, 101);
  Serial.printf("OLED_DRAW=%u,%u\n", first, second);
}

void loop() { delay(100); }
