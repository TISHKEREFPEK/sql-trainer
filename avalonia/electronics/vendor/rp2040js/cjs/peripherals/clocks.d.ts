import { RP2040 } from '../rp2040.js';
import { BasePeripheral, Peripheral } from './peripheral.js';
export declare class RPClocks extends BasePeripheral implements Peripheral {
    gpout0Ctrl: number;
    gpout0Div: number;
    gpout1Ctrl: number;
    gpout1Div: number;
    gpout2Ctrl: number;
    gpout2Div: number;
    gpout3Ctrl: number;
    gpout3Div: number;
    refCtrl: number;
    refDiv: number;
    periCtrl: number;
    periDiv: number;
    usbCtrl: number;
    usbDiv: number;
    sysCtrl: number;
    sysDiv: number;
    adcCtrl: number;
    adcDiv: number;
    rtcCtrl: number;
    rtcDiv: number;
    constructor(rp2040: RP2040, name: string);
    /** clk_ref frequency, in Hz. GPIN0/GPIN1 are not modelled and yield 0. */
    get refFreq(): number;
    /** clk_sys frequency, in Hz. GPIN0/GPIN1 are not modelled and yield 0. */
    get sysFreq(): number;
    /**
     * clk_peri frequency, in Hz. Feeds the UART and SPI baud rate generators. Returns 0 while
     * the clock generator is stopped, and for the GPIN0/GPIN1 sources we do not model.
     */
    get periFreq(): number;
    private get refSourceFreq();
    private get sysSourceFreq();
    readUint32(offset: number): number;
    writeUint32(offset: number, value: number): void;
}
