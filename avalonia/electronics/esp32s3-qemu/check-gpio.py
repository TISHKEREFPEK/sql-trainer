"""Regression checks against the patched QEMU binary and a local test fixture."""

import argparse
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
def open_qemu(executable, bios, flash=None):
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
            args += ["-S", "-serial", "null"]
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


def check_firmware(qtest, log):
    def wait_for(message):
        deadline = time.monotonic() + 8
        output = ""
        while time.monotonic() < deadline:
            if log.exists():
                output = log.read_text(encoding="utf-8", errors="replace")
                if message in output:
                    return
            time.sleep(.03)
        raise RuntimeError(f"Missing UART message {message!r}.\n{output}")

    wait_for("GPIO_READY")
    wait_for("gpio=1 irq=0")
    assert_register(qtest, 0x10, 1 << (39 - 32))
    qtest.pad(12, 0)
    wait_for("gpio=0 irq=1")
    assert_register(qtest, 0x10, 0)
    qtest.pad(12, 1)
    wait_for("gpio=1 irq=2")
    assert_register(qtest, 0x10, 1 << (39 - 32))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qemu", type=Path, required=True)
    parser.add_argument("--bios", type=Path, required=True)
    parser.add_argument("--flash", type=Path, help="4 MiB flash image built from gpio-probe.ino")
    args = parser.parse_args()
    qemu = str(args.qemu.resolve(strict=True))
    bios = args.bios.resolve(strict=True)
    with open_qemu(qemu, bios) as (qtest, _):
        check_registers(qtest)
    print("ESP32-S3 GPIO register and pad checks passed")
    if args.flash:
        flash = args.flash.resolve(strict=True)
        if flash.stat().st_size != 4 * 1024 * 1024:
            parser.error("the test flash image must be 4 MiB")
        with open_qemu(qemu, bios, flash) as (qtest, log):
            check_firmware(qtest, log)
        print("Arduino GPIO read, interrupt and output checks passed")


if __name__ == "__main__":
    main()
