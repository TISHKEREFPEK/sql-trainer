'use strict';
const assert=require('node:assert/strict');
const {RP2040}=require('../electronics/vendor/rp2040js/cjs/index.js');
const {SimulationClock}=require('../electronics/vendor/rp2040js/cjs/clock/simulation-clock.js');
const {installWatchdog,bootTarget,clockDomain}=require('../electronics/pico-watchdog.cjs');
function fixture(){const clock=new SimulationClock(),mcu=new RP2040(clock);mcu.roscFreq=6e6;const resets=[];const watchdog=installWatchdog(mcu,clock,r=>resets.push(r));mcu.peripherals[0x40010].wdsel=0x1fffc;return {clock,mcu,watchdog,resets};}
{
  const f=fixture();f.watchdog.writeUint32(0,0x80000000);assert.equal(f.resets[0].reason,2); // FORCE needs no running tick.
}
{
  const f=fixture();f.watchdog.writeUint32(0x2c,0x206); // 6 MHz ROSC / 6, one-microsecond ticks.
  assert.equal(f.watchdog.readUint32(0x2c)&0x1ff,6);f.watchdog.writeUint32(4,4000);f.watchdog.writeUint32(0,0x40000000);
  f.clock.tick(1e6);assert.equal(f.resets.length,0);f.watchdog.writeUint32(4,4000);f.clock.tick(1e6);assert.equal(f.resets.length,0);
  f.clock.tick(1e6);assert.equal(f.resets.length,1);assert.equal(f.resets[0].reason,1);f.clock.tick(1e9);assert.equal(f.resets.length,1);
}
{
  const f=fixture();f.watchdog.writeUint32(0x2c,0x206);f.watchdog.writeUint32(4,4000);f.watchdog.writeUint32(0,0xc0000000);f.clock.tick(1e9);assert.equal(f.resets.length,1);assert.equal(f.resets[0].reason,2);
}
{
  const f=fixture();assert.throws(()=>f.watchdog.writeUint32(0x2c,0x20c),/tick/);assert.throws(()=>f.watchdog.writeUint32(0,0x40000000),/configured/);
  f.mcu.peripherals[0x40010].wdsel=0;assert.throws(()=>f.watchdog.writeUint32(0,0x80000000),/selective/);
}
{
  const clock=new SimulationClock();clock.tick(7e9);const domain=clockDomain(clock);assert.equal(domain.nanos,0);clock.tick(2e9);assert.equal(domain.micros,2e6);assert.equal(clock.nanos,9e9);
  const mcu=new RP2040(clock),image={vector:0x10000000};mcu.flashView.setUint32(0,0x20042000,true);mcu.flashView.setUint32(4,0x10000101,true);
  assert.deepEqual(bootTarget(mcu,image),{stack:0x20042000,entry:0x10000100});
  const pc=0x20000101,scratch=[0,0,0,0,0xb007c0d3,(pc^-0xb007c0d3)>>>0,0x20041000,pc];
  assert.deepEqual(bootTarget(mcu,image,scratch),{stack:0x20041000,entry:0x20000100});assert.equal(scratch[4],0);
  mcu.flashView.setUint32(4,0xffffffff,true);assert.throws(()=>bootTarget(mcu,image),/boot target/);
}
console.log('Watchdog feeds, one-shot timer/FORCE, tick validation, warm clock and BootROM handoff: passed');
