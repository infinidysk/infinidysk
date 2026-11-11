//! PAR2's GF(2^16), polynomial 0x1100b and primitive element 2.

use std::sync::LazyLock;

pub(super) static FIELD: LazyLock<Field> = LazyLock::new(Field::new);

/// Shared fixed tables take 458748 bytes, outside each repair's workspace budget.
pub(super) struct Field {
    log: Vec<u16>,
    exp: Vec<u16>,
    pub constants: Vec<u16>,
}

impl Field {
    fn new() -> Self {
        let mut log = vec![0; 65_536];
        let mut exp = vec![0; 131_070];
        let mut value = 1u32;
        for (index, entry) in exp[..65_535].iter_mut().enumerate() {
            let word = u16::try_from(value).expect("polynomial reduction keeps values below 65536");
            *entry = word;
            log[usize::from(word)] = u16::try_from(index).expect("field order is 65535");
            value <<= 1;
            if value & 0x1_0000 != 0 {
                value ^= 0x1_100b;
            }
        }
        exp.copy_within(..65_535, 65_535);
        let constants = (1..65_535)
            .filter(|n| n % 3 != 0 && n % 5 != 0 && n % 17 != 0 && n % 257 != 0)
            .map(|n| exp[n])
            .collect();
        Self {
            log,
            exp,
            constants,
        }
    }

    pub fn mul(&self, a: u16, b: u16) -> u16 {
        if a == 0 || b == 0 {
            0
        } else {
            self.exp[usize::from(self.log[usize::from(a)]) + usize::from(self.log[usize::from(b)])]
        }
    }

    pub fn inverse(&self, value: u16) -> u16 {
        debug_assert_ne!(value, 0, "only nonzero matrix pivots are inverted");
        self.exp[65_535 - usize::from(self.log[usize::from(value)])]
    }

    pub fn pow(&self, value: u16, exponent: u32) -> u16 {
        if exponent == 0 {
            return 1;
        }
        if value == 0 {
            return 0;
        }
        let power = u64::from(self.log[usize::from(value)]) * u64::from(exponent);
        self.exp[usize::try_from(power % 65_535).expect("field exponent is below 65535")]
    }
}

#[cfg(test)]
mod tests {
    use super::FIELD;

    #[test]
    fn tables_match_polynomial_arithmetic() {
        for a in [0u16, 1, 2, 255, 256, 1025, 32_768, u16::MAX] {
            for b in 0..=u16::MAX {
                let (mut left, mut right, mut expected) = (u32::from(a), b, 0u32);
                for _ in 0..16 {
                    if right & 1 != 0 {
                        expected ^= left;
                    }
                    right >>= 1;
                    left <<= 1;
                    if left & 0x1_0000 != 0 {
                        left ^= 0x1_100b;
                    }
                }
                assert_eq!(u32::from(FIELD.mul(a, b)), expected);
            }
        }
        assert_eq!(FIELD.constants.len(), 32_768);
        assert_eq!(
            &FIELD.constants[..8],
            &[2, 4, 16, 128, 256, 2048, 8192, 16384]
        );
        for value in 1..=u16::MAX {
            assert_eq!(FIELD.mul(value, FIELD.inverse(value)), 1);
        }
    }
}
