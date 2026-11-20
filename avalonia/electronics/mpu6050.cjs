'use strict';

// Register layout and ranges: TDK InvenSense RM-MPU-6000A-00, rev. 4.2.
const RATE = 0x19, CONFIG = 0x1a, GYRO = 0x1b, ACCEL = 0x1c;
const INT_PIN = 0x37, INT_ENABLE = 0x38, STATUS = 0x3a, OUTPUT = 0x3b;
const POWER = 0x6b, STANDBY = 0x6c, IDENTITY = 0x75;
const masks = new Map([
  [RATE, 0xff], [CONFIG, 7], [GYRO, 0x18], [ACCEL, 0x18],
  [INT_PIN, 0x10], [INT_ENABLE, 1], [POWER, 0x4f], [STANDBY, 0x3f]
]);
const fields = ['ax', 'ay', 'az', 'temperature', 'gx', 'gy', 'gz'];
const acceleration = [16384, 8192, 4096, 2048];
const rotation = [131, 65.5, 32.8, 16.4];

function validMotion(motion) {
  return !!motion && fields.every((field, index) => {
    const value = motion[field];
    const limit = index < 3 ? 16 : 2000;
    return Number.isFinite(value) && (field === 'temperature'
      ? value >= -40 && value <= 85 : Math.abs(value) <= limit);
  });
}

class Mpu6050 {
  constructor(cpu, hz, config) {
    this.cpu = cpu;
    this.hz = hz;
    this.config = config;
    this.powered = false;
    this.readable = true;
    this.maxFrequency = 400000;
    this.reset();
  }

  reset() {
    this.registers = new Uint8Array(128);
    this.registers[POWER] = 0x40;
    this.registers[IDENTITY] = 0x68;
    this.pointer = 0;
    this.snapshot = null;
    this.lastSample = this.cpu.cycles;
    this.lastAcceleration = this.lastSample;
  }

  update(config) {
    if (this.powered) this.sample();
    if (config.powered && !this.powered) this.reset();
    this.config = config;
    this.powered = config.powered;
  }

  sample() {
    const r = this.registers;
    if (!this.powered || (r[POWER] & 0x40)) return;
    const filter = r[CONFIG] & 7;
    const interval = (1 + r[RATE]) * this.hz / (filter === 0 || filter === 7 ? 8000 : 1000);
    const elapsed = this.cpu.cycles - this.lastSample;
    if (elapsed < interval) return;
    this.lastSample += Math.floor(elapsed / interval) * interval;
    const accelInterval = this.hz / 1000;
    const accelSamples = Math.floor((this.lastSample - this.lastAcceleration) / accelInterval);
    this.lastAcceleration += accelSamples * accelInterval;
    const aScale = acceleration[r[ACCEL] >> 3];
    const gScale = rotation[r[GYRO] >> 3];
    const motion = this.config.motion;
    const values = [
      motion.ax * aScale, motion.ay * aScale, motion.az * aScale, (motion.temperature - 36.53) * 340,
      motion.gx * gScale, motion.gy * gScale, motion.gz * gScale
    ];
    const standBy = [32, 16, 8, 0, 4, 2, 1];
    for (let i = 0; i < values.length; i++) {
      if ((i < 3 && !accelSamples) || (r[STANDBY] & standBy[i]) || (i === 3 && (r[POWER] & 8))) continue;
      const raw = Math.max(-32768, Math.min(32767, Math.round(values[i])));
      r[OUTPUT + 2 * i] = raw >> 8;
      r[OUTPUT + 2 * i + 1] = raw & 255;
    }
    r[STATUS] |= 1;
  }

  begin(write) {
    this.sample();
    this.firstWrite = write;
    this.snapshot = write ? null : this.registers.slice(OUTPUT, OUTPUT + 14);
  }

  write(value) {
    if (this.firstWrite) {
      this.pointer = value & 127;
      this.firstWrite = false;
      return;
    }
    this.sample();
    const address = this.nextRegister();
    if (address === POWER && (value & 0x80)) {
      this.reset();
      return;
    }
    if ((address === 0x23 && value) || (address === 0x6a && (value & 0xe0)) ||
        (address === POWER && (value & 0x20)))
      throw Error('MPU-6050: FIFO, DMP, вспомогательная шина I²C и циклический режим пока не моделируются.');
    const mask = masks.get(address);
    if (mask === undefined) return;
    if (address === POWER && ((this.registers[address] ^ value) & 0x40))
      this.lastAcceleration = this.lastSample = this.cpu.cycles;
    this.registers[address] = value & mask;
  }

  read() {
    this.sample();
    const address = this.nextRegister();
    const result = address >= OUTPUT && address < OUTPUT + 14
      ? this.snapshot[address - OUTPUT] : this.registers[address];
    if (address === STATUS || (this.registers[INT_PIN] & 0x10)) this.registers[STATUS] = 0;
    return result;
  }

  nextRegister() {
    const address = this.pointer;
    this.pointer = (this.pointer + 1) & 127;
    return address;
  }
}

module.exports = { Mpu6050, validMotion };
