use std::ffi::CString;
use std::path::Path;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::audio::AudioInfo;
use crate::error::ObsError;
use crate::source::ObsSource;
use crate::video::VideoInfo;

static INITIALIZED: AtomicBool = AtomicBool::new(false);

/// Handle to the global libobs state.
///
/// NOTE: there is intentionally NO `Drop` impl — `obs_shutdown` is never called
/// (known OBS shutdown crashes). The context is effectively a leaked singleton
/// and every process exit routes through `platform::exit_process`.
pub struct ObsContext {
    _not_send_sync: std::marker::PhantomData<*mut ()>,
}

impl ObsContext {
    pub fn new(locale: &str) -> Result<Self, ObsError> {
        if INITIALIZED.swap(true, Ordering::SeqCst) {
            return Err(ObsError::AlreadyInitialized);
        }
        let locale_c = CString::new(locale).unwrap();
        let ok = unsafe { prisc_obs_sys::obs_startup(locale_c.as_ptr(), ptr::null(), ptr::null_mut()) };
        if !ok {
            INITIALIZED.store(false, Ordering::SeqCst);
            return Err(ObsError::Startup);
        }
        Ok(ObsContext {
            _not_send_sync: std::marker::PhantomData,
        })
    }

    pub fn reset_video(&self, info: &VideoInfo) -> Result<(), ObsError> {
        let mut raw = info.to_raw();
        let ret = unsafe { prisc_obs_sys::obs_reset_video(&mut raw) };
        if ret != 0 {
            return Err(ObsError::VideoReset(ret as i32));
        }
        // libobs never initialises its SDR white / HDR nominal peak levels:
        // `obs_core` is zero-allocated and only `obs_set_video_levels` writes
        // them, which OBS Studio's frontend calls after every video reset
        // (OBSBasic.cpp, 300 / 1000 nit defaults). Left at zero, the
        // HDR-to-SDR capture paths divide by `obs_get_video_sdr_white_level()`
        // (win-capture and libobs-winrt DrawMultiplyTonemap: 80 / 0 = inf, the
        // tonemap saturates the NaN to black) and the SDR-to-scRGB presentation
        // path multiplies by it (obs_render_main_texture on an HDR swapchain:
        // zero). obs_reset_video does not touch the levels, so after the first
        // reset this re-applies the same values; done after every reset for
        // parity with the frontend. The graphics thread is already rendering
        // (and reading the levels) by the time obs_reset_video returns, so
        // take the graphics context around the plain float stores.
        unsafe {
            prisc_obs_sys::obs_enter_graphics();
            prisc_obs_sys::obs_set_video_levels(300.0, 1000.0);
            prisc_obs_sys::obs_leave_graphics();
        }
        Ok(())
    }

    pub fn reset_audio(&self, info: &AudioInfo) -> Result<(), ObsError> {
        let raw = info.to_raw();
        let ok = unsafe { prisc_obs_sys::obs_reset_audio(&raw) };
        if !ok {
            return Err(ObsError::AudioReset(false));
        }
        Ok(())
    }

    /// Registers a libobs core data path (`obs_add_data_path`). libobs stores the
    /// string verbatim and concatenates file names onto it, so it must end with a
    /// path separator — this wrapper appends `/` when missing.
    pub fn add_data_path(&self, path: &Path) {
        let mut s = path.to_string_lossy().into_owned();
        if !s.ends_with('/') && !s.ends_with('\\') {
            s.push('/');
        }
        let c = CString::new(s.replace('\0', "")).unwrap();
        unsafe { prisc_obs_sys::obs_add_data_path(c.as_ptr()) };
    }

    pub fn add_module_path(&self, bin: &str, data: &str) {
        let bin_c = CString::new(bin).unwrap();
        let data_c = CString::new(data).unwrap();
        unsafe { prisc_obs_sys::obs_add_module_path(bin_c.as_ptr(), data_c.as_ptr()) };
        #[cfg(target_os = "linux")]
        linux_modules::register_dir(bin);
        #[cfg(not(target_os = "linux"))]
        plugin_paths::register(bin_c, data_c);
    }

    pub fn load_all_modules(&self) {
        unsafe {
            #[cfg(not(target_os = "linux"))]
            plugin_paths::load_registered();
            #[cfg(target_os = "linux")]
            linux_modules::load_registered();
            prisc_obs_sys::obs_post_load_modules();
        }
    }

    pub fn set_output_source(&self, channel: u32, source: Option<&ObsSource>) {
        let ptr = source.map_or(ptr::null_mut(), |s| s.as_ptr());
        unsafe { prisc_obs_sys::obs_set_output_source(channel, ptr) };
    }

    /// Raw-pointer variant, used for the scene's source on channel 0.
    /// (Pointer is passed straight to libobs, never dereferenced here.)
    #[allow(clippy::not_unsafe_ptr_arg_deref)]
    pub fn set_output_source_raw(&self, channel: u32, source: *mut prisc_obs_sys::obs_source_t) {
        unsafe { prisc_obs_sys::obs_set_output_source(channel, source) };
    }

    pub fn get_video(&self) -> *mut prisc_obs_sys::video_t {
        unsafe { prisc_obs_sys::obs_get_video() }
    }

    pub fn get_audio(&self) -> *mut prisc_obs_sys::audio_t {
        unsafe { prisc_obs_sys::obs_get_audio() }
    }
}

/// Windows / macOS: load the modules found on the registered paths.
///
/// OBS 33 split module loading in two and turned `obs_load_all_modules` into a
/// no-op: "core modules" load from a fixed, build-time layout
/// (`obs_load_core_modules`), everything else through `obs_load_plugins` on a
/// caller-supplied path. prisc_recorder ships its own flat layout (see
/// `prisc_obs_platform::default_obs_paths`) rather than OBS's per-module
/// `core/<module>/` tree, so every registered path is loaded as a plain plugin
/// path, which keeps libobs's `%module%` data-dir resolution.
#[cfg(not(target_os = "linux"))]
mod plugin_paths {
    use std::ffi::CString;
    use std::sync::Mutex;

    static PATHS: Mutex<Vec<(CString, CString)>> = Mutex::new(Vec::new());

    pub(super) fn register(bin: CString, data: CString) {
        PATHS
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push((bin, data));
    }

    pub(super) unsafe fn load_registered() {
        let paths = PATHS.lock().unwrap_or_else(|e| e.into_inner());
        for (bin, data) in paths.iter() {
            let mut info = prisc_obs_sys::obs_runtime_module_info {
                path_info: prisc_obs_sys::obs_runtime_module_path {
                    binary: bin.as_ptr(),
                    data: data.as_ptr(),
                },
                type_: prisc_obs_sys::obs_runtime_module_type_OBS_MODULE_TYPE_PLUGIN,
                name: std::ptr::null(),
            };
            let mut failures = std::mem::zeroed::<prisc_obs_sys::obs_module_failure_info>();
            prisc_obs_sys::obs_load_plugins(&mut info, &mut failures);
            prisc_obs_sys::obs_module_failure_info_free(&mut failures);
        }
    }
}

/// Linux: load plugins ONLY from the directories registered through
/// [`ObsContext::add_module_path`], never from libobs's built-in defaults.
///
/// Before OBS 33, `obs_startup` called `add_default_module_paths()`
/// (libobs/obs-nix.c), which registered, ahead of anything we add:
///   - `<exe dir>/../lib/obs-plugins` (exe-relative; e.g. a bundle unpacked to
///     `/usr/local/prisc-recorder` would pick up `/usr/local/lib/obs-plugins`),
///   - the CWD-relative `../../obs-plugins/64bit`,
///   - `OBS_INSTALL_PREFIX/lib/obs-plugins` and the Flatpak plugin dir.
/// The bogus install prefix prisc_obs_sys compiles in neutralises only the third.
/// `obs_load_all_modules` dlopens every `.so` found on any of those paths and
/// has no duplicate-name check (obs-module.c load_all_callback), so a system
/// OBS's `obs-ffmpeg.so` would load beside ours - dragging a second, system
/// FFmpeg into the process and registering `ffmpeg_muxer` / `obs_x264` etc.
/// against a foreign build first. There is no API to remove a module path, so
/// instead we walk the same search (`obs_find_modules2`, which keeps libobs's
/// `%module%` data-dir resolution) and open only modules that live directly in
/// one of our own directories.
///
/// OBS 33 dropped the default paths and made `obs_load_all_modules` a no-op, so
/// this walk is now the only thing loading plugins; it is kept (rather than
/// switched to `obs_load_plugins`) because it still guarantees a module is
/// opened only from a directory we registered.
#[cfg(target_os = "linux")]
mod linux_modules {
    use std::collections::HashSet;
    use std::ffi::{c_void, CStr};
    use std::path::{Path, PathBuf};
    use std::sync::Mutex;

    /// Directories passed to `add_module_path`, i.e. the only places a plugin
    /// may be loaded from.
    static DIRS: Mutex<Vec<PathBuf>> = Mutex::new(Vec::new());

    pub(super) fn register_dir(bin: &str) {
        // A `%module%` pattern (per-module subdirectories) is not something
        // prisc_recorder uses on Linux; take the part before it so the check
        // below would still accept those modules' parent tree.
        let base = bin.split("%module%").next().unwrap_or(bin);
        let mut dirs = DIRS.lock().unwrap_or_else(|e| e.into_inner());
        dirs.push(PathBuf::from(base));
    }

    struct Walk {
        dirs: Vec<PathBuf>,
        seen: HashSet<String>,
    }

    unsafe extern "C" fn on_module(param: *mut c_void, info: *const prisc_obs_sys::obs_module_info2) {
        let walk = &mut *(param as *mut Walk);
        let info = &*info;
        if info.bin_path.is_null() || info.name.is_null() {
            return;
        }
        let bin_path = CStr::from_ptr(info.bin_path).to_string_lossy().into_owned();
        let name = CStr::from_ptr(info.name).to_string_lossy().into_owned();
        // Path comparison ignores trailing and doubled separators, which
        // libobs may introduce when it appends '/' to the search dir.
        let ours = walk
            .dirs
            .iter()
            .any(|d| Path::new(&bin_path).starts_with(d));
        if !ours {
            return;
        }
        // The same directory registered twice would be globbed twice.
        if !walk.seen.insert(name) {
            return;
        }
        let mut module: *mut prisc_obs_sys::obs_module_t = std::ptr::null_mut();
        // obs_open_module logs its own warning on dlopen / version failures;
        // a non-zero code here just means "skip", as in load_all_callback.
        if prisc_obs_sys::obs_open_module(&mut module, info.bin_path, info.data_path) != 0
            || module.is_null()
        {
            return;
        }
        // On failure libobs logs "Failed to initialize module" and leaves the
        // module on its loaded list; load_all_callback frees it, but
        // free_module is not exported and an inert entry is harmless.
        prisc_obs_sys::obs_init_module(module);
    }

    pub(super) unsafe fn load_registered() {
        let dirs = DIRS.lock().unwrap_or_else(|e| e.into_inner()).clone();
        let mut walk = Walk {
            dirs,
            seen: HashSet::new(),
        };
        prisc_obs_sys::obs_find_modules2(Some(on_module), &mut walk as *mut Walk as *mut c_void);
    }
}
