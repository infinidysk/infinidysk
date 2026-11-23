use std::ops::Range;

use infinidysk_nntp::{Body, MAX_DECODED_LEN};

use super::{Article, Budget, Damage, Error};
use crate::nzb;

#[derive(Clone)]
struct Slot {
    id: Option<String>,
    range: Option<Range<u64>>,
    damage: Option<Damage>,
}

pub(super) struct Slots {
    parts: Vec<Slot>,
    length: Option<u64>,
    total: Option<usize>,
}

impl Slots {
    pub fn new(file: nzb::File, budget: &mut Budget) -> Result<Self, Error> {
        let mut slots = Self {
            parts: Vec::new(),
            length: None,
            total: None,
        };
        let count = file.segments.iter().try_fold(0, |count, segment| {
            let number = usize::try_from(segment.number)
                .ok()
                .filter(|&n| n > 0)
                .ok_or(Error::Unavailable(
                    "NZB part numbers cannot place unavailable articles",
                ))?;
            Ok::<_, Error>(count.max(number))
        })?;
        slots.grow(count, budget)?;
        for segment in file.segments {
            let number = usize::try_from(segment.number)
                .ok()
                .filter(|&n| n > 0)
                .ok_or(Error::Unavailable(
                    "NZB part numbers cannot place unavailable articles",
                ))?;
            if slots.parts[number - 1].id.replace(segment.id).is_some() {
                return Err(Error::Ambiguous("duplicate NZB part number"));
            }
        }
        Ok(slots)
    }

    pub(super) fn grow(&mut self, length: usize, budget: &mut Budget) -> Result<(), Error> {
        if length <= self.parts.len() {
            return Ok(());
        }
        let additional = length - self.parts.len();
        // Includes the simultaneous output Article array during finalization.
        budget.metadata(
            additional
                .checked_mul(size_of::<Slot>() + size_of::<Article>())
                .ok_or(Error::Limit("metadata"))?,
        )?;
        self.parts
            .try_reserve_exact(additional)
            .map_err(|_| Error::Allocation)?;
        self.parts.resize(
            length,
            Slot {
                id: None,
                range: None,
                damage: Some(Damage::Omitted),
            },
        );
        Ok(())
    }

    pub fn len(&self) -> usize {
        self.parts.len()
    }
    pub fn id(&self, index: usize) -> Option<&str> {
        self.parts.get(index).and_then(|slot| slot.id.as_deref())
    }
    pub fn damage(&mut self, index: usize, damage: Damage) {
        self.parts[index].damage = Some(damage);
    }

    pub(super) fn remember(&mut self, index: usize, range: Range<u64>) -> Result<(), Error> {
        let slot = &mut self.parts[index];
        if slot.range.as_ref().is_some_and(|old| *old != range) {
            return Err(Error::Changed("stored article ranges conflict"));
        }
        slot.range = Some(range);
        Ok(())
    }

    pub fn observe(&mut self, index: usize, body: &Body, budget: &mut Budget) -> Result<(), Error> {
        if self.length.is_some_and(|length| length != body.file_size) {
            return Err(Error::Unavailable("yEnc file lengths disagree"));
        }
        let number =
            u32::try_from(index + 1).map_err(|_| Error::Unavailable("yEnc part number"))?;
        if body.part != Some(number)
            && !(body.part.is_none() && self.parts.len() == 1 && index == 0)
        {
            return Err(Error::Unavailable("NZB and yEnc part numbers disagree"));
        }
        if let Some(total) = body.total {
            let total = usize::try_from(total).map_err(|_| Error::Limit("part count"))?;
            if total < self.parts.len() || self.total.is_some_and(|previous| previous != total) {
                return Err(Error::Unavailable("yEnc total part counts disagree"));
            }
            self.total = Some(total);
            self.grow(total, budget)?;
        }
        let end = body
            .offset
            .checked_add(body.data.len() as u64)
            .filter(|&end| end <= body.file_size)
            .ok_or(Error::Unavailable(
                "yEnc article is outside the protected file",
            ))?;
        if self.parts[index]
            .range
            .as_ref()
            .is_some_and(|range| *range != (body.offset..end))
        {
            return Err(Error::Changed(
                "stored article range disagrees with yEnc headers",
            ));
        }
        if body.data.is_empty() {
            return Err(Error::Unavailable("empty source article"));
        }
        self.length = Some(body.file_size);
        self.parts[index].range = Some(body.offset..end);
        self.parts[index].damage = None;
        Ok(())
    }

    pub fn finish(self, length: u64) -> Result<Vec<Article>, Error> {
        if self.length.is_some_and(|observed| observed != length) {
            return Err(Error::Changed(
                "observed file length disagrees with the selected source",
            ));
        }
        let mut articles = Vec::new();
        articles
            .try_reserve_exact(self.parts.len())
            .map_err(|_| Error::Allocation)?;
        let mut offset = 0;
        for (i, slot) in self.parts.iter().enumerate() {
            let range = if let Some(range) = &slot.range {
                range.clone()
            } else {
                let end = if let Some(next) = self.parts.get(i + 1) {
                    next.range
                        .as_ref()
                        .ok_or(Error::Unavailable(
                            "adjacent unavailable articles have ambiguous boundaries; trusted stored byte ranges are needed",
                        ))?
                        .start
                } else if self.total == Some(self.parts.len()) {
                    // Only a validated yEnc part count proves no article follows.
                    length
                } else {
                    return Err(Error::Unavailable(
                        "an unavailable last article has no known end; a yEnc part count or trusted stored byte ranges are needed",
                    ));
                };
                offset..end
            };
            if range.start != offset
                || range.end <= offset
                || range.end > length
                || range.end - offset > MAX_DECODED_LEN as u64
            {
                return Err(Error::Unavailable(
                    "article ranges do not provide exact contiguous source coverage",
                ));
            }
            offset = range.end;
            articles.push(Article {
                id: None,
                range,
                damage: slot.damage,
            });
        }
        if offset != length {
            return Err(Error::Unavailable(
                "source ranges do not cover the whole file",
            ));
        }
        for (article, slot) in articles.iter_mut().zip(self.parts) {
            article.id = slot.id;
        }
        Ok(articles)
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;

    use infinidysk_nntp::Body;
    use tokio_util::sync::CancellationToken;

    use super::super::{Budget, Damage, Error, Limits};
    use super::Slots;
    use crate::nzb;

    fn budget() -> Budget {
        Budget {
            limits: Limits {
                requests: 10,
                decoded_bytes: 10_000_000,
                memory_bytes: 10_000_000,
                comparisons: 100,
                retained_bytes: 0,
            },
            scratch: 0,
            cancel: CancellationToken::new(),
            network_bytes: 0,
            source: None,
            candidates: None,
            probe: None,
            progress: None,
            legacy_ids: BTreeSet::new(),
            legacy_bytes: 0,
        }
    }

    fn file(parts: i64) -> nzb::File {
        nzb::File {
            subject: String::new(),
            date: None,
            segments: (1..=parts)
                .map(|number| nzb::Segment {
                    id: format!("{number}@test"),
                    bytes: 10,
                    number,
                })
                .collect(),
            gap: false,
        }
    }

    fn body(part: u32, total: Option<u32>, file_size: u64) -> Body {
        Body {
            data: vec![0; 10].into(),
            offset: u64::from(part - 1) * 10,
            file_size,
            part: Some(part),
            total,
        }
    }

    #[test]
    fn an_unavailable_last_article_ends_at_eof_only_with_a_known_part_count() {
        let mut budget = budget();
        let mut slots = Slots::new(file(2), &mut budget).unwrap();
        slots.observe(0, &body(1, None, 20), &mut budget).unwrap();
        slots.damage(1, Damage::Missing);
        assert!(matches!(slots.finish(20), Err(Error::Unavailable(_))));

        let mut slots = Slots::new(file(2), &mut budget).unwrap();
        slots
            .observe(0, &body(1, Some(2), 20), &mut budget)
            .unwrap();
        slots.damage(1, Damage::Missing);
        let articles = slots.finish(20).unwrap();
        assert_eq!(articles[1].range, 10..20);
    }

    #[test]
    fn an_unavailable_interior_article_spans_its_known_neighbours() {
        let mut budget = budget();
        let mut slots = Slots::new(file(3), &mut budget).unwrap();
        slots.observe(0, &body(1, None, 30), &mut budget).unwrap();
        slots.observe(2, &body(3, None, 30), &mut budget).unwrap();
        slots.damage(1, Damage::Missing);
        let articles = slots.finish(30).unwrap();
        assert_eq!(articles[1].range, 10..20);
    }

    #[tokio::test]
    async fn a_damaged_local_repair_falls_back_to_providers() {
        use infinidysk_nntp::{
            Client, Downloads, Endpoint, MAX_DECODED_LEN, Pool, PoolConfig, Provider, RepairKey,
            TlsMode,
        };
        use infinidysk_testkit::{LoopbackServer, post};
        let data = b"posted article";
        let articles = post("fallback", "f.bin", data, data.len());
        let id = articles[0].message_id.clone();
        let server = LoopbackServer::start(articles).await.unwrap();
        let endpoint = Endpoint::new(
            "127.0.0.1",
            server.addr().port(),
            TlsMode::Plain,
            None,
            std::time::Duration::from_secs(5),
        )
        .unwrap();
        let config = PoolConfig {
            max_connections: 1,
            pipeline_depth: 1,
            idle_timeout: std::time::Duration::from_secs(5),
            min_connections: 0,
        };
        let client = Client::new([Provider {
            pool: Pool::new(endpoint, config, Downloads::new(4)).unwrap(),
            key: "test".to_owned(),
            name: String::new(),
            priority: 0,
            backbone: None,
        }]);
        let lookup: infinidysk_nntp::RepairLookup = Box::new(|key| match key {
            RepairKey::Article(_) => Some(Box::pin(async {
                Err(infinidysk_nntp::Error::Repair(
                    "stored article is missing or corrupt",
                ))
            })),
            RepairKey::Omitted(_) => None,
        });
        client.set_repairs(lookup);
        let mut budget = budget();
        budget.limits.decoded_bytes = 4 * MAX_DECODED_LEN as u64;
        let body = budget.body(&client, &id).await.unwrap();
        assert_eq!(body.data.as_ref(), data);
        assert!(matches!(
            budget.body(&client, "missing@test").await,
            Err(Error::Source(infinidysk_nntp::Error::NotFound))
        ));
    }
}
