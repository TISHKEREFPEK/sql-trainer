'use strict';
// PL011 FIFO trigger/receive timeout and DMA request configuration. UART byte
// transmission still uses the upstream functional (not cycle-exact) model.
function installUart(mcu,clock){
  for(const uart of mcu.uart){
    let ifls=0x12,dma=0;
    const read=uart.readUint32.bind(uart),write=uart.writeUint32.bind(uart),check=uart.checkInterrupts.bind(uart),feed=uart.feedByte.bind(uart);
    const syncDma=()=>{
      if(uart.enabled&&uart.txEnabled&&(dma&2))mcu.dma.setDREQ(uart.dreq.tx);else mcu.dma.clearDREQ(uart.dreq.tx);
      if(uart.enabled&&uart.rxEnabled&&(dma&1)&&!uart.rxFIFO.empty)mcu.dma.setDREQ(uart.dreq.rx);else mcu.dma.clearDREQ(uart.dreq.rx);
    };
    const timeout=clock.createAlarm(()=>{if(uart.enabled&&!uart.rxFIFO.empty){uart.interruptStatus|=64;uart.checkInterrupts();}});
    const schedule=()=>{timeout.cancel();if(uart.enabled&&!uart.rxFIFO.empty&&Number.isFinite(uart.baudRate)&&uart.baudRate>0)timeout.schedule(32e9/uart.baudRate);};
    Object.defineProperty(uart,'flags',{get(){return 128|(uart.rxFIFO.itemCount>=(uart.fifosEnabled?32:1)?64:0)|(uart.rxFIFO.empty?16:0);}});
    uart.checkInterrupts=()=>{const threshold=uart.fifosEnabled?[4,8,16,24,28][ifls>>>3&7]:1;if(uart.rxFIFO.itemCount>=threshold)uart.interruptStatus|=16;else uart.interruptStatus&=~16;check();};
    uart.feedByte=value=>{feed(value);schedule();syncDma();};
    uart.readUint32=offset=>{
      if(offset===0x34)return ifls;if(offset===0x48)return dma;
      const value=read(offset);if(offset===0){uart.interruptStatus&=~64;schedule();uart.checkInterrupts();syncDma();}return value;
    };
    uart.writeUint32=(offset,value)=>{
      if(offset===0x34){if((value&7)>4||(value>>>3&7)>4)throw Error('Reserved RP2040 UART FIFO trigger');ifls=value&63;uart.checkInterrupts();return;}
      if(offset===0x48){dma=value&7;syncDma();return;}
      write(offset,value);
      if(offset===0x30||offset===0x2c){if(!uart.enabled)uart.interruptStatus&=~64;schedule();uart.checkInterrupts();syncDma();}
    };
  }
}
module.exports={installUart};
