'use strict';
// Original functional ILI9341 four-wire controller. Register facts: Ilitek
// ILI9341 V1.11. No Cirkit source or image is embedded here. This panel has
// reversed native column wiring and BGR filters (normal Adafruit MADCTL 0x48).
const W=240,H=320;
const parameters=new Map([[0x2a,4],[0x2b,4],[0x30,4],[0x33,6],[0x36,1],[0x37,2],[0x3a,1],[0xd9,1],
  [0xb1,2],[0xb2,2],[0xb3,4],[0xb4,1],[0xb6,3],[0xb7,1],[0xc0,1],[0xc1,1],[0xc5,2],[0xc7,1],
  [0xcb,5],[0xcf,3],[0xe0,15],[0xe1,15],[0xe8,3],[0xea,2],[0xed,4],[0xef,3],[0xf2,1],[0xf7,1],[0x26,1],[0x51,1],[0x53,1],[0x55,1]]);
class Ili9341 {
  constructor(id,now=()=>0){this.id=id;this.now=now;this.gram=new Uint32Array(W*H);this.powered=false;this.resetHigh=false;this.backlight=false;this.generation=0;this.cached=null;this.reset();}
  reset(){
    this.command=0;this.params=[];this.pixelBytes=[];this.madctl=0;this.format=0x66;this.asleep=true;this.displayOn=false;this.inverted=false;this.idle=false;this.partial=false;
    this.x0=0;this.x1=239;this.y0=0;this.y1=319;this.x=0;this.y=0;this.partialTop=0;this.partialBottom=319;
    this.scrollTop=0;this.scrollHeight=320;this.scrollStart=0;this.readOffset=0;this.readIndex=0;this.readyAt=Infinity;this.registers=new Map();this.dirty();
  }
  dirty(){this.generation++;this.cached=null;}
  update({powered,resetHigh=true,backlight=true}){
    if(typeof powered!=='boolean'||typeof resetHigh!=='boolean'||typeof backlight!=='boolean')throw Error('Invalid ILI9341 electrical state');
    if(powered!==this.powered){this.gram.fill(0);this.reset();}
    if(this.resetHigh&&!resetHigh)this.reset();
    if(powered!==this.powered||resetHigh!==this.resetHigh||backlight!==this.backlight)this.dirty();
    this.powered=powered;this.resetHigh=resetHigh;this.backlight=backlight;
  }
  get visible(){return this.powered&&this.resetHigh&&this.backlight&&!this.asleep&&this.displayOn&&this.now()>=this.readyAt;}
  peek(dc){
    if(!this.powered||!this.resetHigh)return 255;
    if(!dc||![0x04,0x09,0x0a,0x0b,0x0c,0xda,0xdb,0xdc,0xd3,0x2e,0x3e].includes(this.command))return 0;
    const {readIndex,x,y}=this;const value=this.read();this.readIndex=readIndex;this.x=x;this.y=y;return value;
  }
  transfer(data,dc){
    if(!Number.isInteger(data)||data<0||data>255||typeof dc!=='boolean')throw Error('Invalid ILI9341 SPI byte');
    if(!this.powered||!this.resetHigh)return 255;
    if(!dc){this.receiveCommand(data);return 0;}
    if([0x04,0x09,0x0a,0x0b,0x0c,0xda,0xdb,0xdc,0xd3,0x2e,0x3e].includes(this.command))return this.read();
    this.receiveData(data);return 0;
  }
  receiveCommand(value){
    this.command=value;this.params=[];this.pixelBytes=[];this.readIndex=0;
    switch(value){
      case 1:this.reset();break;
      case 0x10:this.asleep=true;this.dirty();break;
      case 0x11:this.asleep=false;this.readyAt=this.now()+120e6;this.dirty();break;
      case 0x12:this.partial=true;this.dirty();break;
      case 0x13:this.partial=false;this.dirty();break;
      case 0x20:this.inverted=false;this.dirty();break;
      case 0x21:this.inverted=true;this.dirty();break;
      case 0x28:this.displayOn=false;this.dirty();break;
      case 0x29:this.displayOn=true;this.dirty();break;
      case 0x38:this.idle=false;this.dirty();break;
      case 0x39:this.idle=true;this.dirty();break;
      case 0x2c:case 0x2e:this.x=this.x0;this.y=this.y0;break;
    }
  }
  receiveData(value){
    if(this.command===0x2c||this.command===0x3c){
      const bits=this.format&7,bytes=bits===5?2:bits===6?3:0;
      if(!bytes)throw Error('ILI9341 SPI pixel format must be RGB565 or RGB666');
      this.pixelBytes.push(value);if(this.pixelBytes.length<bytes)return;
      const p=this.pixelBytes;let color;
      if(bytes===2){const v=(p[0]<<8)|p[1],r=v>>>11,g=v>>>5&63,b=v&31;color=((r<<3|r>>>2)<<16)|((g<<2|g>>>4)<<8)|(b<<3|b>>>2);}
      else{const expand=v=>(v&252)|(v>>>6);color=(expand(p[0])<<16)|(expand(p[1])<<8)|expand(p[2]);}
      const at=this.address();if(at!==null){this.gram[at]=color;this.dirty();}this.next();this.pixelBytes=[];return;
    }
    const count=parameters.get(this.command);if(!count||this.params.length>=count)return;
    this.params.push(value);if(this.params.length!==count)return;
    const p=this.params,word=i=>(p[i]<<8)|p[i+1];this.registers.set(this.command,p.slice());
    switch(this.command){
      case 0x2a:this.x0=word(0);this.x1=word(2);break;
      case 0x2b:this.y0=word(0);this.y1=word(2);break;
      case 0x30:this.partialTop=word(0);this.partialBottom=word(2);this.dirty();break;
      case 0x33:if(word(0)+word(2)+word(4)!==H||word(2)===0)throw Error('ILI9341 scroll areas must cover 320 rows');this.scrollTop=word(0);this.scrollHeight=word(2);this.dirty();break;
      case 0x36:this.madctl=p[0];this.dirty();break;
      case 0x37:this.scrollStart=word(0);this.dirty();break;
      case 0x3a:this.format=p[0];break;
      case 0xd9:this.readOffset=p[0]&15;break;
    }
  }
  address(){
    let x=(this.madctl&32)?this.y:this.x,y=(this.madctl&32)?this.x:this.y;
    if(!(this.madctl&64))x=W-1-x;if(this.madctl&128)y=H-1-y;
    return x>=0&&x<W&&y>=0&&y<H?y*W+x:null;
  }
  next(){if(++this.x>this.x1){this.x=this.x0;if(++this.y>this.y1)this.y=this.y0;}}
  read(){
    if(this.command===0x2e||this.command===0x3e){
      if(this.readIndex++===0)return 0;
      const component=(this.readIndex-2)%3,at=this.address(),color=at===null?0:this.gram[at],result=(color>>>(16-component*8))&252;
      if(component===2)this.next();return result;
    }
    const power=8|(!this.asleep?0x90:0)|(this.displayOn?4:0)|(this.idle?64:0)|(this.partial?32:0);
    const replies={4:[0,0,0x93,0x41],9:[0,0,0,power,0],10:[power],11:[this.madctl],12:[this.format],0xda:[0],0xdb:[0x93],0xdc:[0x41],0xd3:[0,0,0x93,0x41]};
    return replies[this.command]?.[this.readOffset+this.readIndex++]??0;
  }
  frame(){
    const visible=this.visible;
    if(this.cached&&this.cachedVisible===visible)return this.cached;
    const bytes=Buffer.alloc(W*H*3);const swap=!(this.madctl&8);
    if(visible)for(let y=0;y<H;y++){
      if(this.partial&&(this.partialTop<=this.partialBottom?(y<this.partialTop||y>this.partialBottom):(y<this.partialTop&&y>this.partialBottom)))continue;
      const top=this.scrollTop,height=this.scrollHeight,scroll=((this.scrollStart-top)%height+height)%height;
      const row=y>=top&&y<top+height?top+(y-top+scroll)%height:y;
      for(let x=0;x<W;x++){
        let color=this.gram[row*W+x];if(swap)color=((color&255)<<16)|(color&65280)|(color>>>16&255);
        if(this.inverted)color^=0xffffff;
        if(this.idle)color=(color&0x800000?0xff0000:0)|(color&0x8000?0xff00:0)|(color&0x80?255:0);
        const at=(y*W+x)*3;bytes[at]=color>>>16;bytes[at+1]=color>>>8;bytes[at+2]=color;
      }
    }
    this.cachedVisible=visible;return this.cached={id:this.id,width:W,height:H,pixels:bytes.toString('base64'),display:visible};
  }
}
module.exports={Ili9341};
