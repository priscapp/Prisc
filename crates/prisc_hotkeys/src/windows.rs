//! The Windows backend: a WH_KEYBOARD_LL hook on its own message-loop thread. `ll_proc` is glue —
//! every decision lives in `WinState` and `HookCore`.

use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::Ordering;
use std::sync::mpsc::{self, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};

use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Threading::{GetCurrentThread, GetCurrentThreadId, SetThreadPriority, THREAD_PRIORITY_HIGHEST};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, GetKeyboardLayout, SendInput, ToUnicodeEx, HKL, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYBD_EVENT_FLAGS,
    KEYEVENTF_KEYUP, VIRTUAL_KEY, VK_CAPITAL, VK_CONTROL, VK_MENU, VK_SHIFT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, DispatchMessageW, GetForegroundWindow, GetGUIThreadInfo, GetMessageW, GetWindowThreadProcessId, PeekMessageW,
    PostThreadMessageW, SetForegroundWindow, SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx, GUITHREADINFO, HHOOK,
    KBDLLHOOKSTRUCT, MSG, PM_NOREMOVE, WH_KEYBOARD_LL, WM_QUIT,
};

use crate::core::{HookCore, Verdict};
use crate::hotkeys::{Shared, StartFailure};
use crate::winstate::{WinEvent, WinState, MASK_MARKER, MASK_VK, SIDE_KEYS};
use crate::{Error, Event};

thread_local! {
    /// The hook thread's state. `ll_proc` runs on this thread (LL hooks are called through its
    /// message loop), so a thread-local is the whole synchronization story. It outlives a re-hook.
    static STATE: RefCell<Option<(HookCore, WinState)>> = const { RefCell::new(None) };
}

/// The running hook thread. Dropping it posts WM_QUIT and joins.
pub(crate) struct Backend {
    thread_id: u32,
    thread: Option<JoinHandle<()>>,
}

impl Drop for Backend {
    fn drop(&mut self) {
        // SAFETY: posting to a thread id is always safe; the queue exists (created before `spawn`
        // returned), so WM_QUIT is delivered even if the loop is not waiting yet.
        let _ = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0)) };
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

pub(crate) fn spawn(shared: Arc<Shared>, tx: Sender<Event>) -> Result<Backend, StartFailure> {
    let (ready_tx, ready_rx) = mpsc::channel();
    let thread = thread::Builder::new()
        .name("prisc-hotkeys-hook".into())
        .spawn(move || run(shared, tx, ready_tx))
        .map_err(|e| Error::HookFailed(e.to_string()))?;
    match ready_rx.recv() {
        Ok(Ok(thread_id)) => Ok(Backend {
            thread_id,
            thread: Some(thread),
        }),
        Ok(Err(error)) => {
            let _ = thread.join();
            Err(error.into())
        }
        Err(_) => {
            let _ = thread.join();
            Err(Error::HookFailed("the hook thread exited during startup".into()).into())
        }
    }
}

fn run(shared: Arc<Shared>, tx: Sender<Event>, ready: Sender<Result<u32, Error>>) {
    let mut msg = MSG::default();
    // SAFETY: plain Win32 calls on this thread with valid out-pointers. PeekMessageW creates the
    // thread's message queue so PostThreadMessageW(WM_QUIT) can never be lost.
    unsafe {
        let _ = PeekMessageW(&mut msg, None, 0, 0, PM_NOREMOVE);
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    }
    STATE.with(|state| *state.borrow_mut() = Some((HookCore::new(shared.clone(), tx), WinState::new())));

    let hook = match install() {
        Ok(hook) => hook,
        Err(error) => {
            STATE.with(|state| state.borrow_mut().take());
            shared.alive.store(false, Ordering::Release);
            let _ = ready.send(Err(Error::HookFailed(error.message())));
            return;
        }
    };
    // SAFETY: trivially safe.
    let _ = ready.send(Ok(unsafe { GetCurrentThreadId() }));

    // No watchdog that re-installs a hook Windows removed silently (after callback timeouts, e.g. a
    // debugger pause): it needs a keyboard raw-input sink in this process, and while a process has
    // one, Windows does not call that process's own WH_KEYBOARD_LL hook at all whenever one of its
    // windows is in the foreground — registered gestures then reach the OS (PrintScreen opens the
    // Snipping Tool) exactly while a Prisc window has focus.
    let mut failed = None;
    loop {
        // SAFETY: `msg` is a valid MSG for the duration of each call.
        match unsafe { GetMessageW(&mut msg, None, 0, 0) }.0 {
            0 => break,
            -1 => {
                failed = Some("message loop failed".to_string());
                break;
            }
            // SAFETY: `msg` came from GetMessageW.
            _ => unsafe {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            },
        }
    }

    // SAFETY: `hook` came from SetWindowsHookExW on this thread and is unhooked once.
    let _ = unsafe { UnhookWindowsHookEx(hook) };
    // Dropping the state drops the only Sender, which closes Events once it is drained.
    if let Some((mut core, _)) = STATE.with(|state| state.borrow_mut().take()) {
        if let Some(reason) = failed {
            core.stopped(reason);
        }
    }
    shared.alive.store(false, Ordering::Release);
}

/// Installs `ll_proc` for the calling thread, which must run a message loop.
fn install() -> windows::core::Result<HHOOK> {
    // SAFETY: ll_proc matches HOOKPROC; a null hmod is fine for a low-level hook, which is never
    // injected into other processes.
    unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(ll_proc), None, 0) }
}

/// The hook callback: decide and enqueue, nothing else. No allocation beyond the queue node, no
/// logging, no user code; a panic passes the key.
unsafe extern "system" fn ll_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code < 0 {
        return CallNextHookEx(None, code, wparam, lparam);
    }
    // SAFETY: for HC_ACTION, lparam points at a KBDLLHOOKSTRUCT valid for this call.
    let info = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
    let ev = WinEvent {
        vk: info.vkCode,
        scan: info.scanCode,
        flags: info.flags.0,
        time: info.time,
        extra: info.dwExtraInfo,
    };
    let (verdict, inject) = catch_unwind(AssertUnwindSafe(|| {
        STATE.with(|state| match state.try_borrow_mut().as_deref_mut() {
            Ok(Some((core, win))) => win.on_event(core, &ev, async_mods, || translate(ev.vk, ev.scan)),
            _ => (Verdict::Pass, false),
        })
    }))
    .unwrap_or((Verdict::Pass, false));
    // The borrow is released: SendInput may re-enter the hook.
    if inject {
        send_mask();
    }
    match verdict {
        Verdict::Swallow => LRESULT(1),
        Verdict::Pass => CallNextHookEx(None, code, wparam, lparam),
    }
}

/// The physical (async) state of `vk`, read the way every crate reads it (prisc_platform).
fn key_down(vk: u16) -> bool {
    prisc_platform::input::is_key_down(u32::from(vk)).unwrap_or(false)
}

/// The eight side bits in the async key state: the logical state (injected edges count) of every
/// earlier edge, not this one.
fn async_mods() -> u8 {
    SIDE_KEYS
        .iter()
        .filter(|(vk, _)| key_down(*vk))
        .fold(0, |mods, (_, bit)| mods | bit)
}

/// The character this key-down types under the focused thread's layout. Flag 0x4 keeps
/// ToUnicodeEx from touching the keyboard state (the foreground app's dead-key state survives).
/// The async state includes the phantom AltGr LCtrl, so AltGr characters come out right.
fn translate(vk: u32, scan: u32) -> Option<char> {
    let mut state = [0u8; 256];
    for modifier in [VK_SHIFT, VK_CONTROL, VK_MENU] {
        if key_down(modifier.0) {
            state[modifier.0 as usize] = 0x80;
        }
    }
    // SAFETY: plain Win32 queries; `state` and `buffer` outlive the calls.
    let (n, buffer) = unsafe {
        if GetKeyState(VK_CAPITAL.0 as i32) & 1 != 0 {
            state[VK_CAPITAL.0 as usize] = 1;
        }
        let mut buffer = [0u16; 8];
        (ToUnicodeEx(vk, scan, &state, &mut buffer, 0x4, Some(focus_layout())), buffer)
    };
    if n < 1 {
        return None;
    }
    let len = (n as usize).min(buffer.len());
    char::decode_utf16(buffer[..len].iter().copied())
        .next()?
        .ok()
        .filter(|c| !c.is_control())
}

/// The keyboard layout of the thread that owns the keyboard focus. GetGUIThreadInfo(0) describes the
/// foreground thread: its focus window, else its active window, else the foreground window names
/// the thread (console hosts and Store apps focus a window of another thread than the foreground
/// window's). With no window at all, layout 0: this thread's own.
fn focus_layout() -> HKL {
    let mut info = GUITHREADINFO {
        cbSize: size_of::<GUITHREADINFO>() as u32,
        ..Default::default()
    };
    // SAFETY: plain Win32 queries; `info` outlives the call.
    unsafe {
        let window = match GetGUIThreadInfo(0, &mut info) {
            Ok(()) if !info.hwndFocus.is_invalid() => info.hwndFocus,
            Ok(()) if !info.hwndActive.is_invalid() => info.hwndActive,
            _ => GetForegroundWindow(),
        };
        let thread = if window.is_invalid() {
            0
        } else {
            GetWindowThreadProcessId(window, None)
        };
        GetKeyboardLayout(thread)
    }
}

/// One injected key event, marked so our own hook passes it untouched.
fn marked(vk: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(vk),
                wScan: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: MASK_MARKER,
            },
        },
    }
}

fn send(inputs: &[INPUT]) {
    // SAFETY: fully initialized INPUTs and their exact size.
    let _ = unsafe { SendInput(inputs, size_of::<INPUT>() as i32) };
}

/// Injects a down+up of the unassigned mask VK, marked so our own hook passes it.
fn send_mask() {
    inject_mask_chord(false);
}

/// The menu mask chord, in one `SendInput`: an Alt down when `press_alt`, then a down+up of the
/// unassigned mask VK (so no app sees a lone Alt tap and enters menu mode), every event marked so
/// this crate's hook passes it untouched. The hook injects it after a swallowed Win or Alt combo;
/// [`take_foreground`] presses it, with Alt, to satisfy the foreground lock, and lifts that Alt
/// with [`release_injected_alt`] once the window is in front.
fn inject_mask_chord(press_alt: bool) {
    let alt = marked(VK_MENU.0, KEYBD_EVENT_FLAGS(0));
    let mask = [marked(MASK_VK, KEYBD_EVENT_FLAGS(0)), marked(MASK_VK, KEYEVENTF_KEYUP)];
    if press_alt {
        send(&[alt, mask[0], mask[1]]);
    } else {
        send(&mask);
    }
}

/// Lifts the Alt an [`inject_mask_chord`]`(true)` pressed, marked like it.
fn release_injected_alt() {
    send(&[marked(VK_MENU.0, KEYEVENTF_KEYUP)]);
}

/// Brings `window` to the front with the keyboard and says whether it ended up there, for
/// prisc_interop's `prisc_window_take_foreground` and prisc_capture's overlay. Nothing when it
/// already is; then a plain `SetForegroundWindow` (allowed after a click on one of our windows, or
/// with a shell's `AllowSetForegroundWindow` grant); failing that, the foreground lock is lifted by
/// making this process the last to send input: Alt with the mask chord inside it, so no app sees
/// a lone Alt tap and enters menu mode (winit's `focus_window` taps a bare Alt, which leaves the
/// previous app in menu mode, eating its next key or wheel). Every injected event is marked, so
/// this crate's hook, in this process or another, passes it untouched. An Alt the user is
/// physically holding (part of the hotkey) is left alone: injecting its release would tell
/// Windows they let go. Any thread.
pub fn take_foreground(window: HWND) -> bool {
    // SAFETY: GetForegroundWindow takes nothing; SetForegroundWindow takes any HWND value and
    // fails on one that is not a window.
    let in_front = || unsafe { GetForegroundWindow() } == window;
    if in_front() {
        return true;
    }
    if unsafe { SetForegroundWindow(window) }.as_bool() && in_front() {
        return true;
    }
    let alt_held = key_down(VK_MENU.0);
    inject_mask_chord(!alt_held);
    let _ = unsafe { SetForegroundWindow(window) };
    if !alt_held {
        release_injected_alt();
    }
    in_front()
}
