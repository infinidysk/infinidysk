//! `POST /api/v1/nzb/inspect`: what an NZB contains, read the way an import reads it,
//! without importing it.

use std::sync::Arc;
use std::time::Duration;

use axum::Json;
use axum::extract::{Multipart, State};
use infinidysk_core::media;
use infinidysk_queue::import::{
    Access, ArchiveSet, ArchiveType, Encrypted, Failure, FailureKind, Inspection, NameSource, Step,
};
use infinidysk_queue::service;
use serde::Serialize;
use utoipa::ToSchema;

use super::{Admin, ApiError, Fail};
use crate::sab;

/// One NZB to inspect.
#[derive(ToSchema)]
#[allow(dead_code)] // OpenAPI shape only; `post_nzb_inspect` reads the form fields itself.
pub(super) struct InspectUpload {
    /// Plain or gzip, with the size limit of a queue upload.
    #[schema(value_type = String, format = Binary)]
    nzb: Vec<u8>,
    /// Archive password for the files this NZB posts. It wins over one in `name` and over
    /// the NZB's own. An NZB file inside them opens its files with its own password.
    password: Option<String>,
    /// The NZB file name an import would get, such as `Release{{password}}.nzb`: the
    /// release name and a password come from it. Like `password`, that password opens
    /// only what this NZB posts. The uploaded file's name when absent.
    name: Option<String>,
}

#[derive(Serialize, ToSchema)]
pub(super) struct NzbInspection {
    /// Whether the inner file list is complete. An archive left as a file is listed in
    /// `files` without its members. Such an archive, a reached limit or an unfinished
    /// plan makes this false. It says nothing about passwords or article availability.
    manifest_complete: bool,
    files: Vec<InspectedFile>,
    archives: Vec<InspectedArchive>,
    diagnostics: InspectDiagnostics,
}

#[derive(Serialize, ToSchema)]
struct InspectedFile {
    /// The path as posted or as stored in the archive, made safe for a file system.
    /// Match on this.
    raw_path: String,
    /// The path an import publishes below the job folder: obfuscated videos take the
    /// release name and duplicates get a number. Equals `raw_path` for an unreadable member.
    planned_name: String,
    /// Null when the file cannot be read and its archive does not record a size.
    #[schema(required = true)]
    size_bytes: Option<u64>,
    /// Lowercase extension when the file is video, audio or a book; null otherwise.
    #[schema(required = true)]
    media_extension: Option<String>,
    name_source: InspectNameSource,
    /// The entry of `archives` the file is a member of; null for a posted file.
    #[schema(required = true)]
    archive_set_id: Option<usize>,
    /// `nesting_depth` of that archive; 0 for a posted file.
    nesting_depth: usize,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectNameSource {
    /// The PAR2 index named the posted file.
    Par2,
    Subject,
    ArchiveHeader,
}

/// Volumes an import reads as one set. RAR volumes whose names name no set form one set.
#[derive(Serialize, ToSchema)]
struct InspectedArchive {
    archive_set_id: usize,
    archive_type: InspectArchiveType,
    /// 0 when the volumes are posted files; one more for each archive opened to reach
    /// them.
    nesting_depth: usize,
    encryption: InspectEncryption,
    content_access: InspectAccess,
    /// Whether this kind of content can be streamed at all, given the right password.
    /// Null until every member was checked, such as for encrypted headers without the
    /// password.
    #[schema(required = true)]
    stream_supported: Option<bool>,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectArchiveType {
    Rar4,
    Rar5,
    #[serde(rename = "7z")]
    SevenZip,
    /// A raw byte split such as `video.mkv.001`.
    Split,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectEncryption {
    None,
    /// File data only; names and sizes are readable.
    Data,
    /// Headers too; nothing is listed without the password.
    Headers,
    /// The headers were not read to the end.
    Unknown,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectAccess {
    Plain,
    /// Encrypted, and the password is absent or wrong.
    PasswordRequired,
    /// Encrypted, and the archive confirmed the password.
    PasswordValidated,
    /// Compressed, or encrypted with a scheme older than AES.
    Unsupported,
    /// Not established: the headers were not read to the end, or the archive has
    /// nothing to check the password against.
    Unknown,
}

#[derive(Serialize, ToSchema)]
struct InspectDiagnostics {
    /// Names of the NZB files whose first article every provider reported missing; observed,
    /// not inferred. A corrupt copy or an article for another part of the file is not listed.
    missing_first_segments: Vec<String>,
    warnings: Vec<InspectWarning>,
    /// Why an import of this NZB would not complete, as far as the inspection read;
    /// null when no failure was found.
    #[schema(required = true)]
    failure: Option<InspectFailure>,
    /// Time each stage took, for the NZB the report describes.
    stages: InspectStages,
    /// Article requests issued, for every NZB read. Failover inside the provider client
    /// is not counted.
    article_requests: u64,
    /// NZB files followed to reach the release, as an import follows a download that
    /// holds only an NZB file; 0 when the uploaded NZB names the release.
    inner_nzbs: usize,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectWarning {
    /// A password was used on an archive that has nothing to check it against.
    PasswordNotValidated,
    /// A limit cut the inspection short; `manifest_complete` is false.
    LimitExceeded,
    /// An archive inside an archive stayed a file: nested deeper than three levels,
    /// inside an encrypted member, or a member that cannot be read. Its members are not
    /// listed; `manifest_complete` is false.
    NestedArchiveOpaque,
}

#[derive(Serialize, ToSchema)]
struct InspectFailure {
    kind: InspectFailureKind,
    stage: InspectStage,
    message: String,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectFailureKind {
    /// The release was recently replaced because its articles are missing.
    Rejected,
    /// Providers could not answer; an import would retry later.
    ProvidersUnavailable,
    MissingArticles,
    /// An archive password is absent or wrong.
    Password,
    /// Archive content that cannot be streamed.
    Unsupported,
    /// The inspection reached a limit; it says nothing about an import.
    Limit,
    Other,
}

#[derive(Serialize, ToSchema)]
#[serde(rename_all = "snake_case")]
enum InspectStage {
    /// Before any provider request for the NZB that failed.
    Submission,
    FirstSegments,
    Par2,
    Layouts,
    Archives,
}

/// Time each stage took; null for a stage that did not finish.
#[derive(Serialize, ToSchema)]
#[allow(clippy::struct_field_names)] // API fields that carry a unit say so.
struct InspectStages {
    #[schema(required = true)]
    first_segments_ms: Option<u64>,
    #[schema(required = true)]
    par2_ms: Option<u64>,
    #[schema(required = true)]
    layouts_ms: Option<u64>,
    #[schema(required = true)]
    archives_ms: Option<u64>,
}

fn archive(id: usize, set: &ArchiveSet) -> InspectedArchive {
    InspectedArchive {
        archive_set_id: id,
        archive_type: match set.kind {
            ArchiveType::Rar4 => InspectArchiveType::Rar4,
            ArchiveType::Rar5 => InspectArchiveType::Rar5,
            ArchiveType::SevenZip => InspectArchiveType::SevenZip,
            ArchiveType::Split => InspectArchiveType::Split,
        },
        nesting_depth: set.depth,
        encryption: match set.encryption {
            Encrypted::Unknown => InspectEncryption::Unknown,
            Encrypted::None => InspectEncryption::None,
            Encrypted::Data => InspectEncryption::Data,
            Encrypted::Headers => InspectEncryption::Headers,
        },
        content_access: match set.access() {
            Access::Plain => InspectAccess::Plain,
            Access::PasswordRequired => InspectAccess::PasswordRequired,
            Access::PasswordValidated => InspectAccess::PasswordValidated,
            Access::Unsupported => InspectAccess::Unsupported,
            Access::Unknown => InspectAccess::Unknown,
        },
        stream_supported: set.streamable(),
    }
}

fn failure(failure: Failure) -> InspectFailure {
    InspectFailure {
        kind: match failure.kind {
            FailureKind::Rejected => InspectFailureKind::Rejected,
            FailureKind::ProvidersUnavailable => InspectFailureKind::ProvidersUnavailable,
            FailureKind::MissingArticles => InspectFailureKind::MissingArticles,
            FailureKind::Password => InspectFailureKind::Password,
            FailureKind::Unsupported => InspectFailureKind::Unsupported,
            FailureKind::Limit => InspectFailureKind::Limit,
            FailureKind::Other => InspectFailureKind::Other,
        },
        stage: match failure.step {
            Step::Headers => InspectStage::FirstSegments,
            Step::Par2 => InspectStage::Par2,
            Step::Layouts => InspectStage::Layouts,
            Step::Archives => InspectStage::Archives,
            // An inspection ends before the later steps.
            Step::Starting | Step::Checking | Step::Verifying | Step::Saving => {
                InspectStage::Submission
            }
        },
        message: failure.message,
    }
}

fn view(report: Inspection) -> NzbInspection {
    let ms = |from: Option<Duration>, to: Option<Duration>| {
        Some(u64::try_from(to?.saturating_sub(from?).as_millis()).unwrap_or(u64::MAX))
    };
    let at = report.timings;
    let mut warnings = Vec::new();
    if report.password_not_validated {
        warnings.push(InspectWarning::PasswordNotValidated);
    }
    if report.limit_exceeded {
        warnings.push(InspectWarning::LimitExceeded);
    }
    if report.nested_archive_opaque {
        warnings.push(InspectWarning::NestedArchiveOpaque);
    }
    let files = report
        .files
        .into_iter()
        .map(|file| InspectedFile {
            size_bytes: file.size,
            media_extension: media::kind(&file.planned_name)
                .and_then(|_| file.planned_name.rsplit_once('.'))
                .map(|(_, ext)| ext.to_ascii_lowercase()),
            name_source: match file.source {
                NameSource::Par2 => InspectNameSource::Par2,
                NameSource::Subject => InspectNameSource::Subject,
                NameSource::ArchiveHeader => InspectNameSource::ArchiveHeader,
            },
            archive_set_id: file.set,
            nesting_depth: file
                .set
                .and_then(|set| report.archives.get(set))
                .map_or(0, |set| set.depth),
            raw_path: file.raw_path,
            planned_name: file.planned_name,
        })
        .collect();
    NzbInspection {
        manifest_complete: report.manifest_complete,
        files,
        archives: report
            .archives
            .iter()
            .enumerate()
            .map(|(id, set)| archive(id, set))
            .collect(),
        diagnostics: InspectDiagnostics {
            missing_first_segments: report.missing_first_segments,
            warnings,
            failure: report.failure.map(failure),
            stages: InspectStages {
                first_segments_ms: ms(Some(Duration::ZERO), at.headers),
                par2_ms: ms(at.headers, at.par2),
                layouts_ms: ms(at.par2, at.layouts),
                archives_ms: ms(at.layouts, at.archives),
            },
            article_requests: report.article_requests,
            inner_nzbs: report.inner_nzbs,
        },
    }
}

/// Reports what an import of an NZB would publish, without queueing or publishing it.
///
/// Reads first articles, the PAR2 index and archive headers through the providers, so
/// it shows in provider statistics. It does not check that every article exists. When the
/// NZB's download holds only an NZB file, it reports the release that NZB names, as an
/// import would. One inspection runs at a time. It stops after 1,000 NZB files, 200
/// archive sets, 5,000 reported files, 3,000 article requests, 2 GB of decoded articles
/// or two minutes; the report then has the `limit_exceeded` warning.
#[utoipa::path(post, path = "/api/v1/nzb/inspect", tag = "queue",
    request_body(content = InspectUpload, content_type = "multipart/form-data"),
    responses(
        (status = 200, body = NzbInspection),
        (status = 400, body = ApiError, description = "No NZB, or one that does not parse"),
        (status = 409, body = ApiError, description = "An inspection is already running"),
    ))]
pub(super) async fn post_nzb_inspect(
    State(admin): State<Arc<Admin>>,
    mut form: Multipart,
) -> Result<Json<NzbInspection>, Fail> {
    // Taken before the body is read, so a refused request buffers nothing.
    let Some(slot) = admin.queue.start_inspection() else {
        return Err(Fail::Conflict(
            "an inspection is already running".to_owned(),
        ));
    };
    let (mut nzb, mut password, mut name) = (None, None, None);
    while let Some(field) = form
        .next_field()
        .await
        .map_err(|e| Fail::Bad(e.body_text()))?
    {
        match field.name() {
            Some("nzb") => {
                let uploaded = field.file_name().unwrap_or_default().to_owned();
                let data = field.bytes().await.map_err(|e| Fail::Bad(e.body_text()))?;
                nzb = Some((uploaded, data));
            }
            Some("password") => {
                password = Some(field.text().await.map_err(|e| Fail::Bad(e.body_text()))?);
            }
            Some("name") => {
                name = Some(field.text().await.map_err(|e| Fail::Bad(e.body_text()))?);
            }
            _ => {}
        }
    }
    let Some((uploaded, data)) = nzb else {
        return Err(Fail::Bad("no NZB file was sent".to_owned()));
    };
    let name = name.filter(|n| !n.trim().is_empty()).unwrap_or(uploaded);
    let file_name = sab::nzb_file_name(&name).map_err(|e| Fail::Bad(e.to_owned()))?;
    let password = password.filter(|p| !p.is_empty());
    let report = match slot.inspect(&file_name, &data, password.as_deref()).await {
        Ok(report) => report,
        Err(error @ service::Error::Nzb(_)) => return Err(Fail::Bad(error.to_string())),
        Err(error) => {
            tracing::warn!(reason = %error, "could not inspect an NZB");
            return Err(Fail::Internal);
        }
    };
    drop(slot);
    tracing::info!(
        files = report.files.len(),
        archives = report.archives.len(),
        complete = report.manifest_complete,
        requests = report.article_requests,
        "inspected an NZB"
    );
    Ok(Json(view(report)))
}
