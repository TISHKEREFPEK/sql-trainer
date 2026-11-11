'use strict';
// Original ideal digital HC-SR04 model. All times are simulated nanoseconds.
class Hcsr04Bus {
  constructor(now,schedule,drive,pinCount,devices=new Map()){
    Object.assign(this,{now,schedule,drive,pinCount,devices});
    for(const d of devices.values()){d.cancel?.();d.cancel=null;d.rise=null;d.sawLow=false;this.arm(d);}
  }
  prepare(configs){
    if(!Array.isArray(configs)||configs.length>4)throw Error('HC-SR04: не более четырёх датчиков.');
    const ids=new Set(),echoes=new Set();
    const pin=p=>Number.isInteger(p)&&p>=0&&p<this.pinCount;
    const result=configs.map(c=>{
      if(!c||typeof c.id!=='string'||!c.id.length||c.id.length>64||ids.has(c.id)||!pin(c.trig)||typeof c.powered!=='boolean'||typeof c.connected!=='boolean'||
        typeof c.target!=='boolean'||!Number.isFinite(c.distance)||c.distance<2||c.distance>400||!Array.isArray(c.echoes)||c.echoes.length>this.pinCount)
        throw Error('HC-SR04: некорректное подключение или расстояние.');
      for(const e of c.echoes){
        if(!e||!pin(e.pin)||typeof e.low!=='boolean'||typeof e.high!=='boolean'||e.pin===c.trig||c.connected&&echoes.has(e.pin))throw Error('HC-SR04: конфликт вывода Echo.');
        if(c.connected)echoes.add(e.pin);
      }
      ids.add(c.id);return {...c,echoes:c.echoes.map(e=>({...e}))};
    });
    if(result.some(c=>c.connected&&echoes.has(c.trig)))throw Error('HC-SR04: Trig и Echo используют один GPIO.');
    return result;
  }
  cancel(d){d.cancel?.();d.cancel=null;d.high=false;d.edges=[];d.edge=0;d.rise=null;d.sawLow=false;}
  update(configs){
    for(const [id,d]of this.devices)if(!configs.some(c=>c.id===id)){this.cancel(d);this.devices.delete(id);}
    for(const c of configs){
      let d=this.devices.get(c.id);
      if(!d){d={config:c,high:false,edges:[],edge:0,rise:null,sawLow:false,nextTrigger:0};this.devices.set(c.id,d);}
      if(!c.powered||!c.connected||d.config.trig!==c.trig||JSON.stringify(d.config.echoes)!==JSON.stringify(c.echoes))this.cancel(d);
      d.config=c;
    }
    this.drive();
  }
  observe(pin,high){
    const at=this.now();
    for(const d of this.devices.values())if(d.config.powered&&d.config.connected&&d.config.trig===pin){
      if(!high){
        if(d.rise!==null&&at-d.rise>=10000&&at>=d.nextTrigger&&!d.edges.length)this.start(d);
        d.rise=null;d.sawLow=true;
      }else if(d.rise===null&&d.sawLow)d.rise=at;
    }
  }
  start(d){
    const at=this.now();d.nextTrigger=at+60e6;
    if(d.config.target){d.edges=[[at+200e3,true],[at+200e3+Math.round(d.config.distance*58)*1000,false]];d.edge=0;this.arm(d);}
    // No target: no Echo pulse. The 60 ms re-trigger interval also exceeds the
    // selected 38 ms no-return observation window; clone behavior can differ.
  }
  arm(d){
    if(d.edge>=d.edges.length)return;
    d.cancel=this.schedule(d.edges[d.edge][0],()=>{d.cancel=null;d.high=d.edges[d.edge++][1];if(d.edge===d.edges.length){d.edges=[];d.edge=0;}this.drive();this.arm(d);});
  }
  input(pin){
    for(const d of this.devices.values())if(d.config.powered&&d.config.connected){const e=d.config.echoes.find(e=>e.pin===pin);if(e)return d.high?e.high:e.low;}
    return null;
  }
}
module.exports={Hcsr04Bus};
