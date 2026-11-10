'use strict';
const assert=require('node:assert/strict');
const {Ili9341}=require('../electronics/ili9341.cjs');
function fixture(){let now=0;const panel=new Ili9341('tft',()=>now);panel.update({powered:true});
  const cmd=(c,...data)=>{panel.transfer(c,false);for(const b of data)panel.transfer(b,true);};
  const read=(c,count)=>{cmd(c);return Array.from({length:count},()=>panel.transfer(0,true));};
  const window=(x,y,x1=x,y1=y)=>{cmd(0x2a,x>>>8,x&255,x1>>>8,x1&255);cmd(0x2b,y>>>8,y&255,y1>>>8,y1&255);};
  const pixel=(x,y)=>Array.from(Buffer.from(panel.frame().pixels,'base64').subarray((y*240+x)*3,(y*240+x)*3+3));
  cmd(0x36,0x48);cmd(0x3a,0x55);cmd(0x11);cmd(0x29);now=120e6;
  return {panel,cmd,read,window,pixel};
}
{
  const f=fixture();f.window(2,3,3,3);f.cmd(0x2c,0xf8,0,0x07,0xe0);assert.deepEqual(f.pixel(2,3),[255,0,0]);assert.deepEqual(f.pixel(3,3),[0,255,0]);
  assert.deepEqual(f.read(0xd3,4),[0,0,0x93,0x41]);assert.deepEqual(f.read(0x0c,1),[0x55]);
  f.cmd(0xd9,0x13);assert.deepEqual(f.read(0xd3,1),[0x41]);f.cmd(0xd9,0x10);
  f.window(2,3);assert.deepEqual(f.read(0x2e,4),[0,252,0,0]);
  f.cmd(0x21);assert.deepEqual(f.pixel(2,3),[0,255,255]);f.cmd(0x20);f.cmd(0x28);assert.deepEqual(f.pixel(2,3),[0,0,0]);f.cmd(0x29);assert.deepEqual(f.pixel(2,3),[255,0,0]);
  f.panel.update({powered:true,backlight:false});assert.equal(f.panel.frame().display,false);f.panel.update({powered:true});assert.deepEqual(f.pixel(2,3),[255,0,0]);
}
for(const [rotation,madctl,x,y] of [[0,0x48,0,0],[1,0x28,239,0],[2,0x88,239,319],[3,0xe8,0,319]]){
  const f=fixture();f.cmd(0x36,madctl);f.window(0,0);f.cmd(0x2c,0,31);assert.deepEqual(f.pixel(x,y),[0,0,255],'rotation '+rotation);
}
{
  const f=fixture();f.cmd(0x3a,0x66);f.window(1,1);f.cmd(0x2c,0xfc,0x80,0x40);assert.deepEqual(f.pixel(1,1),[255,130,65]);
  f.cmd(0x10);assert.equal(f.panel.frame().display,false);f.cmd(0x11);assert.equal(f.panel.frame().display,false);
}
{
  const f=fixture();f.window(0,1);f.cmd(0x2c,0xf8,0);f.cmd(0x33,0,1,1,62,0,1);f.cmd(0x37,0,2);
  assert.deepEqual(f.pixel(0,318),[255,0,0]);assert.deepEqual(f.pixel(0,319),[0,0,0]);
  f.cmd(0x30,1,62,1,62);f.cmd(0x12);assert.deepEqual(f.pixel(0,318),[255,0,0]);assert.deepEqual(f.pixel(0,317),[0,0,0]);
  f.panel.update({powered:false});assert.equal(f.panel.frame().display,false);assert.equal(f.panel.transfer(0,true),255);
}
console.log('ILI9341 controller foundation: windows, RGB565/RGB666, rotations, readback, sleep, backlight, inversion, scroll and partial area passed');
