'use strict';
// Offline AVR runner. AVR8js is MIT licensed; see vendor/avr8js/LICENSE.
const avr = require('./vendor/avr8js/cjs/index.js');
const {getBoard,configure} = require('./avr-devices.cjs');
const {StringDecoder} = require('node:string_decoder');
const {I2cBus} = require('./i2c-bus.cjs');
const readline = require('node:readline');
let cpu, board, ports, adc, usarts, serial, decoders, incoming, pinMap, rising, pulses, frequencies, lcdBus, transmitted=0;
function load(hex, id='uno') {
  board=getBoard(id);
  if(typeof hex!=='string'||hex.length>1000000)throw Error('Firmware is too large');
  const program=new Uint16Array(board.flashBytes/2);program.fill(0xffff);
  const bytes=new Uint8Array(program.buffer);let upper=0,ended=false;
  for(const line of hex.trim().split(/\r?\n/)) {
    if(!/^:[0-9a-f]+$/i.test(line)||line.length%2!==1)throw Error('Invalid Intel HEX');
    const record=Buffer.from(line.slice(1),'hex'),count=record[0];
    if(record.length!==count+5||record.reduce((a,b)=>a+b,0)%256!==0)throw Error('Invalid HEX checksum');
    const address=upper+record.readUInt16BE(1),type=record[3];
    if(type===0){if(address+count>bytes.length)throw Error('Firmware exceeds '+board.name+' flash');bytes.set(record.subarray(4,4+count),address);}
    else if(type===1){if(count!==0||record.readUInt16BE(1)!==0)throw Error('Invalid HEX end record');ended=true;break;}
    else if(type===2||type===4){if(count!==2)throw Error('Invalid HEX address record');upper=record.readUInt16BE(4)*(type===2?16:65536);}
    else if(type!==3&&type!==5)throw Error('Unsupported HEX record');
  }
  if(!ended)throw Error('Incomplete HEX');
  cpu=new avr.CPU(program,board.ramStart+board.ramBytes-0x100);
  const peripherals=configure(cpu,board);({ports,adc,usarts}=peripherals);lcdBus=new I2cBus(cpu,board.clockHz,peripherals.twi);
  pinMap=board.pinMap.map(pin=>({port:ports[pin[0]],bit:Number(pin.slice(1))}));
  serial=usarts.map(()=>[]);decoders=usarts.map(()=>new StringDecoder('utf8'));incoming=usarts.map(()=>[]);transmitted=0;
  usarts.forEach((uart,index)=>{uart.onByteTransmit=value=>{serial[index].push(value);transmitted++;};uart.onRxComplete=()=>feed(index);});
  rising=pinMap.map(()=>null);pulses=pinMap.map(()=>null);frequencies=pinMap.map(()=>null);
  return {ready:true,board:board.id,flashBytes:bytes.length,dataBytes:cpu.data.length,pins:pinMap.length,serialPorts:usarts.length};
}
function feed(index){if(incoming[index].length&&!usarts[index].rxBusy&&usarts[index].writeByte(incoming[index][0]))incoming[index].shift();}
function step(input) {
  if(!cpu)throw Error('Load firmware first');
  lcdBus.update(input.devices??[]);
  if(input.digital!=null&&(!Array.isArray(input.digital)||input.digital.length>board.digitalCount))throw Error('Invalid digital input count');
  if(input.analog!=null&&(!Array.isArray(input.analog)||input.analog.length>board.analogCount))throw Error('Invalid analog input count');
  if(input.serialInput!=null){
    const index=input.serialPort??0;
    if(typeof input.serialInput!=='string'||!Number.isInteger(index)||index<0||index>=usarts.length)throw Error('Invalid serial input');
    const values=Buffer.from(input.serialInput,'utf8');if(values.length+incoming[index].length>4096)throw Error('Serial input queue is full');incoming[index].push(...values);
  }
  for(let i=0;i<board.digitalCount;i++)pinMap[i].port.setPin(pinMap[i].bit,Boolean(input.digital?.[i]));
  for(let i=0;i<board.analogCount;i++){
    const value=input.analog?.[i]??0;if(typeof value!=='number'||!Number.isFinite(value))throw Error('Invalid analog voltage');
    adc.channelValues[i]=Math.max(0,Math.min(5,value));const pin=pinMap[board.digitalCount+i];pin.port.setPin(pin.bit,value>2.5);
  }
  const start=cpu.cycles,target=start+board.clockHz/20;
  const accumulated=pinMap.map(()=>0),last=pinMap.map(()=>start),levels=pinMap.map(pin=>pin.port.pinState(pin.bit)===avr.PinState.High);
  const listeners=Object.values(ports).map(port=>{
    const indices=pinMap.map((pin,index)=>pin.port===port?index:-1).filter(index=>index>=0);
    const listener=()=>{for(const index of indices){
      const high=port.pinState(pinMap[index].bit)===avr.PinState.High;
      if(levels[index])accumulated[index]+=cpu.cycles-last[index];
      if(high!==levels[index]){if(high){if(rising[index]!==null&&cpu.cycles>rising[index])frequencies[index]=board.clockHz/(cpu.cycles-rising[index]);rising[index]=cpu.cycles;}
        else if(rising[index]!==null)pulses[index]=(cpu.cycles-rising[index])*1000000/board.clockHz;}
      last[index]=cpu.cycles;levels[index]=high;
    }};port.addListener(listener);return {port,listener};
  });
  // Finish a frame early under heavy serial traffic, preserving every byte
  // rather than silently dropping output when a host-side buffer fills.
  try {let instructions=0;while(cpu.cycles<target&&transmitted<8192&&lcdBus.trace.length<128){if((instructions++&127)===0)usarts.forEach((_,i)=>feed(i));avr.avrInstruction(cpu);cpu.tick();}}
  finally{listeners.forEach(({port,listener})=>port.removeListener(listener));}
  const pins=pinMap.map((pin,index)=>{
    if(levels[index])accumulated[index]+=cpu.cycles-last[index];const state=pin.port.pinState(pin.bit);
    return {output:state===avr.PinState.High||state===avr.PinState.Low,voltage:5*accumulated[index]/(cpu.cycles-start),pullUp:state===avr.PinState.InputPullUp,pulseUs:pulses[index],frequency:frequencies[index]};
  });
  const serialPorts=serial.map((values,index)=>decoders[index].write(Buffer.from(values)));serial=usarts.map(()=>[]);transmitted=0;
  return {pins,serial:serialPorts[0],serialPorts,serialPending:incoming.map(values=>values.length),milliseconds:cpu.cycles*1000/board.clockHz,...lcdBus.frame()};
}
readline.createInterface({input:process.stdin,crlfDelay:Infinity}).on('line',line=>{
  try{if(line.length>1100000)throw Error('Request too large');const input=JSON.parse(line);const output=input.hex!=null?load(input.hex,input.board??'uno'):step(input);process.stdout.write(JSON.stringify(output)+'\n');}
  catch(error){process.stdout.write(JSON.stringify({error:error.message})+'\n');}
});
