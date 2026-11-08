'use strict';
// RP2040 CPU/peripheral foundation. No original Raspberry Pi BootROM bytes are
// included. ROM-dependent firmware fails explicitly until the ROM ABI is ready.
const {RP2040,GPIOPinState,USBCDC}=require('./vendor/rp2040js/cjs/index.js');
const {SimulationClock}=require('./vendor/rp2040js/cjs/clock/simulation-clock.js');
const {StringDecoder}=require('node:string_decoder');
const {decodeUf2}=require('./pico-uf2.cjs');
let state;
function load(uf2) {
  const image=decodeUf2(uf2),clock=new SimulationClock(),mcu=new RP2040(clock),serial=[[],[],[]],incoming=[[],[],[]],messages=[];
  const log=(name,text)=>{if(messages.length<32)messages.push(name+': '+text);};
  mcu.logger={debug(){},info(){},warn:log,error(name,text){throw Error(name+': '+text);}};
  mcu.flash.fill(0xff);mcu.flash.set(image.flash);mcu.core.SP=image.stack;mcu.core.PC=image.entry;mcu.core.VTOR=image.vector;
  for(const method of ['readUint8','readUint16','readUint32']) {
    const read=mcu[method].bind(mcu);mcu[method]=address=>{
      if(address>=0&&address<0x4000)throw Error('RP2040 BootROM ABI is not implemented yet; this firmware cannot run in the current Pico foundation');
      return read(address);
    };
  }
  mcu.onBreak=code=>{throw Error('RP2040 breakpoint '+code);};
  let transmitted=0;const emit=(port,values)=>{for(const value of values){serial[port].push(value);transmitted++;}};
  mcu.uart.forEach((uart,index)=>uart.onByte=value=>emit(index+1,[value]));
  let usbReady=false;const usb=new USBCDC(mcu.usbCtrl);usb.onSerialData=values=>emit(0,values);usb.onDeviceConnected=()=>usbReady=true;
  state={mcu,clock,serial,incoming,usb,messages,decoders:serial.map(()=>new StringDecoder('utf8')),rising:Array(30).fill(null),pulses:Array(30).fill(null),frequency:Array(30).fill(null),failed:false,get usbReady(){return usbReady;},
    get transmitted(){return transmitted;},resetTransmitted(){transmitted=0;}};
  return {ready:true,board:'pico',flashBytes:2*1024*1024,ramBytes:264*1024,vector:image.vector,entry:image.entry,serialPorts:3,bootrom:false};
}
function step(input) {
  if(!state||state.failed)throw Error('Load Pico firmware before stepping');
  const {mcu,clock,incoming}=state,digital=input.digital??[],analog=input.analog??[],port=input.serialPort??0,duration=input.milliseconds??10;
  if(!Array.isArray(digital)||digital.length>30||digital.some(v=>typeof v!=='boolean'))throw Error('Invalid Pico GPIO input');
  if(!Array.isArray(analog)||analog.length>4||analog.some(v=>typeof v!=='number'||!Number.isFinite(v)||v<0||v>3.3))throw Error('Pico ADC voltage must be 0..3.3 V');
  if(!Number.isInteger(port)||port<0||port>2||input.serialInput!=null&&typeof input.serialInput!=='string')throw Error('Invalid Pico serial port/input');
  if(!Number.isFinite(duration)||duration<=0||duration>50)throw Error('Pico step must be 0..50 ms');
  const bytes=Buffer.from(input.serialInput??'','utf8');if(bytes.length+incoming[port].length>4096)throw Error('Pico serial input queue is full');
  if(port===0&&bytes.length&&!state.usbReady)throw Error('Pico USB CDC has not initialized');
  if(input.devices?.length)throw Error('Pico circuit I2C bridge is not connected yet');
  digital.forEach((v,i)=>mcu.gpio[i].setInputValue(v));analog.forEach((v,i)=>mcu.adc.channelValues[i]=Math.round(v*4095/3.3));incoming[port].push(...bytes);
  function feed(){
    if(incoming[0].length&&state.usbReady&&!state.usb.txFIFO.full)state.usb.sendSerialByte(incoming[0].shift());
    mcu.uart.forEach((uart,i)=>{if(incoming[i+1].length&&uart.rxEnabled&&!(uart.flags&64))uart.feedByte(incoming[i+1].shift());});
  }
  const start=clock.nanos,target=start+duration*1e6,levels=mcu.gpio.map(p=>p.value===GPIOPinState.High),last=Array(30).fill(start),accumulated=Array(30).fill(0);
  const release=mcu.gpio.map((pin,i)=>pin.addListener(value=>{
    const now=clock.nanos,high=value===GPIOPinState.High;if(levels[i])accumulated[i]+=now-last[i];
    if(high!==levels[i]){if(high){if(state.rising[i]!==null&&now>state.rising[i])state.frequency[i]=1e9/(now-state.rising[i]);state.rising[i]=now;}
      else if(state.rising[i]!==null)state.pulses[i]=(now-state.rising[i])/1000;}
    levels[i]=high;last[i]=now;
  }));
  let instructions=0;
  try {
    while(clock.nanos<target&&instructions<1000000&&state.transmitted<8192) {
      if((instructions&127)===0)feed();
      if(mcu.core.waiting){const next=clock.nanosToNextAlarm;clock.tick(Math.min(target-clock.nanos,next>0?next:target-clock.nanos));}
      else{const cycles=mcu.core.executeInstruction();clock.tick(cycles*1e9/mcu.clkSys);instructions++;}
    }
  }catch(error){state.failed=true;throw error;}finally{release.forEach(remove=>remove());}
  const elapsed=clock.nanos-start;
  const pins=mcu.gpio.map((pin,i)=>{if(levels[i])accumulated[i]+=clock.nanos-last[i];return {output:pin.outputEnable,voltage:elapsed>0?3.3*accumulated[i]/elapsed:pin.outputValue?3.3:0,pullUp:pin.value===GPIOPinState.InputPullUp,pulseUs:state.pulses[i],frequency:state.frequency[i]};});
  const serialPorts=state.serial.map((values,i)=>{const text=state.decoders[i].write(Buffer.from(values));values.length=0;return text;});state.resetTransmitted();
  const diagnostics=state.messages.splice(0);
  return {pins,serialPorts,serialPending:incoming.map(values=>values.length),milliseconds:clock.nanos/1e6,complete:clock.nanos>=target,instructions,clockHz:mcu.clkSys,diagnostics};
}
module.exports={load,step};
if(require.main===module)require('node:readline').createInterface({input:process.stdin,crlfDelay:Infinity}).on('line',line=>{
  try{if(line.length>5700000)throw Error('Pico request is too large');const request=JSON.parse(line);const output=request.uf2!=null?load(request.uf2):step(request);process.stdout.write(JSON.stringify(output)+'\n');}
  catch(error){process.stdout.write(JSON.stringify({error:error.message})+'\n');}
});
