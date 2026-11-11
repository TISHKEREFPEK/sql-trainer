'use strict';
// Apply a physical sample without inventing an edge on every host frame.
// RP2040js's setter asserts EDGE flags even for the same input level.
function setPicoInput(pin,value){
  const status=pin.irqStatus,level=value&&pin.inputEnable;
  const previous=status&3?!!(status&2):pin.rawInputValue&&pin.inputEnable;
  if(previous!==level){pin.setInputValue(value);return;}
  // No sensed edge (including a raw change behind a disabled input pad).
  // Keep existing edge latches and initialise/update the level IRQ state.
  pin.rawInputValue=value;const next=(status&~3)|(level?2:1);
  if(next!==status){pin.irqStatus=next;pin.rp2040.updateIOInterrupt();}
}
module.exports={setPicoInput};
