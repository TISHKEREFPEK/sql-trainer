'use strict';
const assert=require('node:assert/strict');
const {Ds18b20Bus,crc8}=require('../electronics/ds18b20.cjs');
let now=0,events=[],master=null,bus;
const schedule=(at,callback)=>{const e={at,callback};events.push(e);return ()=>e.cancelled=true;};
const rom=n=>{const a=[0x28,n,0x23,0x45,0x67,0x89,0xab];return Buffer.from([...a,crc8(a)]).toString('hex');};
const config=(id,n,t,parasite=false)=>({id,pin:6,powered:true,connected:true,temperature:t,parasite,pullup:true,strongPullup:false,rom:rom(n),memory:[75,70,127]});
let configs=[config('cold',1,-10.125),config('warm',2,31.5)];
bus=new Ds18b20Bus(()=>now,schedule,()=>{},30);
const update=()=>bus.update(bus.prepare(configs));
function advance(ns){const target=now+ns;while(true){events.sort((a,b)=>a.at-b.at);const e=events.find(e=>!e.cancelled&&e.at<=target);if(!e)break;events.splice(events.indexOf(e),1);now=e.at;e.callback();}now=target;}
function set(value){master=value;bus.observe(6,value,false);}
function bit(value){set(false);advance(value?6000:65000);set(true);advance(value?64000:5000);}
function byte(value){for(let i=0;i<8;i++)bit((value>>>i)&1);}
function readBit(){set(false);advance(6000);set(null);advance(9000);const v=bus.input(6,master,false)?1:0;advance(55000);return v;}
function readBytes(n){return Array.from({length:n},()=>{let v=0;for(let i=0;i<8;i++)v|=readBit()<<i;return v;});}
function reset(){set(false);advance(500000);set(null);advance(70000);assert.equal(bus.input(6,master,false),false,'presence');advance(410000);}
function select(address){reset();byte(0x55);for(const b of Buffer.from(address,'hex'))byte(b);}
function scratch(address){select(address);byte(0xbe);const a=readBytes(9);assert.equal(crc8(a),0,'scratch CRC');return a;}
function temperature(a){let n=a[0]|a[1]<<8;if(n&0x8000)n-=65536;return n/16;}
update();set(false);advance(479250);set(null);advance(70000);assert.equal(bus.input(6,master,false),true,'reset below 480 us must not receive presence');advance(410000);assert.equal(temperature(scratch(rom(1))),85);
// Independently traverse each discrepancy path: the model must provide both
// ROM bit and complement, not return a prepared search result.
function search(prefix=[]){reset();byte(0xf0);let bits=[];
  for(let i=0;i<64;i++){const b=readBit(),inverse=readBit();assert(!(b&&inverse),'search lost all devices');let choice=b;
    if(!b&&!inverse){choice=prefix[i]??0;if(prefix[i]===undefined)branches.push([...bits,1]);}
    if(prefix[i]!==undefined)choice=prefix[i];bits.push(choice);bit(choice);
  }return Buffer.from(Array.from({length:8},(_,n)=>bits.slice(n*8,n*8+8).reduce((v,b,i)=>v|(b<<i),0))).toString('hex');
}
const branches=[];const found=[search()];while(branches.length)found.push(search(branches.pop()));assert.deepEqual(found.sort(),[rom(1),rom(2)].sort());
reset();byte(0xcc);byte(0x44);assert.equal(readBit(),0);advance(300e6);assert.equal(temperature(scratch(rom(1))),85,'conversion cannot complete early');advance(460e6);assert.equal(temperature(scratch(rom(1))),-10.125);assert.equal(temperature(scratch(rom(2))),31.5);
select(rom(1));byte(0x4e);byte(0xf6);byte(0xe2);byte(0x1f);assert.equal(scratch(rom(1))[4],0x1f);select(rom(1));byte(0x48);advance(11e6);assert.deepEqual(bus.frame().oneWireWrites.map(w=>w.memory),[[246,226,31]]);
configs[0]={...configs[0],powered:false};update();configs[0]={...configs[0],powered:true};update();assert.deepEqual(scratch(rom(1)).slice(2,5),[246,226,31]);assert.equal(temperature(scratch(rom(1))),85);
select(rom(1));byte(0x44);advance(95e6);assert.equal(temperature(scratch(rom(1))),-10.5,'9-bit quantisation');reset();byte(0xec);assert.equal(readBit(),0);assert.equal(readBit(),1,'only alarming sensor participates');
configs=[config('parasite',3,12.5,true)];update();select(rom(3));byte(0xb4);assert.equal(readBit(),0);
select(rom(3));byte(0x44);set(null);advance(751e6);assert.equal(temperature(scratch(rom(3))),85,'weak pullup cannot supply conversion');
select(rom(3));byte(0x44);set(true);advance(751e6);assert.equal(temperature(scratch(rom(3))),12.5);
select(rom(3));byte(0x44);set(true);advance(100e6);bus=new Ds18b20Bus(()=>now,schedule,()=>{},30,bus.devices);update();set(true);advance(651e6);assert.equal(temperature(scratch(rom(3))),12.5);
assert.throws(()=>bus.prepare([{...configs[0],rom:'2800000000000001'}]));assert.throws(()=>bus.prepare([{...configs[0],memory:[0,0,0]}]));
// Seven undrained writes followed by eight simultaneous completions must not
// overflow a frame or lose events during MCU rebinding.
configs=Array.from({length:7},(_,i)=>config('queue-'+i,i+10,20));update();
reset();byte(0xcc);byte(0x48);advance(11e6);assert.equal(bus.writes.length,7);
configs.push(config('queue-7',17,20));update();reset();byte(0xcc);byte(0x48);advance(11e6);
assert.equal(bus.full,true);assert.equal(bus.writes.length,15);
const first=bus.frame().oneWireWrites;assert.equal(first.length,8);
bus=new Ds18b20Bus(()=>now,schedule,()=>{},30,bus.devices,bus.writes);update();
const second=bus.frame().oneWireWrites;assert.equal(second.length,7);assert.equal(bus.frame().oneWireWrites.length,0);
assert.deepEqual([...first,...second].map(w=>w.id),[...configs.slice(0,7),...configs].map(c=>c.id));
console.log('DS18B20: real slots, multidrop search, ROM/scratch CRC, Match/Skip, signed temperature, conversion time, resolution, alarm, EEPROM/power cycle, parasite power and MCU replacement passed.');
