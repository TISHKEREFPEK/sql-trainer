"""Regression checks against the patched QEMU binary and a local test fixture."""

import argparse
from base64 import b64decode, b64encode
from contextlib import contextmanager
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time


GPIO = 0x60004000
IOMUX = 0x60009000
SENS = 0x60008800
DEVICE = "/machine/soc/gpio"
GPIO_INPUT = 0x1200
PULL_UP = 1 << 8
OPEN_DRAIN = 1 << 2
IRQ_ENABLE = 1 << 13
IRQ_RISING = 1 << 7
IRQ_HIGH = 5 << 7


class QTest:
    def __init__(self, process):
        self.process = process
        self.lines = queue.Queue()
        self.edges = []
        self.reader = threading.Thread(target=self.read_responses, daemon=True)
        self.reader.start()

    def read_responses(self):
        for line in self.process.stdout:
            self.lines.put(line.strip())
        self.lines.put(None)

    def command(self, command):
        self.process.stdin.write(command + "\n")
        self.process.stdin.flush()
        while True:
            response = self.lines.get(timeout=5)
            if response is None:
                raise RuntimeError("QEMU closed qtest before replying")
            if response.startswith("IRQ "):
                self.edges.append(response)
                continue
            if response != "OK" and not response.startswith("OK "):
                raise RuntimeError(f"{command}: {response}")
            return response[2:].strip()

    def read(self, address):
        return int(self.command(f"readl {address:#x}"), 0)

    def write(self, address, value):
        self.command(f"writel {address:#x} {value:#x}")

    def pad(self, pin, level):
        self.command(f"set_irq_in {DEVICE} pad-in {pin} {level}")


@contextmanager
def open_qemu(executable, bios, flash=None, devices=(), host_i2c=False):
    with tempfile.TemporaryDirectory(prefix="classroom-s3-qtest-") as folder:
        root = Path(folder)
        log = root / "uart0.log"
        rom = "esp32s3_rev0_rom.bin"
        shutil.copy2(bios / rom, root / rom)
        args = [
            executable, "-M", "esp32s3", "-L", ".", "-display", "none",
            "-monitor", "none", "-nic", "none", "-no-reboot",
            "-accel", "tcg,tb-size=32,thread=single", "-qtest", "stdio",
        ]
        if flash:
            (root / "flash.bin").write_bytes(flash.read_bytes())
            args += [
                "-drive", "file=flash.bin,if=mtd,format=raw",
                "-serial", "file:uart0.log",
            ]
        else:
            args.append("-S")
            for port in range(3):
                name = f"classroom-uart{port}"
                args += ["-chardev", f"ringbuf,id={name},size=16", "-serial", f"chardev:{name}"]
        for device in devices:
            args += ["-device", device]
        if host_i2c:
            args += ["-global", "driver=esp32s3.i2c,property=classroom-host,value=on"]
        with (root / "qemu.log").open("w+", encoding="utf-8", errors="replace") as diagnostics:
            process = subprocess.Popen(
                args, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=diagnostics, text=True, encoding="utf-8",
            )
            try:
                yield QTest(process), log
            except Exception as error:
                diagnostics.seek(0, 2)
                end = diagnostics.tell()
                diagnostics.seek(max(0, end - 4000))
                if hasattr(error, "add_note"):
                    error.add_note(diagnostics.read())
                raise
            finally:
                process.terminate()
                process.wait(timeout=5)
                process.stdin.close()
                process.stdout.close()


def assert_register(qtest, offset, expected):
    actual = qtest.read(GPIO + offset)
    assert actual == expected, f"GPIO+{offset:#x}: {actual:#x}, expected {expected:#x}"


def check_uart(qtest):
    assert qtest.command("classroom_uart_version") == "1"
    for port, fifo in enumerate((0x60000000, 0x60010000, 0x6002E000)):
        sent = bytes(range(40))
        for value in sent:
            qtest.write(fifo, value)
        dropped, data = qtest.command(f"classroom_uart_read {port}").split()
        assert (int(dropped), b64decode(data)) == (24, sent[-16:])
        assert qtest.command(f"classroom_uart_read {port}") == "0 -"

        data = bytes(range(128))
        encoded = b64encode(data).decode("ascii")
        assert qtest.command(f"classroom_uart_write {port} {encoded}") == "128"
        assert qtest.command(f"classroom_uart_write {port} eA==") == "0"
        assert bytes(qtest.read(fifo) for _ in data) == data


def check_adc(qtest):
    assert qtest.command("classroom_adc_version") == "1"
    for unit, (ctrl, attenuation) in enumerate(((0x0C, 0x14), (0x30, 0x38))):
        address = SENS + ctrl
        start_force, start, done = 1 << 18, 1 << 17, 1 << 16
        pad_force, channel = 1 << 31, 9
        config = pad_force | start_force | (1 << (19 + channel))
        pad = unit * 10 + channel
        qtest.command(f"set_irq_in /machine/soc/adc input-uv {pad} 1550000")
        assert qtest.read(SENS + attenuation) == 0xFFFFFFFF

        def sample(settings):
            qtest.write(address, settings)
            assert qtest.read(address) & done == 0
            qtest.write(address, settings | start)
            result = qtest.read(address)
            assert result & done
            return result & 0xFFFF

        assert sample(config) == 2048
        qtest.write(SENS + attenuation, 0)
        assert sample(config) == 4095
        qtest.command(f"set_irq_in /machine/soc/adc input-uv {pad} 475000")
        assert sample(config) == 2048
        assert sample(pad_force | start_force) == 0


def check_empty_i2c_buses(qtest):
    for base in (0x60013000, 0x60027000):
        start_master, done = (1 << 4) | (1 << 5), 1 << 31
        nack, timeout = 1 << 10, 1 << 8
        qtest.write(base + 0x28, nack | timeout)
        qtest.write(base + 0x58, 6 << 11)
        qtest.write(base + 0x5C, (1 << 11) | (1 << 8) | 1)
        qtest.write(base + 0x1C, 0x50 << 1)
        qtest.write(base + 4, start_master)
        assert qtest.read(base + 0x2C) == nack
        assert qtest.read(base + 0x5C) & done
        assert not qtest.read(base + 8) & (1 << 4)
        qtest.write(base + 0x24, nack)
        assert not qtest.read(base + 0x2C)

        # The second command needs a byte that has not been placed in the FIFO.
        qtest.write(base + 4, start_master)
        assert qtest.read(base + 0x2C) == timeout


def check_i2c_pin_selection(qtest):
    for pin, signal in ((8, 90), (9, 89), (15, 92), (16, 91)):
        matrix_input = qtest.read(GPIO + 0x154 + signal * 4)
        matrix_output = qtest.read(GPIO + 0x554 + pin * 4)
        mux = qtest.read(IOMUX + (pin + 1) * 4)
        assert (matrix_input & 0xFF) == (0x80 | pin)
        assert (matrix_output & 0x1FF) == signal
        assert ((mux >> 12) & 7) == 1


def check_i2c_host_pause(qtest, log):
    assert qtest.command("classroom_i2c_version") == "1"
    for port, pins in enumerate(((8, 9), (15, 16))):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            wire = qtest.command(f"classroom_i2c_poll {port}")
            if wire != "-":
                break
            time.sleep(.01)
        else:
            raise RuntimeError(f"I2C{port} did not send a request to the host")
        seq, _, _, sda, scl, _, _, _, tx = wire.split(":")
        assert (int(sda), int(scl)) == pins and tx == "a2"
        instant = qtest.command("classroom_i2c_clock")
        time.sleep(.1)
        assert qtest.command("classroom_i2c_clock") == instant
        qtest.command(f"classroom_i2c_reply {port} {seq}:1024:2:1:0:")
        wait_uart(log, f"I2C{port}_MISSING=2")


def check_registers(qtest):
    low_pin = 2
    high_pin = 39
    low = 1 << low_pin
    high = 1 << (high_pin - 32)
    pin_config = GPIO + 0x74 + 4 * low_pin
    mux_low = IOMUX + 4 * (low_pin + 1)
    mux_high = IOMUX + 4 * (high_pin + 1)
    assert_register(qtest, 0x38, 4)
    qtest.command(f"irq_intercept_out {DEVICE} pad-level")
    qtest.write(mux_low, GPIO_INPUT | PULL_UP)
    qtest.write(mux_high, GPIO_INPUT | PULL_UP)
    assert_register(qtest, 0x3C, low)
    assert_register(qtest, 0x40, high)
    assert f"IRQ raise {high_pin}" in qtest.edges

    qtest.write(GPIO + 0x24, low)
    qtest.write(GPIO + 0x30, high)
    assert_register(qtest, 0x3C, 0)
    assert_register(qtest, 0x40, 0)
    qtest.write(GPIO + 0x08, low)
    qtest.write(GPIO + 0x14, high)
    assert_register(qtest, 0x04, low)
    assert_register(qtest, 0x10, high)
    assert_register(qtest, 0x40, high)
    qtest.write(GPIO + 0x18, high)
    assert_register(qtest, 0x40, 0)
    assert f"IRQ lower {high_pin}" in qtest.edges

    qtest.write(pin_config, OPEN_DRAIN)
    qtest.pad(low_pin, 0)
    assert_register(qtest, 0x3C, 0)
    qtest.pad(low_pin, -1)
    assert_register(qtest, 0x3C, low)
    qtest.write(GPIO + 0x0C, low)
    qtest.pad(low_pin, 1)
    assert_register(qtest, 0x3C, 0)

    qtest.write(GPIO + 0x28, low)
    qtest.pad(low_pin, 0)
    qtest.write(pin_config, IRQ_ENABLE | IRQ_RISING)
    qtest.pad(low_pin, 1)
    assert_register(qtest, 0x44, low)
    assert_register(qtest, 0x5C, low)
    qtest.write(GPIO + 0x4C, low)
    assert_register(qtest, 0x44, 0)
    qtest.pad(low_pin, 0)
    assert_register(qtest, 0x44, 0)

    qtest.write(pin_config, IRQ_ENABLE | IRQ_HIGH)
    qtest.pad(low_pin, 1)
    qtest.write(GPIO + 0x4C, low)
    assert_register(qtest, 0x44, low)
    qtest.pad(low_pin, 0)
    qtest.write(GPIO + 0x4C, low)
    assert_register(qtest, 0x44, 0)
    qtest.write(GPIO + 0x08, 1 << 22)
    assert_register(qtest, 0x04, 0)


def wait_uart(log, message):
    deadline = time.monotonic() + 8
    output = ""
    while time.monotonic() < deadline:
        if log.exists():
            output = log.read_text(encoding="utf-8", errors="replace")
            if message in output:
                return output
        time.sleep(.03)
    raise RuntimeError(f"Missing UART message {message!r}.\n{output}")


def check_firmware(qtest, log):
    wait_uart(log, "GPIO_READY")
    wait_uart(log, "gpio=1 irq=0")
    assert_register(qtest, 0x10, 1 << (39 - 32))
    qtest.pad(12, 0)
    wait_uart(log, "gpio=0 irq=1")
    assert_register(qtest, 0x10, 0)
    qtest.pad(12, 1)
    wait_uart(log, "gpio=1 irq=2")
    assert_register(qtest, 0x10, 1 << (39 - 32))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qemu", type=Path, required=True)
    parser.add_argument("--bios", type=Path, required=True)
    parser.add_argument("--flash", type=Path, help="4 MiB flash image built from gpio-probe.ino")
    parser.add_argument("--i2c-flash", type=Path, help="4 MiB flash image built from i2c-probe.ino")
    args = parser.parse_args()
    qemu = str(args.qemu.resolve(strict=True))
    bios = args.bios.resolve(strict=True)
    for image in (args.flash, args.i2c_flash):
        if image and image.stat().st_size != 4 * 1024 * 1024:
            parser.error("the test flash image must be 4 MiB")
    with open_qemu(qemu, bios) as (qtest, _):
        check_registers(qtest)
        print("ESP32-S3 GPIO register and pad checks passed")
        check_uart(qtest)
        print("ESP32-S3 UART RX capacity and TX overflow checks passed")
        check_adc(qtest)
        print("ESP32-S3 RTC ADC channel, start/done and attenuation checks passed")
        check_empty_i2c_buses(qtest)
        print("ESP32-S3 I2C FIFO limits and masked interrupts passed")
    if args.flash:
        with open_qemu(qemu, bios, args.flash) as (qtest, log):
            check_firmware(qtest, log)
        print("Arduino GPIO read, interrupt and output checks passed")
    if args.i2c_flash:
        eeproms = [f"at24c-eeprom,bus=esp32s3-i2c{port},address=0x50,rom-size=8192" for port in range(2)]
        with open_qemu(qemu, bios, args.i2c_flash, eeproms) as (qtest, log):
            output = wait_uart(log, "I2C_PROBE_OK")
            for port in range(2):
                assert f"I2C{port}_MISSING=2" in output
                assert f"I2C{port}_RESTART_OK=73" in output
            check_i2c_pin_selection(qtest)
        print("Arduino Wire and Wire1 verified independent EEPROMs, NACK, repeated START and pin selection")
        with open_qemu(qemu, bios, args.i2c_flash, host_i2c=True) as (qtest, log):
            check_i2c_host_pause(qtest, log)
        print("ESP32-S3 I2C host exchange pauses virtual time, then resumes both controllers")


if __name__ == "__main__":
    main()
