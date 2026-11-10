'use strict';
const assert=require('node:assert/strict');
const {SpiDisplays}=require('../electronics/spi-displays.cjs');
let now=0;const levels=new Map([[10,false],[9,false]]);
const bus=new SpiDisplays(()=>now,p=>levels.get(p)??false,20,1,c=>c.mosi===11&&c.sck===13,undefined,c=>c.miso===12);
const config={id:'tft',kind:'ili9341',controller:0,mosi:11,sck:13,miso:12,cs:{pin:10},dc:{pin:9},rst:{level:true},led:{level:true},powered:true,connected:true};
bus.update([config]);
function byte(value,dc=false){levels.set(9,dc);bus.refresh();return bus.begin(0,value,8,0,4e6)();}
byte(0x11);byte(0x29);now=120e6;assert.equal(bus.frame().tftDisplays[0].display,true);
byte(0xd3);assert.deepEqual(Array.from({length:4},()=>byte(0,true)),[0,0,0x93,0x41]);
levels.set(9,false);bus.refresh();let finish=bus.begin(0,0x28,8,0,4e6);
levels.set(10,true);bus.refresh();levels.set(10,false);bus.refresh();assert.equal(finish(),255);assert.equal(bus.frame().tftDisplays[0].display,true); // Transient CS release aborts the byte.
finish=bus.begin(0,0x28,8,0,4e6);bus.update([JSON.parse(JSON.stringify(config))]);assert.equal(finish(),0);assert.equal(bus.frame().tftDisplays[0].display,false); // Equivalent frame binding preserves an in-flight word.
bus.update([{...config,mosi:12}]);assert.equal(byte(0xd3),255);assert.equal(byte(0,true),255);
bus.update([{...config,miso:14}]);byte(0xd3);assert.equal(byte(0,true),255);
byte(0x29);assert.equal(bus.frame().tftDisplays[0].display,true);
bus.update([{...config,connected:false}]);assert.equal(byte(0x28),255);assert.equal(bus.frame().tftDisplays[0].display,true); // Breaking a signal wire is not a panel power cut.
bus.update([{...config,powered:false}]);assert.equal(byte(0xd3),255);assert.equal(bus.frame().tftDisplays[0].display,false);
assert.throws(()=>bus.update([config,config]),/configuration/);assert.throws(()=>bus.update([{...config,cs:{pin:10,level:false}}]),/configuration/);
bus.update([config]);levels.set(9,false);assert.throws(()=>bus.begin(0,0,8,1,4e6),/mode/);assert.throws(()=>bus.begin(0,0,8,0,11e6),/clock/);
// A shared MISO must report conflicting register responses, not pick a winner.
const second={...config,id:'tft2',cs:{pin:15}};levels.set(15,true);bus.update([config,second]);byte(0x36);byte(0x48,true);
levels.set(10,true);levels.set(15,false);byte(0x36);byte(0x28,true);levels.set(10,false);byte(0x0b);assert.throws(()=>byte(0,true),/MISO contention/);
console.log('SPI display bus: valid/invalid bindings, power/MISO, transient CS abort, cross-frame continuation, protocol bounds and two-panel contention passed');
