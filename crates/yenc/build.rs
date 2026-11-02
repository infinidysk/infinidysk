fn main() {
    println!("cargo::rerun-if-changed=build.rs");
    println!("cargo::rerun-if-changed=rapidyenc");

    // Always optimized: this is the playback hot path, even in dev builds.
    let dst = cmake::Config::new("rapidyenc")
        .profile("Release")
        .define("DISABLE_SHARED", "ON")
        .define("DISABLE_TOOL", "ON")
        .build_target("rapidyenc_static")
        .build();

    println!(
        "cargo::rustc-link-search=native={}",
        dst.join("build/rapidyenc_static").display()
    );
    println!("cargo::rustc-link-lib=static=rapidyenc");
    let cxx = if std::env::var("CARGO_CFG_TARGET_VENDOR").as_deref() == Ok("apple") {
        "c++"
    } else {
        "stdc++"
    };
    println!("cargo::rustc-link-lib=dylib={cxx}");
}
