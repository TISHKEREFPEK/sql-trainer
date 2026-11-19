constexpr int Button = 12;
constexpr int Led = 39;

volatile unsigned edges = 0;

void IRAM_ATTR onButtonChange() {
  ++edges;
}

void setup() {
  Serial.begin(115200);
  Serial1.begin(115200, SERIAL_8N1, 18, 17);
  Serial2.begin(115200, SERIAL_8N1, 16, 15);
  pinMode(Button, INPUT_PULLUP);
  pinMode(Led, OUTPUT);
  attachInterrupt(digitalPinToInterrupt(Button), onButtonChange, CHANGE);
  Serial.println("GPIO_READY");
  Serial1.println("UART1_READY");
  Serial2.println("UART2_READY");
}

void echo(HardwareSerial& uart) {
  while (uart.available()) {
    uart.write(uart.read());
  }
}

void loop() {
  const int level = digitalRead(Button);
  digitalWrite(Led, level);
  static int previous = -1;
  if (level != previous) {
    previous = level;
    Serial.printf("gpio=%d irq=%u\n", level, edges);
  }
  echo(Serial);
  echo(Serial1);
  echo(Serial2);
  delay(10);
}
