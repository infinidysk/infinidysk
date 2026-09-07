#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

node --input-type=module <<'JS'
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';

for (const [name, length] of [['alpha.bin', 16384], ['beta.bin', 24579], ['gamma.bin', 8192]]) {
    const bytes = Buffer.alloc(length);
    for (let offset = 0; offset < length; offset += 32) {
        const block = createHash('sha256').update(`${name}:${offset / 32}`, 'ascii').digest();
        block.copy(bytes, offset, 0, Math.min(block.length, length - offset));
    }
    writeFileSync(name, bytes);
}
JS

rm -f -- set*.par2 single*.par2 uneven*.par2
par2 create -q -t1 -s4096 -c8 -n1 set.par2 alpha.bin beta.bin gamma.bin
par2 create -q -t1 -s4096 -c4 -n1 single.par2 alpha.bin
par2 create -q -t1 -s6144 -c2 -n1 uneven.par2 alpha.bin
par2 verify -q -t1 set.par2
par2 verify -q -t1 single.par2
par2 verify -q -t1 uneven.par2
if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -- alpha.bin beta.bin gamma.bin *.par2 > SHA256SUMS
elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -- alpha.bin beta.bin gamma.bin *.par2 > SHA256SUMS
else
    printf '%s\n' 'SHA-256 checksum generation requires sha256sum or shasum.' >&2
    exit 1
fi