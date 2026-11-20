'use strict';
const {Lcd}=require('./lcd-i2c.cjs');
const {Oled}=require('./ssd1306.cjs');
const {Mpu6050,validMotion}=require('./mpu6050.cjs');
class I2cBus {
  constructor(cpu,hz,twi,options={}){this.cpu=cpu;this.hz=hz;this.twi=twi;this.devices=options.devices??new Map();this.accepts=options.accepts??(()=>true);this.trace=[];this.traceBytes=0;this.packet=null;this.active=[];this.repeated=false;
    const delay=(callback,bits=9)=>cpu.addClockEvent(callback,Math.max(1,Math.round(bits*hz/twi.sclFrequency)));
    twi.eventHandler={
      start:()=>{const repeated=this.packet!==null;this.flush();this.repeated=repeated;delay(()=>twi.completeStart(),1);},
      stop:()=>{this.flush();this.active=[];delay(()=>twi.completeStop(),1);},
      connectToSlave:(address,write)=>{
        this.active=[...this.devices.values()].filter(d=>this.connected(d)&&d.config.address===address&&(write||d.readable));
        if(this.active.some(d=>twi.sclFrequency>d.maxFrequency+1))throw Error('I2C: превышена поддержанная частота устройства ('+Math.min(...this.active.map(d=>d.maxFrequency))/1000+' кГц).');
        for(const d of this.active)d.begin?.(write);
        const ack=this.active.length>0;this.packet={address,read:!write,data:[],ack,repeatedStart:this.repeated};delay(()=>{this.active=this.active.filter(d=>this.connected(d));this.packet.ack=this.active.length>0;twi.completeConnect(this.packet.ack);});
      },
      writeByte:value=>{if(this.packet.data.length>=4096)throw Error('I2C transfer exceeds 4096 bytes');delay(()=>{this.packet.data.push(value);const targets=this.active.filter(d=>this.connected(d));for(const d of targets)d.write(value);this.packet.ack&&=targets.length>0;twi.completeWrite(targets.length>0);});},
      readByte:()=>{if(this.packet.data.length>=4096)throw Error('I2C transfer exceeds 4096 bytes');delay(()=>{const value=this.active.filter(d=>this.connected(d)).reduce((a,d)=>a&d.read(),255);this.packet.data.push(value);twi.completeRead(value);});}
    };
  }
  connected(device){return this.devices.get(device.config.id)===device&&device.powered&&device.config.connected&&this.accepts(device.config)&&(!this.packet||device.config.address===this.packet.address);}
  validate(configs){
    if(!Array.isArray(configs)||configs.length>80)throw Error('Invalid I2C devices');const ids=new Set(),valid=[];
    for(const source of configs){const config={...source,kind:source?.kind??'lcd'};
      if(typeof config.id!=='string'||config.id.length<1||config.id.length>64||ids.has(config.id)||!Number.isInteger(config.address)||typeof config.connected!=='boolean'||typeof config.powered!=='boolean')throw Error('Invalid I2C device');
      const validProfile = config.kind==='lcd'
        ? (config.address>=32&&config.address<=39||config.address>=56&&config.address<=63)&&(config.columns===16&&config.rows===2||config.columns===20&&config.rows===4)
        : config.kind==='ssd1306'
        ? (config.address===60||config.address===61)&&config.columns===128&&config.rows===64
        : config.kind==='mpu6050'&&(config.address===0x68||config.address===0x69)&&config.columns===0&&config.rows===0&&validMotion(config.motion);
      if(!validProfile)throw Error('Invalid I2C model');
      ids.add(config.id);const device=this.devices.get(config.id);
      if(device&&device.config.kind!==config.kind)throw Error('I2C model changed during execution');
      valid.push(config);
    }
    return valid;
  }
  update(configs){
    const valid=this.validate(configs),ids=new Set(valid.map(c=>c.id));
    for(const config of valid){let device=this.devices.get(config.id);
      if(!device){const Type=config.kind==='lcd'?Lcd:config.kind==='ssd1306'?Oled:Mpu6050;device=new Type(this.cpu,this.hz,config);this.devices.set(config.id,device);}device.update(config);
    }
    for(const id of this.devices.keys())if(!ids.has(id))this.devices.delete(id);
  }
  flush(){if(this.packet){this.trace.push(this.packet);this.traceBytes+=this.packet.data.length;this.packet=null;}}
  frame(){const i2c=this.trace;this.trace=[];this.traceBytes=0;const devices=[...this.devices.values()];return {i2c,displays:devices.filter(d=>d.config.kind==='lcd').map(d=>d.frame()),oledDisplays:devices.filter(d=>d.config.kind==='ssd1306').map(d=>d.frame())};}
}
module.exports={I2cBus};
