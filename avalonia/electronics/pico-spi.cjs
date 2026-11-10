'use strict';
const {SpiDisplays}=require('./spi-displays.cjs');
const {GpioSpi}=require('./spi-gpio.cjs');
function physicalNow(clock){return ()=>clock.nanos;}
function installSpi(mcu,clock,panels){
  let external=[];
  const level=pin=>{const p=mcu.gpio[pin];return p.outputEnable&&!p.outputDisable?p.outputValue:external[pin]??(p.pullupEnabled?true:p.pulldownEnabled?false:p.rawInputValue);};
  const role=(pin,index,remainder)=>pin!=null&&((pin>>>3)&1)===index&&pin%4===remainder&&mcu.gpio[pin].functionSelect===1;
  const bus=new SpiDisplays(physicalNow(clock),level,30,2,c=>role(c.sck,c.controller,2)&&role(c.mosi,c.controller,3),panels,c=>role(c.miso,c.controller,0));
  const gpio=new GpioSpi(bus,c=>mcu.gpio[c.sck].functionSelect===5&&mcu.gpio[c.mosi].functionSelect===5,c=>c.miso!=null&&mcu.gpio[c.miso].functionSelect===5&&!(mcu.gpio[c.miso].outputEnable&&!mcu.gpio[c.miso].outputDisable));
  const update=bus.update.bind(bus);bus.update=configs=>{update(configs);gpio.tick();};
  const abort=bus.abort.bind(bus);bus.abort=()=>{abort();gpio.abortAll();};
  bus.setInputs=values=>{external=values;};
  mcu.spi.forEach((spi,index)=>{
    spi.onTransmit=value=>{
      if(!spi.enabled)throw Error('RP2040 SPI transfer while disabled');
      if(spi.control1&4)throw Error('RP2040 SPI slave mode is not connected');
      if(spi.control0&0x30)throw Error('RP2040 non-Motorola SPI frame format is not connected');
      const bits=spi.dataBits,hz=spi.clockFrequency;
      if(!Number.isFinite(hz)||hz<=0)throw Error('RP2040 SPI clock is stopped');
      const finish=bus.begin(index,value,bits,((spi.control0>>>6)&1)*2+((spi.control0>>>7)&1),hz);
      clock.createAlarm(()=>spi.completeTransmit(finish())).schedule(Math.max(1,bits*1e9/hz));
    };
  });
  for(const pin of mcu.gpio){
    const previous=pin.checkForUpdates.bind(pin);let value=pin.value,ctrl=pin.ctrl,pad=pin.padValue,input=pin.rawInputValue;
    pin.checkForUpdates=()=>{previous();const next=pin.value;if(next!==value||pin.ctrl!==ctrl||pin.padValue!==pad||pin.rawInputValue!==input){value=next;ctrl=pin.ctrl;pad=pin.padValue;input=pin.rawInputValue;gpio.tick();}};
  }
  const read=mcu.sio.readUint32.bind(mcu.sio);
  mcu.sio.readUint32=offset=>{
    let value=read(offset);if(offset!==4)return value;gpio.tick();if(gpio.states.size===0)return value;
    for(let i=0;i<30;i++){const bit=gpio.input(i);if(bit===null)continue;const p=mcu.gpio[i];let high=bit&&p.inputEnable;const override=p.inputOverride;high=override===1?!high:override===2?false:override===3?true:high;value=high?value|(1<<i):value&~(1<<i);}
    return value>>>0;
  };
  return bus;
}
module.exports={installSpi};
