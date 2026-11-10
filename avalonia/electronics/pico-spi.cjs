'use strict';
const {SpiDisplays}=require('./spi-displays.cjs');
function physicalNow(clock){return ()=>clock.nanos;}
function installSpi(mcu,clock,panels){
  let external=[];
  const level=pin=>{const p=mcu.gpio[pin];return p.outputEnable&&!p.outputDisable?p.outputValue:external[pin]??(p.pullupEnabled?true:p.pulldownEnabled?false:p.rawInputValue);};
  const role=(pin,index,remainder)=>pin!=null&&((pin>>>3)&1)===index&&pin%4===remainder&&mcu.gpio[pin].functionSelect===1;
  const bus=new SpiDisplays(physicalNow(clock),level,30,2,c=>role(c.sck,c.controller,2)&&role(c.mosi,c.controller,3),panels,c=>role(c.miso,c.controller,0));
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
  for(const pin of mcu.gpio){const previous=pin.checkForUpdates.bind(pin);pin.checkForUpdates=()=>{previous();bus.refresh();};}
  return bus;
}
module.exports={installSpi};
