# Основание Pico/RP2040 · 8 ноября 2026

Это промежуточный блок E11, **не готовая плата в редакторе**. Pico по-прежнему Visual: нельзя объявлять её исполняемой по результату этих проверок. Uno/Mega и прежние сохранения не меняются. Импорт Cirkit и отправка GitHub остаются отложенными.

## Что реализовано

- После `c9a2e90` добавлены sin/cos/tan/sincos/exp/log/atan2 двух точностей: bounded 320-bit fixed-point, вычисляемые π/ln2 и точный общий encoder. Независимые mpmath-fixtures проверяются через настоящий ARM ABI, включая оба результата sincos. Подготовленный fixture не требует Python в приложении или обычном dotnet test.
- После `ba121b2` добавлены int/fixed 32/64-bit signed/unsigned conversions, compare, float↔double и точно округлённый sqrt. ROM floor/saturation не подменяется C truncation; экстремальный scale ограничен до BigInt allocation. Проверен настоящий Arduino analogWrite: аппаратный PWM около 1000 Гц/25%, delay на другом выводе около 100 Гц. Это первый эталон, не все режимы PWM.
- После `00b4836` реализованы add/sub/mul/div binary32/64: точные рациональные BigInt, ближайшее чётное округление, знаки нуля, overflow и обнуление input/output denormals по ROM. Значения payload не проходят через Number; это функциональные host-вызовы, не обещание оригинальных циклов ROM. Неподдержанные indeterminate infinity combinations явно отклоняются.
- После первого foundation-коммита `1cc2d46` добавлен собственный host ROM ABI: lookup, memcpy/memset/word variants и битовые функции. Это собственные таблицы/Thumb entry points и реальные операции над памятью MCU, без оригинального ROM. Неизвестные функции и инструкции явно останавливают выполнение.
- Обычные проверенные Arduino setup/loop теперь запускаются: GPIO/ADC/UART, настоящая USB enumeration через TinyUSB/IRQ и UTF-8, в том числе многопакетный ввод/эхо. Отложенная доставка USB SETUP устраняет вызов до завершения предыдущего события; OUT без данных ждёт, а не порождает пустые пакеты. PicoFrame сообщает UsbReady.
- Добавлена функциональная модель 2 МиБ serial NOR с QSPI/SSI FIFO и командами статуса/UID/JEDEC/чтения. SDK flash erase/program и возврат через настоящий boot2 проверены: байты меняются, program выполняет AND, erase возвращает FF. UID `53514C4300000001` синтетический. ROM игнорирует bus writes без изменения данных, XIP writes по умолчанию ошибочны. Точные циклы ROM/NOR и аналоговый USB PHY не заявлены.
- Отдельные `PicoCompiler` / `PicoFirmwareRunner` и Node worker выполняют настоящий машинный код ARM Cortex-M0+, без интерпретации строк Arduino. RP2040js 1.4.1 закреплён по upstream commit / npm SHA512; неизменённый CJS-дистрибутив и MIT-лицензия находятся в `electronics/vendor/rp2040js/`.
- Строгий UF2-декодер проверяет семью RP2040, последовательность блоков, перекрытия, диапазон flash 2 МиБ, загруженные векторы и стек в 264 КиБ RAM. Отказ чужой/повреждённой прошивке происходит до запуска CPU.
- Проверен bare-metal C++: реальный GPIO2 управляет GPIO25, ADC0 измеряет 1,65 В, UART0 выводит результат и пересылает UTF-8-ввод в независимый UART1. Перезапуск создаёт новое состояние. Один шаг ограничен 50 мс моделируемого времени / 1 000 000 инструкций / объёмом serial; неполный шаг отмечается `Complete=false` и может продолжаться.
- Закреплён Arduino-Pico 6.3.2 / инструменты 5.0.0-9576866. Для AppContainer используется xPack ARM GCC 14.2.1-1.1 x64 и уже подготовленный native x64 Arduino ctags. Оригинальные комплектные Windows g++/ctags дали Windows 623 при создании процесса; обход через небезопасную компиляцию от пользователя не добавлен.
- Arduino-скетч действительно компилируется без сети в AppContainer. Только фиксированный исходник приложения `void setup(){} void loop(){}` получает до 5 минут на первоначальную подготовку ядра; ученический текст ещё не записан и не передаётся туда. Ученические задания сохраняют лимит 60 секунд, запрет сети, ограничения памяти/процессов, завершение всего дерева и существующие lease-журналы восстановления.
- Проверенный SHA256 архив `core.a` хранится один раз в кэше runtime. Перед созданием дочернего процесса он копируется в scratch текущей компиляции под именем для фактической буквы диска. Arduino CLI 1.5.1 отключает core cache при явном `--build-path`, а компоновщик не принимает архив на другом виртуальном диске; поэтому этот профиль использует штатный scratch build cache. Ученик не меняет общий архив. Разработка прежних форматов кэша 1/2 обновляется в 3 по проверенному архиву, без удаления старых папок.

## Важная незавершённая граница

Общий первоначальный отказ всем ROM-обращениям снят для перечисленных функций, включая scientific slots. Прямые trig ROM calls требуют |x|<128 для float и |x|<1024 для double; нарушение явно отклоняется. SDK wrappers уменьшают большие углы по rounded 2π, поэтому на огромных углах воспроизводится погрешность самого SDK, а не sin исходного математического значения. Некоторые indeterminate infinity combinations всё ещё явно отклоняются. Reset/BOOTSEL, копирование всей ROM-библиотеки в RAM и другие непокрытые ROM-возможности не объявлены готовыми. Оригинальные байты Raspberry Pi BootROM из demo upstream не включены: лицензия эмулятора MIT не распространяется автоматически на отдельные floating-point routines ROM.

USB CDC подтверждён перечисленными сценариями, не всеми режимами USB. PWM/PIO/DMA/RTC/watchdog, остальные прерывания, частоты и протоколы требуют собственных эталонов. Исполнение второго ядра через SIO FIFO явно не поддержано; начальный single-core сценарий работает. I2C-связь с circuit solver явно отклоняется до реализации. Не подключены физическая распиновка, новый native PartKind, редактор кода/проверочная копия/история Pico и поставка Pico runtime пользователю. ESP32-S3 и многоплатность также открыты.

## Подготовка на другом ПК

Из каталога `avalonia`:

```powershell
./scripts/prepare-electronics.ps1
./scripts/prepare-pico.ps1
$env:CLASSROOM_PICO_RUNTIME = Join-Path (Get-Location) 'artifacts/pico-runtime'
dotnet test tests/Classroom.UiTests/Classroom.UiTests.csproj -c Release --filter 'FullyQualifiedName~PicoFoundationTests|FullyQualifiedName~PicoRomTests|FullyQualifiedName~PicoConversionTests|FullyQualifiedName~PicoScienceTests'
```

Первая подготовка SDK требует интернета и свободного места для обоих наборов инструментов и их изолированного кэша; компиляция скетча сети не требует. Папки Pico/AVR должны быть раздельными и не вложенными. Скрипт сохраняет лицензии/исходники ctags, проверяет SHA256 xPack и не перезаписывает неизвестный ARM-комплект. Короткая временная папка распаковки учитывает ограничения PowerShell 5.1 на длину пути. `build-windows.ps1` подготавливает Pico для тестов, но пока упаковывает пользователям только прежний AVR runtime.

На этом ПК тесты используют отдельный `.codex/pico-probe/runtime`, `.codex/next-version-compiler-runtime` и GUID scratch, без замены установленных приложений/личных БД. Результаты: `pico-foundation-sandbox` — 12/12 (Pico + общая изоляция + lease), `pico-foundation-final` — 6/6 Pico после уточнения тестов. Второй прогон дополнительно проверяет запрет private include и в Arduino-профиле и ограничение зависшей программы. Это пересекающиеся наборы, не 18 уникальных тестов. Полная solution, чистая установка всех SDK, Win10 и LAN в этом блоке не проверены. Результат проверки скрипта подготовки фиксируется в baseline отдельно.

## Первичные источники

- Повторение scientific oracle: `node tests/check-pico-science.cjs`. Регенерация fixtures: установить mpmath 1.3.0 в отдельное служебное окружение и выполнить `python scripts/generate-pico-science-vectors.py`; metadata фиксирует Python 3.14.4 и seed. Генератор использует 140 decimal digits и собственное IEEE rounding; mpmath не является runtime dependency. Для SDK range отдельный эталон использует точные encoded period constants из закреплённого float_aeabi_rp2040.S / double_aeabi_rp2040.S.
- [SDK runtime / ROM API](https://www.raspberrypi.com/documentation/pico-sdk/runtime.html#group_pico_bootrom) и [RP2040 datasheet](https://datasheets.raspberrypi.com/rp2040/rp2040-datasheet.pdf). Реализация сверялась также с закреплёнными SDK headers / mem_ops_aeabi.S / hardware_flash и boot2 в Arduino-Pico 6.3.2. Оригинальный vendor не изменён.
- [RP2040js](https://github.com/wokwi/rp2040js/tree/8693315df527adce8bf2bc315aeb4a1ca61c02a3), [npm archive](https://registry.npmjs.org/rp2040js/-/rp2040js-1.4.1.tgz) — точный origin/hash в vendor/ORIGIN.md.
- [Arduino-Pico 6.3.2](https://github.com/earlephilhower/arduino-pico/tree/6.3.2), [установка](https://arduino-pico.readthedocs.io/en/latest/install.html).
- [xPack ARM 14.2.1-1.1](https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/releases/tag/v14.2.1-1.1), [исходники](https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/tree/v14.2.1-1.1.1); SHA256 архива зафиксирован в prepare-pico.ps1.
- [Arduino CLI builder cache](https://github.com/arduino/arduino-cli/blob/v1.5.1/internal/arduino/builder/core.go) — источник алгоритма имени кэша; MD5 используется только для имени, целостность проверяется SHA256.
- [BootROM license](https://github.com/raspberrypi/pico-bootrom/blob/00a4a19114195e20fb817bdfbca1165e157eef37/LICENSE.TXT), отдельные условия в заголовках `bootrom/mufplib.S` / `mufplib-double.S`. Оригинальный ROM не поставляется.
