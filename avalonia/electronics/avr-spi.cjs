'use strict';
const {SpiDisplays}=require('./spi-displays.cjs');
const {GpioSpi}=require('./spi-gpio.cjs');
function installAvrSpi(cpu,board,ports,spi,pinMap){
  const level=pin=>{const {port,bit}=pinMap[pin],mask=1<<bit;return !!((cpu.data[port.portConfig.DDR]&mask?port.lastValue:cpu.data[port.portConfig.PIN])&mask);};
  const mega=board.id==='mega2560',sck=mega?52:13,mosi=mega?51:11,miso=mega?50:12;
  const output=pin=>{const p=pinMap[pin];return !!(cpu.data[p.port.portConfig.DDR]&(1<<p.bit));};
  const bus=new SpiDisplays(()=>cpu.cycles*1e9/board.clockHz,level,pinMap.length,1,c=>c.sck===sck&&c.mosi===mosi&&output(sck)&&output(mosi),undefined,c=>c.miso===miso);
  const owned=pin=>!!(cpu.data[spi.config.SPCR]&0x40)&&spi.isMaster&&(pin===sck||pin===mosi);
  const gpio=new GpioSpi(bus,c=>!owned(c.sck)&&!owned(c.mosi),c=>c.miso!=null&&!output(c.miso));
  const update=bus.update.bind(bus);bus.update=configs=>{update(configs);gpio.tick();};
  const abort=bus.abort.bind(bus);bus.abort=()=>{abort();gpio.abortAll();};
  spi.onByte=value=>{
    if(!spi.isMaster)throw Error('AVR SPI slave mode is not connected');
    const ctrl=cpu.data[spi.config.SPCR],mode=((ctrl&8)?2:0)|((ctrl&4)?1:0);
    const finish=bus.begin(0,value,8,mode,spi.spiFrequency,spi.dataOrder==='lsbFirst');
    cpu.addClockEvent(()=>spi.completeTransfer(finish()),spi.transferCycles);
  };
  for(const port of Object.values(ports)){
    port.addListener(()=>gpio.tick());const address=port.portConfig.PIN,previous=cpu.readHooks[address];
    cpu.readHooks[address]=()=>{let value=previous?previous(address):cpu.data[address];gpio.tick();if(gpio.states.size===0)return value;for(let i=0;i<pinMap.length;i++){const p=pinMap[i];if(p.port!==port)continue;const bit=gpio.input(i);if(bit!==null)value=bit?value|(1<<p.bit):value&~(1<<p.bit);}return value;};
  }
  return bus;
}
module.exports={installAvrSpi};
