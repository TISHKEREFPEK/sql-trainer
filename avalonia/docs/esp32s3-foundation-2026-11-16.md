# Основание ESP32-S3 · 16 ноября 2026

Часть E01/E11 после `b43c4ef`. Подготовлены отдельный автономный компилятор bare-metal C++ и ограниченный запуск настоящего Xtensa ELF. **ESP32-S3 в редакторе остаётся Visual.** Arduino, физические выводы и полноценная периферия этим не реализованы. Ни GPIO-заглушка, ни Ethernet вместо Wi-Fi не выдаются за совместимость Cirkit.

## Первичные источники и выбор основания

- [Cirkit ESP32-S3](https://www.cirkitdesigner.com/docs/platforms/esp32-s3): требуются Arduino, два Xtensa CPU, GPIO/маршрутизация/IRQ, три UART, SPI/I2C, таймеры/PWM/ADC, Wi-Fi/TCP-протоколы, RMT TX, crypto, flash/PSRAM и watchdog. Ранее принятый полный объём E11 сохраняется; импорт и ИИ этим не возвращаются в текущую работу.
- [Матрица Espressif QEMU](https://github.com/espressif/esp-toolchain-docs/blob/main/qemu/README.md) и [ESP32-S3 guide](https://github.com/espressif/esp-toolchain-docs/blob/main/qemu/esp32s3/README.md): основание настоящих CPU, UART, памяти, таймеров и части crypto, но нет полного GPIO/IOMUX, обычных SPI/I2C, LEDC/S3, Wi-Fi/USB/RMT. OpenCores Ethernet — отдельное виртуальное устройство, не реализация Wi-Fi. Бинарный пакет не закрывает нашу функциональную матрицу.
- Закреплённый [GPIO Espressif](https://github.com/espressif/qemu/blob/esp-develop-9.2.2-20260417/hw/gpio/esp32_gpio.c) читает strap и игнорирует остальные записи; S3 наследует эту модель. В [machine S3](https://github.com/espressif/qemu/blob/esp-develop-9.2.2-20260417/hw/xtensa/esp32s3.c) подключены UART0/1, UART2 ещё не привязан к внешнему chardev. Эти пробелы не скрываются своим каталогом.
- [Espressif Clang instructions](https://github.com/espressif/esp-toolchain-docs/blob/main/clang/esp-idf-app-clang-build.md), [закреплённый tool manifest IDF 5.5.2](https://github.com/espressif/esp-idf/blob/v5.5.2/tools/tools.json). Используется официальный Clang 19.1.2_20250312 с собственным LLVM assembler и GNU ld 2.43.1 из того же пакета. Это отдельный bare-metal профиль, не подтверждённая замена GCC для произвольных Arduino-библиотек.

## Подготовка и API

Из корня checkout, после подготовки обычного AVR runtime:

```powershell
./avalonia/scripts/prepare-esp32s3.ps1
$env:CLASSROOM_ESP32S3_RUNTIME = (Resolve-Path ./avalonia/artifacts/esp32s3-runtime).Path
```

Можно явно указать `-RuntimePath` в `.codex` или `avalonia/artifacts` этого checkout; выход за эти папки/переход через links отвергается. BaseRuntimePath нужен для закреплённого Arduino CLI/лицензии, которые использует общая инфраструктура копирования SDK; Arduino core в этот профиль ещё не установлен. Скрипт проверяет SHA256 до распаковки, готовит новую staging-папку и marker, не перезаписывает непустую неизвестную/другую установку. При ошибке стадия сохраняется. В готовой папке проверяются версии и hashes ключевых файлов. Сеть нужна для первичной подготовки, не для обычной компиляции/исполнения.

На текущем ПК тестовый runtime — `.codex/esp32-probe/runtime-clang`; независимая холодная подготовка прошла в `.codex/esp32-probe/runtime-final`, ключевые hashes совпадают. Оба пути игнорируются Git и задаются через CLASSROOM_ESP32S3_RUNTIME. Более ранние runtime/runtime-x86/failed PE research folders не использовать. Default `avalonia/artifacts/esp32s3-runtime` здесь пока не готовился и не заменяет эти свидетельства.

`Esp32S3Compiler.CompileBareMetal(code, cancellation)` принимает до 100000 символов и функцию `extern "C" void classroom_main()`. Служебный отдельный startup.S устанавливает стек и вызывает её по call0 ABI; возврат завершает ограниченную пробу через simcall. Clang получает target esp32s3 с отключённым windowed ABI, integrated cc1/assembler и явным GNU linker. `-no-canonical-prefixes` и явный ld path сохраняют ASCII drive aliases: без них дочерний clang восстанавливал физический путь с кириллицей, после чего получал Permission denied. Библиотеки C/C++/Arduino и полноценный startup/конструкторы/обработчики firmware пока не подключены.

Linker выделяет для профиля CODE `0x40378000..0x40398000`, DATA `0x3fc90000..0x3fcb0000`, стек сверху `0x3fcc0000`. Это выбранные окна тестового загрузчика, не утверждение о полной физической RAM/alias/cache/MMU модели реального S3. `Esp32S3Image` проверяет ELF32 little endian, EM_XTENSA/EXEC, ограниченные file/program/section tables, размеры, файл/память/entry, отсутствие dynamic interpreter, MMIO/недопустимых сегментов, перекрытий и writable code. Проверка относится к этому профилю; Arduino flash/partition images ещё не принимаются.

`Esp32S3Foundation.Run(elf, cancellation)` клонирует/валидирует образ и запускает QEMU без host monitor и сети; ELF загружается непосредственно, без загрузки vendor ROM. UART0/1 выдаются отдельно от диагностики QEMU. Возврат/ExitCode — результат гостя, не готовая строка, подставленная вместо исполнения. Это ограниченная проба до окончания процесса, не интерактивный Step runner редактора. Двухъядерное взаимодействие, все timing modes и периферия отдельно не приняты.

Компиляция и QEMU работают через прежний WindowsSandboxProcess: новый AppContainer без network capabilities, read-only snapshot SDK, отдельный scratch, job/process/memory/time limits и завершение дерева. Профиль `sandbox-xtensa` добавлен в whitelist CompilerRuntime, AVR/Pico не заменяются. QEMU использует TCG cache 32 MiB и single host thread: его default 1 GiB JIT не помещался в прежний лимит процесса 768 MiB. Лимиты Windows не повышались. UART-файлы контролируются во время исполнения: после превышения 1 MiB канал отменяет весь job; возможен небольшой объём между опросами, выходной файл сверх лимита не принимается. Отмена/тайм-аут завершают процесс, затем временная папка убирается. Включённый semihosting не предоставляет разрешения читать личные файлы: действует граница AppContainer; отдельная синтетическая проверка подтверждает отказ гостевому open.

## Закреплённые пакеты и лицензии

| Пакет | SHA256 архива |
|---|---|
| [Espressif Clang Windows x64](https://github.com/espressif/llvm-project/releases/tag/esp-19.1.2_20250312) | `0fb20439a3d1d31088e646a6f2a8c9277ac6aed858188e2afa470bdb42e13afb` |
| [Espressif QEMU Windows x64](https://github.com/espressif/qemu/releases/tag/esp-develop-9.2.2-20260417) | `3c483d77f5350a568df1faf4d8dbc82c95d6bc2b826d0d4be910485e0a68ca2a` |

Clang bundle сохраняется целиком с LICENSE.txt, THIRD-PARTY-LICENSES.txt, VERSION.txt и third-party-licenses: LLVM-компоненты, GNU binutils, Picolibc, MinGW и другие части имеют собственные условия. QEMU — GPLv2 с исходными per-file notices; к бинарному пакету отдельно добавляются точные upstream COPYING/COPYING.LIB. Не объявлять весь SDK одной лицензией и не шифровать обязательные сторонние исходники. Бинарники/SDK/кэши не входят в Git. Перед включением в распространяемый установщик остаётся полный corresponding-source/dependency/ROM audit; эта подготовка его не заменяет. Сейчас build-windows/build-installers этот новый runtime не включают.

Официальные GCC x64/x86 14.2.0_20260121 оказались несовместимы с нашим AppContainer (Windows 623); запуск без изоляции не добавлен. Опыт добавления одной PE relocation-записи привёл к 0xC0000005 и отвергнут: ни patch, ни изменённые EXE не входят в текущий профиль/исходники. LLD 19 в выбранной программе с RAM-переменными аварийно завершился и вне sandbox; вместо него выбран штатный GNU ld из Clang bundle. Эти отказы и последние прогоны зафиксированы в [baseline](next-version-baseline-2026-11-04.md), не замаскированы успешным hello world.

## Следующая работа

1. Подтвердить источники/лицензии расширенного S3 engine и конкретный путь реализации всех обязательных периферийных моделей. Не считать QEMU без них достаточным. Повторно использовать допустимый upstream, если он закрывает требуемое поведение.
2. Подготовить закреплённый Arduino SDK и доказать обычные скетчи/библиотеки, boot/flash/partition/PSRAM/ROM. Текущий bare-metal linker не подменяет этот этап. Пример исследованного Arduino core 3.3.12 пока не установлен в исполняемый профиль.
3. Реализовать/проверить GPIO/IOMUX/IRQ, ADC/PWM, SPI/I2C/RMT, третий UART, clocks/watchdogs, dual-core и реальный сетевой тракт совместимый с Wi-Fi-кодом. Ethernet с изменённым скетчем не является доказательством исходного Wi-Fi API.
4. Добавить ограниченный интерактивный runner/время/ошибки/остановку и только после доказательств — native плату, код/компоненты/редактор, immutable сдачи и полную историю. Старые Visual-проекты сохранять.
5. Обновить автономную поставку с notices/sources, проверить физический Win10, ресурсы 16–32 ГБ, offline LAN и нагрузку класса. Полная E11/E14/графическая приёмка открыта.

Рабочие приложения/личные базы/пароли, обычные Windows policies и GitHub не менялись. Изоляционные ACL касаются только прежних специальных временных compiler-копий/scratch; защита файлов установленного приложения ещё не реализована. Импорт/учебная редактура/GitHub по-прежнему отложены, ИИ исключён. Цель активна.
