'use strict';
const {scientific}=require('../electronics/pico-science.cjs');
const fixture=require('./fixtures/pico-science.json');let count=0,maxError=0n;
for(const v of fixture.vectors){
  const actual=scientific(BigInt('0x'+v.a),v.width,v.operation,BigInt('0x'+v.b));
  if(actual.length!==v.results.length)throw Error('Scientific result count differs');
  actual.forEach((raw,i)=>{
    const expected=BigInt('0x'+v.results[i]),error=raw>expected?raw-expected:expected-raw;
    if(error>maxError)maxError=error;
    const fraction=v.width===32?23n:52n,mask=v.width===32?255n:2047n,special=raw=>{const exponent=raw>>fraction&mask;return exponent===0n||exponent===mask;};
    if(error>1n||error&&(special(raw)||special(expected)))throw Error(JSON.stringify({vector:v,actual:actual.map(x=>x.toString(16)),error:error.toString()}));
    count++;
  });
}
process.stdout.write(`${count} scientific results within 1 ULP; maximum error ${maxError} ULP\n`);
