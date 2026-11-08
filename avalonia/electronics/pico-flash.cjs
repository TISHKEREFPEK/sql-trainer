'use strict';
const {BasePeripheral}=require('./vendor/rp2040js/cjs/peripherals/peripheral.js');
// Functional 2 MiB serial NOR model for the RP2040 SDK's flash/boot2 path.
// It has a declared synthetic UID, not the identity of a physical computer.
function installFlash(mcu) {
  const ssi=mcu.findPeripheral(0x18000000),read=ssi.readUint32.bind(ssi),write=ssi.writeUint32.bind(ssi),uid=[0x53,0x51,0x4c,0x43,0,0,0,1];
  let fifo=[],command=null,index=0,address=0,lastOverride=mcu.qspi[1].outputOverride,wel=false,status1=0,status2=2,connected=true,xip=true,quadCommand=null;
  const qspi=mcu.findPeripheral(0x40018000),qread=qspi.readUint32.bind(qspi),qwrite=qspi.writeUint32.bind(qspi);
  qspi.readUint32=offset=>{if(offset<0x30){const pin=mcu.qspi[offset>>>3];return offset&4?pin.ctrl:pin.status;}return qread(offset);};
  qspi.writeUint32=(offset,value)=>{if(offset<0x30){if(offset&4){const pin=mcu.qspi[offset>>>3];pin.ctrl=value>>>0;pin.checkForUpdates();}return;}return qwrite(offset,value);};
  class XipControl extends BasePeripheral {
    constructor(){super(mcu,'XIP_CTRL');this.control=3;}
    readUint32(offset){if(offset===0)return this.control;throw Error('RP2040 XIP register is not implemented: '+offset);}
    writeUint32(offset,value){if(offset===0){this.control=value&0xb;return;}throw Error('RP2040 XIP register is not implemented: '+offset);}
  }
  const xipControl=new XipControl();mcu.peripherals[0x14000]=xipControl;
  function resetTransaction(){command=null;index=0;address=0;quadCommand=null;}
  function select(){const override=mcu.qspi[1].outputOverride;if(override!==lastOverride){resetTransaction();lastOverride=override;}return override!==3;}
  function transfer(value){
    if(!connected||!select())return 0xff;
    if(command===null){if(![0x05,0x35,0x4b,0x9f,0x03,0x0b,0x01].includes(value))throw Error('RP2040 flash SPI command is not implemented: 0x'+value.toString(16));command=value;index=0;return 0xff;}
    const at=index++;
    switch(command){
      case 0x05:return status1|(wel?2:0);
      case 0x35:return status2;
      case 0x4b:return at<4?0xff:uid[(at-4)%uid.length];
      case 0x9f:return [0xef,0x40,0x15][at%3];
      case 0x03:case 0x0b:
        if(at<3){address=(address<<8)|value;return 0xff;}
        if(command===0x0b&&at===3)return 0xff;
        if(address>=0x200000)throw Error('RP2040 flash SPI address exceeds 2 MiB');return mcu.flash[address++];
      case 0x01:
        if(!wel)throw Error('RP2040 flash status write requires write-enable');
        if(at===0)status1=value&0xfc;else if(at===1){status2=value;wel=false;}else throw Error('Invalid RP2040 flash status payload');return 0xff;
      default:throw Error('RP2040 flash SPI command is not implemented: 0x'+command.toString(16));
    }
  }
  ssi.readUint32=offset=>{
    if(offset===0x20)return 0; // synchronous transmit queue is drained
    if(offset===0x24)return fifo.length;
    if(offset===0x28)return 6|(fifo.length?8:0);
    if(offset===0x60){if(!fifo.length)throw Error('RP2040 SSI RX FIFO is empty');return fifo.shift();}
    return read(offset);
  };
  ssi.writeUint32=(offset,value)=>{
    if(offset===8){
      write(offset,value);fifo=[];resetTransaction();xip=false;
      if(value&&(ssi.crtlr0>>>8&3)===3){const command=ssi.spictlr0>>>24,instructionLength=ssi.spictlr0>>>8&3;xip=instructionLength===0&&command===0xa0||instructionLength===2&&[0x03,0x0b,0xeb,0xbb].includes(command);}
      return;
    }
    if(offset!==0x60)return write(offset,value);
    if(!ssi.ssienr)throw Error('RP2040 SSI is disabled');
    if(fifo.length>=16)throw Error('RP2040 SSI RX FIFO overflow');
    if((ssi.crtlr0>>>8&3)===3){
      if(quadCommand===null){quadCommand=value;return;}
      if(![0x03,0x0b,0xeb,0xbb].includes(quadCommand))throw Error('Unsupported RP2040 SSI EEPROM-read command');
      address=quadCommand===0xeb||quadCommand===0xbb?value>>>8:value&0xffffff;
      const count=ssi.crtlr1+1;if(count>16||address+count*4>0x200000)throw Error('Invalid RP2040 SSI EEPROM-read range');
      for(let i=0;i<count;i++)fifo.push(mcu.flashView.getUint32(address+i*4,true));quadCommand=null;return;
    }
    const byte=value&255;select();
    if(command===null&&byte===0x06){wel=true;fifo.push(0xff);return;}
    if(command===null&&byte===0x04){wel=false;fifo.push(0xff);return;}
    fifo.push(transfer(byte));
  };
  // Reading XIP while the serial command mode is active must not silently work.
  for(const method of ['readUint8','readUint16','readUint32']){
    const previous=mcu[method].bind(mcu);mcu[method]=address=>{if(address>=0x10000000&&address<0x14000000&&!xip)throw Error('RP2040 flash XIP is disabled');return previous(address);};
  }
  for(const method of ['writeUint8','writeUint16','writeUint32']){
    const previous=mcu[method].bind(mcu);mcu[method]=(address,value)=>{if(address>=0x10000000&&address<0x14000000){if(xipControl.control&2)throw Error('RP2040 flash is read-only through XIP');return;}return previous(address,value);};
  }
  function connect(){connected=true;for(const pin of mcu.qspi){pin.ctrl&=~0x1f;pin.padValue|=0x40;}}
  function exit(){
    if(!connected)throw Error('RP2040 flash is disconnected');xip=false;fifo=[];resetTransaction();
    ssi.crtlr0=7<<16;ssi.crtlr1=0;ssi.spictlr0=0;ssi.baudr=6;ssi.ssienr=1;
    mcu.qspi[1].ctrl=(mcu.qspi[1].ctrl&~0x300)|0x300;lastOverride=3;
  }
  function flush(){mcu.qspi[1].ctrl&=~0x300;lastOverride=0;resetTransaction();}
  function enter(){flush();ssi.crtlr0=(31<<16)|(3<<8);ssi.spictlr0=(0x03<<24)|(6<<2)|(2<<8);ssi.ssienr=1;xip=true;}
  function range(offset,count,alignment){if(!connected||xip)throw Error('RP2040 flash operation requires serial command mode');if(offset%alignment||count%alignment||offset+count>0x200000)throw Error('Invalid RP2040 flash program/erase range');}
  function erase(offset,count,blockSize,blockCommand){range(offset,count,4096);if(blockSize!==65536||blockCommand!==0xd8)throw Error('RP2040 flash erase command is not implemented');mcu.flash.fill(0xff,offset,offset+count);}
  function program(offset,source,count){range(offset,count,256);if(source<0x20000000||source+count>0x20042000)throw Error('RP2040 flash source must be in SRAM');for(let i=0;i<count;i++)mcu.flash[offset+i]&=mcu.sram[source-0x20000000+i];}
  enter(); // The UF2 loader starts at the application vector, after XIP setup.
  return {connect,exit,flush,enter,erase,program,uid};
}
module.exports={installFlash};
