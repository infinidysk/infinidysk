//! In-process stand-in for `/dev/fuse`, so tests can drive the dispatcher without a mount.

use std::collections::VecDeque;
use std::io;
use std::io::IoSlice;
use std::ops::DerefMut;
use std::sync::{Arc, Mutex as SyncMutex};

use futures_util::lock::Mutex;
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};

use super::CompleteIoResult;
use crate::raw::abi::FUSE_IN_HEADER_SIZE;

#[derive(Debug)]
enum ScriptedWrite {
    Fail(i32),
    Short(usize),
    #[cfg(target_os = "linux")]
    Gate(gate::GatedWrite),
}

/// Only Linux moves blocking writes off the executor, so only Linux tests can gate one.
#[cfg(target_os = "linux")]
mod gate {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;
    use std::time::Duration;

    use tokio::sync::oneshot;

    /// A gated write gives up waiting after this, so a test that blocks its own thread still ends.
    const GATE_LIMIT: Duration = Duration::from_secs(2);

    #[derive(Debug)]
    pub(super) struct GatedWrite {
        entered: oneshot::Sender<()>,
        release: mpsc::Receiver<()>,
        timed_out: Arc<AtomicBool>,
    }

    impl GatedWrite {
        pub(super) fn new() -> (Self, WriteGate) {
            let (entered_tx, entered) = oneshot::channel();
            let (release, release_rx) = mpsc::channel();
            let timed_out = Arc::new(AtomicBool::new(false));

            (
                Self {
                    entered: entered_tx,
                    release: release_rx,
                    timed_out: timed_out.clone(),
                },
                WriteGate {
                    entered,
                    release,
                    timed_out,
                },
            )
        }

        /// Blocks the writing thread until the gate is released or gives up.
        pub(super) fn wait(self) {
            let _ = self.entered.send(());
            if let Err(mpsc::RecvTimeoutError::Timeout) = self.release.recv_timeout(GATE_LIMIT) {
                self.timed_out.store(true, Ordering::SeqCst);
            }
        }
    }

    /// Holds a write inside the syscall, like the kernel waiting for a lock, until released.
    #[derive(Debug)]
    pub(crate) struct WriteGate {
        /// Resolves once the write is waiting at the gate.
        pub(crate) entered: oneshot::Receiver<()>,
        release: mpsc::Sender<()>,
        timed_out: Arc<AtomicBool>,
    }

    impl WriteGate {
        /// Lets the write finish. Returns false if it had already given up waiting.
        pub(crate) fn release(self) -> bool {
            let _ = self.release.send(());
            !self.timed_out.load(Ordering::SeqCst)
        }
    }
}

#[derive(Debug)]
pub(crate) struct MemoryConnection {
    requests: Mutex<UnboundedReceiver<io::Result<Vec<u8>>>>,
    replies: UnboundedSender<Vec<u8>>,
    write_script: Arc<SyncMutex<VecDeque<ScriptedWrite>>>,
}

/// The kernel's end: push request frames (or scripted read errors) and read reply frames.
#[derive(Debug)]
pub(crate) struct KernelSide {
    pub(crate) requests: UnboundedSender<io::Result<Vec<u8>>>,
    pub(crate) replies: UnboundedReceiver<Vec<u8>>,
    write_script: Arc<SyncMutex<VecDeque<ScriptedWrite>>>,
}

impl KernelSide {
    fn script(&self, write: ScriptedWrite) {
        self.write_script
            .lock()
            .expect("no test panics while holding the write-script lock")
            .push_back(write);
    }

    /// The next write fails with `errno` and is not recorded as a reply.
    pub(crate) fn fail_next_write(&self, errno: i32) {
        self.script(ScriptedWrite::Fail(errno));
    }

    pub(crate) fn short_next_write(&self, written: usize) {
        self.script(ScriptedWrite::Short(written));
    }

    /// The next write blocks its thread until the gate is released, then succeeds.
    #[cfg(target_os = "linux")]
    pub(crate) fn gate_next_write(&self) -> gate::WriteGate {
        let (gated, gate) = gate::GatedWrite::new();
        self.script(ScriptedWrite::Gate(gated));
        gate
    }
}

impl MemoryConnection {
    pub(crate) fn new() -> (Self, KernelSide) {
        let (request_tx, request_rx) = unbounded_channel();
        let (reply_tx, reply_rx) = unbounded_channel();
        let write_script = Arc::new(SyncMutex::new(VecDeque::new()));

        (
            Self {
                requests: Mutex::new(request_rx),
                replies: reply_tx,
                write_script: write_script.clone(),
            },
            KernelSide {
                requests: request_tx,
                replies: reply_rx,
                write_script,
            },
        )
    }

    /// Fills the buffers like `readv`. A closed request queue reads as `ENODEV`, like an unmounted device.
    pub(crate) async fn read_vectored<T: DerefMut<Target = [u8]>>(
        &self,
        mut header_buf: Vec<u8>,
        mut data_buf: T,
    ) -> CompleteIoResult<(Vec<u8>, T), usize> {
        let frame = match self.requests.lock().await.recv().await {
            None => Err(io::Error::from_raw_os_error(libc::ENODEV)),
            Some(frame) => frame,
        };

        let res = frame.map(|frame| {
            let header_len = frame.len().min(FUSE_IN_HEADER_SIZE).min(header_buf.len());
            header_buf[..header_len].copy_from_slice(&frame[..header_len]);

            let data = &frame[header_len..];
            let data_len = data.len().min(data_buf.len());
            data_buf[..data_len].copy_from_slice(&data[..data_len]);

            header_len + data_len
        });

        ((header_buf, data_buf), res)
    }

    /// Records one write as one frame, like a single `writev` to the device.
    pub(crate) fn write_vectored(&self, bufs: &[IoSlice<'_>]) -> io::Result<usize> {
        let scripted = self
            .write_script
            .lock()
            .expect("no test panics while holding the write-script lock")
            .pop_front();
        match scripted {
            Some(ScriptedWrite::Fail(errno)) => return Err(io::Error::from_raw_os_error(errno)),
            Some(ScriptedWrite::Short(written)) => return Ok(written),

            #[cfg(target_os = "linux")]
            Some(ScriptedWrite::Gate(gate)) => gate.wait(),

            None => {}
        }

        let frame = bufs
            .iter()
            .flat_map(|buf| buf.iter().copied())
            .collect::<Vec<_>>();
        let len = frame.len();

        self.replies
            .send(frame)
            .map_err(|_| io::Error::from_raw_os_error(libc::ENODEV))?;

        Ok(len)
    }
}
