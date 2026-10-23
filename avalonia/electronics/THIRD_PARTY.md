# Offline firmware runtime

Vendored AVR8js 0.21.0: https://github.com/wokwi/avr8js — MIT, Copyright Uri Shaked and contributors. Unmodified CommonJS distribution from https://registry.npmjs.org/avr8js/-/avr8js-0.21.0.tgz (SHA-256 4dcaf64abfc5a3ad39bb03472810e1ae8914914edd424623428dcaeca597731d). License retained in vendor/avr8js/LICENSE. Only package metadata was added to declare CommonJS beneath the site's ES-module root.

Arduino CLI 1.5.1, Arduino AVR core 1.8.6, GCC toolchain and Node.js 24.19.0 are prepared separately under ignored artifacts/electronics-runtime. No binary toolchain is committed to Git. Their upstream notices, licenses and corresponding source requirements apply independently; preserve original license files and source availability when redistributing a packaged runtime. Source repositories: https://github.com/arduino/arduino-cli, https://github.com/arduino/ArduinoCore-avr, https://gcc.gnu.org/, https://github.com/nodejs/node. Core/tool archive metadata is retained in runtime/data/package_index.json.

Cirkit Designer was consulted as a functional reference. No proprietary application source, component assets, cloud models, accounts or services are included.

Servo 1.2.2 is installed into runtime/user/libraries/Servo by Arduino CLI and distributed with its upstream license. Source: https://github.com/arduino-libraries/Servo (LGPL-2.1). Preserve its library sources and license in the portable user directory. offline-ready-v2.json records this additional runtime dependency.

assets/components-*.png are original generated illustrations, not Cirkit Designer images. Their structured generation prompt set and built-in image_gen provenance are recorded in assets/generation-prompts.md. catalog-reference.json contains 130 names observed in the public Simulation Parts catalogue on 2026-10-23; it is not the entire service library or a collection of its models/assets.
