'use strict';
const assert=require('node:assert/strict');
const {Hcsr04Bus}=require('../electronics/hcsr04.cjs');
let now=0,events=[],levels=[],bus;
const schedule=(at,callback)=>{const e={at,callback};events.push(e);return ()=>e.cancelled=true;};
const drive=()=>levels.push([now,bus.input(7)]);
bus=new Hcsr04Bus(()=>now,schedule,drive,30);
const config={id:'sonar',trig:6,echoes:[{pin:7,low:false,high:true}],connected:true,powered:true,distance:100,target:true};
const update=(c={})=>bus.update(bus.prepare([{...config,...c}]));
function advance(to){while(true){events.sort((a,b)=>a.at-b.at);const e=events.find(e=>!e.cancelled&&e.at<=to);if(!e)break;events.splice(events.indexOf(e),1);now=e.at;e.callback();}now=to;}
function pulse(width=10000){bus.observe(6,false);bus.observe(6,true);advance(now+width);bus.observe(6,false);}
update();levels=[];pulse(9000);advance(now+7e6);assert(!levels.some(p=>p[1]));
levels=[];pulse();const start=now;advance(start+200000);assert.equal(bus.input(7),true);advance(start+6000000);assert.deepEqual(levels,[[start+200000,true],[start+6000000,false]]);
levels=[];pulse();advance(now+7e6);assert(!levels.some(p=>p[1]),'fast retrigger ignored');
advance(now+60e6);levels=[];pulse();update({distance:200});advance(now+7e6);assert.equal(levels.filter(p=>!p[1]).at(-1)[0]-levels.find(p=>p[1])[0],5800000,'distance sampled at trigger');
advance(now+60e6);levels=[];pulse();advance(now+12e6);assert.equal(levels.filter(p=>!p[1]).at(-1)[0]-levels.find(p=>p[1])[0],11600000);
advance(now+60e6);update({target:false});levels=[];pulse();advance(now+40e6);assert(!levels.some(p=>p[1]));
advance(now+60e6);update();pulse();advance(now+300000);assert.equal(bus.input(7),true);update({powered:false});advance(now+7e6);assert.equal(bus.input(7),null);
update();levels=[];bus.observe(6,true);advance(now+20000);bus.observe(6,false);advance(now+7e6);assert(!levels.some(p=>p[1]),'HIGH spanning power-up is not a trigger');
advance(now+60e6);levels=[];pulse();advance(now+300000);bus=new Hcsr04Bus(()=>now,schedule,drive,30,bus.devices);advance(now+7e6);assert.equal(bus.input(7),false);assert.equal(levels.filter(p=>p[1]).length,1);
assert.throws(()=>bus.prepare([{...config,distance:NaN}]));assert.throws(()=>bus.prepare([{...config,echoes:[{pin:6,low:false,high:true}]}]));assert.throws(()=>bus.prepare([config,{...config,id:'other'}]));
console.log('HC-SR04: trigger threshold, exact Echo widths, sample capture, cycle limit, no target, power loss, warm MCU replacement and bounded input passed.');
