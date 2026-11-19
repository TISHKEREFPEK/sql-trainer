constexpr int Button = 12;
constexpr int Led = 39;

volatile unsigned edges = 0;

void IRAM_ATTR onButtonChange() {
  ++edges;
}

void setup() {
  Serial.begin(115200);
  pinMode(Button, INPUT_PULLUP);
  pinMode(Led, OUTPUT);
  attachInterrupt(digitalPinToInterrupt(Button), onButtonChange, CHANGE);
  Serial.println("GPIO_READY");
}

void loop() {
  const int level = digitalRead(Button);
  digitalWrite(Led, level);
  static int previous = -1;
  if (level != previous) {
    previous = level;
    Serial.printf("gpio=%d irq=%u\n", level, edges);
  }
  delay(10);
}
