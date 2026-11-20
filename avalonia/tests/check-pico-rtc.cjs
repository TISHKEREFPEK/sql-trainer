'use strict';
const assert=require('node:assert/strict');
const {RP2040}=require('../electronics/vendor/rp2040js/cjs/index.js');
const {SimulationClock}=require('../electronics/vendor/rp2040js/cjs/clock/simulation-clock.js');
const {installRtc}=require('../electronics/pico-rtc.cjs');
function fixture(){
  const clock=new SimulationClock(),mcu=new RP2040(clock),rtc=installRtc(mcu,clock);
  mcu.clocks.writeUint32(0x6c,0x860);mcu.clocks.writeUint32(0x70,256*256);rtc.writeUint32(0,46874);
  let irq=false;const original=mcu.setInterrupt.bind(mcu);mcu.setInterrupt=(id,value)=>{if(id===25)irq=value;original(id,value);};
  const set=(year,month,day,dotw,hour=23,min=59,sec=59,force=false)=>{
    rtc.writeUint32(12,0);rtc.writeUint32(4,year<<12|month<<8|day);rtc.writeUint32(8,dotw<<24|hour<<16|min<<8|sec);
    rtc.writeUint32(12,16|(force?256:0));rtc.writeUint32(12,1|(force?256:0));
  };
  const get=()=>{const t=rtc.readUint32(28),d=rtc.readUint32(24);return [d>>>12&4095,d>>>8&15,d&31,t>>>24&7,t>>>16&31,t>>>8&63,t&63];};
  return {clock,mcu,rtc,set,get,get irq(){return irq;}};
}
{
  const f=fixture();f.set(2024,2,28,4);f.clock.tick(1e9);assert.deepEqual(f.get(),[2024,2,29,5,0,0,0]);
  f.set(2100,2,28,1);f.clock.tick(1e9);assert.deepEqual(f.get(),[2100,2,29,2,0,0,0]); // Hardware /4 rule, not Gregorian Date.
  f.set(2100,2,28,1,23,59,59,true);f.clock.tick(1e9);assert.deepEqual(f.get(),[2100,3,1,2,0,0,0]);
  f.set(99,12,31,6);f.clock.tick(1e9);assert.deepEqual(f.get(),[100,1,1,0,0,0,0]);
  f.set(4095,12,31,5);f.clock.tick(1e9);assert.deepEqual(f.get(),[0,1,1,6,0,0,0]);
}
{
  const f=fixture();f.set(2026,3,29,6,1,59,59);f.clock.tick(1e9);assert.deepEqual(f.get(),[2026,3,29,6,2,0,0]);
  f.clock.tick(250e6);f.rtc.writeUint32(12,0);f.clock.tick(3e9);assert.equal(f.get()[6],0);
  f.rtc.writeUint32(12,1);f.clock.tick(750e6);assert.equal(f.get()[6],1);
  f.clock.tick(500e6);f.mcu.clocks.writeUint32(0x6c,0x60);f.clock.tick(5e9);assert.equal(f.get()[6],1);
  f.mcu.clocks.writeUint32(0x6c,0x860);f.clock.tick(500e6);assert.equal(f.get()[6],2);
}
{
  const f=fixture();f.set(2026,12,31,2);f.rtc.readUint32(28);f.clock.tick(1e9);
  assert.equal(f.rtc.readUint32(24),2026<<12|12<<8|31);assert.deepEqual(f.get(),[2027,1,1,3,0,0,0]);
  f.rtc.writeUint32(28,0xffffffff);f.rtc.writeUint32(24,0xffffffff);assert.equal(f.get()[0],2027);
}
{
  const f=fixture();f.set(2024,2,28,4);
  f.rtc.writeUint32(16,0x17000000|2024<<12|2<<8|29);f.rtc.writeUint32(20,(0xf0000000|5<<24)>>>0);
  f.clock.tick(1e9);assert.equal(f.rtc.readUint32(32),1);assert.equal(f.rtc.readUint32(44),0);assert.equal(f.irq,false);
  f.rtc.writeUint32(36,1);assert.equal(f.irq,true);f.rtc.writeUint32(32,0);assert.equal(f.irq,true); // INTR is read-only.
  f.rtc.writeUint32Atomic(16,0x10000000,3);assert.equal(f.rtc.readUint32(32),0);assert.equal(f.irq,false);
  f.rtc.writeUint32(36,0);f.rtc.writeUint32(40,1);assert.equal(f.irq,true);assert.equal(f.rtc.readUint32(44),1);
  f.rtc.writeUint32(40,0);assert.equal(f.irq,false);
  f.rtc.writeUint32(16,0x10000000);f.rtc.writeUint32(20,0x10000002);f.clock.tick(2e9);assert.equal(f.irq,false); // Still masked.
  assert.equal(f.rtc.readUint32(32),1);f.mcu.peripherals[0x4000c].writeUint32Atomic(0,0x8000,2);
  assert.equal(f.rtc.readUint32(12),0);assert.equal(f.rtc.readUint32(32),0);
}
{
  const f=fixture();f.mcu.clocks.writeUint32(0x70,0x12345678);assert.equal(f.mcu.clocks.readUint32(0x70),0x12345678);
  f.mcu.clocks.writeUint32(0x70,256*256);f.set(2026,1,1,4,0,0,0);f.rtc.writeUint32(0,23436);
  f.clock.tick(500e6);assert.equal(f.get()[6],1); // Physical divider affects time, not a hard-coded second.
  assert.throws(()=>f.set(2026,2,30,4),/invalid RTC/);
}
{
  const f=fixture();f.mcu.clocks.writeUint32(0x70,127); // INT=0 means 65536, not 127/256.
  f.rtc.writeUint32(0,0);f.set(2026,1,1,4,0,0,0);f.clock.tick(65536e9/12e6);assert.equal(f.get()[6],1);
  f.rtc.writeUint32(12,0);f.mcu.clocks.writeUint32(0x70,256);assert.throws(()=>f.rtc.writeUint32(12,1),/reference must/);
}
for(const wrong of [0,1,2,3,4,5,6]){
  const f=fixture();f.set(2024,2,28,4);
  const expected=[2024,2,29,5,0,0,0];expected[wrong]++;
  const [year,month,day,dotw,hour,min,sec]=expected;
  f.rtc.writeUint32(16,0x17000000|year<<12|month<<8|day);
  f.rtc.writeUint32(20,(0xf0000000|dotw<<24|hour<<16|min<<8|sec)>>>0);
  f.clock.tick(1e9);assert.equal(f.rtc.readUint32(32),0,'Alarm must reject mismatched calendar field '+wrong);
}
console.log('RTC calendar, independent day-of-week, leap control, latching, freeze, clock divider, IRQ and reset: passed');
