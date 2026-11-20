"""Independent mpmath 1.3.0 oracle. Requires mpmath only when regenerating."""
import argparse
import json
import platform
from pathlib import Path
import random
import struct
import mpmath as mp

if mp.__version__ != '1.3.0':
    raise RuntimeError('Regeneration requires the pinned mpmath 1.3.0 oracle')
mp.mp.dps = 140
rng = random.Random(0x2040320)
vectors = []

def raw(number, width):
    return int.from_bytes(struct.pack('<f' if width == 32 else '<d', number), 'little')

def decode(value, width):
    fraction, bias = (23, 127) if width == 32 else (52, 1023)
    sign = bool(value >> (width - 1))
    exponent = (value >> fraction) & (255 if width == 32 else 2047)
    if exponent == (255 if width == 32 else 2047):
        return -mp.inf if sign else mp.inf
    if exponent == 0:
        return mp.mpf(0)
    magnitude = mp.mpf((value & ((1 << fraction) - 1)) | (1 << fraction)) * mp.power(2, exponent-bias-fraction)
    return -magnitude if sign else magnitude

def rounded(value, width, negative_zero=False):
    fraction, bias, max_exponent = (23, 127, 255) if width == 32 else (52, 1023, 2047)
    sign = value < 0 or value == 0 and negative_zero
    sign_bit = int(sign) << (width-1)
    if mp.isinf(value):
        return sign_bit | (max_exponent << fraction)
    if value == 0:
        return sign_bit
    value = abs(value)
    exponent = int(mp.floor(mp.log(value, 2)))
    if exponent > bias:
        return sign_bit | (max_exponent << fraction)
    scale = max(exponent, 1-bias)-fraction
    scaled = value / mp.power(2, scale)
    mantissa = int(mp.floor(scaled))
    tail = scaled-mantissa
    if tail > mp.mpf('.5') or tail == mp.mpf('.5') and mantissa & 1:
        mantissa += 1
    if mantissa >= 1 << (fraction+1):
        mantissa >>= 1
        exponent += 1
    if exponent > bias:
        return sign_bit | (max_exponent << fraction)
    if mantissa < 1 << fraction:
        return sign_bit
    return sign_bit | ((max(exponent, 1-bias)+bias) << fraction) | (mantissa & ((1 << fraction)-1))

def add(width, operation, a, b=0):
    x, y = decode(a, width), decode(b, width)
    negative = bool(a >> (width-1))
    if operation in ('sin','cos','tan','sincos'):
        result = [mp.sin(x), mp.cos(x)] if operation == 'sincos' else [getattr(mp, operation)(x)]
        zeros = [negative, False] if operation == 'sincos' else [negative and operation != 'cos']
    elif operation == 'exp':
        result = [mp.inf if x > 1024 else mp.mpf(0) if x < -1024 else mp.exp(x)]; zeros = [False]
    elif operation == 'log':
        result = [-mp.inf if x <= 0 else mp.log(x)]; zeros = [False]
    else:
        # mpmath does not preserve signed zeros; keep quadrant semantics here.
        b_negative = bool(b >> (width-1))
        if not x:
            value = -mp.pi if negative else mp.pi if b_negative else mp.mpf(0)
            if not b_negative:
                value = mp.mpf(0)
        elif mp.isinf(x) and mp.isinf(y):
            value = (3*mp.pi/4 if b_negative else mp.pi/4) * (-1 if negative else 1)
        else:
            value = mp.atan2(x, y)
        result, zeros = [value], [negative]
    vectors.append(dict(width=width, operation=operation, a=f'{a:0{width//4}x}', b=f'{b:0{width//4}x}', results=[f'{rounded(v,width,z):0{width//4}x}' for v,z in zip(result,zeros)]))

for width in (32,64):
    limit = 128 if width == 32 else 1024
    normal_min = 2.0**(-126 if width == 32 else -1022)
    boundary = [0.0,-0.0,normal_min,-normal_min,2.0**-90,-2.0**-90,.5,-.5,1.0,-1.0,float(mp.pi/2),-float(mp.pi),limit-0.001,-limit+0.001]
    for operation in ('sin','cos','tan','sincos'):
        for x in boundary+[rng.uniform(-limit+.01,limit-.01) for _ in range(50)]:
            add(width,operation,raw(x,width))
        for angle in (mp.pi/2,mp.pi):
            center=raw(float(angle),width)
            for adjacent in (center-1,center,center+1):
                add(width,operation,adjacent)
    for x in [0.0,-0.0,normal_min,-normal_min,.5,-.5,1.0,-1.0,88.7,-87.4,709.7,-708.4,1000.0,-1000.0]+[rng.uniform(-110,100) if width==32 else rng.uniform(-750,720) for _ in range(50)]:
        add(width,'exp',raw(x,width))
    maximum = ((254 if width==32 else 2046) << (23 if width==32 else 52)) | ((1 << (23 if width==32 else 52))-1)
    fraction,bias=(23,127) if width==32 else (52,1023)
    for threshold in (mp.log(mp.power(2,bias+1)-mp.power(2,bias-fraction-1)),mp.log(mp.power(2,1-bias)-mp.power(2,1-bias-fraction-1))):
        center=raw(float(threshold),width)
        for adjacent in (center-1,center,center+1):
            add(width,'exp',adjacent)
    for a in [raw(x,width) for x in [0.0,-0.0,normal_min,1.0,.5,2.0,-1.0,1+2**(-23 if width==32 else -52)]]+[maximum]+[rng.randrange(1,maximum) for _ in range(50)]:
        add(width,'log',a)
    add(width,'log',raw(1.0,width)-1)
    pairs = [(y,x) for y in (0.0,-0.0,1.0,-1.0) for x in (0.0,-0.0,1.0,-1.0)] + [(normal_min,1.0),(1.0,normal_min),(normal_min,-1.0),(float('inf'),float('inf')),(-float('inf'),-float('inf'))]
    for y,x in pairs:
        add(width,'atan2',raw(y,width),raw(x,width))
    for _ in range(50):
        a=rng.randrange(maximum);b=rng.randrange(maximum)
        if rng.getrandbits(1):a |= 1 << (width-1)
        if rng.getrandbits(1):b |= 1 << (width-1)
        add(width,'atan2',a,b)

parser=argparse.ArgumentParser()
parser.add_argument('--output',type=Path,default=Path(__file__).resolve().parents[1]/'tests'/'fixtures'/'pico-science.json')
args=parser.parse_args()
args.output.parent.mkdir(parents=True,exist_ok=True)
sdk_range=[]
# SDK sincostan_remainder uses these encoded 2*pi constants, not exact pi.
for width,number,period_bits in [(64,1e20,0x401921fb54442d18),(32,1e10,0x40c90fdb)]:
    a=raw(number,width);x=decode(a,width);period=decode(period_bits,width)
    reduced=x-mp.nint(x/period)*period
    reduced_bits=rounded(reduced,width);reduced=decode(reduced_bits,width)
    for operation in ('sin','cos','tan'):
        result=rounded(getattr(mp,operation)(reduced),width)
        printed=struct.unpack('<f' if width==32 else '<d',result.to_bytes(width//8,'little'))[0]
        sdk_range.append(dict(width=width,operation=operation,a=f'{a:x}',period=f'{period_bits:x}',remainder=f'{reduced_bits:x}',result=f'{result:x}',printed=f'{printed:.6f}'))
args.output.write_text(json.dumps(dict(oracle='mpmath 1.3.0',python=platform.python_version(),precision_decimal_digits=140,seed='0x2040320',vectors=vectors,sdk_range_reduction=sdk_range),indent=2)+'\n',encoding='utf-8')
print(f'Generated {len(vectors)} independent scientific vectors')
