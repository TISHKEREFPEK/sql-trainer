'use strict';
const FLASH=0x10000000,SIZE=2*1024*1024,RAM=0x20000000,RAM_END=RAM+264*1024,FAMILY=0xe48bff56;
function decodeUf2(base64) {
  if(typeof base64!=='string'||base64.length>5600000)throw Error('Pico UF2 is too large');
  const bytes=Buffer.from(base64,'base64');
  if(bytes.toString('base64')!==base64||bytes.length<512||bytes.length>4*1024*1024||bytes.length%512)throw Error('Invalid Pico UF2 encoding or length');
  const count=bytes.length/512,indices=new Set(),ranges=[],flash=new Uint8Array(SIZE);flash.fill(0xff);
  for(let at=0;at<bytes.length;at+=512) {
    if(bytes.readUInt32LE(at)!==0x0a324655||bytes.readUInt32LE(at+4)!==0x9e5d5157||bytes.readUInt32LE(at+508)!==0x0ab16f30)throw Error('Invalid UF2 magic');
    if(bytes.readUInt32LE(at+8)!==0x2000||bytes.readUInt32LE(at+28)!==FAMILY)throw Error('UF2 is not an RP2040 firmware image');
    const address=bytes.readUInt32LE(at+12),size=bytes.readUInt32LE(at+16),index=bytes.readUInt32LE(at+20);
    if(size<1||size>476||address<FLASH||address+size>FLASH+SIZE||bytes.readUInt32LE(at+24)!==count||index>=count||indices.has(index))throw Error('Invalid UF2 block range or sequence');
    indices.add(index);ranges.push({start:address-FLASH,end:address-FLASH+size,data:bytes.subarray(at+32,at+32+size)});
  }
  ranges.sort((a,b)=>a.start-b.start);let end=0;
  for(const range of ranges){if(range.start<end)throw Error('Overlapping UF2 flash blocks');flash.set(range.data,range.start);end=range.end;}
  const view=new DataView(flash.buffer);
  function contains(start,size){return ranges.some(r=>start>=r.start&&start+size<=r.end);}
  for(const offset of [0,256]) {
    if(!contains(offset,8))continue;const stack=view.getUint32(offset,true),reset=view.getUint32(offset+4,true),entry=(reset&0xfffffffe)>>>0;
    if(stack>RAM&&stack<=RAM_END&&stack%8===0&&(reset&1)&&entry>=FLASH&&entry<FLASH+SIZE&&contains(entry-FLASH,2))
      return {flash,stack,entry,vector:FLASH+offset,blocks:count};
  }
  throw Error('Pico firmware has no valid loaded Cortex-M0+ reset vector');
}
module.exports={decodeUf2};
