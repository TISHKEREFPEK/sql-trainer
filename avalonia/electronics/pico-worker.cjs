'use strict';
// RP2040 CPU/peripheral foundation. No original Raspberry Pi BootROM bytes are
// included. Unknown calls fail explicitly in our original host ROM ABI.
const {RP2040,GPIOPinState,USBCDC}=require('./vendor/rp2040js/cjs/index.js');
const {SimulationClock}=require('./vendor/rp2040js/cjs/clock/simulation-clock.js');
const {StringDecoder}=require('node:string_decoder');
const {decodeUf2}=require('./pico-uf2.cjs');
const {installRomAbi}=require('./pico-rom.cjs');
const {PicoI2c}=require('./pico-i2c.cjs');
let state;
function load(uf2) {
  const image=decodeUf2(uf2),clock=new SimulationClock(),mcu=new RP2040(clock),serial=[[],[],[]],incoming=[[],[],[]],messages=[];
  const log=(name,text)=>{if(messages.length<32)messages.push(name+': '+text);};
  mcu.logger={debug(){},info(){},warn(name,text){if(/Instruction .*not implemented|Opcode:/.test(text))throw Error(name+': '+text);log(name,text);},error(name,text){throw Error(name+': '+text);}};
  mcu.flash.fill(0xff);mcu.flash.set(image.flash);mcu.core.SP=image.stack;mcu.core.PC=image.entry;mcu.core.VTOR=image.vector;
  installRomAbi(mcu);
  const sioRead=mcu.sio.readUint32.bind(mcu.sio),sioWrite=mcu.sio.writeUint32.bind(mcu.sio);let fifoErrors=0;
  mcu.sio.readUint32=offset=>{if(offset===0x50)return 2|fifoErrors;if(offset===0x58){fifoErrors|=8;return 0;}return sioRead(offset);};
  mcu.sio.writeUint32=(offset,value)=>{if(offset===0x50){fifoErrors&=~(value&12);return;}if(offset===0x54)throw Error('RP2040 second-core FIFO execution is not supported');return sioWrite(offset,value);};
  let transmitted=0;const emit=(port,values)=>{for(const value of values){serial[port].push(value);transmitted++;}};
  mcu.uart.forEach((uart,index)=>uart.onByte=value=>emit(index+1,[value]));
  let usbReady=false;const usb=new USBCDC(mcu.usbCtrl);usb.onSerialData=values=>emit(0,values);usb.onDeviceConnected=()=>usbReady=true;
  // A host SETUP packet follows the previous device event on the bus; delivering
  // it inside the register write acknowledging reset races endpoint setup.
  const setup=mcu.usbCtrl.sendSetupPacket.bind(mcu.usbCtrl);let setupPending=0;
  mcu.usbCtrl.sendSetupPacket=packet=>{if(++setupPending>8)throw Error('RP2040 USB setup queue overflow');const copy=packet.slice();clock.createAlarm(()=>{setupPending--;setup(copy);}).schedule(50000);};
  const usbRead=mcu.usbCtrl.readUint32.bind(mcu.usbCtrl),usbWrite=mcu.usbCtrl.writeUint32.bind(mcu.usbCtrl);let phyDirect=0,phyOverride=0;
  mcu.usbCtrl.readUint32=offset=>{if(offset===0x7c){const line=mcu.usbCtrl.sieStatus>>>2&3,dp=line&1,dm=line>>>1;return phyDirect|(dp&&!dm?0x10000:0)|(dp?0x20000:0)|(dm?0x40000:0);}if(offset===0x80)return phyOverride;return usbRead(offset);};
  mcu.usbCtrl.writeUint32=(offset,value)=>{if(offset===0x7c){phyDirect=value&0xff77;return;}if(offset===0x80){phyOverride=value&0x9fff;return;}return usbWrite(offset,value);};
  const endpointRead=mcu.usbCtrl.onEndpointRead;let waitingUsbRead=null;
  mcu.usbCtrl.onEndpointRead=(endpoint,size)=>{if(endpoint===usb.outEndpoint&&size>0&&usb.txFIFO.itemCount===0){waitingUsbRead={endpoint,size};return;}endpointRead(endpoint,size);};
  function resumeUsbRead(){if(waitingUsbRead&&usb.txFIFO.itemCount){const pending=waitingUsbRead;waitingUsbRead=null;endpointRead(pending.endpoint,pending.size);}}
  state={mcu,clock,serial,incoming,usb,messages,i2c:new PicoI2c(mcu,clock),decoders:serial.map(()=>new StringDecoder('utf8')),rising:Array(30).fill(null),pulses:Array(30).fill(null),frequency:Array(30).fill(null),failed:false,get usbReady(){return usbReady;},
    get transmitted(){return transmitted;},resetTransmitted(){transmitted=0;},resumeUsbRead};
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
  const devices=state.i2c.prepare(input.devices??[]);state.i2c.update(devices);
  digital.forEach((v,i)=>mcu.gpio[i].setInputValue(v));analog.forEach((v,i)=>mcu.adc.channelValues[i]=Math.round(v*4095/3.3));incoming[port].push(...bytes);
  function feed(){
    while(incoming[0].length&&state.usbReady&&!state.usb.txFIFO.full)state.usb.sendSerialByte(incoming[0].shift());state.resumeUsbRead();
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
    while(clock.nanos<target&&instructions<1000000&&state.transmitted<8192&&!state.i2c.full) {
      if((instructions&127)===0)feed();
      if(mcu.core.waiting){const next=clock.nanosToNextAlarm;clock.tick(Math.min(target-clock.nanos,next>0?next:target-clock.nanos));}
      else{const cycles=mcu.core.executeInstruction();clock.tick(cycles*1e9/mcu.clkSys);instructions++;}
    }
  }catch(error){state.failed=true;throw error;}finally{release.forEach(remove=>remove());}
  const elapsed=clock.nanos-start;
  const pins=mcu.gpio.map((pin,i)=>{if(levels[i])accumulated[i]+=clock.nanos-last[i];return {output:pin.outputEnable,voltage:elapsed>0?3.3*accumulated[i]/elapsed:pin.outputValue?3.3:0,pullUp:pin.value===GPIOPinState.InputPullUp,pulseUs:state.pulses[i],frequency:state.frequency[i]};});
  const serialPorts=state.serial.map((values,i)=>{const text=state.decoders[i].write(Buffer.from(values));values.length=0;return text;});state.resetTransmitted();
  const diagnostics=state.messages.splice(0);
  return {pins,serialPorts,serialPending:incoming.map(values=>values.length),usbReady:state.usbReady,milliseconds:clock.nanos/1e6,complete:clock.nanos>=target,instructions,clockHz:mcu.clkSys,diagnostics,...state.i2c.frame()};
}
module.exports={load,step};
if(require.main===module)require('node:readline').createInterface({input:process.stdin,crlfDelay:Infinity}).on('line',line=>{
  try{if(line.length>5700000)throw Error('Pico request is too large');const request=JSON.parse(line);const output=request.uf2!=null?load(request.uf2):step(request);process.stdout.write(JSON.stringify(output)+'\n');}
  catch(error){process.stdout.write(JSON.stringify({error:error.message})+'\n');}
});
