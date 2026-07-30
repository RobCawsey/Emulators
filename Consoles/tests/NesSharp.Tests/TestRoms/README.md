# Test ROMs

`6502_functional_test.bin` — Klaus Dormann's 6502 functional test, GPLv3, from
https://github.com/Klaus2m5/6502_65C02_functional_tests (`bin_files/6502_functional_test.bin`).
Vendored here so the test suite doesn't depend on network access to run.

This binary is built with `disable_decimal = 0`, i.e. decimal-mode ADC/SBC *is* tested.
The NES's Ricoh 2A03 (and this core, matching it) never implements BCD correction, so
`Cpu6502Tests.KlausDormannFunctionalTest_*` intentionally stops at the first decimal-mode
check rather than running to the suite's final success loop — see that test for details.

`nestest.nes` and `nestest.log` — Kevin Horton's nestest ROM and its captured real-hardware
execution log, a long-standing free diagnostic tool in the NES emulator development
community. Fetched from https://raw.githubusercontent.com/christopherpow/nes-test-roms and
https://www.qmtpro.com/~nes/misc/nestest.log respectively. `NestestLogTest` runs the ROM
from PC=$C000 (nestest's documented "automation mode" entry point, which needs no PPU/APU
interaction) and asserts every one of the ~8991 logged instructions matches exactly:
PC, A/X/Y/P/SP, PPU dot/scanline, and CPU cycle count.

`mmc1_a12.nes` — Bregalad's MMC1 WRAM-disable/scanline-counter test, from the same
christopherpow/nes-test-roms repo (`MMC1_A12/`). No reference log; used as a visual sanity
check (see the session transcript for the rendered screenshot) rather than an automated test.

`mmc3/*.nes` — blargg's mmc3_irq_tests suite (1.Clocking, 2.Details, 3.A12_clocking,
4.Scanline_timing), from the same repo (`mmc3_irq_tests/`). Uses blargg's standard test
convention: $6000 holds $80 while running and a result code (0 = passed) once done, with
human-readable diagnostic text at $6004+. See `Mmc3IrqBlarggTests`.
