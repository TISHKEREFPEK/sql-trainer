import { IClock } from './clock/clock.js';
import { CortexM0Core } from './cortex-m0-core.js';
import { GPIOPin } from './gpio-pin.js';
import { RPADC } from './peripherals/adc.js';
import { RPClocks } from './peripherals/clocks.js';
import { RPDMA } from './peripherals/dma.js';
import { RPI2C } from './peripherals/i2c.js';
import { Peripheral } from './peripherals/peripheral.js';
import { RPPIO } from './peripherals/pio.js';
import { RPPLL } from './peripherals/pll.js';
import { RPPPB } from './peripherals/ppb.js';
import { RPPWM } from './peripherals/pwm.js';
import { RPSPI } from './peripherals/spi.js';
import { RPUART } from './peripherals/uart.js';
import { RPUSBController } from './peripherals/usb.js';
import { RPSIO } from './sio.js';
import { Logger } from './utils/logging.js';
export declare const FLASH_START_ADDRESS = 268435456;
export declare const FLASH_END_ADDRESS = 335544320;
export declare const RAM_START_ADDRESS = 536870912;
export declare const APB_START_ADDRESS = 1073741824;
export declare const DPRAM_START_ADDRESS = 1343225856;
export declare const SIO_START_ADDRESS = 3489660928;
/**
 * Notified after clk_sys changes. Listeners that cache a frequency derived from clk_sys
 * (PIO clock dividers, for instance) use this to re-derive it.
 */
export type ClockListener = (clkSys: number, oldClkSys: number) => void;
export declare class RP2040 {
    readonly clock: IClock;
    readonly bootrom: Uint32Array<ArrayBuffer>;
    readonly sram: Uint8Array<ArrayBuffer>;
    readonly sramView: DataView<ArrayBuffer>;
    readonly flash: Uint8Array<ArrayBuffer>;
    readonly flash16: Uint16Array<ArrayBuffer>;
    readonly flashView: DataView<ArrayBuffer>;
    readonly usbDPRAM: Uint8Array<ArrayBuffer>;
    readonly usbDPRAMView: DataView<ArrayBuffer>;
    readonly core: CortexM0Core;
    clkSys: number;
    clkPeri: number;
    /** Crystal oscillator frequency. 12 MHz on the Raspberry Pi Pico and most other boards. */
    xoscFreq: number;
    /** Ring oscillator frequency. Varies with voltage/temperature on real silicon. */
    roscFreq: number;
    readonly pllSys: RPPLL;
    readonly pllUsb: RPPLL;
    readonly clocks: RPClocks;
    readonly ppb: RPPPB;
    readonly sio: RPSIO;
    readonly uart: RPUART[];
    readonly i2c: RPI2C[];
    readonly pwm: RPPWM;
    readonly adc: RPADC;
    readonly gpio: GPIOPin[];
    readonly qspi: GPIOPin[];
    readonly dma: RPDMA;
    readonly pio: RPPIO[];
    readonly usbCtrl: RPUSBController;
    readonly spi: RPSPI[];
    logger: Logger;
    private readonly clockListeners;
    readonly peripherals: {
        [index: number]: Peripheral;
    };
    onBreak: (code: number) => void;
    constructor(clock?: IClock);
    loadBootrom(bootromData: Uint32Array): void;
    reset(): void;
    readUint32(address: number): number;
    /**
     * Recomputes `clkSys` and `clkPeri` from the PLL and CLOCKS registers and updates the
     * peripherals derived from them. Until firmware configures the clock tree, each keeps its default.
     */
    updateClocks(): void;
    private updateClkSys;
    private updateClkPeri;
    /** Registers a listener to be notified whenever clk_sys changes. Returns an unsubscribe function. */
    addClockListener(listener: ClockListener): () => boolean;
    findPeripheral(address: number): Peripheral;
    /** We assume the address is 16-bit aligned */
    readUint16(address: number): number;
    readUint8(address: number): number;
    writeUint32(address: number, value: number): void;
    writeUint8(address: number, value: number): void;
    writeUint16(address: number, value: number): void;
    get gpioValues(): number;
    setInterrupt(irq: number, value: boolean): void;
    updateIOInterrupt(): void;
    step(): void;
}
