constexpr int Potentiometer = 1;
constexpr int ReferenceInput = 11;

void setup() {
  Serial.begin(115200);
  analogReadResolution(12);
  analogSetPinAttenuation(Potentiometer, ADC_11db);
  analogSetPinAttenuation(ReferenceInput, ADC_11db);
  Serial.printf("ADC_START=%d,%d\n", analogRead(Potentiometer), analogRead(ReferenceInput));
}

void loop() {
  Serial.printf("ADC_SAMPLE=%d,%d\n", analogRead(Potentiometer), analogRead(ReferenceInput));
  delay(100);
}
