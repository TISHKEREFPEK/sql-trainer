'use strict';

const daySeconds = 86400;
const centurySeconds = 3155760000;
const epoch = Date.UTC(2000, 0, 1);
const masks = [0xff, 0x7f, 0x7f, 0x07, 0x3f, 0x1f, 0xff, 0x93];
const bcd = value => (Math.floor(value / 10) << 4) | (value % 10);
const decimal = value => (value & 15) > 9 ? -1 : (value >> 4) * 10 + (value & 15);

class Ds1307 {
  constructor(cpu, hz, config) {
    this.cpu = cpu;
    this.hz = hz;
    this.config = config;
    this.maxFrequency = 100000;
    this.readable = true;
    this.powered = config.powered;
    this.registers = new Uint8Array(64);
    this.reset();
  }

  reset() {
    this.registers.fill(0);
    const date = new Date(epoch + this.config.rtcStart * 1000);
    this.setCalendar(date, date.getUTCDay() + 1);
    this.registers[0] |= 0x80; // CH is set on initial power-up.
    this.anchor = this.cpu.cycles;
    this.pointer = 0;
    this.snapshot = this.registers.slice(0, 7);
  }

  setCalendar(date, weekday) {
    const registers = this.registers;
    const hours = date.getUTCHours();
    registers[0] = bcd(date.getUTCSeconds());
    registers[1] = bcd(date.getUTCMinutes());
    registers[2] = registers[2] & 0x40
      ? 0x40 | (hours >= 12 ? 0x20 : 0) | bcd((hours % 12) || 12)
      : bcd(hours);
    registers[3] = weekday;
    registers[4] = bcd(date.getUTCDate());
    registers[5] = bcd(date.getUTCMonth() + 1);
    registers[6] = bcd(date.getUTCFullYear() - 2000);
  }

  advance() {
    const now = this.cpu.cycles;
    const registers = this.registers;
    if ((!this.powered && !this.config.backup) || registers[0] & 0x80) {
      this.anchor = now;
      return;
    }
    const elapsed = Math.floor((now - this.anchor) / this.hz);
    if (elapsed <= 0) return;
    this.anchor += elapsed * this.hz;

    const seconds = decimal(registers[0]);
    const minutes = decimal(registers[1]);
    const twelveHour = (registers[2] & 0x40) !== 0;
    let hours = decimal(registers[2] & (twelveHour ? 0x1f : 0x3f));
    if (twelveHour) {
      if (hours < 1 || hours > 12) return;
      hours = hours % 12 + (registers[2] & 0x20 ? 12 : 0);
    }
    const weekday = registers[3];
    const day = decimal(registers[4]), month = decimal(registers[5]), year = decimal(registers[6]);
    if (seconds < 0 || seconds > 59 || minutes < 0 || minutes > 59 || hours < 0 || hours > 23 ||
        weekday < 1 || weekday > 7 || day < 1 || month < 1 || month > 12 || year < 0) return;
    const base = Date.UTC(2000 + year, month - 1, day, hours, minutes, seconds);
    if (new Date(base).getUTCDate() !== day) return;
    const current = (base - epoch) / 1000;
    const next = current + elapsed;
    const daysAdded = Math.floor(next / daySeconds) - Math.floor(current / daySeconds);
    this.setCalendar(new Date(epoch + (next % centurySeconds) * 1000), (weekday - 1 + daysAdded) % 7 + 1);
  }

  update(config) {
    this.advance();
    const lostPower = (this.powered || this.config.backup) && !config.powered && !config.backup;
    const newStart = config.rtcStart !== this.config.rtcStart;
    this.config = config;
    this.powered = config.powered;
    if (lostPower || newStart) this.reset();
  }

  begin(write) {
    this.advance();
    this.setPointer = write;
    if (!write) this.snapshot = this.registers.slice(0, 7);
  }

  write(value) {
    if (this.setPointer) {
      this.setPointer = false;
      this.pointer = value & 63;
      return;
    }
    this.advance();
    this.registers[this.pointer] = value & (masks[this.pointer] ?? 0xff);
    if (this.pointer === 0) this.anchor = this.cpu.cycles;
    this.incrementPointer();
  }

  read() {
    const value = this.pointer < 7 ? this.snapshot[this.pointer] : this.registers[this.pointer];
    this.incrementPointer();
    return value;
  }

  incrementPointer() { this.pointer = (this.pointer + 1) & 63; }
}

module.exports = { Ds1307, centurySeconds };
