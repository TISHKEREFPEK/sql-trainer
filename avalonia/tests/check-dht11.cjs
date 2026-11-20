'use strict';
const assert=require('node:assert/strict');
const {Dht11Bus}=require('../electronics/dht11.cjs');
let now=0,events=[],levels=[];
const schedule=(at,callback)=>{const e={at,callback};events.push(e);return ()=>e.cancelled=true;};
let bus=new Dht11Bus(()=>now,schedule,()=>{levels.push([now,bus.input(6,null,true)]);},30);
const config={id:'sensor',pin:6,powered:true,connected:true,pullup:false,temperature:24,humidity:53};
function update(changes={}){bus.update(bus.prepare([{...config,...changes}]));}
function advance(to){while(true){events.sort((a,b)=>a.at-b.at);const e=events.find(e=>!e.cancelled&&e.at<=to);if(!e)break;events.splice(events.indexOf(e),1);now=e.at;e.callback();}now=to;}
function trigger(duration=18e6,pullup=true){bus.observe(6,false,pullup);advance(now+duration);bus.observe(6,null,pullup);}
function decode(){
  const bits=[];
  // Recognise real response preamble; decode independently from sampled HIGH widths.
  const start=levels.findIndex((p,i)=>i+2<levels.length&&p[1]===false&&levels[i+1][1]===true&&levels[i+1][0]-p[0]===80000&&levels[i+2][0]-levels[i+1][0]===80000);
  assert(start>=0,'80 us response preamble missing');
  for(let i=start+3;bits.length<40;i+=2){assert(levels[i][1]);assert(!levels[i+1][1]);bits.push(levels[i+1][0]-levels[i][0]>50000?1:0);}
  return Array.from({length:5},(_,i)=>bits.slice(i*8,i*8+8).reduce((a,b)=>(a<<1)|b,0));
}
update();trigger();advance(now+6e6);assert(!levels.some(p=>!p[1]),'must not respond during power-up');
advance(1e9);trigger(17e6);advance(now+6e6);assert(!levels.some(p=>!p[1]),'short start must not respond');
levels=[];trigger();advance(now+6e6);assert.deepEqual(decode(),[53,0,24,0,77]);
levels=[];trigger();advance(now+6e6);assert(!levels.some(p=>!p[1]),'too frequent read must not respond');
update({temperature:31,humidity:71});advance(now+1e9);levels=[];trigger();advance(now+6e6);assert.deepEqual(decode(),[53,0,24,0,77]);
advance(now+1e9);levels=[];trigger();advance(now+6e6);assert.deepEqual(decode(),[71,0,31,0,102]);
advance(now+1e9);levels=[];trigger(18e6,false);advance(now+6e6);assert.equal(levels.length,0,'released bus without any pull-up cannot start');
advance(now+1e9);levels=[];trigger();advance(now+200e3);update({powered:false});levels=[];advance(now+6e6);assert.equal(bus.input(6,null,true),null);assert.equal(levels.length,0,'power loss cancels scheduled transitions');
assert.throws(()=>bus.prepare([{...config,temperature:NaN}]));assert.throws(()=>bus.prepare([config,{...config,id:'other'}]));assert.throws(()=>bus.prepare([{...config,pin:30}]));
update();advance(now+1e9);levels=[];trigger();advance(now+210e3);
bus=new Dht11Bus(()=>now,schedule,()=>levels.push([now,bus.input(6,null,true)]),30,bus.devices);
advance(now+6e6);assert.deepEqual(decode(),[53,0,24,0,77],'external device continues across MCU replacement');
console.log('DHT11 protocol: response/checksum, power-up, start duration, cooldown, previous sample, pull-up, power loss, validation and warm MCU replacement passed.');
