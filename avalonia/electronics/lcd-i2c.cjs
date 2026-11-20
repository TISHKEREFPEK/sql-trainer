'use strict';
// HD44780 command protocol and common PCF8574 backpack wiring:
// P0 RS, P1 RW, P2 E, P3 backlight, P4..P7 DB4..DB7.
// Sources/limits are documented in docs/architecture.md; no Cirkit code used.
class Lcd {
  constructor(cpu,hz,config){this.cpu=cpu;this.hz=hz;this.config=config;this.powered=false;this.readable=true;this.maxFrequency=100000;this.reset();}
  reset(){this.ddram=new Uint8Array(128).fill(32);this.cgram=new Uint8Array(64);this.address=0;this.cg=false;this.shift=0;this.increment=true;this.entryShift=false;this.display=false;this.cursor=false;this.blink=false;this.four=false;this.twoLines=false;this.high=null;this.readHigh=true;this.latch=255;this.busyUntil=this.cpu.cycles+this.hz*.015;}
  update(config){if(config.powered&&!this.powered)this.reset();this.config=config;this.powered=config.powered;}
  busy(){return this.cpu.cycles<this.busyUntil;}
  next(){
    if(this.cg)this.address=(this.address+(this.increment?1:63))&63;
    else if(!this.twoLines)this.address=(this.address+(this.increment?1:79))%80;
    else if(this.increment)this.address=this.address===0x27?0x40:this.address===0x67?0:(this.address+1)&127;
    else this.address=this.address===0?0x67:this.address===0x40?0x27:(this.address-1)&127;
  }
  accept(value,rs){
    if(this.busy())return;
    let micros=37;
    if(rs){if(this.cg)this.cgram[this.address&63]=value&31;else this.ddram[this.address]=value;this.next();if(!this.cg&&this.entryShift)this.shift=(this.shift+(this.increment?1:39))%40;}
    else if(value&128){this.cg=false;this.address=value&127;}
    else if(value&64){this.cg=true;this.address=value&63;}
    else if(value&32){if(value&4)throw Error('LCD: режим символов 5x10 пока не реализован; используйте 5x8.');if(!(value&8)&&this.display)throw Error('LCD: однострочный режим контроллера пока не реализован для этих модулей.');this.twoLines=!!(value&8);this.four=!(value&16);this.high=null;this.readHigh=true;}
    else if(value&16){const right=!!(value&4);if(value&8)this.shift=(this.shift+(right?39:1))%40;else {const previous=this.increment;this.increment=right;this.next();this.increment=previous;}}
    else if(value&8){if(value&4&&!this.twoLines)throw Error('LCD: однострочный режим контроллера пока не реализован для этих модулей.');this.display=!!(value&4);this.cursor=!!(value&2);this.blink=!!(value&1);}
    else if(value&4){this.increment=!!(value&2);this.entryShift=!!(value&1);}
    else if(value&2){this.cg=false;this.address=0;this.shift=0;micros=1520;}
    else if(value&1){this.ddram.fill(32);this.cg=false;this.address=0;this.shift=0;this.increment=true;micros=1520;}
    this.busyUntil=this.cpu.cycles+Math.ceil(micros*this.hz/1000000);
  }
  write(value){
    const previous=this.latch;this.latch=value;
    if(!(previous&4)||(value&4))return; // data latched on E falling edge
    const rs=!!(previous&1),read=!!(previous&2);
    if(read){if(this.four)this.readHigh=!this.readHigh;if(!this.four||this.readHigh){if(rs&&!this.busy()){this.next();this.busyUntil=this.cpu.cycles+Math.ceil(37*this.hz/1000000);}}return;}
    this.readHigh=true;
    const nibble=previous>>4;
    if(!this.four){this.accept(nibble<<4,rs);return;}
    if(this.high===null){this.high={value:nibble,rs};return;}
    const high=this.high;this.high=null;if(high.rs===rs)this.accept(high.value<<4|nibble,rs);
  }
  read(){
    if(!(this.latch&2)||!(this.latch&4))return this.latch;
    const rs=!!(this.latch&1);const value=rs?(this.cg?this.cgram[this.address&63]:this.ddram[this.address]):((this.busy()?128:0)|this.address);
    return (this.latch&15)|(((this.readHigh?value>>4:value)&15)<<4 & this.latch&240);
  }
  frame(){
    const {id,columns,rows}=this.config,offsets=[0,64,columns,64+columns],cells=[];
    for(let row=0;row<rows;row++)for(let col=0;col<columns;col++)cells.push(this.ddram[(offsets[row]&64)+((offsets[row]%64+col+this.shift)%40)]);
    const visibleCursor=this.cg?-1:offsets.slice(0,rows).flatMap((base,row)=>Array.from({length:columns},(_,col)=>({index:row*columns+col,address:(base&64)+((base%64+col+this.shift)%40)}))).find(c=>c.address===this.address)?.index??-1;
    return {id,columns,rows,cells,cgram:Array.from(this.cgram),powered:this.powered,display:this.display&&this.powered,backlight:!!(this.latch&8)&&this.powered,cursor:this.cursor||this.blink?visibleCursor:-1,underline:this.cursor,blink:this.blink&&Math.floor(this.cpu.cycles/this.hz/.4096)%2===0};
  }
}
module.exports={Lcd};
