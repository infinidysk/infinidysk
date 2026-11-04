//! A 1.x install on PostgreSQL: where its connection string points, and a copy of the
//! tables the import reads into a SQLite file shaped like 1.x's own `db.sqlite`.
//!
//! The connection string is resolved as Npgsql 10.0.3 (the client 1.x shipped with) resolves
//! it. The string, the resolved settings and the connect options are never logged or
//! `Debug`-printed: they hold the password.

use std::collections::HashMap;
use std::fs::OpenOptions;
use std::io;
use std::os::unix::fs::OpenOptionsExt as _;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use futures_util::TryStreamExt as _;
use sqlx::postgres::{PgArguments, PgConnectOptions, PgConnection, PgSslMode};
use sqlx::query::Query;
use sqlx::sqlite::{SqliteArguments, SqliteConnectOptions, SqliteJournalMode, SqliteSynchronous};
use sqlx::{AssertSqlSafe, ConnectOptions as _, Connection as _, Postgres, Row as _, Sqlite};

use crate::{Error, Progress, Stage};

/// The copy, inside the C# `/config`. While it exists, the importer reads it in place of
/// `db.sqlite`; it exists only while an import from PostgreSQL runs.
pub const STAGED: &str = "v1-postgres-import.sqlite";
const STAGED_TMP: &str = "v1-postgres-import.sqlite.tmp";
/// The tables the import reads.
const TABLES: [&str; 7] = [
    "__EFMigrationsHistory",
    "Accounts",
    "ConfigItems",
    "DavItems",
    "HistoryItems",
    "QueueItems",
    "QueueNzbContents",
];
/// Npgsql's default `Timeout`; covers every connect attempt together.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(15);
const PROGRESS_ROWS: u64 = 1000;
/// Copied text between progress reports, so large NZB rows still give a heartbeat.
const PROGRESS_BYTES: usize = 16_000_000;
/// The relation an unqualified name resolves to through `search_path`, fully qualified.
const RELATION: &str = "SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname) \
                        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace \
                        WHERE c.oid = to_regclass(quote_ident($1))";
const COLUMNS: &str = "SELECT a.attname::text, format_type(a.atttypid, NULL) \
                       FROM pg_attribute a WHERE a.attrelid = to_regclass(quote_ident($1)) \
                       AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum";

/// Why `DATABASE_CONNECTION_STRING` cannot be used. Never carries a value from it.
#[derive(Debug, thiserror::Error)]
pub enum ConnectionError {
    #[error("DATABASE_CONNECTION_STRING is malformed: check its quotes and '=' signs")]
    Malformed,
    #[error("DATABASE_CONNECTION_STRING: entry {0} has a key Npgsql does not know")]
    UnknownKey(usize),
    #[error("DATABASE_CONNECTION_STRING has no {0}; add the value 1.x used")]
    Missing(&'static str),
    #[error("DATABASE_CONNECTION_STRING: {0} has a value the import cannot read")]
    Invalid(&'static str),
    /// `fix` says what to change instead; see `CHECKS` and the Host forms.
    #[error("DATABASE_CONNECTION_STRING: {setting} is not supported by the import from 1.x; {fix}")]
    Unsupported {
        setting: &'static str,
        fix: &'static str,
    },
    #[error(
        "the PostgreSQL password file cannot be read ({0}); fix it, or remove Passfile and PGPASSFILE"
    )]
    PassfileUnreadable(#[source] io::Error),
    #[error(
        "line {0} of the PostgreSQL password file is malformed; fix it, or remove Passfile and PGPASSFILE"
    )]
    PassfileMalformed(usize),
    #[error(
        "DATABASE_CONNECTION_STRING: SSL Mode VerifyCA and VerifyFull are not supported by the import \
         from 1.x yet: it cannot limit trust to your root certificate the way 1.x did. Keep running \
         1.x for now, or run the import with SSL Mode=Require over a network you trust"
    )]
    CertificateCheck,
    #[error(
        "the import cannot send the PostgreSQL options 1.x sent (Options, PGOPTIONS); unset PGOPTIONS for the import"
    )]
    OptionsMismatch,
}

/// A setting of Npgsql 10.0.3's connection string, aliases folded.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum Setting {
    Host,
    Port,
    Database,
    Username,
    Password,
    Passfile,
    SslMode,
    RootCertificate,
    SslCertificate,
    SslKey,
    SearchPath,
    RequireAuth,
    ChannelBinding,
    GssEncryptionMode,
    SslNegotiation,
    SslPassword,
    KerberosServiceName,
    IncludeRealm,
    TargetSessionAttributes,
    Options,
    ReplicationMode,
    /// Tunes only the .NET client, or (Check Certificate Revocation) takes effect only in
    /// the refused `VerifyCA`/`VerifyFull`.
    Ignored,
}

/// The setting a lowercased keyword names: each setting's display name, property name and
/// synonyms in Npgsql 10.0.3. `None` when Npgsql rejects the keyword.
fn setting(keyword: &str) -> Option<Setting> {
    Some(match keyword {
        "host" | "server" => Setting::Host,
        "port" => Setting::Port,
        "database" | "db" => Setting::Database,
        "username" | "user name" | "userid" | "user id" | "uid" => Setting::Username,
        "password" | "psw" | "pwd" => Setting::Password,
        "passfile" => Setting::Passfile,
        "ssl mode" | "sslmode" => Setting::SslMode,
        "root certificate" | "rootcertificate" => Setting::RootCertificate,
        "ssl certificate" | "sslcertificate" => Setting::SslCertificate,
        "ssl key" | "sslkey" => Setting::SslKey,
        "search path" | "searchpath" => Setting::SearchPath,
        "require auth" | "requireauth" => Setting::RequireAuth,
        "channel binding" | "channelbinding" => Setting::ChannelBinding,
        "gss encryption mode" | "gssencryptionmode" => Setting::GssEncryptionMode,
        "ssl negotiation" | "sslnegotiation" => Setting::SslNegotiation,
        "ssl password" | "sslpassword" => Setting::SslPassword,
        "kerberos service name" | "kerberosservicename" | "krbsrvname" => {
            Setting::KerberosServiceName
        }
        "include realm" | "includerealm" => Setting::IncludeRealm,
        "target session attributes" | "targetsessionattributes" => Setting::TargetSessionAttributes,
        "options" => Setting::Options,
        "replication mode" | "replicationmode" => Setting::ReplicationMode,
        "check certificate revocation"
        | "checkcertificaterevocation"
        | "application name"
        | "applicationname"
        | "enlist"
        | "client encoding"
        | "clientencoding"
        | "encoding"
        | "timezone"
        | "persist security info"
        | "persistsecurityinfo"
        | "log parameters"
        | "logparameters"
        | "include error detail"
        | "includeerrordetail"
        | "include failed batched command"
        | "includefailedbatchedcommand"
        | "pooling"
        | "minimum pool size"
        | "minpoolsize"
        | "maximum pool size"
        | "maxpoolsize"
        | "connection idle lifetime"
        | "connectionidlelifetime"
        | "connection pruning interval"
        | "connectionpruninginterval"
        | "connection lifetime"
        | "connectionlifetime"
        | "load balance timeout"
        | "timeout"
        | "command timeout"
        | "commandtimeout"
        | "cancellation timeout"
        | "cancellationtimeout"
        | "load balance hosts"
        | "loadbalancehosts"
        | "host recheck seconds"
        | "hostrecheckseconds"
        | "keepalive"
        | "tcp keepalive"
        | "tcpkeepalive"
        | "tcp keepalive time"
        | "tcpkeepalivetime"
        | "tcp keepalive interval"
        | "tcpkeepaliveinterval"
        | "read buffer size"
        | "readbuffersize"
        | "write buffer size"
        | "writebuffersize"
        | "socket receive buffer size"
        | "socketreceivebuffersize"
        | "socket send buffer size"
        | "socketsendbuffersize"
        | "max auto prepare"
        | "maxautoprepare"
        | "auto prepare min usages"
        | "autoprepareminusages"
        | "no reset on close"
        | "noresetonclose"
        | "array nullability mode"
        | "arraynullabilitymode"
        | "multiplexing"
        | "write coalescing buffer threshold bytes"
        | "writecoalescingbufferthresholdbytes"
        | "load table composites"
        | "loadtablecomposites"
        | "server compatibility mode"
        | "servercompatibilitymode"
        | "trust server certificate"
        | "trustservercertificate"
        | "internal command timeout"
        | "internalcommandtimeout" => Setting::Ignored,
        _ => return None,
    })
}

/// `(setting, name in errors, variable Npgsql falls back to, values the copy honors, fix)`.
type Check = (
    Setting,
    &'static str,
    Option<&'static str>,
    &'static [&'static str],
    &'static str,
);

/// Security settings sqlx cannot enforce, or mechanisms it does not have.
const CHECKS: &[Check] = &[
    (
        Setting::RequireAuth,
        "Require Auth",
        Some("PGREQUIREAUTH"),
        &[""],
        "remove it and PGREQUIREAUTH for the import",
    ),
    (
        Setting::ChannelBinding,
        "Channel Binding",
        None,
        &["prefer", "disable"],
        "set it to Prefer for the import",
    ),
    (
        Setting::GssEncryptionMode,
        "GSS Encryption Mode",
        Some("PGGSSENCMODE"),
        &["prefer", "disable"],
        "use Prefer, in the string and in PGGSSENCMODE, for the import",
    ),
    (
        Setting::SslNegotiation,
        "SSL Negotiation",
        Some("PGSSLNEGOTIATION"),
        &["postgres"],
        "use Postgres, in the string and in PGSSLNEGOTIATION, for the import",
    ),
    (
        Setting::SslPassword,
        "SSL Password",
        None,
        &[""],
        "use an unencrypted client key file for the import",
    ),
    (
        Setting::KerberosServiceName,
        "Kerberos Service Name",
        None,
        &["postgres"],
        "the import has no Kerberos login; use a password login",
    ),
    (
        Setting::IncludeRealm,
        "Include Realm",
        None,
        &["true"],
        "the import has no Kerberos login; use a password login",
    ),
    (
        Setting::TargetSessionAttributes,
        "Target Session Attributes",
        None,
        &["any"],
        "remove it; the import connects to a single server",
    ),
    (
        Setting::Options,
        "Options",
        None,
        &[""],
        "move its value into PGOPTIONS, which the import passes on unchanged",
    ),
    (
        Setting::ReplicationMode,
        "Replication Mode",
        None,
        &["off"],
        "remove it",
    ),
];

/// Where and how 1.x connected, resolved as Npgsql 10.0.3 does. Holds a password: no `Debug`.
struct Target {
    host: String,
    port: u16,
    username: String,
    database: String,
    password: Option<String>,
    ssl_mode: PgSslMode,
    client_cert: Option<PathBuf>,
    client_key: Option<PathBuf>,
    search_path: Option<String>,
    /// What Npgsql would send as `options`: `Settings.Options ?? PGOPTIONS`; empty sends nothing.
    options: Option<String>,
}

/// Entries in input order, keys trimmed and lowercased; `None` for an empty value.
fn entries(s: &str) -> Result<Vec<(String, Option<String>)>, ConnectionError> {
    fn skip(s: &str) -> &str {
        s.trim_start_matches(|c: char| c.is_whitespace() || c == ';')
    }
    let mut out = Vec::new();
    let mut rest = skip(s);
    while !rest.is_empty() {
        let (key, after) = key(rest)?;
        let after = after.trim_start();
        let (value, tail) = if let Some(q @ ('"' | '\'')) = after.chars().next() {
            let (value, tail) = quoted(&after[1..], q)?;
            (Some(value), tail)
        } else {
            let end = after.find(';').unwrap_or(after.len());
            let value = after[..end].trim();
            if value.ends_with(['"', '\'']) || value.contains(char::is_control) {
                return Err(ConnectionError::Malformed);
            }
            ((!value.is_empty()).then(|| value.to_owned()), &after[end..])
        };
        let tail = tail.trim_start();
        if !(tail.is_empty() || tail.starts_with(';')) {
            return Err(ConnectionError::Malformed);
        }
        out.push((key, value));
        rest = skip(tail);
    }
    Ok(out)
}

/// The key up to the first single `=` (`==` is a literal `=`), trimmed and lowercased.
fn key(s: &str) -> Result<(String, &str), ConnectionError> {
    let mut key = String::new();
    let mut chars = s.char_indices().peekable();
    while let Some((i, c)) = chars.next() {
        match c {
            '=' if chars.next_if(|&(_, n)| n == '=').is_some() => key.push('='),
            '=' => {
                let key = key.trim().to_lowercase();
                return if key.is_empty() {
                    Err(ConnectionError::Malformed)
                } else {
                    Ok((key, &s[i + 1..]))
                };
            }
            c if c.is_control() => return Err(ConnectionError::Malformed),
            c => key.push(c),
        }
    }
    Err(ConnectionError::Malformed)
}

/// Reads up to the closing `q`; a doubled `q` is one literal `q`. Returns the value and the rest.
fn quoted(s: &str, q: char) -> Result<(String, &str), ConnectionError> {
    let mut value = String::new();
    let mut chars = s.char_indices().peekable();
    while let Some((i, c)) = chars.next() {
        if c != q {
            value.push(c);
        } else if chars.next_if(|&(_, n)| n == q).is_some() {
            value.push(q);
        } else {
            return Ok((value, &s[i + c.len_utf8()..]));
        }
    }
    Err(ConnectionError::Malformed)
}

/// Resolves `connection` with variables read through `env` (`HOME` included).
fn target(
    connection: &str,
    env: &(dyn Fn(&str) -> Option<String> + Sync),
) -> Result<Target, ConnectionError> {
    // 1. Fold aliases; the last entry wins and an empty value unsets.
    let mut settings: HashMap<Setting, Option<String>> = HashMap::new();
    for (n, (key, value)) in entries(connection)?.into_iter().enumerate() {
        match setting(&key) {
            None => return Err(ConnectionError::UnknownKey(n + 1)),
            Some(Setting::Ignored) => {}
            Some(s) => {
                settings.insert(s, value);
            }
        }
    }
    let get = |s: Setting| settings.get(&s).cloned().flatten();
    let env = |name: &str| env(name).filter(|v| !v.is_empty());

    // 2. Security checks, before anything else is resolved.
    for &(setting, name, variable, honored, fix) in CHECKS {
        let Some(value) = get(setting).or_else(|| variable.and_then(env)) else {
            continue;
        };
        let value = value.trim().to_lowercase();
        if setting == Setting::IncludeRealm && !matches!(value.as_str(), "true" | "false") {
            return Err(ConnectionError::Invalid(name));
        }
        if !honored.contains(&value.as_str()) {
            return Err(ConnectionError::Unsupported { setting: name, fix });
        }
    }

    // 3. Host and port.
    let host = get(Setting::Host)
        .map(|h| h.trim().to_owned())
        .filter(|h| !h.is_empty())
        .ok_or(ConnectionError::Missing("Host"))?;
    if host.starts_with('@') {
        return Err(ConnectionError::Unsupported {
            setting: "Host as an abstract socket",
            fix: "use a host name or a socket directory",
        });
    }
    if host.contains(',') {
        return Err(ConnectionError::Unsupported {
            setting: "Host with more than one server",
            fix: "use a single server",
        });
    }
    let port = match get(Setting::Port) {
        None => 5432,
        Some(p) => parse_port(&p).ok_or(ConnectionError::Invalid("Port"))?,
    };
    let (host, port) = if host.starts_with('/') {
        (host, port)
    } else {
        split_host_port(&host)?.unwrap_or((host, port))
    };
    let host = match host.strip_prefix('[').and_then(|h| h.strip_suffix(']')) {
        Some(bare) => bare.to_owned(),
        None => host,
    };

    // 4. Username and database.
    let username = get(Setting::Username)
        .filter(|u| !u.is_empty())
        .or_else(|| env("PGUSER"))
        .ok_or(ConnectionError::Missing("Username"))?;
    let database_key = get(Setting::Database).filter(|d| !d.is_empty());
    let database = database_key.clone().unwrap_or_else(|| username.clone());

    // 5. SSL mode.
    let ssl_mode = match get(Setting::SslMode)
        .map(|m| m.trim().to_lowercase())
        .as_deref()
    {
        None | Some("prefer") => PgSslMode::Prefer,
        Some("disable") => PgSslMode::Disable,
        Some("allow") => PgSslMode::Allow,
        Some("require") => PgSslMode::Require,
        Some("verifyca" | "verifyfull") => return Err(ConnectionError::CertificateCheck),
        Some(_) => return Err(ConnectionError::Invalid("SSL Mode")),
    };

    // 6. Password: the key, PGPASSWORD, then the password file.
    let home = env("HOME").map(PathBuf::from);
    let mut password = get(Setting::Password).or_else(|| env("PGPASSWORD"));
    if password.is_none() {
        let file = get(Setting::Passfile)
            .or_else(|| env("PGPASSFILE"))
            .map(PathBuf::from)
            .or_else(|| {
                home.as_ref()
                    .map(|h| h.join(".pgpass"))
                    .filter(|p| p.is_file())
            });
        if let Some(file) = file {
            let text =
                std::fs::read_to_string(file).map_err(ConnectionError::PassfileUnreadable)?;
            let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
            password = passfile(text, &host, port, database_key.as_deref(), &username)?;
        }
    }

    // 7. Client certificate and key.
    let default = |name: &str| {
        home.as_ref()
            .map(|h| h.join(".postgresql").join(name))
            .filter(|p| p.exists())
    };
    let client_cert = get(Setting::SslCertificate)
        .or_else(|| env("PGSSLCERT"))
        .map(PathBuf::from)
        .or_else(|| default("postgresql.crt"));
    let client_key = get(Setting::SslKey)
        .or_else(|| env("PGSSLKEY"))
        .map(PathBuf::from)
        .or_else(|| default("postgresql.key"));

    // 8. and 9. Search path and options. `Options=""` hides PGOPTIONS; `Options=` does not.
    let search_path = get(Setting::SearchPath).filter(|s| !s.is_empty());
    let options = get(Setting::Options).or_else(|| env("PGOPTIONS"));

    Ok(Target {
        host,
        port,
        username,
        database,
        password,
        ssl_mode,
        client_cert,
        client_key,
        search_path,
        options,
    })
}

/// A port as Npgsql's `int.Parse` reads it, limited to 1–65535.
fn parse_port(s: &str) -> Option<u16> {
    s.trim().parse::<u16>().ok().filter(|&p| p > 0)
}

/// Npgsql's `TrySplitHostPort`: `host:port` and `[addr]:port` split; a bare IPv6 address
/// does not.
fn split_host_port(host: &str) -> Result<Option<(String, u16)>, ConnectionError> {
    let Some(colon) = host.rfind(':') else {
        return Ok(None);
    };
    let before = &host[..colon];
    let other_colon = before.rfind(':');
    let ipv6_end = before.rfind(']');
    if other_colon.is_none() || ipv6_end.is_some_and(|end| colon == end + 1) {
        let port = parse_port(&host[colon + 1..]).ok_or(ConnectionError::Invalid("Host"))?;
        return Ok(Some((before.to_owned(), port)));
    }
    Ok(None)
}

/// The password of the first `.pgpass` line matching the query; `database: None` matches any.
fn passfile(
    text: &str,
    host: &str,
    port: u16,
    database: Option<&str>,
    username: &str,
) -> Result<Option<String>, ConnectionError> {
    for (n, line) in text.lines().enumerate() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        let malformed = || ConnectionError::PassfileMalformed(n + 1);
        let [h, p, d, u, password] = fields(line).ok_or_else(malformed)?;
        let p = p
            .map(|p| p.trim().parse::<i32>())
            .transpose()
            .map_err(|_| malformed())?;
        if h.as_deref().is_none_or(|h| h == host)
            && p.is_none_or(|p| p == i32::from(port))
            && d.as_deref().zip(database).is_none_or(|(d, q)| d == q)
            && u.as_deref().is_none_or(|u| u == username)
        {
            return Ok(password);
        }
    }
    Ok(None)
}

/// The 5 `:`-separated fields of a line; `\:` and `\\` are literal; `*` is `None`.
fn fields(line: &str) -> Option<[Option<String>; 5]> {
    let mut parts = Vec::with_capacity(5);
    let mut field = String::new();
    let mut chars = line.chars().peekable();
    let finish = |field: &mut String| {
        let part = std::mem::take(field);
        (part != "*").then_some(part)
    };
    while let Some(c) = chars.next() {
        match c {
            '\\' if chars.peek().is_some() => {
                let next = chars.next()?;
                if !matches!(next, ':' | '\\') {
                    field.push('\\');
                }
                field.push(next);
            }
            ':' => parts.push(finish(&mut field)),
            c => field.push(c),
        }
    }
    parts.push(finish(&mut field));
    parts.try_into().ok()
}

/// The mode of the first attempt: `Allow` starts without TLS (`OpenCore`).
const fn first(mode: PgSslMode) -> PgSslMode {
    match mode {
        PgSslMode::Allow => PgSslMode::Disable,
        mode => mode,
    }
}

/// Npgsql 10's one retry after `error` (`OpenCore`), or `None`.
fn retry(mode: PgSslMode, error: &sqlx::Error) -> Option<PgSslMode> {
    match mode {
        PgSslMode::Allow => Some(PgSslMode::Require),
        // Only a server error proves the handshake did not fail; Npgsql never drops TLS
        // after a failed one.
        PgSslMode::Prefer if matches!(error, sqlx::Error::Database(_)) => Some(PgSslMode::Disable),
        _ => None,
    }
}

impl Target {
    /// Options with host, port, user, database and SSL mode always set, so sqlx's own reads
    /// of `PGHOST`, `PGHOSTADDR`, `PGPORT`, `PGDATABASE` and `PGSSLMODE` never decide them.
    /// `OptionsMismatch` when sqlx would send other `options` than `self.options`.
    fn options(&self, ssl_mode: PgSslMode) -> Result<PgConnectOptions, ConnectionError> {
        let mut options = PgConnectOptions::new_without_pgpass()
            .host(&self.host)
            .port(self.port)
            .username(&self.username)
            .database(&self.database)
            .ssl_mode(ssl_mode);
        if let Some(password) = &self.password {
            options = options.password(password);
        }
        if let Some(cert) = &self.client_cert {
            options = options.ssl_client_cert(cert);
        }
        if let Some(key) = &self.client_key {
            options = options.ssl_client_key(key);
        }
        let sent = options.get_options().unwrap_or_default();
        if sent != self.options.as_deref().unwrap_or_default() {
            return Err(ConnectionError::OptionsMismatch);
        }
        Ok(options)
    }
}

/// Connects as Npgsql would: `first`, then at most one `retry`, both within `CONNECT_TIMEOUT`.
async fn connect(target: &Target) -> Result<PgConnection, Error> {
    let deadline = tokio::time::Instant::now() + CONNECT_TIMEOUT;
    let attempt = |mode| async move {
        let options = target.options(mode)?;
        tokio::time::timeout_at(deadline, options.connect())
            .await
            .map_err(|_| Error::ConnectTimeout)
    };
    let error = match attempt(first(target.ssl_mode)).await? {
        Ok(conn) => return Ok(conn),
        Err(error) => error,
    };
    match retry(target.ssl_mode, &error) {
        Some(mode) => attempt(mode).await?.map_err(Error::Connect),
        None => Err(Error::Connect(error)),
    }
}

/// How a column is read from PostgreSQL and stored in the copy.
#[derive(Clone, Copy)]
enum Kind {
    Int,
    Text,
}

/// The select expression, the SQLite type and the cell kind EF on SQLite uses.
fn column(name: &str, data_type: &str) -> (String, &'static str, Kind) {
    let c = ident(name);
    match data_type {
        "uuid" => (format!("upper({c}::text)"), "TEXT", Kind::Text),
        "boolean" => (format!("{c}::int::int8"), "INTEGER", Kind::Int),
        "smallint" | "integer" | "bigint" => (format!("{c}::int8"), "INTEGER", Kind::Int),
        // `timestamp`: with sqlx's `DateStyle=ISO` this is EF's `yyyy-MM-dd HH:mm:ss.FFFFFFF`.
        _ => (format!("{c}::text"), "TEXT", Kind::Text),
    }
}

/// A quoted identifier, `"` doubled. Names come only from `TABLES` and the 1.x catalog.
fn ident(name: &str) -> String {
    format!("\"{}\"", name.replace('"', "\"\""))
}

fn dynamic_pg(sql: String) -> Query<'static, Postgres, PgArguments> {
    sqlx::query(AssertSqlSafe(sql)).persistent(false)
}

fn dynamic_sqlite(sql: Arc<str>) -> Query<'static, Sqlite, SqliteArguments> {
    sqlx::query(AssertSqlSafe(sql))
}

/// One table to copy: its qualified name and its columns.
struct Table {
    name: &'static str,
    relation: String,
    columns: Vec<(String, String)>,
}

/// Copies `TABLES` from the 1.x database into `config/STAGED` in one read-only snapshot,
/// replacing an earlier copy. Reports [`Stage::Copying`] by rows.
///
/// `env` and `progress` are borrowed `Sync` trait objects: the returned future holds them
/// across awaits and runs inside a spawned task, so it must be `Send`, and `&T` is `Send`
/// only when `T: Sync`.
pub async fn stage(
    config: &Path,
    connection: &str,
    env: &(dyn Fn(&str) -> Option<String> + Sync),
    progress: &(dyn Fn(Progress) + Sync),
) -> Result<(), Error> {
    progress(Progress::counting(Stage::Copying));
    let target = target(connection, env)?;
    remove_staged(config)?;

    let mut pg = connect(&target).await?;
    sqlx::raw_sql("BEGIN ISOLATION LEVEL REPEATABLE READ, READ ONLY")
        .execute(&mut pg)
        .await?;
    sqlx::query("SELECT set_config('lock_timeout', '30s', true)")
        .execute(&mut pg)
        .await?;
    if let Some(search_path) = &target.search_path {
        sqlx::query("SELECT set_config('search_path', $1, true)")
            .bind(search_path)
            .execute(&mut pg)
            .await?;
    }

    let mut tables = Vec::with_capacity(TABLES.len());
    let mut total: u64 = 0;
    for name in TABLES {
        progress(Progress::counting(Stage::Copying));
        let relation: Option<String> = sqlx::query_scalar(RELATION)
            .bind(name)
            .fetch_optional(&mut pg)
            .await?;
        let relation = relation.ok_or(Error::MissingTable(name))?;
        let columns: Vec<(String, String)> = sqlx::query_as(COLUMNS)
            .bind(name)
            .fetch_all(&mut pg)
            .await?;
        let rows: i64 =
            sqlx::query_scalar(AssertSqlSafe(format!("SELECT count(*) FROM {relation}")))
                .persistent(false)
                .fetch_one(&mut pg)
                .await?;
        total += u64::try_from(rows).unwrap_or_default();
        tables.push(Table {
            name,
            relation,
            columns,
        });
    }
    progress(Progress::at(Stage::Copying, 0, total));

    let tmp = config.join(STAGED_TMP);
    OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&tmp)
        .map_err(Error::Stage)?;
    let mut copy = SqliteConnectOptions::new()
        .filename(&tmp)
        .journal_mode(SqliteJournalMode::Off)
        .synchronous(SqliteSynchronous::Off)
        .connect()
        .await?;
    sqlx::raw_sql("BEGIN").execute(&mut copy).await?;

    let mut done: u64 = 0;
    let mut bytes: usize = 0;
    for table in &tables {
        let mapped: Vec<_> = table
            .columns
            .iter()
            .map(|(name, data_type)| (name, column(name, data_type)))
            .collect();
        let definition = mapped
            .iter()
            .map(|(name, (_, sqlite_type, _))| format!("{} {sqlite_type}", ident(name)))
            .collect::<Vec<_>>()
            .join(", ");
        sqlx::raw_sql(AssertSqlSafe(format!(
            "CREATE TABLE {} ({definition})",
            ident(table.name)
        )))
        .execute(&mut copy)
        .await?;
        let placeholders = (1..=mapped.len())
            .map(|i| format!("${i}"))
            .collect::<Vec<_>>()
            .join(", ");
        let insert: Arc<str> =
            format!("INSERT INTO {} VALUES ({placeholders})", ident(table.name)).into();
        let kinds: Vec<Kind> = mapped.iter().map(|(_, (_, _, kind))| *kind).collect();
        let select = mapped
            .iter()
            .map(|(_, (expr, _, _))| expr.as_str())
            .collect::<Vec<_>>()
            .join(", ");
        let select = dynamic_pg(format!("SELECT {select} FROM {}", table.relation));
        let mut rows = select.fetch(&mut pg);
        while let Some(row) = rows.try_next().await? {
            let mut insert = dynamic_sqlite(Arc::clone(&insert));
            for (i, kind) in kinds.iter().enumerate() {
                insert = match kind {
                    Kind::Int => insert.bind(row.try_get::<Option<i64>, _>(i)?),
                    Kind::Text => {
                        let text: Option<String> = row.try_get(i)?;
                        bytes += text.as_ref().map_or(0, String::len);
                        insert.bind(text)
                    }
                };
            }
            insert.execute(&mut copy).await?;
            done += 1;
            if done.is_multiple_of(PROGRESS_ROWS) || bytes >= PROGRESS_BYTES {
                bytes = 0;
                progress(Progress::at(Stage::Copying, done, total.max(done)));
            }
        }
    }

    sqlx::raw_sql("COMMIT").execute(&mut copy).await?;
    copy.close().await?;
    pg.close().await?;
    std::fs::rename(&tmp, config.join(STAGED)).map_err(Error::Stage)?;
    progress(Progress::at(Stage::Copying, done, total.max(done)));
    tracing::info!(rows = done, "C# import: 1.x PostgreSQL tables copied");
    Ok(())
}

/// Deletes the copy and a partial one; nothing when absent.
pub fn remove_staged(config: &Path) -> Result<(), Error> {
    for name in [STAGED, STAGED_TMP] {
        match std::fs::remove_file(config.join(name)) {
            Err(error) if error.kind() != io::ErrorKind::NotFound => {
                return Err(Error::Stage(error));
            }
            _ => {}
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::process::Command;
    use std::sync::Mutex;

    use tokio::io::{AsyncReadExt as _, AsyncWriteExt as _};
    use tokio::net::{TcpListener, TcpStream};
    use uuid::Uuid;

    use super::*;

    fn fake(vars: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> + Sync {
        let vars: HashMap<String, String> = vars
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        move |name| vars.get(name).cloned()
    }

    fn resolve(connection: &str) -> Result<Target, ConnectionError> {
        target(connection, &fake(&[]))
    }

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("infinidysk-{name}-{}", Uuid::now_v7()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn unsupported(result: Result<Target, ConnectionError>) -> &'static str {
        match result {
            Err(ConnectionError::Unsupported { setting, .. }) => setting,
            Err(other) => panic!("expected Unsupported, got {other}"),
            Ok(_) => panic!("expected Unsupported, got a target"),
        }
    }

    fn error(result: Result<Target, ConnectionError>) -> ConnectionError {
        match result {
            Err(error) => error,
            Ok(_) => panic!("expected an error, got a target"),
        }
    }

    #[test]
    fn reads_npgsql_keys_aliases_and_quotes() {
        let t = resolve(
            r#" server = db.lan ; PORT=5433;DB=dav;User Id=u;Pwd="p;w=""x""";SslMode=require;Search Path='a''b'"#,
        )
        .unwrap();
        assert_eq!(t.host, "db.lan");
        assert_eq!(t.port, 5433);
        assert_eq!(t.database, "dav");
        assert_eq!(t.username, "u");
        assert_eq!(t.password.as_deref(), Some(r#"p;w="x""#));
        assert!(matches!(t.ssl_mode, PgSslMode::Require));
        assert_eq!(t.search_path.as_deref(), Some("a'b"));
    }

    #[test]
    fn the_last_alias_wins() {
        let database = |rest: &str| {
            resolve(&format!("Host=h;Username=u;{rest}"))
                .unwrap()
                .database
        };
        assert_eq!(database("Database=old;DB=current"), "current");
        assert_eq!(database("DB=current;Database=old"), "old");
        assert_eq!(database("Database=a;DB=b;Database=c"), "c");
        assert_eq!(database("Database=x;DB="), "u");
    }

    #[test]
    fn host_forms_follow_npgsql() {
        let at = |connection: &str| {
            let t = resolve(&format!("{connection};Username=u")).unwrap();
            (t.host, t.port)
        };
        assert_eq!(at("Host=db:6543;Port=5432"), ("db".to_owned(), 6543));
        assert_eq!(at("Host=[::1]:6543"), ("::1".to_owned(), 6543));
        assert_eq!(at("Host=::1"), ("::1".to_owned(), 5432));
        assert_eq!(
            at("Host=/run/postgresql"),
            ("/run/postgresql".to_owned(), 5432)
        );
        assert_eq!(
            unsupported(resolve("Host=@sock;Username=u")),
            "Host as an abstract socket"
        );
        let several = resolve("Host=a,b;Username=u");
        assert!(
            error(resolve("Host=a,b;Username=u"))
                .to_string()
                .contains("use a single server")
        );
        assert_eq!(unsupported(several), "Host with more than one server");
        assert!(matches!(
            error(resolve("Host=db:x;Username=u")),
            ConnectionError::Invalid("Host")
        ));
        assert!(matches!(
            error(resolve("Username=u")),
            ConnectionError::Missing("Host")
        ));
    }

    #[test]
    fn npgsql_ignores_the_variables_sqlx_reads() {
        let env = fake(&[
            ("PGHOST", "evil"),
            ("PGHOSTADDR", "10.9.9.9"),
            ("PGPORT", "1"),
            ("PGDATABASE", "other"),
            ("PGSSLMODE", "disable"),
            ("PGUSER", "u"),
        ]);
        let t = target("Host=db", &env).unwrap();
        assert_eq!((t.host.as_str(), t.port), ("db", 5432));
        assert_eq!((t.username.as_str(), t.database.as_str()), ("u", "u"));
        assert!(matches!(t.ssl_mode, PgSslMode::Prefer));
        let options = t.options(PgSslMode::Prefer).unwrap();
        assert_eq!(options.get_host(), "db");
        assert_eq!(options.get_port(), 5432);
        assert_eq!(options.get_username(), "u");
        assert_eq!(options.get_database(), Some("u"));
        assert!(matches!(options.get_ssl_mode(), PgSslMode::Prefer));
        assert!(matches!(
            error(resolve("Host=db")),
            ConnectionError::Missing("Username")
        ));
    }

    #[test]
    fn the_password_file_is_read_with_the_final_identity() {
        let dir = temp_dir("pgpass");
        let home = temp_dir("home");
        let file = dir.join("pgpass");
        let home_var = home.to_str().unwrap().to_owned();
        let password = |lines: &[&str], extra: &str, vars: &[(&str, &str)]| {
            std::fs::write(&file, lines.join("\n")).unwrap();
            let mut all = vec![("HOME", home_var.as_str())];
            all.extend_from_slice(vars);
            target(
                &format!(
                    "Host=db.lan:5433;Username=u;Passfile={}{extra}",
                    file.display()
                ),
                &fake(&all),
            )
            .map(|t| t.password)
        };
        let lines = [
            "# 1.x",
            "localhost:5432:*:u:from-defaults",
            r"db.lan:5433:*:u:right\:pw",
        ];
        assert_eq!(
            password(&lines, "", &[]).unwrap().as_deref(),
            Some("right:pw")
        );
        assert_eq!(
            password(&lines, ";Password=given", &[]).unwrap().as_deref(),
            Some("given")
        );
        assert_eq!(
            password(&lines, "", &[("PGPASSWORD", "env")])
                .unwrap()
                .as_deref(),
            Some("env")
        );
        let before = [lines[0], lines[1], "broken", lines[2]];
        assert!(matches!(
            password(&before, "", &[]),
            Err(ConnectionError::PassfileMalformed(3))
        ));
        let after = [lines[0], lines[1], lines[2], "broken"];
        assert_eq!(
            password(&after, "", &[]).unwrap().as_deref(),
            Some("right:pw")
        );
        std::fs::remove_dir_all(dir).unwrap();
        std::fs::remove_dir_all(home).unwrap();
    }

    #[test]
    fn security_requirements_the_copy_cannot_enforce_are_refused() {
        let base = "Host=h;Username=u;";
        assert_eq!(
            unsupported(resolve(&format!("{base}Require Auth=scram-sha-256"))),
            "Require Auth"
        );
        let from_env = target(base, &fake(&[("PGREQUIREAUTH", "md5")]));
        assert!(
            error(target(base, &fake(&[("PGREQUIREAUTH", "md5")])))
                .to_string()
                .contains("PGREQUIREAUTH")
        );
        assert_eq!(unsupported(from_env), "Require Auth");
        for (entry, name) in [
            ("Channel Binding=Require", "Channel Binding"),
            ("GSS Encryption Mode=Require", "GSS Encryption Mode"),
            ("SSL Negotiation=Direct", "SSL Negotiation"),
            ("Include Realm=false", "Include Realm"),
        ] {
            assert_eq!(unsupported(resolve(&format!("{base}{entry}"))), name);
        }
        assert!(matches!(
            error(resolve(&format!("{base}Include Realm=maybe"))),
            ConnectionError::Invalid("Include Realm")
        ));
        resolve(&format!(
            "{base}Check Certificate Revocation=true;Require Auth=;Include Realm=True;Channel Binding=Prefer"
        ))
        .unwrap();
    }

    #[test]
    fn client_tuning_keys_are_ignored() {
        resolve(
            "Host=h;Username=u;Pooling=false;Maximum Pool Size=5;MinPoolSize=1;Load Balance Timeout=5;\
             Timeout=30;Command Timeout=0;Include Error Detail=true;Trust Server Certificate=true;\
             Application Name=x;GSS Encryption Mode=Disable",
        )
        .unwrap();
    }

    #[test]
    fn unusable_keys_are_named_without_their_values() {
        let base = "Host=h;Username=u;";
        let mut texts = Vec::new();
        let unknown = error(resolve(&format!("{base}Frobnicate=s3cret")));
        assert!(matches!(unknown, ConnectionError::UnknownKey(3)));
        texts.push(unknown.to_string());
        for (entry, name) in [
            ("SSL Password=s3cret", "SSL Password"),
            ("Options=-c s3cret=1", "Options"),
            ("Kerberos Service Name=s3cret", "Kerberos Service Name"),
            (
                "Target Session Attributes=primary",
                "Target Session Attributes",
            ),
        ] {
            let result = resolve(&format!("{base}{entry}"));
            texts.push(error(resolve(&format!("{base}{entry}"))).to_string());
            assert_eq!(unsupported(result), name);
        }
        for (entry, name) in [("Port=s3cret", "Port"), ("SSL Mode=s3cret", "SSL Mode")] {
            let error = error(resolve(&format!("{base}{entry}")));
            assert!(matches!(error, ConnectionError::Invalid(n) if n == name));
            texts.push(error.to_string());
        }
        for entry in [r#"Password="s3cret"#, r#"Password=s3cret""#, "s3cret"] {
            let error = error(resolve(&format!("{base}{entry}")));
            assert!(matches!(error, ConnectionError::Malformed));
            texts.push(error.to_string());
        }
        for text in texts {
            assert!(!text.contains("s3cret"), "{text}");
        }
    }

    #[test]
    fn verify_modes_wait_for_gate_g1() {
        for mode in ["VerifyCA", "verifyfull"] {
            assert!(matches!(
                error(resolve(&format!(
                    "Host=h;Username=u;SSL Mode={mode};Root Certificate=/ca.pem"
                ))),
                ConnectionError::CertificateCheck
            ));
        }
    }

    #[test]
    fn options_follow_npgsql() {
        let env = fake(&[("PGOPTIONS", "-c search_path=other")]);
        let options = |rest: &str| target(&format!("Host=h;Username=u{rest}"), &env);
        assert_eq!(
            options(r#";Options="""#).unwrap().options.as_deref(),
            Some("")
        );
        assert_eq!(
            options(";Options=").unwrap().options.as_deref(),
            Some("-c search_path=other")
        );
        assert_eq!(
            options("").unwrap().options.as_deref(),
            Some("-c search_path=other")
        );
        let text = error(options(";Options=-c a=b")).to_string();
        assert!(text.contains("PGOPTIONS"), "{text}");
        assert_eq!(unsupported(options(";Options=-c a=b")), "Options");
    }

    #[test]
    fn an_empty_options_entry_is_refused_while_pgoptions_is_set() {
        const CHILD: &str = "INFINIDYSK_PGOPTIONS_TEST_CHILD";
        if std::env::var_os(CHILD).is_none() {
            let output = Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "postgres::tests::an_empty_options_entry_is_refused_while_pgoptions_is_set",
                    "--nocapture",
                ])
                .env(CHILD, "1")
                .env("PGOPTIONS", "-c search_path=other")
                .output()
                .unwrap();
            let stdout = String::from_utf8_lossy(&output.stdout);
            assert!(
                output.status.success() && stdout.contains("1 passed"),
                "{stdout}\n{}",
                String::from_utf8_lossy(&output.stderr),
            );
            return;
        }
        let env = |name: &str| std::env::var(name).ok();
        let hidden = target(r#"Host=h;Username=u;Options="""#, &env).unwrap();
        assert!(matches!(
            hidden.options(PgSslMode::Prefer),
            Err(ConnectionError::OptionsMismatch)
        ));
        let inherited = target("Host=h;Username=u;Options=", &env).unwrap();
        assert!(inherited.options(PgSslMode::Prefer).is_ok());
    }

    /// The first message of a connection, or of the one after an `N`.
    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    enum Opened {
        Ssl,
        Startup,
    }

    /// What the scripted server answers an `SSLRequest` with.
    #[derive(Clone, Copy)]
    enum SslReply {
        /// `S`, then bytes that are not TLS, then close.
        BrokenTls,
        /// `N`: no TLS; the client may go on in plaintext.
        No,
    }

    /// Accepts connections until dropped, recording each one's messages. Every startup is
    /// rejected with a FATAL `ErrorResponse`.
    async fn scripted(reply: SslReply) -> (u16, Arc<Mutex<Vec<Vec<Opened>>>>) {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let seen = Arc::new(Mutex::new(Vec::new()));
        let log = Arc::clone(&seen);
        tokio::spawn(async move {
            while let Ok((socket, _)) = listener.accept().await {
                let index = {
                    let mut log = log.lock().unwrap();
                    log.push(Vec::new());
                    log.len() - 1
                };
                tokio::spawn(serve(socket, reply, Arc::clone(&log), index));
            }
        });
        (port, seen)
    }

    async fn serve(
        mut socket: TcpStream,
        reply: SslReply,
        log: Arc<Mutex<Vec<Vec<Opened>>>>,
        index: usize,
    ) {
        loop {
            let Ok(len) = socket.read_u32().await else {
                return;
            };
            let mut body = vec![0; usize::try_from(len).unwrap() - 4];
            if socket.read_exact(&mut body).await.is_err() {
                return;
            }
            let ssl = len == 8 && body[..4] == 80_877_103_u32.to_be_bytes();
            log.lock().unwrap()[index].push(if ssl { Opened::Ssl } else { Opened::Startup });
            if !ssl {
                let fields = b"SFATAL\0VFATAL\0C28000\0Mrejected\0\0";
                let mut message = vec![b'E'];
                message.extend_from_slice(&u32::try_from(fields.len() + 4).unwrap().to_be_bytes());
                message.extend_from_slice(fields);
                let _ = socket.write_all(&message).await;
                return;
            }
            match reply {
                SslReply::BrokenTls => {
                    let _ = socket.write_all(b"Snot tls").await;
                    return;
                }
                SslReply::No => {
                    if socket.write_all(b"N").await.is_err() {
                        return;
                    }
                }
            }
        }
    }

    async fn attempt(port: u16, mode: &str) -> Result<PgConnection, Error> {
        let t = resolve(&format!(
            "Host=127.0.0.1;Port={port};Username=u;SSL Mode={mode}"
        ))
        .unwrap();
        tokio::time::timeout(Duration::from_secs(10), connect(&t))
            .await
            .expect("connect gave up within its deadline")
    }

    #[tokio::test]
    async fn tls_retries_follow_npgsql() {
        use Opened::{Ssl, Startup};

        let (port, seen) = scripted(SslReply::BrokenTls).await;
        assert!(attempt(port, "Prefer").await.is_err());
        assert_eq!(*seen.lock().unwrap(), vec![vec![Ssl]]);

        let (port, seen) = scripted(SslReply::No).await;
        assert!(matches!(
            attempt(port, "Prefer").await,
            Err(Error::Connect(sqlx::Error::Database(_)))
        ));
        assert_eq!(
            *seen.lock().unwrap(),
            vec![vec![Ssl, Startup], vec![Startup]]
        );

        let (port, seen) = scripted(SslReply::No).await;
        assert!(attempt(port, "Allow").await.is_err());
        assert_eq!(*seen.lock().unwrap(), vec![vec![Startup], vec![Ssl]]);
    }
}
