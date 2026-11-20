'use strict';
const assert=require('node:assert/strict');
const {SpiTrace}=require('../electronics/spi-trace.cjs');
const {SpiDisplays}=require('../electronics/spi-displays.cjs');
let now=0;
const c={id:'tft',kind:'ili9341',controller:0,mosi:11,sck:13,miso:12,cs:{level:false},dc:{level:false},rst:{level:true},led:{level:true},powered:true,connected:true};
const p={bits:8,mode:0,hz:4e6,lsb:false};
const trace=new SpiTrace(()=>now);
for(const byte of Buffer.from('123456789')){now+=2000;trace.record(c,p,now-2000,'completed',[byte],[byte],true);}
let sample=trace.frame()[0];assert.equal(sample.txCrc32,0xcbf43926);assert.equal(sample.rxCrc32,0xcbf43926);assert.equal(sample.bytes,9);assert.equal(sample.dataBytes,9);assert.deepEqual(trace.frame(),[]);
for(let i=0;i<200000;i++){now+=1000;trace.record(c,p,now-1000,'completed',[i&255],[255],i%3!==0);}
sample=trace.frame()[0];assert.equal(sample.bytes,200000);assert.equal(sample.words,200000);assert.equal(sample.txPrefix.length,32);assert.equal(sample.commandPrefix.length,16);assert.ok(JSON.stringify(sample).length<1500);
const bus=new SpiDisplays(()=>now,()=>false,20,1,()=>true);
bus.update([c]);const finish=bus.begin(0,0x11,8,0,4e6);assert.deepEqual(bus.frame().spi,[]);now+=2000;finish();
sample=bus.frame().spi[0];assert.equal(sample.commands,1);assert.equal(sample.completedWords,1);assert.ok(sample.endedMs>sample.startedMs);assert.deepEqual(sample.commandPrefix,[0x11]);assert.deepEqual(bus.frame().spi,[]);
const late=bus.begin(0,0x29,8,0,4e6);bus.abort();sample=bus.frame().spi[0];assert.equal(sample.abortedWords,1);assert.equal(sample.bytes,0);late();assert.deepEqual(bus.frame().spi,[]); // No duplicate after abort.
bus.update([{...c,connected:false}]);bus.begin(0,0x29,8,0,4e6)();sample=bus.frame().spi[0];assert.equal(sample.ignoredWords,1);assert.deepEqual(sample.issues,['wiring']);assert.equal(sample.txCrc32,0);
bus.update([{...c,miso:null}]);bus.begin(0,0x29,16,3,2e6)();sample=bus.frame().spi[0];assert.equal(sample.bytes,2);assert.deepEqual(sample.rxPrefix,[255,255]);assert.deepEqual(sample.issues,['no-miso']);
console.log('SPI diagnostics: standard CRC32 vector, bounded 200000-byte summary, cross-frame completion, abort and ignored/readback accounting passed');
