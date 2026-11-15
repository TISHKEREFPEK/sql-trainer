'use strict';
const {Ds18b20Bus}=require('./ds18b20.cjs');
const {setPicoInput}=require('./pico-gpio-input.cjs');
function installAvrDs18b20(cpu,board,pinMap,ports){
  const master=i=>{const p=pinMap[i],mask=1<<p.bit;return cpu.data[p.port.portConfig.DDR]&mask?!!(p.port.lastValue&mask):null;};
  const pullup=i=>{const p=pinMap[i];return !!(cpu.data[p.port.portConfig.PORT]&(1<<p.bit));};
  let busy=false,bus;
  function sync(){if(busy||!bus)return;busy=true;try{for(const i of bus.lines.keys()){
    bus.observe(i,master(i),pullup(i));const value=bus.input(i,master(i),pullup(i));
    if(value!==null)pinMap[i].port.setPin(pinMap[i].bit,value);
  }}finally{busy=false;}}
  bus=new Ds18b20Bus(()=>cpu.cycles*1e9/board.clockHz,(at,callback)=>{cpu.addClockEvent(callback,Math.max(1,Math.ceil(at*board.clockHz/1e9-cpu.cycles)));return ()=>cpu.clearClockEvent(callback);},sync,pinMap.length);
  for(const port of Object.values(ports))port.addListener(sync);
  bus.sync=sync;return bus;
}
function installPicoDs18b20(mcu,clock,devices,writes){
  let bus,busy=false;
  const master=i=>{const p=mcu.gpio[i];return p.functionSelect===5&&p.outputEnable&&!p.outputDisable?p.outputValue:null;};
  const pullup=i=>mcu.gpio[i].pullupEnabled;
  function sync(){if(busy||!bus)return;busy=true;try{for(const i of bus.lines.keys()){
    if(mcu.gpio[i].functionSelect!==5)continue;
    bus.observe(i,master(i),pullup(i));const value=bus.input(i,master(i),pullup(i));if(value!==null)setPicoInput(mcu.gpio[i],value);
  }}finally{busy=false;}}
  bus=new Ds18b20Bus(()=>clock.nanos,(at,callback)=>{const alarm=clock.createAlarm(callback);alarm.schedule(Math.max(1,at-clock.nanos));return ()=>alarm.cancel();},sync,30,devices,writes);
  for(const p of mcu.gpio){const previous=p.checkForUpdates.bind(p);p.checkForUpdates=()=>{previous();if(bus.lines.has(p.index))sync();};}
  const read=mcu.sio.readUint32.bind(mcu.sio);
  mcu.sio.readUint32=offset=>{
    let value=read(offset);if(offset!==4)return value;
    for(const i of bus.lines.keys()){
      const p=mcu.gpio[i];if(p.functionSelect!==5)continue;
      let high=bus.input(i,master(i),pullup(i));if(high===null)continue;
      high=high&&p.inputEnable;const override=p.inputOverride;high=override===1?!high:override===2?false:override===3?true:high;
      value=high?value|(1<<i):value&~(1<<i);
    }return value>>>0;
  };
  bus.sync=sync;return bus;
}
module.exports={installAvrDs18b20,installPicoDs18b20};
