'use strict';
const {SpiDisplays}=require('./spi-displays.cjs');
function installAvrSpi(cpu,board,ports,spi,pinMap){
  const level=pin=>{const {port,bit}=pinMap[pin];return !!(cpu.data[port.portConfig.PIN]&(1<<bit));};
  const mega=board.id==='mega2560',sck=mega?52:13,mosi=mega?51:11,miso=mega?50:12;
  const output=pin=>{const p=pinMap[pin];return !!(cpu.data[p.port.portConfig.DDR]&(1<<p.bit));};
  const bus=new SpiDisplays(()=>cpu.cycles*1e9/board.clockHz,level,pinMap.length,1,c=>c.sck===sck&&c.mosi===mosi&&output(sck)&&output(mosi),undefined,c=>c.miso===miso);
  spi.onByte=value=>{
    if(!spi.isMaster)throw Error('AVR SPI slave mode is not connected');
    const ctrl=cpu.data[spi.config.SPCR],mode=((ctrl&8)?2:0)|((ctrl&4)?1:0);
    const finish=bus.begin(0,value,8,mode,spi.spiFrequency,spi.dataOrder==='lsbFirst');
    cpu.addClockEvent(()=>spi.completeTransfer(finish()),spi.transferCycles);
  };
  for(const port of Object.values(ports))port.addListener(()=>bus.refresh());
  return bus;
}
module.exports={installAvrSpi};
