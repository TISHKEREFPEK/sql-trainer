# Компоненты Cirkit: начальная проверяемая матрица · 10 ноября 2026

Продолжение E01/E11, не полная приёмка. [Официальная страница](https://www.cirkitdesigner.com/docs/custom-simulation-parts/component-examples) перечисляет следующие 21 пример. Это примеры, **не полный список** simulation-ready каталога. [Cirkit разделяет](https://www.cirkitdesigner.com/docs/guides/simulation-support) библиотеку схем и моделируемые части. Локальный catalog-reference.json содержит 130 названий; он также не доказывает полноту сегодняшнего каталога. Остальная компонентная матрица и пользовательские модели остаются открытыми.

| Пример | Что подтверждено у нас / что открыто |
|---|---|
| Pushbutton | Есть базовый Switch; точная 4-pin модель/геометрия примера не принята |
| 8 Position DIP Switch | Visual, отдельная модель не подключена |
| 4x4 Membrane Matrix Keypad | Visual, матрица клавиш/сканирование открыты |
| Analog Joystick | Visual, отдельные оси/кнопка открыты |
| HC-SR04 Ultrasonic Distance Sensor | Visual, timing/расстояние открыты |
| Seven Segment Display | Visual, segment driver открыт |
| 4 Digit Seven Segment Display | Visual, multiplexing открыт |
| 16x2 LCD Display | Parallel/16-pin модель не подключена |
| 16x2 I2C LCD Display | Есть PCF8574/HD44780 и настоящие скетчи; оставшиеся режимы/точная графика открыты |
| ILI9341 Display | [Hardware/software GPIO SPI и native TftSpi](ili9341-spi-2026-11-10.md): отдельная 9-pin модель/проводка/RGB/шаблон; полные режимы/две панели/точный ресурс Cirkit ещё открыты |
| Bipolar Stepper Motor (NEMA 17) | Visual, отдельная модель открыта |
| A4988 Stepper Motor Driver Carrier | Visual, step/current/driver модель открыта |
| DHT11 Humidity and Temperature Sensor | Visual, протокол/параметры открыты |
| DS18B20 Temperature Sensor | Visual, 1-Wire открыт |
| MPU6050 Accelerometer + Gyroscope | Visual, I2C/registers/controls открыты |
| OLED 128x64 I2C Monochrome Display VDD-GND | Есть SSD1306/GFX и native/teacher scenarios; полные режимы/графика ещё не приняты |
| DS1307 RTC | Visual; внутренние часы Pico эту внешнюю деталь не заменяют |
| Photoresistor (LDR) Sensor | Базовый двухконтактный LDR есть; конкретный sensor module не принят |
| PIR Motion Sensor | Visual, timing/interactive input открыты |
| 1 Channel 5V Relay Module | Visual, driver/contact model открыта |
| Trimmer Potentiometer | Есть Pot3; соответствие всех параметров/геометрии примера ещё проверяется |

Статус Visual означает возможность построить схему, не доказательство поведения компонента. Не включать Simulated=true по наличию рисунка. Для каждого реализуемого семейства нужны собственные protocol/electrical/interaction и immutable review сценарии. MAX7219/RC522/другие позиции локального списка не исчезают из объёма из-за их отсутствия на странице примеров. MCU-матрица — [отдельный документ](cirkit-mcu-matrix-2026-11-06.md). Импорт прежних Cirkit-проектов отложен владельцем, не выполнен.
