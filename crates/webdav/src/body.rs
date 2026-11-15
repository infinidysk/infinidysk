//! Bound publication waits to the HTTP chunks still owned by the transport.

use std::io;

use bytes::{Bytes, BytesMut};
use infinidysk_vfs::{ReadFence, ReadLease};
use tokio::io::{AsyncRead, AsyncReadExt};

struct Chunk {
    data: Bytes,
    _lease: ReadLease,
}

impl AsRef<[u8]> for Chunk {
    fn as_ref(&self) -> &[u8] {
        &self.data
    }
}

pub(crate) async fn chunk<R: AsyncRead + Unpin>(
    reader: &mut R,
    fence: Option<&ReadFence>,
) -> io::Result<Option<Bytes>> {
    let lease = match fence {
        Some(fence) => Some(fence.read().await?),
        None => None,
    };
    let mut data = BytesMut::with_capacity(super::CHUNK);
    if reader.read_buf(&mut data).await? == 0 {
        return Ok(None);
    }
    let data = data.freeze();
    Ok(Some(match lease {
        Some(lease) => Bytes::from_owner(Chunk {
            data,
            _lease: lease,
        }),
        None => data,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use infinidysk_core::ItemId;
    use infinidysk_db::Db;
    use infinidysk_nntp::Client;
    use infinidysk_stream::Budget;
    use infinidysk_vfs::Vfs;
    use std::io::Cursor;
    use std::sync::Arc;
    use std::time::Duration;

    #[tokio::test]
    async fn chunk_slices_hold_admission_but_the_http_stream_does_not() {
        let vfs = Vfs::new(
            Db::connect("sqlite::memory:").await.unwrap(),
            Arc::new(Client::new([])),
            Budget::new(1),
            0,
            "/mnt",
        );
        let fence = vfs.read_fence(ItemId::new());
        let mut reader = Cursor::new(vec![7; 2 * super::super::CHUNK]);
        let data = chunk(&mut reader, Some(&fence)).await.unwrap().unwrap();
        let slice = data.slice(1..3);
        drop(data);
        assert!(
            tokio::time::timeout(Duration::from_millis(20), fence.write())
                .await
                .is_err()
        );
        drop(slice);
        let mut publication = fence.write().await;
        publication.retire().unwrap();
        drop(publication);
        assert!(chunk(&mut reader, Some(&fence)).await.unwrap().is_some());
        assert!(chunk(&mut reader, Some(&fence)).await.unwrap().is_none());
        drop(fence.write().await);
    }
}
