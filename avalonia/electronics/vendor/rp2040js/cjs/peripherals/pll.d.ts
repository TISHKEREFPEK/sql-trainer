import { BasePeripheral, Peripheral } from './peripheral.js';
/**
 * RP2040 PLL peripheral.
 *
 * Always reports locked (we have no startup delay to model), and computes its output
 * frequency from the divider registers:
 *
 *   f_out = (f_ref / REFDIV * FBDIV) / (POSTDIV1 * POSTDIV2)
 *
 * The frequency is what makes `RP2040.clkSys` follow `set_sys_clock_khz()` and friends.
 */
export declare class RPPLL extends BasePeripheral implements Peripheral {
    private cs;
    private pwr;
    private fbdivInt;
    private prim;
    get refdiv(): number;
    get fbdiv(): number;
    get postdiv1(): number;
    get postdiv2(): number;
    /** PLL output frequency, in Hz, derived from the current register values. */
    get frequency(): number;
    readUint32(offset: number): number;
    writeUint32(offset: number, value: number): void;
}
