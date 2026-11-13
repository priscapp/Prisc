//! macOS: keeps Avalonia's windows out of other views' tracking areas
//! (`prisc_app_install_tracking_area_fix_macos`).
//!
//! Avalonia.Native's `-[AvnWindow sendEvent:]` sets the arrow cursor on every mouse-exited event the
//! window dispatches, and re-sends the pointed-at control's cursor on every mouse-entered one,
//! whichever tracking area the event belongs to. It sends a control's cursor again only when that
//! cursor changes, so an exit from somebody else's area leaves the arrow up until the pointer
//! reaches a control with a different cursor. macOS 26 makes that common: NSThemeFrame adds a
//! window-edge tracking area at every window size and never removes the old ones, so a resized
//! window carries stale edges across its content, and moving up across one swaps the editor
//! gizmo's move cursor for the arrow halfway down the picture.
//!
//! The fix replaces AvnWindow's own `sendEvent:` with [`send_event`]: an enter or exit whose
//! tracking area is owned by anything but an AvnView goes straight to NSWindow's implementation,
//! which delivers it to the area's owner exactly as before; every other event goes through
//! Avalonia's method unchanged. Before calling `super` Avalonia only notes a left-mouse-down, so
//! nothing is skipped for the events rerouted.
//!
//! Reported upstream as https://github.com/AvaloniaUI/Avalonia/issues/22689; this whole module
//! (and its export) goes once a fixed Avalonia.Native ships.

use std::ffi::CStr;
use std::sync::OnceLock;

use objc2::runtime::{AnyClass, AnyObject, Imp, Sel};
use objc2::sel;
use objc2_app_kit::{NSEvent, NSEventType};

use crate::error::Result;
use crate::ffi::status::{PRISC_OK, PRISC_S_NONE};
use crate::panic::{self, CallbackPanic};
use crate::platform::macos::main_thread;

/// Avalonia.Native's window and content view classes (libAvaloniaNative registers both on load).
const WINDOW_CLASS: &CStr = c"AvnWindow";
const VIEW_CLASS: &CStr = c"AvnView";

/// `-sendEvent:` (`v@:@`).
type SendEvent = unsafe extern "C-unwind" fn(*mut AnyObject, Sel, *mut NSEvent);

/// The two implementations the trampoline picks between, and the class whose areas are
/// Avalonia's own. Set once, on the main thread, before the trampoline is installed.
struct Patch {
    avalonia: SendEvent,
    appkit: SendEvent,
    view: &'static AnyClass,
}

static PATCH: OnceLock<Patch> = OnceLock::new();

/// AvnWindow's `-sendEvent:` from now on. AppKit dispatches events on the main thread only.
unsafe extern "C-unwind" fn send_event(this: *mut AnyObject, cmd: Sel, event: *mut NSEvent) {
    let foreign = panic::callback_guard(CallbackPanic::process(), false, || {
        // SAFETY: AppKit passes the event being dispatched (or nil), alive for this call.
        let event = unsafe { event.as_ref() };
        match (PATCH.get(), event) {
            (Some(patch), Some(event)) => is_foreign(event, patch.view),
            _ => false,
        }
    });
    // Installed only after PATCH was set, so this never comes back empty.
    let Some(patch) = PATCH.get() else {
        return;
    };
    let target = if foreign { patch.appkit } else { patch.avalonia };
    // SAFETY: both are `-sendEvent:` implementations of AvnWindow's class chain, called with the
    // receiver, selector and argument AppKit dispatched.
    unsafe { target(this, cmd, event) }
}

/// An enter or exit from a tracking area some other view owns (NSThemeFrame's edges, the title
/// bar's). An event with no area, or no owner, is left to Avalonia as before.
fn is_foreign(event: &NSEvent, view: &AnyClass) -> bool {
    let kind = event.r#type();
    if kind != NSEventType::MouseEntered && kind != NSEventType::MouseExited {
        return false;
    }
    let Some(owner) = event
        .trackingArea()
        .and_then(|area| area.owner())
    else {
        return false;
    };
    let mut class = Some(owner.class());
    while let Some(current) = class {
        if std::ptr::eq(current, view) {
            return false;
        }
        class = current.superclass();
    }
    true
}

/// Installs the trampoline once per process. `PRISC_S_NONE` when Avalonia.Native is not loaded or
/// its window no longer overrides `sendEvent:` (nothing to fix).
pub(crate) fn install() -> Result {
    main_thread("prisc_app_install_tracking_area_fix_macos")?;
    if PATCH.get().is_some() {
        return Ok(PRISC_OK);
    }
    let (Some(window), Some(view)) = (AnyClass::get(WINDOW_CLASS), AnyClass::get(VIEW_CLASS)) else {
        return Ok(PRISC_S_NONE);
    };
    let selector = sel!(sendEvent:);
    // AvnWindow's own method only: replacing an inherited one would patch NSWindow, and so every
    // window in the process.
    let own = window
        .instance_methods()
        .iter()
        .copied()
        .find(|method| method.name() == selector);
    let inherited = window
        .superclass()
        .and_then(|superclass| superclass.instance_method(selector));
    let (Some(own), Some(inherited)) = (own, inherited) else {
        return Ok(PRISC_S_NONE);
    };
    // SAFETY: both are `-sendEvent:` implementations, so they have SendEvent's signature.
    let (avalonia, appkit) = unsafe {
        (
            std::mem::transmute::<Imp, SendEvent>(own.implementation()),
            std::mem::transmute::<Imp, SendEvent>(inherited.implementation()),
        )
    };
    if PATCH
        .set(Patch {
            avalonia,
            appkit,
            view,
        })
        .is_err()
    {
        return Ok(PRISC_OK);
    }
    // SAFETY: send_event has the selector's signature (`v@:@`) and forwards to the implementation
    // it replaces; PATCH is set above, before AppKit can call it.
    unsafe { own.set_implementation(std::mem::transmute::<SendEvent, Imp>(send_event)) };
    Ok(PRISC_OK)
}
