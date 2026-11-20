'use strict';
// ILI9341 samples MOSI on rising edges and changes MISO on falling edges
// (modes 0/3). This observes GPIO, not Arduino symbols or sketch source.
class GpioSpi {
  constructor(bus,allowed,readable){this.bus=bus;this.allowed=allowed;this.readable=readable;this.states=new Map();}
  parameters(s){return {software:true,bits:8,mode:s.mode,hz:Number.isFinite(s.period)?1e9/s.period:0,lsb:false};}
  abort(s){if(s.bits)this.bus.trace.record(s.record.config,this.parameters(s),s.started,'aborted',[],[],s.dc,['interrupted']);s.bits=0;s.output=null;s.active=false;}
  prime(s){s.reply=s.record.panel.peek(s.dc);s.output=!!(s.reply&128);s.complete=false;}
  tick(){
    if(this.bus.records.length===0&&this.states.size===0)return;
    const bus=this.bus;bus.refresh();const live=new Set(bus.records);
    for(const [id,s]of this.states)if(!live.has(s.record)){this.abort(s);this.states.delete(id);}
    for(const r of bus.records){
      const c=r.config,clock=bus.level(c.sck),dc=bus.signal(c.dc);
      let s=this.states.get(c.id);
      if(!s){s={record:r,active:false,bits:0,output:null,lastClock:clock,period:Infinity};this.states.set(c.id,s);}
      const active=c.connected&&r.panel.powered&&r.panel.resetHigh&&bus.signal(c.cs)===false&&dc!==null&&this.allowed(c);
      if(!active){if(s.active)this.abort(s);s.lastClock=clock;continue;}
      if(!s.active||s.epoch!==r.epoch){
        if(s.active)this.abort(s);
        Object.assign(s,{active:true,bits:0,value:0,dc,epoch:r.epoch,mode:clock?3:0,lastClock:clock,period:Infinity,lastRise:null,started:bus.now()});this.prime(s);continue;
      }
      if(clock&&!s.lastClock){
        const now=bus.now();if(s.lastRise!==null){const period=now-s.lastRise;if(period<=0||period<100-1e-6)throw Error('ILI9341 GPIO SPI clock must not exceed 10 MHz');if(s.bits>0)s.period=Math.min(s.period,period);}
        s.lastRise=now;if(s.bits===0){s.started=now;s.value=0;s.period=Infinity;}
        s.value=(s.value<<1)|+bus.level(c.mosi);s.bits++;
        if(s.bits===8){
          const reply=r.panel.transfer(s.value,s.dc),readable=this.readable(c);
          bus.trace.record(c,this.parameters(s),s.started,'completed',[s.value],[readable?reply:255],s.dc,readable?[]:['no-miso']);
          s.bits=0;s.complete=true; // Hold the eighth MISO bit through its sampling edge.
        }
      }else if(!clock&&s.lastClock){
        if(s.complete)this.prime(s);else s.output=!!(s.reply&(128>>>s.bits));
      }
      s.lastClock=clock;
    }
  }
  input(pin){
    let output=null;
    for(const s of this.states.values())if(s.active&&s.record.config.miso===pin&&this.readable(s.record.config)&&s.output!==null){
      if(output!==null&&output!==s.output)throw Error('SPI MISO contention between selected displays');output=s.output;
    }
    return output;
  }
  abortAll(){for(const s of this.states.values())this.abort(s);}
}
module.exports={GpioSpi};
