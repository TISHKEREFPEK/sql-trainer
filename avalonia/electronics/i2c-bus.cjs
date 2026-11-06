'use strict';
const {Lcd}=require('./lcd-i2c.cjs');
const {Oled}=require('./ssd1306.cjs');
class I2cBus {
  constructor(cpu,hz,twi){this.cpu=cpu;this.hz=hz;this.twi=twi;this.devices=new Map();this.trace=[];this.packet=null;this.active=[];this.repeated=false;
    const delay=(callback,bits=9)=>cpu.addClockEvent(callback,Math.max(1,Math.round(bits*hz/twi.sclFrequency)));
    twi.eventHandler={
      start:()=>{const repeated=this.packet!==null;this.flush();this.repeated=repeated;delay(()=>twi.completeStart(),1);},
      stop:()=>{this.flush();this.active=[];delay(()=>twi.completeStop(),1);},
      connectToSlave:(address,write)=>{
        this.active=[...this.devices.values()].filter(d=>d.powered&&d.config.connected&&d.config.address===address&&(write||d.readable));
        if(this.active.some(d=>twi.sclFrequency>d.maxFrequency+1))throw Error('I2C: превышена поддержанная частота устройства ('+Math.min(...this.active.map(d=>d.maxFrequency))/1000+' кГц).');
        for(const d of this.active)d.begin?.();
        const ack=this.active.length>0;this.packet={address,read:!write,data:[],ack,repeatedStart:this.repeated};delay(()=>twi.completeConnect(ack));
      },
      writeByte:value=>{if(this.packet.data.length>=4096)throw Error('I2C transfer exceeds 4096 bytes');delay(()=>{this.packet.data.push(value);const targets=this.active.filter(d=>d.powered&&d.config.connected);for(const d of targets)d.write(value);twi.completeWrite(targets.length>0);});},
      readByte:()=>{const value=this.active.reduce((a,d)=>a&d.read(),255);if(this.packet.data.length>=4096)throw Error('I2C transfer exceeds 4096 bytes');this.packet.data.push(value);delay(()=>twi.completeRead(value));}
    };
  }
  update(configs){
    if(!Array.isArray(configs)||configs.length>80)throw Error('Invalid I2C devices');const ids=new Set();
    for(const source of configs){const config={...source,kind:source?.kind??'lcd'};
      if(typeof config.id!=='string'||config.id.length<1||config.id.length>64||ids.has(config.id)||!Number.isInteger(config.address)||typeof config.connected!=='boolean'||typeof config.powered!=='boolean')throw Error('Invalid I2C device');
      const lcd=config.kind==='lcd',oled=config.kind==='ssd1306';
      if(lcd?!(config.address>=32&&config.address<=39||config.address>=56&&config.address<=63)||!(config.columns===16&&config.rows===2||config.columns===20&&config.rows===4):!oled||!(config.address===60||config.address===61)||config.columns!==128||config.rows!==64)throw Error('Invalid I2C model');
      ids.add(config.id);let device=this.devices.get(config.id);
      if(device&&device.config.kind!==config.kind)throw Error('I2C model changed during execution');
      if(!device){device=lcd?new Lcd(this.cpu,this.hz,config):new Oled(this.cpu,this.hz,config);this.devices.set(config.id,device);}device.update(config);
    }
    for(const id of this.devices.keys())if(!ids.has(id))this.devices.delete(id);
  }
  flush(){if(this.packet){this.trace.push(this.packet);this.packet=null;}}
  frame(){const i2c=this.trace;this.trace=[];const devices=[...this.devices.values()];return {i2c,displays:devices.filter(d=>d.config.kind==='lcd').map(d=>d.frame()),oledDisplays:devices.filter(d=>d.config.kind==='ssd1306').map(d=>d.frame())};}
}
module.exports={I2cBus};
