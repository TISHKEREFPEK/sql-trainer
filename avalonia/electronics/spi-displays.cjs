'use strict';
const {Ili9341}=require('./ili9341.cjs');
// Hardware SPI word bridge. Software-clocked GPIO SPI is a separate pending
// adapter, not silently interpreted as hardware peripheral traffic.
class SpiDisplays {
  constructor(now,level,pinCount,controllers,accepts,panels=new Map(),readable=c=>c.miso!=null){
    this.now=now;this.level=level;this.pinCount=pinCount;this.controllers=controllers;this.accepts=accepts;this.readable=readable;this.panels=panels;this.records=[];
  }
  signal(s){return s?.level??(s?.pin!=null?this.level(s.pin):null);}
  validSignal(s){return s==null||typeof s==='object'&&((Number.isInteger(s.pin)&&s.pin>=0&&s.pin<this.pinCount&&s.level==null)||(s.pin==null&&typeof s.level==='boolean'));}
  prepare(configs){
    if(!Array.isArray(configs)||configs.length>2)throw Error('SPI supports at most two TFT displays per worker');
    const ids=new Set();
    for(const c of configs){
      if(!c||typeof c.id!=='string'||c.id.length<1||c.id.length>100||ids.has(c.id)||c.kind!=='ili9341'||!Number.isInteger(c.controller)||c.controller<0||c.controller>=this.controllers||
        typeof c.powered!=='boolean'||typeof c.connected!=='boolean'||!Number.isInteger(c.mosi)||!Number.isInteger(c.sck)||c.mosi<0||c.mosi>=this.pinCount||c.sck<0||c.sck>=this.pinCount||c.mosi===c.sck||
        c.miso!=null&&(!Number.isInteger(c.miso)||c.miso<0||c.miso>=this.pinCount)||![c.cs,c.dc,c.rst,c.led].every(s=>this.validSignal(s)))throw Error('Invalid SPI display configuration');
      ids.add(c.id);
    }
    return configs;
  }
  update(configs){
    this.prepare(configs);const ids=new Set(configs.map(c=>c.id));
    const previous=new Map(this.records.map(r=>[r.config.id,r]));
    this.records=configs.map(config=>{
      const signal=s=>s==null?null:[s.pin??null,s.level??null];
      const signature=JSON.stringify([config.controller,config.mosi,config.sck,config.miso??null,config.powered,config.connected,...[config.cs,config.dc,config.rst].map(signal)]);
      const old=previous.get(config.id);if(old?.signature===signature){old.config=config;return old;}
      let panel=this.panels.get(config.id);if(!panel){panel=new Ili9341(config.id,this.now);this.panels.set(config.id,panel);}
      return {config,panel,signature,epoch:0,electrical:null};
    });
    for(const id of this.panels.keys())if(!ids.has(id))this.panels.delete(id);
    this.refresh();
  }
  refresh(){for(const r of this.records){
    const c=r.config,powered=c.powered,reset=this.signal(c.rst),cs=this.signal(c.cs),dc=this.signal(c.dc);
    const electrical=JSON.stringify([powered,c.connected,reset,cs,dc,this.accepts(c)]);if(electrical!==r.electrical){r.epoch++;r.electrical=electrical;}
    r.panel.update({powered,resetHigh:reset===true,backlight:this.signal(c.led)===true});
  }}
  begin(controller,value,bits,mode,hz,lsb=false){
    this.refresh();
    const selected=this.records.filter(r=>r.config.connected&&r.config.controller===controller&&r.panel.powered&&r.panel.resetHigh&&this.signal(r.config.cs)===false&&this.accepts(r.config));
    if(!selected.length)return ()=>bits===16?65535:(1<<bits)-1;
    if(bits!==8&&bits!==16)throw Error('TFT hardware SPI currently supports 8/16-bit words');
    if(mode!==0&&mode!==3)throw Error('ILI9341 requires SPI mode 0 or 3');
    if(!Number.isFinite(hz)||hz<=0||hz>1e7)throw Error('ILI9341 SPI clock must not exceed 10 MHz');
    const directions=selected.map(r=>this.signal(r.config.dc));
    const epochs=selected.map(r=>r.epoch);
    function reverse(v,n){let result=0;for(let i=0;i<n;i++){result=result<<1|v&1;v>>>=1;}return result;}
    const wire=lsb?reverse(value,bits):value;
    return ()=>{
      this.refresh();const replies=[];
      selected.forEach((r,index)=>{
        const c=r.config;
        if(!this.records.includes(r)||r.epoch!==epochs[index]||!r.panel.powered||!r.panel.resetHigh||this.signal(c.cs)!==false||this.signal(c.dc)!==directions[index]||!this.accepts(c)||directions[index]===null)return;
        let reply=0;
        for(let shift=bits-8;shift>=0;shift-=8)reply=(reply<<8)|r.panel.transfer(wire>>>shift&255,directions[index]);
        if(this.readable(c))replies.push(reply);
      });
      if(replies.some(v=>v!==replies[0]))throw Error('SPI MISO contention between selected displays');
      const response=replies[0]??(bits===16?65535:255);return lsb?reverse(response,bits):response;
    };
  }
  frame(){this.refresh();return {tftDisplays:[...this.panels.values()].map(p=>p.frame())};}
}
module.exports={SpiDisplays};
