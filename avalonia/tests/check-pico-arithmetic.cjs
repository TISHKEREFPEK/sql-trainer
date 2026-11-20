'use strict';
// Independent finite-value IEEE oracle, normalized to the documented RP2040
// denormal policy. Boundary/ABI vectors also run in PicoRomTests via real ARM.
const {arithmetic,squareRoot,toInteger,fromInteger,convertWidth,compare}=require('../electronics/pico-float.cjs');
const view=new DataView(new ArrayBuffer(8));let seed=0x41c6ce57,cases=0,conversionCases=0,rootCases=0;
function random(){seed^=seed<<13;seed^=seed>>>17;seed^=seed<<5;return seed>>>0;}
function value(bits,width){if(width===32){view.setUint32(0,Number(bits),true);return view.getFloat32(0,true);}view.setBigUint64(0,bits,true);return view.getFloat64(0,true);}
function bits(number,width){if(width===32){view.setFloat32(0,number,true);return BigInt(view.getUint32(0,true));}view.setFloat64(0,number,true);return view.getBigUint64(0,true);}
function normalize(raw,width){const fraction=width===32?23n:52n,exponent=width===32?255n:2047n;return (raw>>fraction&exponent)===0n?raw&(1n<<BigInt(width-1)):raw;}
for(const width of [32,64])for(let i=0;i<3000;i++){
  let a=width===32?BigInt(random()):(BigInt(random())<<32n)|BigInt(random());
  let b=width===32?BigInt(random()):(BigInt(random())<<32n)|BigInt(random());
  a=normalize(a,width);b=normalize(b,width);const x=value(a,width),y=value(b,width);
  if(!Number.isFinite(x)||!Number.isFinite(y))continue;
  for(const [operation,expected] of [['add',x+y],['sub',x-y],['mul',x*y],['div',x/y]]){
    if(Number.isNaN(expected))continue;
    const actual=arithmetic(a,b,width,operation),target=normalize(bits(expected,width),width);
    if(actual!==target)throw Error(JSON.stringify({width,operation,a:a.toString(16),b:b.toString(16),actual:actual.toString(16),target:target.toString(16)}));
    cases++;
  }
  function equal(actual,target,operation){if(actual!==target)throw Error(JSON.stringify({width,operation,a:a.toString(16),actual:actual.toString(16),target:target.toString(16)}));conversionCases++;}
  const scale=i%129-64;
  for(const integerWidth of [32,64])for(const signed of [false,true]){
    const min=signed?-(1n<<BigInt(integerWidth-1)):0n,max=(1n<<BigInt(integerWidth-(signed?1:0)))-1n;
    function clamp(number){if(number===Infinity)return max;if(number===-Infinity)return min;const integer=BigInt(Math.floor(number));return integer<min?min:integer>max?max:integer;}
    equal(toInteger(a,width,integerWidth,signed),clamp(x),'to-integer');
    const scaled=x*2**scale;if(scaled!==0||x===0)equal(toInteger(a,width,integerWidth,signed,scale),clamp(scaled),'to-fixed');
  }
  const integer=BigInt.asIntN(32,a);equal(fromInteger(integer,width,scale),normalize(bits(Number(integer)*2**-scale,width),width),'from-fixed');
  equal(convertWidth(a,width,width===32?64:32),normalize(bits(x,width===32?64:32),width===32?64:32),'convert-width');
  equal(compare(a,b,width),BigInt(x===y?0:x<y?-1:1),'compare');
  const root=normalize(bits(x<0?-Infinity:Math.sqrt(x),width),width);
  if(squareRoot(a,width)!==root)throw Error('Square root mismatch: '+a.toString(16));rootCases++;
}
process.stdout.write(`Seeded finite IEEE comparisons with ROM denormal policy: ${cases} passed\n`);
process.stdout.write(`Conversions/comparisons: ${conversionCases} passed; square roots: ${rootCases} passed\n`);
