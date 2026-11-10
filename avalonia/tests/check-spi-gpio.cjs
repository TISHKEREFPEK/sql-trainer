'use strict';
const assert=require('node:assert/strict');
const {SpiDisplays}=require('../electronics/spi-displays.cjs');
const {GpioSpi}=require('../electronics/spi-gpio.cjs');
let now=0;const levels=new Map([[6,false],[7,false],[10,true],[9,false],[8,true]]);
const config={id:'tft',kind:'ili9341',controller:0,mosi:6,sck:7,miso:5,cs:{pin:10},dc:{pin:9},rst:{pin:8},led:{level:true},powered:true,connected:true};
const bus=new SpiDisplays(()=>now,p=>levels.get(p)??false,20,1,()=>false);
const gpio=new GpioSpi(bus,()=>true,c=>c.miso!=null);
function set(pin,value){now+=500;levels.set(pin,value);gpio.tick();}
function bind(c=config){bus.update([JSON.parse(JSON.stringify(c))]);gpio.tick();}
function bit(value,mode=0){if(mode===3)set(7,false);set(6,value);set(7,true);const result=gpio.input(5)??true;if(mode===0)set(7,false);return +result;}
function byte(value,dc=false,mode=0){set(9,dc);let response=0;for(let i=7;i>=0;i--)response=(response<<1)|bit(!!(value&(1<<i)),mode);return response;}
bind();set(10,false);byte(0x11);byte(0x29);now+=120e6;assert.equal(bus.frame().tftDisplays[0].display,true);
byte(0xd3);assert.deepEqual([byte(0,true),byte(0,true),byte(0,true),byte(0,true)],[0,0,0x93,0x41]);
let sample=bus.frame().spi[0];assert.equal(sample.software,true);assert.equal(sample.mode,0);assert.ok(sample.clockHz>0);assert.equal(sample.commands,1);assert.equal(sample.bytes,5);
// A byte can span a host frame with a value-equivalent descriptor.
set(9,false);for(let i=7;i>=4;i--)bit(!!(0x36&(1<<i)));assert.deepEqual(bus.frame().spi,[]);bind();for(let i=3;i>=0;i--)bit(!!(0x36&(1<<i)));byte(0x48,true);
assert.equal(bus.panels.get('tft').madctl,0x48);assert.equal(bus.frame().spi[0].completedWords,2);
byte(0x2a);for(const v of [0,1,0,1])byte(v,true);byte(0x2b);for(const v of [0,2,0,2])byte(v,true);byte(0x3a);byte(0x55,true);byte(0x2c);byte(0xf8,true);byte(0,true);
assert.deepEqual([...Buffer.from(bus.frame().tftDisplays[0].pixels,'base64').subarray((2*240+1)*3,(2*240+1)*3+3)],[255,0,0]);
const command=bus.panels.get('tft').command;set(9,false);bit(false);set(10,true);sample=bus.frame().spi[0];assert.equal(sample.abortedWords,1);assert.equal(sample.clockHz,0);assert.equal(bus.panels.get('tft').command,command);assert.equal(gpio.input(5),null);
// Mode 3 keeps the last sampled bit high until the following falling edge.
set(7,true);set(10,false);byte(0xd3,false,3);assert.deepEqual(Array.from({length:4},()=>byte(0,true,3)),[0,0,0x93,0x41]);sample=bus.frame().spi[0];assert.equal(sample.mode,3);assert.equal(sample.software,true);
set(10,true);bind({...config,connected:false});set(10,false);byte(0x28);assert.equal(bus.panels.get('tft').displayOn,true);assert.deepEqual(bus.frame().spi,[]);
console.log('GPIO SPI: real rising/falling edges, modes 0/3, MISO ID, RGB pixel, cross-frame byte, partial abort and disconnected wiring passed');
