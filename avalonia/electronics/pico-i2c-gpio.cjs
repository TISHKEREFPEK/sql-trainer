'use strict';
// Decode real SIO edges. No Arduino symbols/return values are intercepted.
class GpioI2cBus {
  constructor(owner,sda,scl,controller){
    Object.assign(this,{owner,sda,scl,controller,packet:null,phase:'idle',low:false,lastSda:true,lastScl:true,lastRise:null,period:Infinity,active:[]});
  }
  live(d){const c=d.config;return this.owner.devices.get(c.id)===d&&d.powered&&c.connected&&c.sda===this.sda&&c.scl===this.scl&&(!this.packet||c.address===this.packet.address);}
  flush(){if(this.packet){this.owner.trace.push({...this.packet,controller:this.controller,software:true,sda:this.sda,scl:this.scl});this.owner.traceBytes+=this.packet.data.length;this.packet=null;}}
  receive(){this.phase='receive';this.bits=0;this.byte=0;}
  start(){const repeated=this.packet!==null;this.flush();this.repeated=repeated;this.address=true;this.active=[];this.low=false;this.lastRise=null;this.period=Infinity;this.receive();}
  push(value){if(this.packet.data.length>=4096)throw Error('I2C transfer exceeds 4096 bytes');this.packet.data.push(value);}
  accept(value){
    if(this.address){
      const address=value>>>1,read=!!(value&1);this.address=false;
      this.active=[...this.owner.devices.values()].filter(d=>this.live(d)&&d.config.address===address&&(!read||d.readable));
      if(this.active.some(d=>1e9/this.period>d.maxFrequency+1))throw Error('I2C: превышена поддержанная частота устройства ('+Math.min(...this.active.map(d=>d.maxFrequency))/1000+' кГц).');
      for(const d of this.active)d.begin?.();
      this.packet={address,read,data:[],ack:this.active.length>0,repeatedStart:this.repeated};
    }else{
      this.push(value);this.active=this.active.filter(d=>this.live(d));for(const d of this.active)d.write(value);this.packet.ack&&=this.active.length>0;
    }
    this.ack=this.active.length>0;this.phase='ack-pending';
  }
  send(){this.active=this.active.filter(d=>this.live(d));this.byte=this.active.reduce((v,d)=>v&d.read(),255);this.bits=0;this.observed=0;this.phase='send';this.low=!(this.byte&128);}
  tick(){
    const {mcu}=this.owner;
    if(mcu.gpio[this.sda].functionSelect!==5||mcu.gpio[this.scl].functionSelect!==5){if(this.packet){this.packet.ack=false;this.flush();}this.phase='idle';this.low=false;this.lastSda=this.lastScl=true;return;}
    this.active=this.active.filter(d=>this.live(d));
    if(this.low&&!this.active.length){this.low=false;if(this.packet)this.packet.ack=false;}
    const scl=this.owner.level(this.scl),master=this.owner.level(this.sda),sda=master&&!this.low;
    if(this.low&&mcu.gpio[this.sda].outputEnable&&mcu.gpio[this.sda].outputValue&&!mcu.gpio[this.sda].outputDisable)throw Error('GPIO I2C SDA must be open-drain; push-pull HIGH conflicts with slave');
    if(scl&&this.lastScl&&sda!==this.lastSda){
      if(!sda)this.start();else{this.flush();this.phase='idle';this.low=false;}
    }else if(scl&&!this.lastScl&&this.phase!=='idle'){
      const now=this.owner.clock.nanos;if(this.lastRise!==null)this.period=Math.min(this.period,now-this.lastRise);this.lastRise=now;
      if(this.phase==='receive'){this.byte=(this.byte<<1)|+sda;if(++this.bits===8)this.accept(this.byte);}
      else if(this.phase==='send'){this.observed=(this.observed<<1)|+sda;if(++this.bits===8){this.push(this.observed);this.phase='master-ack-pending';}}
      else if(this.phase==='master-ack')this.more=!sda;
    }else if(!scl&&this.lastScl){
      if(this.phase==='ack-pending'){this.low=this.ack&&this.active.length>0;this.phase='ack';}
      else if(this.phase==='ack'){this.low=false;if(this.packet.read&&this.ack)this.send();else this.receive();}
      else if(this.phase==='send')this.low=!(this.byte&(128>>>this.bits));
      else if(this.phase==='master-ack-pending'){this.low=false;this.phase='master-ack';}
      else if(this.phase==='master-ack'){if(this.more)this.send();else{this.phase='wait';this.low=false;}}
    }
    this.lastScl=scl;this.lastSda=master&&!this.low;
  }
}
class PicoGpioI2c {
  constructor(mcu,clock,devices){
    Object.assign(this,{mcu,clock,devices,buses:new Map(),trace:[],traceBytes:0,digital:[],pullups:new Set()});
    // GPIO input pads are resolved only for SIO. Explicit host input wins over
    // pull resistors; an output feeds back its actual driven level.
    const read=mcu.sio.readUint32.bind(mcu.sio);
    mcu.sio.readUint32=offset=>{
      let value=read(offset);if(offset!==4)return value;let low=0;for(const b of this.buses.values())if(b.low)low|=1<<b.sda;
      for(let i=0;i<30;i++){const pin=mcu.gpio[i];if(pin.functionSelect!==5)continue;
        let high=this.level(i)&&!(low&(1<<i));
        high=pin.inputEnable&&high;const override=pin.inputOverride;high=override===1?!high:override===2?false:override===3?true:high;
        value=high?value|(1<<i):value&~(1<<i);
      }return value>>>0;
    };
    for(const pin of mcu.gpio){const update=pin.checkForUpdates.bind(pin);pin.checkForUpdates=()=>{update();this.tick();};}
  }
  level(index){const p=this.mcu.gpio[index];return p.outputEnable&&!p.outputDisable?p.outputValue:this.digital[index]??(p.pullupEnabled||this.pullups.has(index)?true:p.pulldownEnabled?false:p.rawInputValue);}
  tick(){for(const bus of this.buses.values())bus.tick();}
  update(configs,digital){
    this.digital=digital;
    this.pullups=new Set(configs.filter(c=>c.powered&&c.connected).flatMap(c=>[c.sda,c.scl]));
    for(const c of configs){const key=c.sda+':'+c.scl;if(!this.buses.has(key))this.buses.set(key,new GpioI2cBus(this,c.sda,c.scl,c.controller));}
    // Retain a removed pair only while its physical transfer is still active.
    const keys=new Set(configs.map(c=>c.sda+':'+c.scl));for(const [key,bus]of this.buses)if(!keys.has(key)&&bus.phase==='idle')this.buses.delete(key);
    this.tick();
  }
  get full(){return this.trace.length>=128||this.traceBytes>=32768;}
  frame(){const i2c=this.trace;this.trace=[];this.traceBytes=0;return i2c;}
}
module.exports={PicoGpioI2c};
