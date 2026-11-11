'use strict';
// Original, deterministic DHT11 four-pin profile (Aosong's 3–5.5 V manual).
// Time is physical simulated nanoseconds. Nothing inspects the sketch or an API.
class Dht11Bus {
  constructor(now,schedule,drive,pinCount,devices=new Map()) {
    Object.assign(this,{now,schedule,drive,pinCount,devices});
    for(const d of devices.values()){d.cancel?.();d.cancel=null;d.masterLow=false;d.lowSince=null;this.arm(d);}
  }
  prepare(configs) {
    if(!Array.isArray(configs)||configs.length>8)throw Error('DHT11: не более восьми датчиков.');
    const ids=new Set(),pins=new Set();
    return configs.map(c=>{
      if(!c||typeof c.id!=='string'||!c.id.length||c.id.length>64||ids.has(c.id)||!Number.isInteger(c.pin)||c.pin<0||c.pin>=this.pinCount||
        typeof c.powered!=='boolean'||typeof c.connected!=='boolean'||typeof c.pullup!=='boolean'||
        !Number.isInteger(c.temperature)||c.temperature<0||c.temperature>50||!Number.isInteger(c.humidity)||c.humidity<20||c.humidity>90)
        throw Error('DHT11: некорректное подключение или показания.');
      if(c.connected&&pins.has(c.pin))throw Error('DHT11: каждому датчику нужен отдельный DATA.');
      ids.add(c.id);if(c.connected)pins.add(c.pin);return {...c};
    });
  }
  update(configs) {
    for(const [id,d]of this.devices)if(!configs.some(c=>c.id===id)){this.cancel(d);this.devices.delete(id);}
    for(const c of configs){
      let d=this.devices.get(c.id);
      if(!d){d={config:c,powered:false,low:false,masterLow:false,lowSince:null,edges:[],edge:0,cancel:null,nextRead:0};this.devices.set(c.id,d);}
      const old=d.config;
      if(old.pin!==c.pin||!c.connected||!c.powered)this.cancel(d);
      if(c.powered&&!d.powered){d.readyAt=this.now()+1e9;d.nextRead=d.readyAt;d.sample=[c.humidity,0,c.temperature,0];}
      d.powered=c.powered;d.config=c;
    }
    this.drive();
  }
  cancel(d){d.cancel?.();d.cancel=null;d.low=false;d.edges=[];d.edge=0;d.masterLow=false;d.lowSince=null;}
  // master: false = driven LOW, true = driven HIGH, null = released.
  observe(pin,master,pullup) {
    for(const d of this.devices.values())if(d.config.pin===pin&&d.config.connected&&d.powered){
      const low=master===false,now=this.now();
      if(low&&!d.masterLow){d.lowSince=now;if(d.edges.length)this.cancel(d);d.lowSince=now;}
      else if(!low&&d.masterLow){
        const duration=now-d.lowSince;d.lowSince=null;
        if(duration>=18e6&&now>=d.nextRead&&(master===true||pullup||d.config.pullup))this.start(d);
      }
      d.masterLow=low;
    }
  }
  start(d) {
    const at=this.now();d.nextRead=at+1e9;
    const bytes=[...d.sample,d.sample.reduce((a,b)=>a+b,0)&255];
    // Host release, response LOW/HIGH, then forty MSB-first LOW/HIGH slots.
    let t=at+20e3;const edges=[[t,true],[t+=80e3,false]];t+=80e3;
    for(const b of bytes)for(let bit=7;bit>=0;bit--){edges.push([t,true],[t+=50e3,false]);t+=b&(1<<bit)?70e3:28e3;}
    edges.push([t,true],[t+=50e3,false]);d.edges=edges;d.edge=0;this.arm(d);
  }
  arm(d) {
    if(d.edge>=d.edges.length)return;
    d.cancel=this.schedule(d.edges[d.edge][0],()=>{
      d.cancel=null;d.low=d.edges[d.edge++][1];
      if(d.edge===d.edges.length){d.sample=[d.config.humidity,0,d.config.temperature,0];d.edges=[];d.edge=0;}
      this.drive();this.arm(d);
    });
  }
  input(pin,master,pullup) {
    const devices=[...this.devices.values()].filter(d=>d.config.pin===pin&&d.config.connected&&d.powered);
    if(!devices.length)return null;
    // Open-drain sensor. An actively driven MCU output still owns its own readback.
    if(master!==null)return master;
    return !devices.some(d=>d.low)&&(pullup||devices.some(d=>d.config.pullup));
  }
}
module.exports={Dht11Bus};
