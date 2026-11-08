'use strict';
const {installFlash}=require('./pico-flash.cjs');
const {invokeArithmetic,invokeConversion}=require('./pico-float.cjs');
// Original host implementation of the documented RP2040 ROM ABI. No Raspberry
// Pi ROM image/instructions are included. Unknown functions fail explicitly.
function installRomAbi(mcu) {
  const flash=installFlash(mcu);
  const bytes=new Uint8Array(mcu.bootrom.buffer),view=new DataView(bytes.buffer),mapped=new Uint8Array(bytes.length),calls=new Map();
  const registers=mcu.core.registers,code=s=>s.charCodeAt(0)|(s.charCodeAt(1)<<8);
  function mark(address,size){mapped.fill(1,address,address+size);}
  function u16(address,value){view.setUint16(address,value,true);mark(address,2);}
  function u32(address,value){view.setUint32(address,value,true);mark(address,4);}
  let cursor=0x1000;
  function entry(name,fn){const at=cursor;cursor+=4;u16(at,0xbe7f);u16(at+2,0x4770);calls.set(at,{name,fn});return at|1;}
  function unsupported(name){return ()=>{throw Error('RP2040 BootROM ABI function is not implemented: '+name);};}
  u32(0,0x20042000);u32(4,entry('boot-reset',unsupported('boot-reset')));
  u32(8,entry('NMI',unsupported('NMI')));u32(12,entry('hard-fault',unsupported('hard-fault')));
  function span(address,size,write=false) {
    if(!Number.isInteger(size)||size<0||size>264*1024||address+size>0x100000000)throw Error('Invalid RP2040 ROM memory range');
    if(!size)return;
    if(address>=0x20000000&&address+size<=0x20042000||address>=0x50100000&&address+size<=0x50101000)return;
    if(!write&&(address>=0x10000000&&address+size<=0x10200000||address<bytes.length&&address+size<=bytes.length&&mapped.subarray(address,address+size).every(Boolean)))return;
    throw Error('RP2040 ROM memory range is not '+(write?'writable':'readable')+': 0x'+address.toString(16));
  }
  function copy(aligned){const [to,from,size]=registers;span(to,size,true);span(from,size);if(aligned&&(to%4||from%4))throw Error('Unaligned RP2040 memcpy44');for(let i=0;i<size;i++)mcu.writeUint8(to+i,mcu.readUint8(from+i));registers[0]=to;}
  function fill(aligned){const [to,value,size]=registers;span(to,size,true);if(aligned&&to%4)throw Error('Unaligned RP2040 memset4');for(let i=0;i<size;i++)mcu.writeUint8(to+i,value&255);registers[0]=to;}
  const functions=new Map([
    ['MS',entry('memset',()=>fill(false))],['S4',entry('memset4',()=>fill(true))],
    ['MC',entry('memcpy',()=>copy(false))],['C4',entry('memcpy44',()=>copy(true))],
    ['L3',entry('clz32',()=>registers[0]=Math.clz32(registers[0]))],
    ['T3',entry('ctz32',()=>{const x=registers[0];registers[0]=x?31-Math.clz32((x&-x)>>>0):32;})],
    ['P3',entry('popcount32',()=>{let x=registers[0],n=0;while(x){x=(x&(x-1))>>>0;n++;}registers[0]=n;})],
    ['R3',entry('reverse32',()=>{let x=registers[0],out=0;for(let i=0;i<32;i++){out=(out<<1)|(x&1);x>>>=1;}registers[0]=out>>>0;})],
  ]);
  for(const [name,fn] of [['IF',flash.connect],['EX',flash.exit],['FC',flash.flush],['CX',flash.enter],['RE',()=>flash.erase(...registers.subarray(0,4))],['RP',()=>flash.program(...registers.subarray(0,3))]])functions.set(name,entry(name,fn));
  for(const name of ['UB','DT','DE','WV'])functions.set(name,entry(name,unsupported(name)));
  const names=['add','sub','mul','div','compare-fast','compare-flags','sqrt','to-int','to-fixed','to-uint','to-ufixed','from-int','from-fixed','from-uint','from-ufixed','cos','sin','tan','sincos','exp','log','compare','atan2','from-int64','from-fixed64','from-uint64','from-ufixed64','to-int64','to-fixed64','to-uint64','to-ufixed64','convert-width'];
  for(const [table,label] of [[0x800,'float'],[0x900,'double']]) {
    const width=label==='float'?32:64;
    u16(table-2,32);for(let i=0;i<32;i++)u32(table+i*4,entry(label+'/'+names[i],i<4||i===6?()=>invokeArithmetic(registers,width,names[i]):i>=7&&i<=14||i===21||i>=23?()=>invokeConversion(registers,width,i):unsupported(label+'/'+names[i])));
  }
  u32(0x10,0x0301754d); // documented magic and compatible v3 table layout
  const data=new Map([['SF',0x800],['SD',0x900],['GR',0xa00],['CR',0xa10]]);u32(0xa00,0);
  const identity=Buffer.from('SQL Classroom RP2040 ABI\0','ascii');bytes.set(identity,0xa10);mark(0xa10,identity.length);
  function table(at,entries){for(const [key,value] of entries){u16(at,code(key));u16(at+2,value);at+=4;}u32(at,0);}
  table(0x100,functions);table(0x200,data);u16(0x14,0x100);u16(0x16,0x200);
  u16(0x18,entry('table-lookup',()=>{
    const table=registers[0],key=registers[1];
    for(let i=0;i<1024;i++){const at=table+i*4;span(at,4);const found=mcu.readUint16(at);if(!found){registers[0]=0;return;}if(found===key){registers[0]=mcu.readUint16(at+2);return;}}
    throw Error('RP2040 ROM lookup table is not terminated');
  }));
  for(const [method,size,get] of [['readUint8',1,'getUint8'],['readUint16',2,'getUint16'],['readUint32',4,'getUint32']]){
    const previous=mcu[method].bind(mcu);mcu[method]=address=>{
      address>>>=0;if(address>=bytes.length)return previous(address);
      if(address+size>bytes.length||!mapped.subarray(address,address+size).every(Boolean))throw Error('RP2040 BootROM ABI address is not implemented: 0x'+address.toString(16));
      return view[get](address,true);
    };
  }
  for(const method of ['writeUint8','writeUint16','writeUint32']){
    // Physical ROM ignores bus writes. TinyUSB's EP0 path even uses this when
    // endpoint_control is NULL; the data must stay immutable, not become RAM.
    const previous=mcu[method].bind(mcu);mcu[method]=(address,value)=>{address>>>=0;if(address<bytes.length)return;return previous(address,value);};
  }
  mcu.onBreak=immediate=>{
    const call=calls.get((mcu.core.PC-2)>>>0);
    if(immediate!==0x7f||!call)throw Error('RP2040 breakpoint '+immediate);
    call.fn();mcu.core.breakRewind=0; // BX LR is the next real Thumb instruction.
  };
  return {version:3,implementation:'classroom-host-abi',span};
}
module.exports={installRomAbi};
