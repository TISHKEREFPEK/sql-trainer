'use strict';
// Original standard-speed 1-Wire engine. Time is physical simulated ns.
function crc8(bytes){let crc=0;for(const byte of bytes){let b=byte;for(let bit=0;bit<8;bit++){const mix=(crc^b)&1;crc>>>=1;if(mix)crc^=0x8c;b>>>=1;}}return crc;}
class Ds18b20Bus {
  constructor(now,schedule,drive,pinCount,devices=new Map(),writes=[]){
    Object.assign(this,{now,schedule,drive,pinCount,devices,lines:new Map(),groups:new Map(),writes});
    for(const d of devices.values()){d.pulseCancel?.();d.pulseCancel=null;d.low=false;d.busyCancel?.();d.guardCancel?.();d.busyCancel=d.guardCancel=null;d.phase='ignore';d.byte=d.bits=0;if(d.job)this.armJob(d);}
  }
  prepare(configs){
    if(!Array.isArray(configs)||configs.length>8)throw Error('DS18B20: не более восьми датчиков.');
    const ids=new Set(),roms=new Set();
    return configs.map(c=>{
      if(!c||typeof c.id!=='string'||!c.id.length||c.id.length>64||ids.has(c.id)||!Number.isInteger(c.pin)||c.pin<0||c.pin>=this.pinCount||
        ['powered','connected','parasite','pullup','strongPullup'].some(k=>typeof c[k]!=='boolean')||!Number.isFinite(c.temperature)||c.temperature< -55||c.temperature>125||
        typeof c.rom!=='string'||!/^28[0-9a-f]{14}$/i.test(c.rom)||roms.has(c.rom.toLowerCase())||
        !Array.isArray(c.memory)||c.memory.length!==3||c.memory.some(v=>!Number.isInteger(v)||v<0||v>255)||(c.memory[2]&0x9f)!==0x1f)
        throw Error('DS18B20: некорректные вывод, ROM, память или температура.');
      const rom=[...Buffer.from(c.rom,'hex')];if(crc8(rom)!==0)throw Error('DS18B20: ошибка CRC адреса ROM.');
      ids.add(c.id);roms.add(c.rom.toLowerCase());return {...c,memory:c.memory.slice(),rom:c.rom.toLowerCase()};
    });
  }
  live(d){return d.config.connected&&d.config.powered;}
  cancelPulse(d){d.pulseCancel?.();d.pulseCancel=null;d.low=false;}
  abortJob(d){d.busyCancel?.();d.guardCancel?.();d.busyCancel=d.guardCancel=null;d.job=null;}
  powerReset(d){this.cancelPulse(d);this.abortJob(d);d.scratch=[0x50,0x05,...d.memory,0xff,0x0c,0x10];d.alarm=false;d.phase='ignore';d.bits=d.byte=0;}
  update(configs){
    for(const [id,d]of this.devices)if(!configs.some(c=>c.id===id)){this.cancelPulse(d);this.abortJob(d);this.devices.delete(id);}
    for(const c of configs){
      let d=this.devices.get(c.id);
      if(!d){d={config:c,memory:c.memory.slice(),rom:[...Buffer.from(c.rom,'hex')],powered:false};this.powerReset(d);this.devices.set(c.id,d);}
      if(d.config.pin!==c.pin||d.config.rom!==c.rom||d.config.parasite!==c.parasite){this.powerReset(d);d.rom=[...Buffer.from(c.rom,'hex')];}
      if(!c.powered||!c.connected){this.cancelPulse(d);this.abortJob(d);d.phase='ignore';}
      else if(!d.powered)this.powerReset(d);
      d.powered=c.powered;d.config=c;
      if(!this.lines.has(c.pin))this.lines.set(c.pin,{master:null,pullup:false,lastLow:false,lowSince:null});
    }
    const pins=new Set(configs.map(c=>c.pin));for(const pin of this.lines.keys())if(!pins.has(pin))this.lines.delete(pin);
    this.groups.clear();for(const d of this.devices.values())if(this.live(d)){const pin=d.config.pin;if(!this.groups.has(pin))this.groups.set(pin,[]);this.groups.get(pin).push(d);}
    this.drive();
  }
  strong(d){return d.config.strongPullup||this.lines.get(d.config.pin)?.master===true;}
  parasiteLost(d){this.powerReset(d);this.drive();}
  observe(pin,master,pullup){
    const line=this.lines.get(pin);if(!line)return;line.master=master;line.pullup=pullup;
    const active=this.groups.get(pin)??[];
    const low=master===false,at=this.now();
    for(const d of active)if(d.job&&d.config.parasite&&at>d.job.started+10000&&!this.strong(d))this.parasiteLost(d);
    if(low===line.lastLow)return;
    line.lastLow=low;
    if(low){
      line.lowSince=at;
      for(const d of active){
        d.slotRead=['send','search-id','search-inverse','power','busy'].includes(d.phase);
        if(d.slotRead&&!this.readBit(d))this.pulse(d,at,at+45000);
      }
    }else{
      const duration=at-line.lowSince;line.lowSince=null;
      if(duration>=480000){for(const d of active){
        this.cancelPulse(d);if(d.config.parasite&&duration>=960000)this.powerReset(d);
        d.phase='rom';d.byte=d.bits=0;d.received=[];
        if(master===true||pullup||d.config.pullup)this.pulse(d,at+30000,at+150000);
      }}else if(duration>=1000){
        const value=duration<=15000?1:duration>=60000?0:null;
        for(const d of active)if(!d.slotRead&&value!==null)this.writeBit(d,value);
      }
    }
  }
  pulse(d,start,end){
    this.cancelPulse(d);
    const release=()=>{d.pulseCancel=null;d.low=false;this.drive();};
    if(start<=this.now()){d.low=true;d.pulseCancel=this.schedule(end,release);}
    else d.pulseCancel=this.schedule(start,()=>{d.low=true;this.drive();d.pulseCancel=this.schedule(end,release);});
  }
  send(d,bytes,next='function'){d.out=bytes.flatMap(b=>Array.from({length:8},(_,i)=>(b>>>i)&1));d.outIndex=0;d.next=next;d.phase='send';}
  readBit(d){
    if(d.phase==='send'){const bit=d.out[d.outIndex++];if(d.outIndex===d.out.length)d.phase=d.next;return bit;}
    if(d.phase==='power'){d.phase='ignore';return d.config.parasite?0:1;}
    if(d.phase==='busy')return d.job?0:1;
    if(d.phase==='search-id'){d.phase='search-inverse';return (d.rom[d.search>>>3]>>>(d.search&7))&1;}
    if(d.phase==='search-inverse'){d.phase='search-branch';return 1-((d.rom[d.search>>>3]>>>(d.search&7))&1);}
    return 1;
  }
  writeBit(d,bit){
    if(d.phase==='search-branch'){
      const own=(d.rom[d.search>>>3]>>>(d.search&7))&1;
      if(bit!==own)d.phase='ignore';else d.phase=++d.search===64?'function':'search-id';return;
    }
    if(!['rom','match','function','scratch-write'].includes(d.phase))return;
    d.byte|=bit<<d.bits;if(++d.bits<8)return;const value=d.byte;d.byte=d.bits=0;this.byte(d,value);
  }
  byte(d,value){
    if(d.phase==='rom'){
      if(value===0x33)this.send(d,d.rom);
      else if(value===0xcc)d.phase='function';
      else if(value===0x55){d.phase='match';d.received=[];}
      else if(value===0xf0||value===0xec&&d.alarm){d.search=0;d.phase='search-id';}
      else d.phase='ignore';return;
    }
    if(d.phase==='match'){d.received.push(value);if(d.received.length===8)d.phase=d.received.every((v,i)=>v===d.rom[i])?'function':'ignore';return;}
    if(d.phase==='scratch-write'){d.received.push(value);if(d.received.length===3){d.scratch.splice(2,3,d.received[0],d.received[1],(d.received[2]&0x60)|0x1f);d.phase='ignore';}return;}
    if(value===0xbe){this.send(d,[...d.scratch,crc8(d.scratch)]);return;}
    if(value===0xb4){d.phase='power';return;}
    if(value===0x4e){d.received=[];d.phase='scratch-write';return;}
    if(value===0x44){this.startJob(d,'convert',[93750000,187500000,375000000,750000000][(d.scratch[4]>>>5)&3]);return;}
    if(value===0x48){this.startJob(d,'copy',10000000);return;}
    if(value===0xb8){this.startJob(d,'recall',1000);return;}
    d.phase='ignore';
  }
  startJob(d,kind,duration){
    d.phase='busy';if(d.job)return;
    const started=this.now();d.job={kind,started,until:started+duration,temperature:d.config.temperature,memory:d.scratch.slice(2,5)};this.armJob(d);
  }
  armJob(d){
    const job=d.job;
    if(d.config.parasite&&job.kind!=='recall')d.guardCancel=this.schedule(Math.max(this.now()+1,job.started+10000),()=>{d.guardCancel=null;if(!this.strong(d))this.parasiteLost(d);});
    d.busyCancel=this.schedule(Math.max(this.now()+1,job.until),()=>{
      d.busyCancel=null;d.guardCancel?.();d.guardCancel=null;
      if(d.config.parasite&&job.kind!=='recall'&&!this.strong(d)){this.parasiteLost(d);return;}
      if(job.kind==='convert'){
        const unused=3-((d.scratch[4]>>>5)&3),raw=Math.round(job.temperature*16)&(~((1<<unused)-1));d.scratch[0]=raw&255;d.scratch[1]=(raw>>8)&255;
        const integer=raw>>4,signed=b=>b&128?b-256:b;d.alarm=integer>=signed(d.scratch[2])||integer<=signed(d.scratch[3]);
      }else if(job.kind==='copy'){
        d.memory=job.memory.slice();this.writes.push({id:d.config.id,memory:d.memory.slice(),milliseconds:this.now()/1e6});
      }else d.scratch.splice(2,3,...d.memory);
      d.job=null;this.drive();
    });
  }
  input(pin,master,pullup){
    let found=false,low=false,external=pullup;
    for(const d of this.groups.get(pin)??[]){found=true;low||=d.low;external||=d.config.pullup||d.config.strongPullup;}
    if(!found)return null;if(master!==null)return master;return !low&&external;
  }
  get full(){return this.writes.length>=8;}
  frame(){
    const ds18b20=[...this.devices.values()].map(d=>{let raw=d.scratch[0]|d.scratch[1]<<8;if(raw&0x8000)raw-=65536;return {id:d.config.id,rom:d.config.rom,reading:raw/16,resolution:9+((d.scratch[4]>>>5)&3),busy:!!d.job,memory:d.memory.slice()};});
    // Up to eight jobs can finish on the same clock tick. Drain at most eight
    // records per RPC, retaining the remainder before the CPU advances again.
    const oneWireWrites=this.writes.splice(0,8);return {ds18b20,oneWireWrites};
  }
}
module.exports={Ds18b20Bus,crc8};
