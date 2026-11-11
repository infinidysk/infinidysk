use std::io;

/// Filesystem boundaries, counted only in tests for process-crash/error injection.
#[derive(Default)]
pub(super) struct Steps {
    #[cfg(test)]
    pub n: usize,
    #[cfg(test)]
    pub fault: Option<(usize, Fault)>,
}

#[cfg(test)]
#[derive(Clone, Copy)]
pub(super) enum Fault {
    Error,
    Exit,
}

impl Steps {
    #[cfg_attr(
        not(test),
        allow(clippy::unused_self, clippy::needless_pass_by_ref_mut)
    )]
    // The counter exists only for tests, following the backup failure-injection pattern.
    pub fn run<T>(&mut self, work: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        #[cfg(test)]
        {
            let n = self.n;
            self.n += 1;
            match self.fault {
                Some((at, Fault::Error)) if at == n => {
                    return Err(io::Error::from_raw_os_error(nix::libc::ENOSPC));
                }
                Some((at, Fault::Exit)) if at == n => {
                    let _ = work();
                    std::process::exit(79);
                }
                _ => {}
            }
        }
        work()
    }
}
