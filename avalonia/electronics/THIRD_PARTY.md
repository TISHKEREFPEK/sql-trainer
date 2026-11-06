# Offline firmware runtime

SSD1306/I2C model in ssd1306.cjs and i2c-bus.cjs is original code based on Solomon Systech SSD1306 Rev 1.1 register/interface facts, distributed by Adafruit at https://cdn-shop.adafruit.com/datasheets/SSD1306.pdf. Cirkit documentation was consulted as a functional reference, without copying application/model code or graphics.

Offline runtime v4 adds these pinned Arduino libraries with all upstream sources/notices copied under user/libraries in the distributed runtime (archives/checksums from the Arduino library index):

- Adafruit SSD1306 2.5.17 — BSD, https://github.com/adafruit/Adafruit_SSD1306; license.txt retained. Archive SHA-256 9b8a9c29a16576d66bce16ed9f6a731632fecbb477171f38a6bc033fda15dd06.
- Adafruit GFX Library 1.13.0 — BSD, https://github.com/adafruit/Adafruit-GFX-Library; license.txt retained. Archive SHA-256 202ec716ff222fb08ffc544a5f315cb47b8630c35fbbc9944fc9f8c93d903924.
- Adafruit BusIO 1.17.4 — MIT, https://github.com/adafruit/Adafruit_BusIO; LICENSE retained. Archive SHA-256 c7bab1b6c6eee64b50964c8e149b795719a01f1d9dfcdcef170b35daa696349b.

Upstream library code remains in the separately prepared runtime, not committed binaries. Preserve its source/license files when packaging and do not substitute newer dependencies without repeating the relevant firmware checks. No classroom metadata, credentials or pupil code are copied to this public toolchain.

LCD protocol implementation in lcd-i2c.cjs is original code based on the HD44780U instruction/register facts published by Newhaven Display (https://support.newhavendisplay.com/hc/en-us/articles/4414493825687-HD44780), NXP PCF8574 (https://www.nxp.com/docs/en/data-sheet/PCF8574_PCF8574A.pdf) and the standard backpack pin assignment documented by LiquidCrystal_PCF8574 (https://github.com/mathertel/LiquidCrystal_PCF8574). No Cirkit implementation or assets were copied.

The classic 5x7 glyph data in vendor/adafruit-font/glcdfont.c is from Adafruit-GFX-Library, https://github.com/adafruit/Adafruit-GFX-Library/blob/master/glcdfont.c, retrieved 2026-11-07 (upstream raw SHA-256 bd6d60ecb9cc350a83f209d7c62de2fe5f6ce3402d5598cde2988b5b44e83e02). BSD license, Copyright (c) 2012 Adafruit Industries, retained verbatim as vendor/adafruit-font/license.txt and distributed alongside the font through the electronics content glob. This is a documented provisional glyph set, not an assertion of exact HD44780 A00/A02 ROM compatibility.

avr-boards.json / avr-devices.cjs contain board configuration facts checked against Microchip ATmega328P/ATmega2560 documentation, avr-libc register headers in the prepared toolkit and ArduinoCore-avr 1.8.6 variants/standard and variants/mega pin mappings. Sources: https://www.microchip.com/en-us/product/ATMEGA2560, https://docs.arduino.cc/resources/pinouts/A000067-full-pinout.pdf, https://github.com/arduino/ArduinoCore-avr/tree/1.8.6/variants/mega. Keep the original ArduinoCore-avr LGPL and avr-libc notices/sources in redistributed toolkits. No MCU vendor implementation was copied over or modified; runtime emulation uses the existing unmodified AVR8js distribution below.

Vendored AVR8js 0.21.0: https://github.com/wokwi/avr8js — MIT, Copyright Uri Shaked and contributors. Unmodified CommonJS distribution from https://registry.npmjs.org/avr8js/-/avr8js-0.21.0.tgz (SHA-256 4dcaf64abfc5a3ad39bb03472810e1ae8914914edd424623428dcaeca597731d). License retained in vendor/avr8js/LICENSE. Only package metadata was added to declare CommonJS beneath the site's ES-module root.

Arduino CLI 1.5.1, Arduino AVR core 1.8.6, GCC toolchain and Node.js 24.19.0 are prepared separately under ignored artifacts/electronics-runtime. No binary toolchain is committed to Git. Their upstream notices, licenses and corresponding source requirements apply independently; preserve original license files and source availability when redistributing a packaged runtime. Source repositories: https://github.com/arduino/arduino-cli, https://github.com/arduino/ArduinoCore-avr, https://gcc.gnu.org/, https://github.com/nodejs/node. Core/tool archive metadata is retained in runtime/data/package_index.json.

Cirkit Designer was consulted as a functional reference. No proprietary application source, component assets, cloud models, accounts or services are included.

Servo 1.2.2 is installed into runtime/user/libraries/Servo by Arduino CLI and distributed with its upstream license. Source: https://github.com/arduino-libraries/Servo (LGPL-2.1). Preserve its library sources and license in the portable user directory. offline-ready-v2.json records this additional runtime dependency.

assets/components-*.png are original generated illustrations, not Cirkit Designer images. Their structured generation prompt set and built-in image_gen provenance are recorded in assets/generation-prompts.md. catalog-reference.json contains 130 names observed in the public Simulation Parts catalogue on 2026-10-23; it is not the entire service library or a collection of its models/assets.
