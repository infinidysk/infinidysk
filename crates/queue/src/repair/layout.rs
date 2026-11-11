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

    fn grow(&mut self, length: usize, budget: &mut Budget) -> Result<(), Error> {
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
        self.parts[index].id.as_deref()
    }
    pub fn damage(&mut self, index: usize, damage: Damage) {
        self.parts[index].damage = Some(damage);
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
        if body.data.is_empty() {
            return Err(Error::Unavailable("empty source article"));
        }
        self.length = Some(body.file_size);
        self.parts[index].range = Some(body.offset..end);
        self.parts[index].damage = None;
        Ok(())
    }

    pub fn finish(self, length: u64) -> Result<Vec<Article>, Error> {
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
                            "adjacent unavailable articles have ambiguous boundaries",
                        ))?
                        .start
                } else {
                    length
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
