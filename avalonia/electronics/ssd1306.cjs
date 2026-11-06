'use strict';
// Original model of SSD1306 Rev 1.1 register/transport facts. The module uses
// a 128x64 panel with alternative COM wiring; A1/C8/DA12 is its normal view.
const parameterCounts={0x20:1,0x21:2,0x22:2,0x81:1,0x8d:1,0xa8:1,0xd3:1,0xd5:1,0xd9:1,0xda:1,0xdb:1,0x26:6,0x27:6,0x29:5,0x2a:5,0xa3:2};
const intervals=[5,64,128,256,3,4,25,2];
class Oled {
  constructor(cpu,hz,config){this.cpu=cpu;this.hz=hz;this.config=config;this.powered=false;this.readable=false;this.maxFrequency=400000;this.reset();}
  reset(){
    this.memory=new Uint8Array(1024);this.mode=2;this.column=0;this.page=0;this.columns=[0,127];this.pages=[0,7];this.pending=null;
    this.on=false;this.pump=false;this.inverse=false;this.entire=false;this.remap=false;this.reverse=false;this.com=0x12;this.contrast=127;
    this.start=0;this.offset=0;this.mux=64;this.divide=1;this.precharge=0x22;this.vcom=0x20;
    this.scroll=null;this.scrollActive=false;this.scrollFrames=0;this.frozen=0;this.scrollArea=[0,64];this.lastCycle=this.cpu.cycles;this.begin();
  }
  frequency(){return 370000/(this.divide*((this.precharge&15)+(this.precharge>>4)+50)*this.mux);}
  advance(){const elapsed=this.cpu.cycles-this.lastCycle;if(this.powered&&this.on&&this.pump&&this.scrollActive)this.scrollFrames+=elapsed/this.hz*this.frequency();this.lastCycle=this.cpu.cycles;}
  update(config){this.advance();if(config.powered&&!this.powered)this.reset();this.config=config;this.powered=config.powered;}
  begin(){this.control=true;this.continuation=false;this.dataMode=false;}
  command(value){
    if(this.pending){this.pending.data.push(value);if(this.pending.data.length===this.pending.count){const {command,data}=this.pending;this.pending=null;this.apply(command,data);}return;}
    if(parameterCounts[value]){this.pending={command:value,count:parameterCounts[value],data:[]};return;}
    this.apply(value,[]);
  }
  apply(command,data){
    const a=data[0];
    if(command<=15){this.column=(this.column&0x70)|command;return;}
    if(command>=0x10&&command<=0x1f){this.column=(this.column&15)|((command&7)<<4);return;}
    if(command>=0x40&&command<=0x7f){this.start=command&63;return;}
    if(command>=0xb0&&command<=0xb7){this.page=command&7;return;}
    switch(command){
      case 0x20:if(a>2)throw Error('OLED: неизвестный режим адресации.');this.mode=a;break;
      case 0x21:case 0x22:{const limit=command===0x21?127:7;const range=data.map(v=>v&limit);if(range[0]>range[1])throw Error('OLED: неверный диапазон памяти.');if(command===0x21){this.columns=range;this.column=range[0];}else{this.pages=range;this.page=range[0];}break;}
      case 0x81:this.contrast=a;break;
      case 0x8d:this.pump=!!(a&4);break;
      case 0xa0:case 0xa1:this.remap=!!(command&1);break;
      case 0xa4:case 0xa5:this.entire=!!(command&1);break;
      case 0xa6:case 0xa7:this.inverse=!!(command&1);break;
      case 0xa8:if((a&63)<15)throw Error('OLED: MUX должен быть 16–64.');this.mux=(a&63)+1;break;
      case 0xae:case 0xaf:this.on=!!(command&1);break;
      case 0xc0:case 0xc8:this.reverse=!!(command&8);break;
      case 0xd3:this.offset=a&63;break;
      case 0xd5:if((a>>4)!==8)throw Error('OLED: частота генератора подтверждена пока только для D5=0x8n.');this.divide=(a&15)+1;break;
      case 0xd9:if(!(a&15)||!(a>>4))throw Error('OLED: нулевой период предзаряда.');this.precharge=a;break;
      case 0xda:this.com=a&0x32;break;
      case 0xdb:this.vcom=a;break; // analog current/voltage are outside the pixel model
      case 0xa3:if(a>63||data[1]<1||a+data[1]>this.mux)throw Error('OLED: неверная область прокрутки.');this.scrollArea=[a,data[1]];break;
      case 0x26:case 0x27:case 0x29:case 0x2a:
        if(this.scrollActive)throw Error('OLED: остановите прокрутку перед изменением её параметров.');
        if((data[1]&7)>(data[3]&7))throw Error('OLED: неверные страницы прокрутки.');
        this.scroll={right:command===0x26||command===0x29,start:data[1]&7,end:data[3]&7,interval:intervals[data[2]&7],vertical:command>=0x29?data[4]&63:0};break;
      case 0x2e:this.frozen=this.steps();this.scrollActive=false;break;
      case 0x2f:if(!this.scroll)throw Error('OLED: сначала задайте параметры прокрутки.');this.scrollActive=true;this.scrollFrames=0;this.frozen=0;break;
      case 0xe3:break;
      default:throw Error('OLED: команда 0x'+command.toString(16)+' пока не реализована.');
    }
  }
  write(value){
    this.advance();
    if(this.control){if(value&63)throw Error('OLED: неверный управляющий байт I2C.');this.continuation=!!(value&128);this.dataMode=!!(value&64);this.control=false;return;}
    if(this.dataMode){
      if(this.scrollActive)throw Error('OLED: остановите прокрутку перед записью экрана.');this.frozen=0;
      this.memory[this.page*128+this.column]=value;
      if(this.mode===2)this.column=(this.column+1)&127;
      else if(this.mode===0){if(++this.column>this.columns[1]){this.column=this.columns[0];if(++this.page>this.pages[1])this.page=this.pages[0];}}
      else if(++this.page>this.pages[1]){this.page=this.pages[0];if(++this.column>this.columns[1])this.column=this.columns[0];}
    }else this.command(value);
    if(this.continuation)this.control=true;
  }
  steps(){return this.scrollActive?Math.floor(this.scrollFrames/(this.scroll?.interval??1)):this.frozen;}
  frame(){
    this.advance();const pixels=new Uint8Array(1024),visible=this.powered&&this.on&&this.pump,steps=this.steps();
    if(visible)for(let scan=0;scan<this.mux;scan++)for(let x=0;x<128;x++){
      let y=(scan+this.start-this.offset+64)&63,sourceX=this.remap?x:127-x;
      if(this.scroll){const [top,height]=this.scrollArea;if(this.scroll.vertical&&y>=top&&y<top+height)y=top+((y-top+steps*this.scroll.vertical)%height);
        if((y>>3)>=this.scroll.start&&(y>>3)<=this.scroll.end)sourceX=(sourceX+(this.scroll.right?-steps:steps)%128+128)&127;}
      let row=this.com&16?scan:scan<32?scan*2:(scan-32)*2+1;if(this.com&32)row^=1;if(!this.reverse)row=63-row;
      const bit=(this.memory[(y>>3)*128+sourceX]>>(y&7))&1;
      if(this.entire||!!bit!==this.inverse)pixels[(row>>3)*128+x]|=1<<(row&7);
    }
    return {id:this.config.id,width:128,height:64,powered:this.powered,display:visible,contrast:this.contrast,pixels:Buffer.from(pixels).toString('base64'),memory:Buffer.from(this.memory).toString('base64')};
  }
}
module.exports={Oled};
