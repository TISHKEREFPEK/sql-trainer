'use strict';
// Offline ATmega328P runner. AVR8js is MIT licensed; see vendor/avr8js/LICENSE.
const avr = require('./vendor/avr8js/cjs/index.js');
const readline = require('node:readline');
let cpu, ports, adc, usart, serial = '', rising = [], pulses = [], frequencies = [];
function load(hex) {
  const program = new Uint16Array(16384), bytes = new Uint8Array(program.buffer);
  let upper = 0, ended = false;
  for (const line of hex.trim().split(/\r?\n/)) {
    if (!/^:[0-9a-f]+$/i.test(line) || line.length % 2 !== 1) throw Error('Invalid Intel HEX');
    const record = Buffer.from(line.slice(1),'hex'), count = record[0];
    if (record.length !== count + 5 || record.reduce((a,b)=>a+b,0) % 256 !== 0) throw Error('Invalid HEX checksum');
    const address = upper + record.readUInt16BE(1), type = record[3];
    if (type === 0) { if (address + count > bytes.length) throw Error('Firmware exceeds Uno flash'); bytes.set(record.subarray(4,4+count),address); }
    else if (type === 1) { ended = true; break; }
    else if (type === 4) upper = record.readUInt16BE(4) * 65536;
    else if (type !== 3 && type !== 5) throw Error('Unsupported HEX record');
  }
  if (!ended) throw Error('Incomplete HEX');
  cpu = new avr.CPU(program);
  new avr.AVRTimer(cpu,avr.timer0Config); new avr.AVRTimer(cpu,avr.timer1Config); new avr.AVRTimer(cpu,avr.timer2Config);
  ports = {b:new avr.AVRIOPort(cpu,avr.portBConfig),c:new avr.AVRIOPort(cpu,avr.portCConfig),d:new avr.AVRIOPort(cpu,avr.portDConfig)};
  adc = new avr.AVRADC(cpu,avr.adcConfig);
  usart = new avr.AVRUSART(cpu,avr.usart0Config,16000000);
  usart.onByteTransmit = value => { if(serial.length < 8192) serial += String.fromCharCode(value); };
  serial = '';
  rising = Array(20).fill(null); pulses = Array(20).fill(null); frequencies = Array(20).fill(null);
}
function step(input) {
  if(!cpu) throw Error('Load firmware first');
  const period = 800000, start = cpu.cycles, target = start + period;
  for(let i=0;i<14;i++) (i<8?ports.d:ports.b).setPin(i<8?i:i-8,Boolean(input.digital?.[i]));
  for(let i=0;i<6;i++) { const value=Number(input.analog?.[i] ?? 0); adc.channelValues[i]=Math.max(0,Math.min(5,value)); ports.c.setPin(i,value>2.5); }
  const accumulated = Array(20).fill(0), last = Array(20).fill(start), levels = Array(20).fill(false);
  const track = (port,offset,count) => {
    for(let i=0;i<count;i++) levels[offset+i]=port.pinState(i)===avr.PinState.High;
    const listener = () => { for(let i=0;i<count;i++) {const n=offset+i, high=port.pinState(i)===avr.PinState.High; if(levels[n]) accumulated[n]+=cpu.cycles-last[n]; if(high!==levels[n]) { if(high) {if(rising[n]!==null&&cpu.cycles>rising[n]) frequencies[n]=16000000/(cpu.cycles-rising[n]); rising[n]=cpu.cycles;} else if(rising[n]!==null) pulses[n]=(cpu.cycles-rising[n])/16;} last[n]=cpu.cycles;levels[n]=high;} };
    port.addListener(listener); return listener;
  };
  const ld=track(ports.d,0,8), lb=track(ports.b,8,6), lc=track(ports.c,14,6);
  while(cpu.cycles<target) { avr.avrInstruction(cpu);cpu.tick(); }
  ports.d.removeListener(ld);ports.b.removeListener(lb);ports.c.removeListener(lc);
  const pins=levels.map((high,i)=>{if(high) accumulated[i]+=cpu.cycles-last[i]; const state=(i<8?ports.d:i<14?ports.b:ports.c).pinState(i<8?i:i<14?i-8:i-14);return {output:state===avr.PinState.High||state===avr.PinState.Low,voltage:5*accumulated[i]/(cpu.cycles-start),pullUp:state===avr.PinState.InputPullUp,pulseUs:pulses[i],frequency:frequencies[i]};});
  const output={pins,serial,milliseconds:cpu.cycles/16000};serial='';return output;
}
readline.createInterface({input:process.stdin,crlfDelay:Infinity}).on('line',line=>{
  try {if(line.length>200000) throw Error('Request too large');const input=JSON.parse(line); const output=input.hex?(load(input.hex),{ready:true}):step(input);process.stdout.write(JSON.stringify(output)+'\n');}
  catch(error) {process.stdout.write(JSON.stringify({error:error.message})+'\n');}
});
