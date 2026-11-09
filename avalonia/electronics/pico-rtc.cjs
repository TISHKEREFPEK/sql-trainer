'use strict';
// Original register-level RTC model, independent of the host Date/time zone.
// RP2040 datasheet 4.8; pico-sdk hardware/regs/rtc.h. Domain-crossing latency
// and external GPIN clocks remain outside this functional model.
const {BasePeripheral}=require('./vendor/rp2040js/cjs/peripherals/peripheral.js');
class PicoRtc extends BasePeripheral {
  constructor(mcu,clock){
    super(mcu,'RTC_BASE');this.clock=clock;
    this.alarm=clock.createAlarm(()=>{this.phase=0;this.last=clock.nanos;this.increment();this.match();this.arm();});
    this.reset();
  }
  reset(){
    this.alarm.cancel();this.divider=0;this.setup0=0;this.setup1=0;this.control=0;
    this.irq0=0;this.irq1=0;this.intr=0;this.inte=0;this.intf=0;this.phase=0;this.last=this.clock.nanos;this.hz=0;
    this.date={year:0,month:0,day:0,dotw:0,hour:0,min:0,sec:0};this.latchedDate=0;this.updateIrq();
  }
  frequency(){
    const c=this.rp2040.clocks,ctrl=c.rtcCtrl;
    if(!(ctrl&0x800)||ctrl&0x400)return 0;
    const source=ctrl>>>5&7;
    if(source>=4)throw Error('RP2040 external RTC clock is not supported');
    const input=[this.rp2040.pllUsb.frequency,this.rp2040.pllSys.frequency,this.rp2040.roscFreq,this.rp2040.xoscFreq][source];
    const divide=(c.rtcDiv>>>8)?(c.rtcDiv>>>0)/256:65536,frequency=input/divide;
    if(!Number.isFinite(frequency)||frequency<1||frequency>65536)throw Error('RP2040 RTC reference must be 1..65536 Hz');
    return frequency;
  }
  settle(){
    if(this.hz>0&&(this.control&1))this.phase+=((this.clock.nanos-this.last)/1e9)*this.hz/(this.divider+1);
    this.last=this.clock.nanos;this.alarm.cancel();
  }
  arm(){
    this.alarm.cancel();this.last=this.clock.nanos;this.hz=(this.control&1)?this.frequency():0;
    if(this.hz>0){const delay=(1-this.phase)*(this.divider+1)*1e9/this.hz;this.alarm.schedule(Math.max(1,delay));}
  }
  updateIrq(){this.rp2040.setInterrupt(25,!!((this.intr&this.inte)|this.intf));}
  monthDays(){const d=this.date;return [0,31,(d.year%4===0&&!(this.control&256))?29:28,31,30,31,30,31,31,30,31,30,31][d.month];}
  load(){
    const a=this.setup0,b=this.setup1;
    this.date={year:a>>>12&4095,month:a>>>8&15,day:a&31,dotw:b>>>24&7,hour:b>>>16&31,min:b>>>8&63,sec:b&63};
    const d=this.date;
    if(d.month<1||d.month>12||d.day<1||d.day>this.monthDays()||d.dotw>6||d.hour>23||d.min>59||d.sec>59)
      throw Error('RP2040 invalid RTC calendar values are not supported');
    this.phase=0;
  }
  increment(){
    const d=this.date;
    if(d.month<1||d.month>12||d.day<1)throw Error('RP2040 RTC must be loaded with a valid calendar before ticking');
    if(++d.sec<60)return;d.sec=0;if(++d.min<60)return;d.min=0;if(++d.hour<24)return;d.hour=0;
    d.dotw=(d.dotw+1)%7;if(++d.day<=this.monthDays())return;d.day=1;if(++d.month<=12)return;d.month=1;d.year=(d.year+1)&4095;
  }
  match(){
    if(!(this.irq0&0x10000000))return;
    const d=this.date,a=this.irq0,b=this.irq1;
    if((a&0x4000000)&&d.year!==(a>>>12&4095)||(a&0x2000000)&&d.month!==(a>>>8&15)||(a&0x1000000)&&d.day!==(a&31))return;
    if((b&0x80000000)&&d.dotw!==(b>>>24&7)||(b&0x40000000)&&d.hour!==(b>>>16&31)||(b&0x20000000)&&d.min!==(b>>>8&63)||(b&0x10000000)&&d.sec!==(b&63))return;
    this.intr=1;this.updateIrq();
  }
  dateWord(){const d=this.date;return d.year<<12|d.month<<8|d.day;}
  readUint32(offset){
    const d=this.date;
    switch(offset){
      case 0:return this.divider;
      case 4:return this.setup0;case 8:return this.setup1;
      case 12:return this.control|((this.control&1)&&this.hz>0?2:0);
      case 16:return (this.irq0|((this.irq0&0x10000000)?0x20000000:0))>>>0;
      case 20:return this.irq1;
      case 24:return this.latchedDate;
      case 28:this.latchedDate=this.dateWord();return d.dotw<<24|d.hour<<16|d.min<<8|d.sec;
      case 32:return this.intr;case 36:return this.inte;case 40:return this.intf;case 44:return (this.intr&this.inte)|this.intf;
      default:return super.readUint32(offset);
    }
  }
  writeUint32(offset,value){
    switch(offset){
      case 0:this.settle();this.divider=value&65535;this.arm();return;
      case 4:this.setup0=value&0xffff1f;return;
      case 8:this.setup1=value&0x71f3f3f;return;
      case 12:this.settle();this.control=value&0x101;if(value&16)this.load();this.arm();return;
      case 16:this.irq0=value&0x17ffff1f;if(!(this.irq0&0x10000000)){this.intr=0;this.updateIrq();}return;
      case 20:this.irq1=(value&0xf71f3f3f)>>>0;return;
      case 36:this.inte=value&1;this.updateIrq();return;
      case 40:this.intf=value&1;this.updateIrq();return;
      case 24:case 28:case 32:case 44:return; // read-only
      default:return super.writeUint32(offset,value);
    }
  }
}
function installRtc(mcu,clock){
  const rtc=new PicoRtc(mcu,clock);mcu.peripherals[0x4005c]=rtc;
  const clocks=mcu.clocks,read=clocks.readUint32.bind(clocks),write=clocks.writeUint32.bind(clocks);
  clocks.readUint32=offset=>offset===0x70?clocks.rtcDiv>>>0:read(offset);
  clocks.writeUint32=(offset,value)=>{const changed=offset===0x6c||offset===0x70;if(changed)rtc.settle();write(offset,value);if(changed)rtc.arm();};
  const resets=mcu.peripherals[0x4000c],resetWrite=resets.writeUint32.bind(resets);
  resets.writeUint32=(offset,value)=>{resetWrite(offset,value);if(offset===0&&(value&0x8000))rtc.reset();};
  return rtc;
}
module.exports={installRtc};
