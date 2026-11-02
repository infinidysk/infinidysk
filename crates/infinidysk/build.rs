//! Makes sure `ui/dist` holds a page to embed: the web UI build, else a placeholder.

use std::error::Error;
use std::path::PathBuf;
use std::{env, fs};

const PLACEHOLDER: &str = "<!doctype html><title>infinidysk</title>\
<p>This build has no web UI. Run <code>npm ci &amp;&amp; npm run build</code> in <code>ui/</code> \
and rebuild.</p>\n";

fn main() -> Result<(), Box<dyn Error>> {
    let dist = PathBuf::from(env::var("CARGO_MANIFEST_DIR")?).join("../../ui/dist");
    // ponytail: writes into the gitignored source tree so the embed path stays fixed; a Vite
    // build replaces the placeholder. Watching a missing path would rerun this every build.
    fs::create_dir_all(&dist)?;
    let index = dist.join("index.html");
    if !index.is_file() {
        fs::write(index, PLACEHOLDER)?;
    }
    println!("cargo::rerun-if-changed={}", dist.display());
    println!("cargo::rerun-if-env-changed=INFINIDYSK_COMMIT");
    if let Ok(commit) = env::var("INFINIDYSK_COMMIT")
        && !commit.is_empty()
        && !(commit.len() == 40
            && commit
                .bytes()
                .all(|b| matches!(b, b'0'..=b'9' | b'a'..=b'f')))
    {
        return Err(format!("INFINIDYSK_COMMIT must be a full commit hash, not {commit:?}").into());
    }
    Ok(())
}
