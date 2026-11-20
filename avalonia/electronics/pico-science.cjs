'use strict';
// Original bounded fixed-point implementations. Constants are computed from
// convergent series, not copied from the Raspberry Pi ROM or host Math tables.
const {representation}=require('./pico-float.cjs');
const formats=new Map([[32,representation(32)],[64,representation(64)]]),P=320n,ONE=1n<<P;
function seriesAtan(z){
  const square=z*z/ONE;let term=z,sum=z;
  for(let i=1;i<512;i++){term=-term*square/ONE;const add=term/BigInt(2*i+1);if(!add)return sum;sum+=add;}
  throw Error('RP2040 atan series did not converge');
}
function seriesLog(z){
  const square=z*z/ONE;let term=z,sum=z;
  for(let i=1;i<512;i++){term=term*square/ONE;const add=term/BigInt(2*i+1);if(!add)return sum*2n;sum+=add;}
  throw Error('RP2040 log series did not converge');
}
const PI=16n*seriesAtan(ONE/5n)-4n*seriesAtan(ONE/239n),HALF_PI=PI/2n,LN2=seriesLog(ONE/3n);
function fixed(a){const shift=a.e+Number(P),n=shift>=0?a.n<<BigInt(shift):a.n>>BigInt(-shift);return a.sign?-n:n;}
function exponent(a){return a.n.toString(2).length-1+a.e;}
function nearest(n,d){const sign=n<0n;if(sign)n=-n;const rounded=(n+d/2n)/d;return sign?-rounded:rounded;}
function sincosFixed(x){
  const quadrant=nearest(x,HALF_PI),z=x-quadrant*HALF_PI,square=z*z/ONE;let sin=z,cos=ONE,s=z,c=ONE;
  for(let i=1;i<128;i++){
    s=-s*square/(ONE*BigInt((2*i)*(2*i+1)));c=-c*square/(ONE*BigInt((2*i-1)*(2*i)));sin+=s;cos+=c;
    if(!s&&!c){switch(Number((quadrant%4n+4n)%4n)){case 0:return [sin,cos];case 1:return [cos,-sin];case 2:return [-sin,-cos];default:return [-cos,sin];}}
  }
  throw Error('RP2040 sin/cos series did not converge');
}
function atanFixed(z){if(z>ONE/2n)return PI/4n+seriesAtan((z-ONE)*ONE/(z+ONE));return seriesAtan(z);}
function scientific(raw,width,operation,other=0n){
  const f=formats.get(width),a=f.decode(raw),encode=n=>f.encode(n,1n,-Number(P)),negativeZero=f.zero(a.sign);
  if(['sin','cos','tan','sincos'].includes(operation)){
    if(a.inf||a.n&&exponent(a)>=(width===32?7:10))throw Error('RP2040 ROM trigonometric argument is outside its documented range');
    if(!a.n||exponent(a)<-80){const sin=a.n?raw:negativeZero,cos=f.encode(1n,1n,0);return operation==='sincos'?[sin,cos]:[operation==='cos'?cos:sin];}
    const [s,c]=sincosFixed(fixed(a));
    if(operation==='tan'){if(!c)throw Error('RP2040 tangent pole is unresolved');return [f.encode(c<0n?-s:s,c<0n?-c:c,0)];}
    return operation==='sincos'?[encode(s),encode(c)]:[encode(operation==='sin'?s:c)];
  }
  if(operation==='exp'){
    if(a.inf||a.n&&exponent(a)>10)return [a.sign?f.zero(false):f.infinity(false)];
    if(!a.n||exponent(a)<-80)return [f.encode(1n,1n,0)];
    const x=fixed(a),k=nearest(x,LN2),r=x-k*LN2;let term=ONE,sum=ONE;
    for(let i=1;i<256;i++){term=term*r/(ONE*BigInt(i));sum+=term;if(!term)return [f.encode(sum,1n,Number(k)-Number(P))];}
    throw Error('RP2040 exp series did not converge');
  }
  if(operation==='log'){
    if(a.sign||!a.inf&&!a.n)return [f.infinity(true)];if(a.inf)return [f.infinity(false)];
    const m=a.n<<BigInt(Number(P)-f.fraction),z=(m-ONE)*ONE/(m+ONE);
    return [encode(seriesLog(z)+BigInt(a.e+f.fraction)*LN2)];
  }
  if(operation==='atan2'){
    const b=f.decode(other),quarter=PI/4n,pi=encode(PI),half=encode(HALF_PI),angle=n=>encode(a.sign?-n:n);
    if(a.inf&&b.inf)return [angle(b.sign?3n*quarter:quarter)];if(a.inf)return [a.sign?half|(1n<<BigInt(width-1)):half];
    if(b.inf)return [b.sign?(a.sign?pi|(1n<<BigInt(width-1)):pi):negativeZero];
    if(!a.n)return [b.sign?(a.sign?pi|(1n<<BigInt(width-1)):pi):negativeZero];
    if(!b.n)return [a.sign?half|(1n<<BigInt(width-1)):half];
    const shift=a.e-b.e,n=shift>=0?a.n<<BigInt(shift):a.n,d=shift>=0?b.n:b.n<<BigInt(-shift);
    if((n<<80n)<d){if(b.sign)return [angle(PI)];return [f.encode(a.sign?-a.n:a.n,b.n,shift)];}
    const yLarger=n>d,small=yLarger?d*ONE/n:n*ONE/d;
    let result=atanFixed(small);if(yLarger)result=HALF_PI-result;if(b.sign)result=PI-result;
    return [angle(result)];
  }
  throw Error('Unknown RP2040 scientific operation');
}
function invokeScientific(registers,width,operation){
  const wide=at=>BigInt(registers[at])|(BigInt(registers[at+1])<<32n),a=width===32?BigInt(registers[0]):wide(0),b=width===32?BigInt(registers[1]):wide(2),results=scientific(a,width,operation,b);
  results.forEach((value,i)=>{const offset=i*(width===32?1:2);registers[offset]=Number(value&0xffffffffn);if(width===64)registers[offset+1]=Number(value>>32n);});
}
module.exports={scientific,invokeScientific};
