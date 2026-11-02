//! Validates `providers/*.toml` and compiles it into a static catalog table.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::path::Path;
use std::{env, fs};

use toml::{Table, Value};

const SCHEMA: i64 = 1;

fn main() {
    let dir = Path::new(&env::var("CARGO_MANIFEST_DIR").expect("cargo sets CARGO_MANIFEST_DIR"))
        .join("../../providers");
    println!("cargo:rerun-if-changed={}", dir.display());

    let backbones = read_backbones(&dir.join("backbones.toml"));
    let mut files: Vec<_> = fs::read_dir(&dir)
        .unwrap_or_else(|e| fail("providers/", &e.to_string()))
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "toml") && !p.ends_with("backbones.toml"))
        .collect();
    files.sort();

    let mut out = String::from("pub static BACKBONES: &[Backbone] = &[\n");
    for (id, name) in &backbones {
        writeln!(out, "    Backbone {{ id: {id:?}, name: {name:?} }},")
            .expect("String writes are infallible");
    }
    out.push_str("];\n\npub static PROFILES: &[Profile] = &[\n");
    for path in &files {
        out.push_str(&profile(path, &backbones));
    }
    out.push_str("];\n");

    let dest = Path::new(&env::var("OUT_DIR").expect("cargo sets OUT_DIR")).join("catalog.rs");
    fs::write(dest, out).expect("OUT_DIR is writable");
}

fn read_backbones(path: &Path) -> BTreeMap<String, String> {
    let file = "providers/backbones.toml";
    let table = parse(path, file);
    table
        .iter()
        .map(|(id, v)| {
            check_id(file, id);
            let t = v
                .as_table()
                .unwrap_or_else(|| fail(file, &format!("[{id}] must be a table")));
            check_keys(file, t, &["name"]);
            (id.clone(), string(file, t, "name"))
        })
        .collect()
}

fn profile(path: &Path, backbones: &BTreeMap<String, String>) -> String {
    let id = path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or_default();
    let file = &format!("providers/{id}.toml");
    check_id(file, id);
    let t = parse(path, file);
    check_keys(
        file,
        &t,
        &["schema", "name", "website", "backbone", "port", "servers"],
    );

    if t.get("schema").and_then(Value::as_integer) != Some(SCHEMA) {
        fail(file, &format!("schema must be {SCHEMA}"));
    }
    let name = string(file, &t, "name");
    let website = string(file, &t, "website");
    if !website.starts_with("https://") {
        fail(file, "website must start with https://");
    }
    let backbone = known_backbone(file, backbones, &string(file, &t, "backbone"));
    let default_port = port(file, &t).unwrap_or_else(|| fail(file, "port is required"));

    let servers = t
        .get("servers")
        .and_then(Value::as_array)
        .filter(|a| !a.is_empty())
        .unwrap_or_else(|| fail(file, "at least one [[servers]] entry is required"));
    let mut seen = Vec::new();
    let mut out =
        format!("    Profile {{ id: {id:?}, name: {name:?}, website: {website:?}, servers: &[\n");
    for s in servers {
        let s = s
            .as_table()
            .unwrap_or_else(|| fail(file, "[[servers]] entries must be tables"));
        check_keys(file, s, &["hostname", "region", "port", "backbone"]);
        let host = string(file, s, "hostname").to_ascii_lowercase();
        check_hostname(file, &host);
        if seen.contains(&host) {
            fail(file, &format!("duplicate hostname {host}"));
        }
        let region = s.contains_key("region").then(|| {
            let r = string(file, s, "region");
            if r.len() != 2 || !r.bytes().all(|b| b.is_ascii_uppercase()) {
                fail(
                    file,
                    &format!("{host}: region must be a two-letter uppercase country code"),
                );
            }
            r
        });
        let server_backbone = s.get("backbone").map_or_else(
            || backbone.clone(),
            |_| known_backbone(file, backbones, &string(file, s, "backbone")),
        );
        let server_port = port(file, s).unwrap_or(default_port);
        writeln!(
            out,
            "        Server {{ hostname: {host:?}, region: {region:?}, port: {server_port}, backbone: {server_backbone:?} }},"
        )
        .expect("String writes are infallible");
        seen.push(host);
    }
    out.push_str("    ] },\n");
    out
}

fn parse(path: &Path, file: &str) -> Table {
    let text = fs::read_to_string(path).unwrap_or_else(|e| fail(file, &e.to_string()));
    text.parse()
        .unwrap_or_else(|e: toml::de::Error| fail(file, &e.to_string()))
}

fn string(file: &str, t: &Table, key: &str) -> String {
    match t.get(key).and_then(Value::as_str).map(str::trim) {
        Some(s) if !s.is_empty() => s.to_owned(),
        _ => fail(file, &format!("{key} must be a non-empty string")),
    }
}

fn port(file: &str, t: &Table) -> Option<u16> {
    t.get("port").map(|v| {
        v.as_integer()
            .and_then(|p| u16::try_from(p).ok())
            .filter(|&p| p != 0)
            .unwrap_or_else(|| fail(file, "port must be 1-65535"))
    })
}

fn known_backbone(file: &str, backbones: &BTreeMap<String, String>, id: &str) -> String {
    if !backbones.contains_key(id) {
        fail(
            file,
            &format!("unknown backbone {id:?}; add it to providers/backbones.toml"),
        );
    }
    id.to_owned()
}

fn check_keys(file: &str, t: &Table, allowed: &[&str]) {
    if let Some(k) = t.keys().find(|k| !allowed.contains(&k.as_str())) {
        fail(
            file,
            &format!("unknown key {k:?} (allowed: {})", allowed.join(", ")),
        );
    }
}

fn check_id(file: &str, id: &str) {
    let ok = !id.is_empty()
        && !id.starts_with('-')
        && !id.ends_with('-')
        && id
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-');
    if !ok {
        fail(
            file,
            &format!("id {id:?} must be lowercase letters, digits and inner dashes"),
        );
    }
}

fn check_hostname(file: &str, host: &str) {
    let ok = host.len() <= 253
        && host.contains('.')
        && host.split('.').all(|l| {
            !l.is_empty()
                && l.len() <= 63
                && !l.starts_with('-')
                && !l.ends_with('-')
                && l.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
        });
    if !ok {
        fail(file, &format!("invalid hostname {host:?}"));
    }
}

fn fail(file: &str, msg: &str) -> ! {
    panic!("{file}: {msg}");
}
