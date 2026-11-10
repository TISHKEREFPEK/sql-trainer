'use strict';
const table=Uint32Array.from({length:256},(_,value)=>{for(let i=0;i<8;i++)value=value&1?0xedb88320^(value>>>1):value>>>1;return value>>>0;});
function crc(value,byte){return table[(value^byte)&255]^(value>>>8);}
// One bounded summary per configured display per response, not a pixel dump.
// Completed/aborted words enter the response in which they finish, including
// words begun before the previous response. CRC32 is diagnostic, not a signature.
class SpiTrace {
  constructor(now){this.now=now;this.entries=new Map();}
  record(config,parameters,started,outcome,tx=[],rx=[],dc=false,issues=[]){
    let entry=this.entries.get(config.id);
    if(!entry){
      entry={deviceId:config.id,controller:config.controller,software:parameters.software??false,mosi:config.mosi,sck:config.sck,miso:config.miso??null,
        words:0,completedWords:0,bytes:0,commands:0,dataBytes:0,ignoredWords:0,abortedWords:0,
        mode:parameters.mode,bits:parameters.bits,clockHz:parameters.hz,lsbFirst:parameters.lsb??false,parameterChanges:0,
        startedMs:started/1e6,endedMs:this.now()/1e6,txPrefix:[],rxPrefix:[],commandPrefix:[],txCrc32:0xffffffff,rxCrc32:0xffffffff,issues:[]};
      this.entries.set(config.id,entry);
    }
    entry.words++;entry.startedMs=Math.min(entry.startedMs,started/1e6);entry.endedMs=Math.max(entry.endedMs,this.now()/1e6);
    if(entry.mode!==parameters.mode||entry.bits!==parameters.bits||entry.clockHz!==parameters.hz||entry.lsbFirst!==(parameters.lsb??false)||entry.software!==(parameters.software??false))entry.parameterChanges++;
    if(outcome==='ignored')entry.ignoredWords++;
    else if(outcome==='aborted')entry.abortedWords++;
    else{
      entry.completedWords++;
      for(let i=0;i<tx.length;i++){
        const value=tx[i],response=rx[i];entry.bytes++;if(dc)entry.dataBytes++;else{entry.commands++;if(entry.commandPrefix.length<16)entry.commandPrefix.push(value);}
        if(entry.txPrefix.length<32){entry.txPrefix.push(value);entry.rxPrefix.push(response);}
        entry.txCrc32=crc(entry.txCrc32,value);entry.rxCrc32=crc(entry.rxCrc32,response);
      }
    }
    for(const issue of issues)if(!entry.issues.includes(issue))entry.issues.push(issue);
  }
  frame(){const result=[...this.entries.values()].map(e=>({...e,txCrc32:(e.txCrc32^0xffffffff)>>>0,rxCrc32:(e.rxCrc32^0xffffffff)>>>0}));this.entries.clear();return result;}
}
module.exports={SpiTrace};
