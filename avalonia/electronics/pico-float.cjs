'use strict';
// Exact rational arithmetic for the RP2040 ROM's binary formats. Floating-point
// payloads never use JavaScript Number arithmetic or original Raspberry Pi code.
function representation(width) {
  const fraction=width===32?23:52,bias=width===32?127:1023,exponentBits=width===32?8:11;
  const signBit=1n<<BigInt(width-1),mask=(1n<<BigInt(fraction))-1n,maxExponent=(1<<exponentBits)-1;
  function decode(raw){const sign=!!(raw&signBit),e=Number(raw>>BigInt(fraction)&BigInt(maxExponent));return {sign,inf:e===maxExponent,n:e===0?0n:(raw&mask)|(1n<<BigInt(fraction)),e:(e||1)-bias-fraction};}
  function infinity(sign){return (sign?signBit:0n)|(BigInt(maxExponent)<<BigInt(fraction));}
  function zero(sign){return sign?signBit:0n;}
  function encode(n,d,e,negativeZero=false){
    const sign=n<0n||n===0n&&negativeZero;if(n<0n)n=-n;if(!n)return zero(sign);
    let log=n.toString(2).length-d.toString(2).length;
    if(log>=0?n<(d<<BigInt(log)):(n<<BigInt(-log))<d)log--;
    let outputExponent=log+e;const target=Math.max(outputExponent,1-bias)-fraction,shift=e-target;
    if(outputExponent>bias)return infinity(sign);
    if(outputExponent<1-bias-fraction-1)return zero(sign);
    const numerator=shift>=0?n<<BigInt(shift):n,denominator=shift>=0?d:d<<BigInt(-shift);
    let mantissa=numerator/denominator;const remainder=numerator%denominator;
    if(remainder*2n>denominator||remainder*2n===denominator&&(mantissa&1n))mantissa++;
    if(mantissa>=(1n<<BigInt(fraction+1))){mantissa>>=1n;outputExponent++;}
    if(outputExponent>bias)return infinity(sign);
    if(mantissa<(1n<<BigInt(fraction)))return zero(sign); // output denormals flush
    outputExponent=Math.max(outputExponent,1-bias);
    return (sign?signBit:0n)|(BigInt(outputExponent+bias)<<BigInt(fraction))|(mantissa&mask);
  }
  return {decode,encode,infinity,zero,fraction};
}
const formats=new Map([[32,representation(32)],[64,representation(64)]]);
function arithmetic(rawA,rawB,width,operation) {
  const {decode,encode,infinity,zero}=formats.get(width);
  const a=decode(rawA),b=decode(rawB),signed=(n,sign)=>sign?-n:n;
  if(operation==='sub')b.sign=!b.sign;
  if(operation==='add'||operation==='sub'){
    if(a.inf||b.inf){if(a.inf&&b.inf&&a.sign!==b.sign)throw Error('RP2040 ROM opposite infinities are not implemented');return infinity(a.inf?a.sign:b.sign);}
    const e=Math.min(a.e,b.e),n=signed(a.n<<BigInt(a.e-e),a.sign)+signed(b.n<<BigInt(b.e-e),b.sign);
    return encode(n,1n,e,a.sign&&b.sign);
  }
  const sign=a.sign!==b.sign;
  if(operation==='mul'){
    if(a.inf||b.inf){if((!a.inf&&!a.n)||(!b.inf&&!b.n))throw Error('RP2040 ROM zero times infinity is not implemented');return infinity(sign);}
    return encode(signed(a.n*b.n,sign),1n,a.e+b.e,sign);
  }
  if(operation==='div'){
    if(a.inf&&b.inf||!a.inf&&!b.inf&&!a.n&&!b.n)throw Error('RP2040 ROM indeterminate division is not implemented');
    if(a.inf||!b.n)return infinity(sign);if(b.inf)return zero(sign);
    return encode(signed(a.n,sign),b.n,a.e-b.e,sign);
  }
  throw Error('Unknown RP2040 arithmetic operation');
}
function toInteger(raw,width,integerWidth,signed,scale=0){
  const {decode,fraction}=formats.get(width),a=decode(raw),min=signed?-(1n<<BigInt(integerWidth-1)):0n,max=(1n<<BigInt(integerWidth-(signed?1:0)))-1n;
  if(a.inf)return a.sign?min:max;if(!a.n)return 0n;if(a.sign&&!signed)return 0n;
  const shift=a.e+scale;
  if(shift>integerWidth+1)return a.sign?min:max;
  if(shift< -fraction-2)return a.sign?-1n:0n;
  const mantissa=a.sign?-a.n:a.n,value=shift>=0?mantissa<<BigInt(shift):mantissa>>BigInt(-shift);
  return value<min?min:value>max?max:value;
}
function fromInteger(value,width,scale=0){return formats.get(width).encode(value,1n,-scale);}
function convertWidth(raw,from,to){const a=formats.get(from).decode(raw),target=formats.get(to);return a.inf?target.infinity(a.sign):target.encode(a.sign?-a.n:a.n,1n,a.e,a.sign);}
function squareRoot(raw,width){
  const format=formats.get(width),a=format.decode(raw);
  if(!a.inf&&!a.n)return format.zero(a.sign);if(a.sign)return format.infinity(true);if(a.inf)return format.infinity(false);
  const exponent=Math.floor((a.n.toString(2).length-1+a.e)/2)-format.fraction;
  const radicand=a.n<<BigInt(a.e-2*exponent);
  let root=1n<<BigInt(Math.ceil(radicand.toString(2).length/2));
  for(;;){const next=(root+radicand/root)>>1n;if(next>=root)break;root=next;}
  // Compare with (root + 1/2)^2 exactly. A quarter-integer tie cannot be
  // equal to this integer radicand; no floating-point approximation is used.
  if(radicand-root*root>root)root++;
  return format.encode(root,1n,exponent);
}
function compare(rawA,rawB,width){
  const {decode}=formats.get(width),a=decode(rawA),b=decode(rawB);
  if(!a.inf&&!b.inf&&!a.n&&!b.n)return 0n;
  if(a.sign!==b.sign)return a.sign?-1n:1n;
  let magnitude;
  if(a.inf||b.inf)magnitude=a.inf===b.inf?0:a.inf?1:-1;
  else{const e=Math.min(a.e,b.e),x=a.n<<BigInt(a.e-e),y=b.n<<BigInt(b.e-e);magnitude=x===y?0:x<y?-1:1;}
  return BigInt(a.sign?-magnitude:magnitude);
}
function invokeConversion(registers,width,index){
  const wide=(at)=>BigInt(registers[at])|(BigInt(registers[at+1])<<32n),input=width===32?BigInt(registers[0]):wide(0);
  let result,resultWidth=width;
  if(index===21){result=compare(input,width===32?BigInt(registers[1]):wide(2),width);resultWidth=32;}
  else if(index===31){result=convertWidth(input,width,width===32?64:32);resultWidth=width===32?64:32;}
  else if(index>=7&&index<=10||index>=27&&index<=30){
    const slot=index>=27?index-27:index-7,integerWidth=index>=27?64:32,scale=slot%2?registers[width===32?1:2]|0:0;
    result=toInteger(input,width,integerWidth,slot<2,scale);resultWidth=integerWidth;
  }else if(index>=11&&index<=14||index>=23&&index<=26){
    const slot=index>=23?index-23:index-11,integerWidth=index>=23?64:32,scale=slot%2?registers[integerWidth===32?1:2]|0:0;
    let integer=integerWidth===32?BigInt(registers[0]):wide(0);if(slot<2)integer=BigInt.asIntN(integerWidth,integer);
    result=fromInteger(integer,width,scale);
  }else throw Error('Unknown RP2040 ROM conversion');
  registers[0]=Number(result&0xffffffffn);if(resultWidth===64)registers[1]=Number(BigInt.asUintN(64,result)>>32n);
}
function invokeArithmetic(registers,width,operation){
  const a=width===32?BigInt(registers[0]):BigInt(registers[0])|(BigInt(registers[1])<<32n);
  const b=width===32?BigInt(registers[1]):BigInt(registers[2])|(BigInt(registers[3])<<32n);
  const result=operation==='sqrt'?squareRoot(a,width):arithmetic(a,b,width,operation);registers[0]=Number(result&0xffffffffn);if(width===64)registers[1]=Number(result>>32n);
}
module.exports={arithmetic,squareRoot,invokeArithmetic,toInteger,fromInteger,convertWidth,compare,invokeConversion};
