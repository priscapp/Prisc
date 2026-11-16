/// The application manifest is shared with the other Prisc binaries and
/// lives in `crates/prisc_build_support/` (see that crate's
/// manifest note). It cannot be embedded *by* that crate — `/MANIFEST:EMBED`
/// is a link argument, and only the crate producing the executable links one
/// — so each binary's build script points at the single copy instead of
/// keeping its own. What it buys us is per-monitor DPI awareness; a copy
/// that silently drifted would make one binary read virtualized coordinates
/// and photograph the wrong pixels.
///
/// The ONNX Runtime itself is `ort-sys`'s job (the `ort` crate's build): it
/// downloads pyke's prebuilt runtime for the cargo TARGET and links it
/// statically, and its `copy-dylibs` feature stages the DirectML EP's helper
/// dylib (DirectML.dll) beside the built exe. macOS needs two things on top,
/// see [`macos`].
const SHARED_MANIFEST: &str = "../prisc_build_support/app.manifest";

fn main() {
    println!("cargo:rerun-if-changed={SHARED_MANIFEST}");

    #[cfg(windows)]
    {
        let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();
        let manifest_path = std::path::Path::new(&manifest_dir).join(SHARED_MANIFEST);
        // Fail loudly rather than link an unmanifested exe: without the
        // manifest the process is DPI-virtualized, which is a subtle
        // wrong-pixels bug rather than an obvious one.
        assert!(
            manifest_path.is_file(),
            "shared app manifest not found at {}",
            manifest_path.display()
        );
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTINPUT:{}", manifest_path.display());

        // The one binary in the workspace that must NOT be `+crt-static`:
        // pyke's prebuilt ONNX Runtime static libs are compiled /MD, and their
        // `__imp_*` CRT references cannot resolve against the static CRT the
        // workspace's .cargo/config.toml selects (rustc treats any occurrence
        // of +crt-static as final, so a config-level opt-out is impossible).
        // rustc contributes the static CRT only as `/defaultlib:libcmt`, which
        // NODEFAULTLIB suppresses cleanly — then the whole link resolves
        // against the dynamic CRT import libs instead, exactly one CRT in the
        // image. The exe then imports msvcp140/vcruntime140 at runtime; CI
        // ships those app-local in the Windows packages. rustc-link-arg (not
        // -bins) so the crate's test binaries link the same way.
        for lib in [
            "libcmt",
            "libcmtd",
            "libucrt",
            "libucrtd",
            "libvcruntime",
            "libvcruntimed",
            "libcpmt",
            "libcpmtd",
        ] {
            println!("cargo:rustc-link-arg=/NODEFAULTLIB:{lib}.lib");
        }
        for lib in ["msvcrt", "msvcprt", "ucrt", "vcruntime"] {
            println!("cargo:rustc-link-arg=/DEFAULTLIB:{lib}.lib");
        }
    }
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos") {
        macos();
    }
}

/// pyke's ONNX Runtime 1.31 objects are built for macOS 13.4, so this binary
/// is too (dyld then refuses it cleanly on older systems, instead of it dying
/// on a missing symbol mid-inference). The rest of the workspace keeps Rust's
/// default deployment target.
///
/// Their CoreML code also calls `__isPlatformVersionAtLeast` (Clang's
/// `@available`). Rust's std defines that symbol, but only weakly, and the
/// release profile's LTO drops it because no Rust code references it, which
/// fails the release link. Linking compiler-rt supplies the real one, as
/// ort-sys did itself up to rc.13.
fn macos() {
    println!("cargo:rustc-env=MACOSX_DEPLOYMENT_TARGET=13.4");

    println!("cargo:rerun-if-env-changed=CC");
    let cc = std::env::var("CC").unwrap_or_else(|_| "cc".to_string());
    let output = std::process::Command::new(&cc)
        .arg("--print-resource-dir")
        .output()
        .unwrap_or_else(|e| panic!("running `{cc} --print-resource-dir`: {e}"));
    assert!(output.status.success(), "`{cc} --print-resource-dir` failed: {}", output.status);
    let darwin = std::path::Path::new(String::from_utf8_lossy(&output.stdout).trim()).join("lib/darwin");
    assert!(
        darwin.join("libclang_rt.osx.a").is_file(),
        "compiler-rt (libclang_rt.osx.a) not found in {}",
        darwin.display()
    );
    println!("cargo:rustc-link-search=native={}", darwin.display());
    println!("cargo:rustc-link-lib=static=clang_rt.osx");
}
