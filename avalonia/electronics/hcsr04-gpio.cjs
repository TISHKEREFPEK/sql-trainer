'use strict';
const {Hcsr04Bus}=require('./hcsr04.cjs');
const {setPicoInput}=require('./pico-gpio-input.cjs');
function installAvrHcsr04(cpu,board,pinMap,ports){
  let bus,busy=false;
  const output=i=>!!(cpu.data[pinMap[i].port.portConfig.DDR]&(1<<pinMap[i].bit));
  const level=i=>{const p=pinMap[i];return !!((output(i)?p.port.lastValue:cpu.data[p.port.portConfig.PIN])&(1<<p.bit));};
  function sync(){if(busy||!bus)return;busy=true;try{for(const d of bus.devices.values()){
    bus.observe(d.config.trig,level(d.config.trig));
    if(!d.config.powered||!d.config.connected)continue;
    for(const e of d.config.echoes){if(output(e.pin))throw Error('HC-SR04: GPIO Echo должен быть входом, а не выходом.');pinMap[e.pin].port.setPin(pinMap[e.pin].bit,d.high?e.high:e.low);}
  }}finally{busy=false;}}
  bus=new Hcsr04Bus(()=>cpu.cycles*1e9/board.clockHz,(at,callback)=>{cpu.addClockEvent(callback,Math.max(1,Math.ceil(at*board.clockHz/1e9-cpu.cycles)));return ()=>cpu.clearClockEvent(callback);},sync,pinMap.length);
  for(const port of Object.values(ports))port.addListener(sync);
  bus.sync=sync;return bus;
}
function installPicoHcsr04(mcu,clock,devices){
  let bus,busy=false;
  const output=i=>mcu.gpio[i].outputEnable&&!mcu.gpio[i].outputDisable;
  const level=i=>{const p=mcu.gpio[i];return output(i)?p.outputValue:p.pullupEnabled||p.rawInputValue;};
  function sync(){if(busy||!bus)return;busy=true;try{for(const d of bus.devices.values()){
    if(mcu.gpio[d.config.trig].functionSelect===5)bus.observe(d.config.trig,level(d.config.trig));
    if(!d.config.powered||!d.config.connected)continue;
    for(const e of d.config.echoes){if(output(e.pin))throw Error('HC-SR04: GPIO Echo должен быть входом, а не выходом.');setPicoInput(mcu.gpio[e.pin],d.high?e.high:e.low);}
  }}finally{busy=false;}}
  bus=new Hcsr04Bus(()=>clock.nanos,(at,callback)=>{const alarm=clock.createAlarm(callback);alarm.schedule(Math.max(1,at-clock.nanos));return ()=>alarm.cancel();},sync,30,devices);
  for(const p of mcu.gpio){const previous=p.checkForUpdates.bind(p);p.checkForUpdates=()=>{previous();sync();};}
  const read=mcu.sio.readUint32.bind(mcu.sio);
  mcu.sio.readUint32=offset=>{
    let value=read(offset);if(offset!==4)return value;
    for(const d of bus.devices.values())for(const e of d.config.echoes){const p=mcu.gpio[e.pin];if(p.functionSelect!==5)continue;let high=bus.input(e.pin);if(high===null)continue;
      high=high&&p.inputEnable;const override=p.inputOverride;high=override===1?!high:override===2?false:override===3?true:high;value=high?value|(1<<e.pin):value&~(1<<e.pin);
    }return value>>>0;
  };
  bus.sync=sync;return bus;
}
module.exports={installAvrHcsr04,installPicoHcsr04};
