'use strict';

const readline = require('node:readline');
const { I2cBus } = require('./i2c-bus.cjs');

const clock = { cycles: 0, addClockEvent(callback, cycles) { this.cycles += cycles; callback(); } };
const devices = new Map();
const controllers = Array.from({ length: 2 }, (_, controller) => {
  const link = {
    frequency: 100000,
    get sclFrequency() { return this.frequency; },
    completeStart() {},
    completeStop() {},
    completeConnect(ack) { this.ack = ack; },
    completeWrite(ack) { this.ack = ack; },
    completeRead(value) { this.read = value; }
  };
  const state = { link, epoch: null, sda: -1, scl: -1, addressExpected: false };
  state.bus = new I2cBus(clock, 1e9, link, {
    devices, accepts: config => state.sda >= 0 && state.scl >= 0 &&
      state.sda !== state.scl && config.sda === state.sda && config.scl === state.scl
  });
  state.controller = controller;
  return state;
});

function transfer(request) {
  const state = controllers[request.port];
  const { bus, link } = state;
  const { eventHandler } = link;
  clock.cycles = Math.max(clock.cycles, request.timeNs);
  link.frequency = request.frequency;
  if (state.epoch !== request.epoch || state.sda !== request.sda || state.scl !== request.scl) {
    if (bus.packet) bus.packet.ack = false;
    bus.flush();
    bus.active = [];
    state.addressExpected = false;
    state.epoch = request.epoch;
    state.sda = request.sda;
    state.scl = request.scl;
  }
  let event = 0;
  let commands = 0;
  let tx = 0;
  const rx = [];
  for (const command of request.commands) {
    const count = command & 255;
    const operation = command >> 11 & 7;
    commands++;
    switch (operation) {
      case 6: // RESTART
        eventHandler.start();
        state.addressExpected = true;
        break;
      case 1: // WRITE (address or data)
        if (count > request.tx.length - tx) { event = 256; break; }
        for (let i = 0; i < count && !event; i++) {
          const value = request.tx[tx++];
          if (state.addressExpected) {
            eventHandler.connectToSlave(value >> 1, !(value & 1));
            Object.assign(bus.packet, { controller: state.controller, software: false,
              sda: state.sda < 0 || state.scl < 0 ? null : state.sda,
              scl: state.sda < 0 || state.scl < 0 ? null : state.scl });
            state.addressExpected = false;
          } else if (bus.packet && !bus.packet.read) {
            eventHandler.writeByte(value);
          } else {
            event = 256;
            break;
          }
          if ((command & 256) && link.ack === !!(command & 512)) event = 1024;
        }
        break;
      case 3: // READ
        if (count > request.rxFree - rx.length) { event = 260; break; }
        if (state.addressExpected || !bus.packet?.read || !bus.packet.ack) { event = 256; break; }
        for (let i = 0; i < count; i++) {
          eventHandler.readByte();
          rx.push(link.read);
        }
        break;
      case 2: event = 128; break; // STOP
      case 4: event = 8; break;   // END, keep the target for the next chunk
      default: event = 256; break;
    }
    if (event) break;
  }
  if (event !== 8) {
    if (event !== 128 && bus.packet) bus.packet.ack = false;
    eventHandler.stop();
    state.addressExpected = false;
  }
  return { event: event || 256, commands, tx, rx,
    busy: event === 8 && (state.addressExpected || !!bus.packet?.ack) };
}

function frame() {
  const result = controllers[0].bus.frame();
  const second = controllers[1].bus;
  result.i2c.push(...second.trace);
  second.trace = [];
  second.traceBytes = 0;
  return result;
}

const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', line => {
  try {
    if (line.length > 65536) throw new Error('I2C: слишком большой запрос.');
    const request = JSON.parse(line);
    let result;
    if (request.devices) {
      controllers[0].bus.update(request.devices);
      result = {};
    } else if (request.frame) {
      if (request.timeNs !== undefined) clock.cycles = Math.max(clock.cycles, request.timeNs);
      result = frame();
    } else {
      result = transfer(request);
    }
    process.stdout.write(JSON.stringify(result) + '\n');
  } catch (error) {
    process.stdout.write(JSON.stringify({ error: error.message }) + '\n');
  }
});
