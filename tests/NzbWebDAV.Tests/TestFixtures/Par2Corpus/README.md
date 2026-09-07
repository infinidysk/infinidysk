# PAR2 Reference Corpus

Generated on 2026-09-06 using **par2cmdline version 1.3.0**, Homebrew arm64
on macOS, with Node.js for deterministic source bytes. Run `./generate.sh`
from any directory to regenerate. The script records SHA-256 hashes and runs
`par2 verify` on all three sets; all files verified successfully at generation.

Source files concatenate SHA-256 digests of the ASCII strings
`<filename>:<zero-based 32-byte block index>`, truncated to the requested size.
This deliberately gives different bytes to every full slice.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| alpha.bin | 16384 | 273c15a145ea0a93def475b1675fb3273152934ae2557b2978f8114fc97e2a19 |
| beta.bin | 24579 | bcdb6af37e13c4245b1efcc47949677b424967ede75ab0d3184801d8da3c5ab6 |
| gamma.bin | 8192 | a9df8a735acd2f98c602a08d15e54b230a368229e88926135033d5e07db70d8b |

Generation commands:

```sh
par2 create -q -t1 -s4096 -c8 -n1 set.par2 alpha.bin beta.bin gamma.bin
par2 create -q -t1 -s4096 -c4 -n1 single.par2 alpha.bin
par2 create -q -t1 -s6144 -c2 -n1 uneven.par2 alpha.bin
```

Binary fixtures are committed so tests have an independent protocol authority
without requiring par2cmdline, network access, or an external process in CI.
Tests use copied fixtures under `AppContext.BaseDirectory`. Source hashes are
reproducible; creator packets can change with the tool version, so regenerate
PAR2 files and SHA256SUMS together and verify every set before committing.