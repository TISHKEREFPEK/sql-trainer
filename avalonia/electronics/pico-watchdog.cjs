'use strict';
// Default Pico SDK watchdog profile. The existing register timer implements the
// RP2040-E1 x2 decrement; request a board reset at an instruction boundary.
function installWatchdog(mcu,clock,request){
  const watchdog=mcu.peripherals[0x40058],read=watchdog.readUint32.bind(watchdog),write=watchdog.writeUint32.bind(watchdog);
  let cycles=0;
  watchdog.readUint32=offset=>offset===0x2c?(read(offset)|cycles):read(offset);
  watchdog.writeUint32=(offset,value)=>{
    if(offset===0x2c){
      // TIMER and SysTick share this generator. Until their variable-rate
      // clock model is connected, fail rather than keep fictitious time.
      if(!(value&0x200)||!(value&0x1ff)||Math.abs(mcu.clocks.refFreq/(value&0x1ff)-1e6)>0.01)
        throw Error('RP2040 alternate or stopped watchdog tick is not supported');
      cycles=value&0x1ff;
    }
    if(offset===0&&(value&0x40000000)&&!(value&0x80000000)&&(!cycles||Math.abs(mcu.clocks.refFreq/cycles-1e6)>0.01))
      throw Error('RP2040 watchdog requires the configured 1 MHz SDK tick');
    write(offset,offset===0&&(value&0x80000000)?value&~0x40000000:value);
  };
  const clocks=mcu.clocks,clockWrite=clocks.writeUint32.bind(clocks);
  clocks.writeUint32=(offset,value)=>{clockWrite(offset,value);if(cycles&&(offset===0x30||offset===0x34)&&Math.abs(clocks.refFreq/cycles-1e6)>0.01)throw Error('RP2040 changing the configured watchdog tick rate is not supported');};
  watchdog.onWatchdogTrigger=()=>{
    const selected=mcu.peripherals[0x40010].wdsel;
    if(selected!==0x1fffc)throw Error('RP2040 selective watchdog reset is not supported');
    // The peripheral's periodic alarm must stop now. Deferring this until
    // the CPU boundary would let clock.tick re-fire forever at the same time.
    watchdog.timer.enable=false;watchdog.alarm.enable=false;
    request({reason:watchdog.reason,scratch:Array.from(watchdog.scratchData)});
  };
  return watchdog;
}
function bootTarget(mcu,image,scratch){
  let stack=mcu.readUint32(image.vector),pc=mcu.readUint32(image.vector+4);
  const magic=0xb007c0d3;
  if(scratch&&scratch[4]===magic&&scratch[5]===((scratch[7]^-magic)>>>0)){
    stack=scratch[6];pc=scratch[7];scratch[4]=0; // documented one-shot BootROM handoff
  }
  const address=(pc&~1)>>>0;
  if(stack<=0x20000000||stack>0x20042000||stack%4||!(pc&1)||
    !(address>=0x10000000&&address<0x10200000||address>=0x20000000&&address<0x20042000))
    throw Error('RP2040 watchdog boot target is outside supported flash/SRAM');
  return {stack,entry:address};
}
// A peripheral clock origin restarts hardware time_us while the circuit's
// physical clock remains monotonic (powered LCD busy intervals must survive).
function clockDomain(clock){
  const origin=clock.nanos;
  return {get nanos(){return clock.nanos-origin;},get micros(){return (clock.nanos-origin)/1000;},
    get frequency(){return clock.frequency;},createAlarm:callback=>clock.createAlarm(callback)};
}
module.exports={installWatchdog,bootTarget,clockDomain};
