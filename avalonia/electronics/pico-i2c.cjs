'use strict';
const {I2cBus}=require('./i2c-bus.cjs');
const {PicoGpioI2c}=require('./pico-i2c-gpio.cjs');
class PicoI2c {
  constructor(mcu,clock,devices=new Map()){
    this.devices=devices;this.tracePending=[];this.buses=mcu.i2c.map((controller,index)=>{
      const pending=new Set(),timer={get cycles(){return clock.nanos;},addClockEvent(callback,nanos){const alarm=clock.createAlarm(()=>{pending.delete(alarm);callback();});pending.add(alarm);alarm.schedule(nanos);}};
      let transmitting=false;
      const transport={get sclFrequency(){const count=controller.sclHighPeriod+controller.sclLowPeriod;if(count<=0||!Number.isFinite(mcu.clkSys)||mcu.clkSys<=0)throw Error('Invalid RP2040 I2C clock');return mcu.clkSys/count;},
        completeStart:()=>controller.completeStart(),completeStop:()=>controller.completeStop(),completeConnect:ack=>controller.completeConnect(ack),completeWrite:ack=>{transmitting=false;controller.completeWrite(ack);if(!transmitting&&controller.txFIFO.itemCount<=controller.txThreshold)controller.setInterrupts(16);},completeRead:value=>controller.completeRead(value)};
      const accepts=config=>config.controller===index&&config.sda%4===index*2&&config.scl%4===index*2+1&&mcu.gpio[config.sda].functionSelect===3&&mcu.gpio[config.scl].functionSelect===3;
      const bus=new I2cBus(timer,1e9,transport,{devices:this.devices,accepts});
      controller.onStart=()=>{if(controller.masterBits!==7||!(controller.control&1))throw Error('RP2040 I2C supports only 7-bit master mode in this bridge');transport.eventHandler.start();};
      controller.onConnect=(address,mode)=>transport.eventHandler.connectToSlave(address,mode===0);
      // TX_EMPTY_CTRL includes the shift register, not merely the FIFO. The
      // SDK waits for this before changing IC_ENABLE/TAR for a repeated START.
      const interrupt=controller.setInterrupts.bind(controller);
      controller.setInterrupts=mask=>interrupt(transmitting&&(controller.control&0x100)?mask&~16:mask);
      const abort=controller.abort.bind(controller);
      controller.abort=reason=>{transmitting=false;abort(reason);controller.rxFIFO.reset();controller.clearInterrupts(4);controller.setInterrupts(16);};
      controller.onWriteByte=value=>{transmitting=true;if(controller.control&0x100)controller.clearInterrupts(16);transport.eventHandler.writeByte(value);};
      controller.onReadByte=()=>transport.eventHandler.readByte();controller.onStop=()=>transport.eventHandler.stop();
      const read=controller.readUint32.bind(controller),write=controller.writeUint32.bind(controller);let hold=1,dma=0;
      controller.readUint32=offset=>offset===0x7c?hold:offset===0x88?dma:read(offset);
      controller.writeUint32=(offset,value)=>{
        if(offset===0x7c){hold=value&0xffffff;return;}
        if(offset===0x88){dma=value&3;return;}
        if(offset===0xa0){controller.spikelen=value&0xff;return;}
        if(offset===0x6c&&(!(value&1)||value&2)&&(pending.size||value&2)){
          for(const alarm of pending)alarm.cancel();pending.clear();
          if(bus.packet){bus.packet.ack=false;bus.flush();}bus.active=[];
          if(value&2)controller.abort(1<<16);
          transmitting=false;controller.state=0;controller.busy=false;controller.stop=false;controller.pendingRestart=false;
        }
        write(offset,value);
      };
      return bus;
    });this.gpio=new PicoGpioI2c(mcu,clock,this.devices);
    for(const channel of mcu.dma.channels){const start=channel.start.bind(channel);channel.start=()=>{if((channel.ctrl&1)&&channel.transCount>0&&channel.treq>=32&&channel.treq<=35)throw Error('RP2040 I2C DMA transfer is not supported yet');return start();};}
  }
  prepare(configs){
    if(!Array.isArray(configs)||configs.length>80)throw Error('Invalid Pico I2C devices');
    const normalized=configs.map(c=>({...c,controller:c?.controller??0,sda:c?.sda??4,scl:c?.scl??5}));
    for(const c of normalized)if(!Number.isInteger(c.controller)||c.controller<0||c.controller>1||!Number.isInteger(c.sda)||c.sda<0||c.sda>29||!Number.isInteger(c.scl)||c.scl<0||c.scl>29||c.sda===c.scl)throw Error('Invalid Pico I2C controller/pins');
    return this.buses[0].validate(normalized);
  }
  update(configs,digital=[]){this.buses[0].update(configs);this.gpio.update(configs,digital);}
  get full(){return this.tracePending.length>0||this.buses.reduce((n,b)=>n+b.trace.length,0)+this.gpio.trace.length>=128||this.buses.reduce((n,b)=>n+b.traceBytes,0)+this.gpio.traceBytes>=32768;}
  abort(){
    for(const bus of [...this.buses,...this.gpio.buses.values()])if(bus.packet){bus.packet.ack=false;bus.flush();}
  }
  frame(){
    const [first,second]=this.buses.map(b=>b.frame());
    this.tracePending.push(...first.i2c.map(t=>({...t,controller:0})),...second.i2c.map(t=>({...t,controller:1})),...this.gpio.frame());
    // One SIO port write can finish several physical buses simultaneously.
    // Drain bounded frames before resuming CPU; never truncate their packets.
    const i2c=[];let bytes=0;
    while(this.tracePending.length&&i2c.length<128&&bytes+this.tracePending[0].data.length<=32768){const packet=this.tracePending.shift();i2c.push(packet);bytes+=packet.data.length;}
    return {displays:first.displays,oledDisplays:first.oledDisplays,i2c};
  }
}
module.exports={PicoI2c};
