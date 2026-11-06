'use strict';
// Register/pin facts: ATmega328P/2560 datasheets, avr-libc iom328p.h/iomxx0_1.h,
// Arduino AVR core 1.8.6 variants/{standard,mega}/pins_arduino.h.
// AVR8js implementation stays unmodified in vendor (MIT); see THIRD_PARTY.md.
const avr = require('./vendor/avr8js/cjs/index.js');
const boards = require('./avr-boards.json').boards;
function getBoard(id) {
  const board = boards.find(b => b.id === id);
  if (!board) throw Error('Unsupported AVR board: ' + id);
  return board;
}
function configure(cpu, board) {
  const mega = board.id === 'mega2560';
  const portConfigs = mega ? Object.fromEntries('ABCDEFGHJKL'.split('').map(letter => [letter, {...avr['port'+letter+'Config'], pinChange:undefined, externalInterrupts:[]}]))
    : {B:avr.portBConfig,C:avr.portCConfig,D:avr.portDConfig};
  if (mega) {
    const ext = index => ({EICR:index<4?0x69:0x6a,EIMSK:0x3d,EIFR:0x3c,index,iscOffset:(index%4)*2,interrupt:(index+1)*2});
    portConfigs.D.externalInterrupts = [ext(0),ext(1),ext(2),ext(3)];
    portConfigs.E.externalInterrupts = [null,null,null,null,ext(4),ext(5),ext(6),ext(7)];
    const change=(group,mask=255,offset=0)=>({PCIE:group,PCICR:0x68,PCIFR:0x3b,PCMSK:0x6b+group,pinChangeInterrupt:(9+group)*2,mask,offset});
    portConfigs.B.pinChange=change(0);portConfigs.E.pinChange=change(1,1);portConfigs.J.pinChange=change(1,0x7f,1);portConfigs.K.pinChange=change(2);
  }
  const ports = Object.fromEntries(Object.entries(portConfigs).map(([name,config])=>[name,new avr.AVRIOPort(cpu,config)]));
  const P=letter=>portConfigs[letter].PORT;
  const timer0 = mega ? {...avr.timer0Config,compAInterrupt:42,compBInterrupt:44,ovfInterrupt:46,compPortA:P('B'),compPinA:7,compPortB:P('G'),compPinB:5,externalClockPort:P('D'),externalClockPin:7} : avr.timer0Config;
  const timer2 = mega ? {...avr.timer2Config,compAInterrupt:26,compBInterrupt:28,ovfInterrupt:30,compPortA:P('B'),compPinA:4,compPortB:P('H'),compPinB:6} : avr.timer2Config;
  const timer16 = (base,number,vector,letter,pins) => ({...avr.timer1Config,
    captureInterrupt:vector*2,compAInterrupt:(vector+1)*2,compBInterrupt:(vector+2)*2,compCInterrupt:(vector+3)*2,ovfInterrupt:(vector+4)*2,
    TIFR:0x35+number,TIMSK:0x6e+number,TCCRA:base,TCCRB:base+1,TCCRC:base+2,TCNT:base+4,ICR:base+6,OCRA:base+8,OCRB:base+10,OCRC:base+12,
    OCFC:8,OCIEC:8,compPortA:P(letter),compPinA:pins[0],compPortB:P(letter),compPinB:pins[1],compPortC:P(letter),compPinC:pins[2],externalClockPort:0,externalClockPin:0});
  const timers = mega ? [timer0,timer16(0x80,1,16,'B',[5,6,7]),timer2,timer16(0x90,3,31,'E',[3,4,5]),timer16(0xa0,4,41,'H',[3,4,5]),timer16(0x120,5,46,'L',[3,4,5])] : [timer0,avr.timer1Config,timer2];
  timers.forEach(config=>new avr.AVRTimer(cpu,config));
  const muxChannels={};
  if(mega) {
    for(let i=0;i<8;i++){muxChannels[i]={type:avr.ADCMuxInputType.SingleEnded,channel:i};muxChannels[32+i]={type:avr.ADCMuxInputType.SingleEnded,channel:8+i};}
    muxChannels[30]={type:avr.ADCMuxInputType.Constant,voltage:1.1};muxChannels[31]={type:avr.ADCMuxInputType.Constant,voltage:0};
  }
  const adc = new avr.AVRADC(cpu,mega?{...avr.adcConfig,numChannels:16,adcInterrupt:58,muxInputMask:0x3f,muxChannels,adcReferences:[avr.ADCReference.AREF,avr.ADCReference.AVCC,avr.ADCReference.Internal1V1,avr.ADCReference.Internal2V56]}:avr.adcConfig);
  const usartConfig=(base,vector)=>({...avr.usart0Config,UCSRA:base,UCSRB:base+1,UCSRC:base+2,UBRRL:base+4,UBRRH:base+5,UDR:base+6,rxCompleteInterrupt:vector*2,dataRegisterEmptyInterrupt:(vector+1)*2,txCompleteInterrupt:(vector+2)*2});
  const usarts=(mega?[usartConfig(0xc0,25),usartConfig(0xc8,36),usartConfig(0xd0,51),usartConfig(0x130,54)]:[avr.usart0Config]).map(config=>new avr.AVRUSART(cpu,config,board.clockHz));
  const eeprom=new avr.EEPROMMemoryBackend(board.eepromBytes);
  new avr.AVREEPROM(cpu,eeprom,mega?{...avr.eepromConfig,eepromReadyInterrupt:60}:avr.eepromConfig);
  const clock=new avr.AVRClock(cpu,board.clockHz);
  new avr.AVRWatchdog(cpu,mega?{...avr.watchdogConfig,watchdogInterrupt:24}:avr.watchdogConfig,clock);
  return {ports,adc,usarts,eeprom,clock};
}
module.exports={getBoard,configure};
