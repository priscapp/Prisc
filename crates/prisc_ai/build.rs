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
/// statically. On Windows this script stages the DirectML EP's helper dylib
/// (DirectML.dll) from that download beside the built exe; see
/// [`stage_directml`].
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
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        stage_directml();
    }
}

/// Copies DirectML.dll from ort-sys's extracted download into the cargo
/// profile dir, beside the exe; `cargo test` puts that dir on the test
/// executables' PATH, and CI packages the DLL from there.
///
/// ort's own `copy-dylibs` feature did this until cargo 1.100 moved build
/// script output dirs one level deeper (`build/<pkg>/<hash>/out`): ort-sys
/// 2.0.0-rc.13 still derives the profile dir with `OUT_DIR.ancestors().nth(3)`
/// and panics. rc.14+ handle the new layout, but they are held back (they need
/// macOS 13.4). Once ort is upgraded, this can go back to `copy-dylibs`.
///
/// ort-sys extracts into `<cache>/dfbin/<TARGET>/<dist hash>/`, where `<cache>`
/// is `ORT_CACHE_DIR` or `%LOCALAPPDATA%\ort.pyke.io` (ort-sys's
/// `internal::dirs::cache_dir`). It runs first: cargo runs a crate's build
/// script after those of its direct `links` dependencies, which is why this
/// crate depends on ort-sys directly as well as through ort. The dist hash is
/// not exported, so this takes the most recently extracted dist that carries
/// the DLL; every dist of one ONNX Runtime release ships the same DirectML.dll.
fn stage_directml() {
    use std::path::PathBuf;

    let cache = std::env::var_os("ORT_CACHE_DIR")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("LOCALAPPDATA").map(|d| PathBuf::from(d).join("ort.pyke.io")))
        .expect("neither ORT_CACHE_DIR nor LOCALAPPDATA is set");
    let dists = cache
        .join("dfbin")
        .join(std::env::var("TARGET").unwrap());
    let extracted = |dist: std::fs::DirEntry| {
        let dll = dist.path().join("DirectML.dll");
        let modified = std::fs::metadata(&dll)
            .and_then(|m| m.modified())
            .ok()?;
        Some((modified, dll))
    };
    let src = std::fs::read_dir(&dists)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(extracted)
        .max()
        .map(|(_, dll)| dll)
        .unwrap_or_else(|| panic!("DirectML.dll not found in ort-sys's downloads under {}", dists.display()));

    let dst = prisc_build_support::cargo_profile_dir().join("DirectML.dll");
    println!("cargo:rerun-if-changed={}", src.display());
    println!("cargo:rerun-if-changed={}", dst.display());
    prisc_build_support::copy_if_newer(&src, &dst);
}
