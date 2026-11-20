# RP2040js 1.4.1

Upstream: https://github.com/wokwi/rp2040js

Commit: `8693315df527adce8bf2bc315aeb4a1ca61c02a3` (annotated tag `v1.4.1`).

Package: https://registry.npmjs.org/rp2040js/-/rp2040js-1.4.1.tgz

Verified npm SHA512 integrity:
`Yzr5xG7ZwbtA50IYAU1k/oSoSyDAhPtT5qC/Q2eFApoLuAYE8DQbPIMPs/0kh4Ietd4P0OpKk3CIkfNqjcr56g==`

`cjs/` is the unmodified CommonJS distribution, including type declarations.
License: MIT; the upstream LICENSE is retained alongside it.

The upstream demo BootROM image is **not included**. Its separately licensed
floating-point routines must not be mistaken for part of the emulator's MIT grant.
Our worker currently rejects firmware accesses to the unimplemented ROM ABI.
