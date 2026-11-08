'use strict';
// Exact rational arithmetic for the RP2040 ROM's binary formats. Floating-point
// payloads never use JavaScript Number arithmetic or original Raspberry Pi code.
function arithmetic(rawA,rawB,width,operation) {
  const fraction=width===32?23:52,bias=width===32?127:1023,exponentBits=width===32?8:11;
  const signBit=1n<<BigInt(width-1),mask=(1n<<BigInt(fraction))-1n,maxExponent=(1<<exponentBits)-1;
  function decode(raw){const sign=!!(raw&signBit),e=Number(raw>>BigInt(fraction)&BigInt(maxExponent));return {sign,inf:e===maxExponent,n:e===0?0n:(raw&mask)|(1n<<BigInt(fraction)),e:(e||1)-bias-fraction};}
  const a=decode(rawA),b=decode(rawB),signed=(n,sign)=>sign?-n:n;
  function infinity(sign){return (sign?signBit:0n)|(BigInt(maxExponent)<<BigInt(fraction));}
  function zero(sign){return sign?signBit:0n;}
  function encode(n,d,e,negativeZero=false){
    const sign=n<0n||n===0n&&negativeZero;if(n<0n)n=-n;if(!n)return zero(sign);
    let log=n.toString(2).length-d.toString(2).length;
    if(log>=0?n<(d<<BigInt(log)):(n<<BigInt(-log))<d)log--;
    let outputExponent=log+e;const target=Math.max(outputExponent,1-bias)-fraction,shift=e-target;
    const numerator=shift>=0?n<<BigInt(shift):n,denominator=shift>=0?d:d<<BigInt(-shift);
    let mantissa=numerator/denominator;const remainder=numerator%denominator;
    if(remainder*2n>denominator||remainder*2n===denominator&&(mantissa&1n))mantissa++;
    if(mantissa>=(1n<<BigInt(fraction+1))){mantissa>>=1n;outputExponent++;}
    if(outputExponent>bias)return infinity(sign);
    if(mantissa<(1n<<BigInt(fraction)))return zero(sign); // output denormals flush
    outputExponent=Math.max(outputExponent,1-bias);
    return (sign?signBit:0n)|(BigInt(outputExponent+bias)<<BigInt(fraction))|(mantissa&mask);
  }
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
function invokeArithmetic(registers,width,operation){
  const a=width===32?BigInt(registers[0]):BigInt(registers[0])|(BigInt(registers[1])<<32n);
  const b=width===32?BigInt(registers[1]):BigInt(registers[2])|(BigInt(registers[3])<<32n);
  const result=arithmetic(a,b,width,operation);registers[0]=Number(result&0xffffffffn);if(width===64)registers[1]=Number(result>>32n);
}
module.exports={arithmetic,invokeArithmetic};
