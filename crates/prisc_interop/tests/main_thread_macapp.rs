//! The macapp area's AppKit cases on the process's real main thread (`harness = false`: libtest never
//! runs a test on the main thread). Each case runs in sequence from `main`; a failure panics, which
//! exits non-zero.
//!
//! Nothing here takes focus: the Regular policy is set without activation (the test process shows a
//! dock icon for a moment), and the Services delivery goes through a private, uniquely named
//! pasteboard, never the user's clipboard. The tracking-area fix runs against stand-in `AvnWindow` /
//! `AvnView` classes (this process has no Avalonia) and a window that is never shown.

#[cfg(target_os = "macos")]
fn main() {
    mac::run();
}

#[cfg(not(target_os = "macos"))]
fn main() {
    println!("main_thread_macapp: the AppKit cases run on macOS only");
}

#[cfg(target_os = "macos")]
mod mac {
    use std::ffi::c_void;
    use std::ptr;
    use std::sync::Mutex;

    use objc2::rc::{Allocated, Retained};
    use objc2::runtime::{AnyClass, AnyObject, ClassBuilder, ProtocolObject, Sel};
    use objc2::{msg_send, sel, ClassType, MainThreadMarker};
    use objc2_app_kit::{
        NSApplication, NSApplicationActivationPolicy, NSEvent, NSEventModifierFlags, NSEventType, NSPasteboard, NSPasteboardWriting,
        NSView, NSWindow,
    };
    use objc2_foundation::{NSArray, NSPoint, NSRect, NSSize, NSString, NSURL};
    use prisc_interop::ffi::app::{
        prisc_app_install_tracking_area_fix_macos, prisc_app_set_activation_policy_macos, PriscActivationPolicy as P,
    };
    use prisc_interop::ffi::services::prisc_services_register_provider_macos;
    use prisc_interop::ffi::status::PriscInteropStatus as S;
    use prisc_interop::ffi::types::PriscString;

    const OK: i32 = S::Ok as i32;

    type Case = (&'static str, fn(MainThreadMarker));

    pub fn run() {
        let mtm = MainThreadMarker::new().expect("a harness = false test runs on the main thread");
        let _app = NSApplication::sharedApplication(mtm);
        let cases: [Case; 6] = [
            ("every_activation_policy_is_set_on_the_app", activation_policies),
            ("an_unknown_policy_changes_nothing", unknown_policy),
            ("a_registered_provider_delivers_the_pasteboard_files", services),
            ("a_second_registration_is_refused", second_registration),
            ("the_tracking_area_fix_without_avalonia_is_none", tracking_fix_without_avalonia),
            ("the_tracking_area_fix_keeps_foreign_areas_from_avalonia", tracking_fix),
        ];
        for (name, case) in cases {
            case(mtm);
            println!("test {name} ... ok");
        }
        println!("main_thread_macapp: {} cases passed", cases.len());
    }

    fn set(policy: i32, activate: u8) -> i32 {
        unsafe { prisc_app_set_activation_policy_macos(policy, activate) }
    }

    fn activation_policies(mtm: MainThreadMarker) {
        let app = NSApplication::sharedApplication(mtm);
        let expected = [
            (P::Regular, NSApplicationActivationPolicy::Regular),
            (P::Prohibited, NSApplicationActivationPolicy::Prohibited),
            (P::Accessory, NSApplicationActivationPolicy::Accessory),
        ];
        for (policy, appkit) in expected {
            assert_eq!(set(policy as i32, 0), OK, "{policy:?} from {:?}", app.activationPolicy());
            assert_eq!(app.activationPolicy(), appkit, "{policy:?}");
        }
        // Activation applies to Regular only: an Accessory app asked to activate stays where it is.
        assert_eq!(set(P::Accessory as i32, 1), OK);
        assert_eq!(app.activationPolicy(), NSApplicationActivationPolicy::Accessory);
        assert!(!app.isActive(), "Accessory with activate_if_regular must not activate the app");
    }

    fn unknown_policy(mtm: MainThreadMarker) {
        let app = NSApplication::sharedApplication(mtm);
        let before = app.activationPolicy();
        assert_eq!(set(3, 1), S::InvalidArg as i32);
        assert_eq!(set(-1, 0), S::InvalidArg as i32);
        assert_eq!(app.activationPolicy(), before);
    }

    /// What the callback saw: the user token and the paths of each delivery.
    static DELIVERED: Mutex<Vec<(usize, Vec<String>)>> = Mutex::new(Vec::new());
    const USER: usize = 0x5EED;

    unsafe extern "C" fn on_files(user: *mut c_void, paths: *const PriscString, count: usize) {
        let paths = std::slice::from_raw_parts(paths, count)
            .iter()
            .map(|s| String::from_utf8(std::slice::from_raw_parts(s.ptr, s.len).to_vec()).expect("UTF-8 path"))
            .collect();
        DELIVERED
            .lock()
            .unwrap()
            .push((user as usize, paths));
    }

    unsafe extern "C" fn never_called(_user: *mut c_void, _paths: *const PriscString, _count: usize) {
        unreachable!("a refused registration must never be delivered to");
    }

    fn port_name() -> String {
        // Unique per run, so the test never takes over the shipped app's "Prisc" port.
        format!("PriscInteropTest.{}", std::process::id())
    }

    /// Invokes the plist's NSMessage on a provider instance with a private pasteboard holding `urls`,
    /// as the pasteboard server does for a Finder Services request.
    fn invoke(mtm: MainThreadMarker, urls: &[&str]) {
        let _ = mtm;
        let class = AnyClass::get(c"PriscServicesProvider").expect("the provider class is registered");
        let provider: Retained<AnyObject> = unsafe { msg_send![class, new] };
        let pasteboard = NSPasteboard::pasteboardWithUniqueName();
        pasteboard.clearContents();
        if !urls.is_empty() {
            let objects: Vec<Retained<ProtocolObject<dyn NSPasteboardWriting>>> = urls
                .iter()
                .map(|path| ProtocolObject::from_retained(NSURL::fileURLWithPath(&NSString::from_str(path))))
                .collect();
            assert!(pasteboard.writeObjects(&NSArray::from_retained_slice(&objects)));
        }
        let _: () = unsafe {
            msg_send![&*provider, uploadFiles: &*pasteboard, userData: ptr::null_mut::<NSString>(), error: ptr::null_mut::<*mut NSString>()]
        };
    }

    fn services(mtm: MainThreadMarker) {
        let name = port_name();
        let status = unsafe { prisc_services_register_provider_macos(name.as_ptr(), name.len(), Some(on_files), USER as *mut c_void) };
        assert_eq!(status, OK);

        invoke(mtm, &["/tmp/prisc services/a b.txt", "/tmp/Caméra.png"]);
        invoke(mtm, &[]);
        let delivered = DELIVERED.lock().unwrap().clone();
        // -[NSURL path] gives the file-system (decomposed) spelling, as it did for the C# provider.
        assert_eq!(
            delivered,
            vec![(
                USER,
                vec!["/tmp/prisc services/a b.txt".to_string(), "/tmp/Came\u{301}ra.png".to_string()]
            )],
            "one delivery for the request with files, none for the empty one"
        );
    }

    fn second_registration(mtm: MainThreadMarker) {
        let name = port_name();
        let status = unsafe { prisc_services_register_provider_macos(name.as_ptr(), name.len(), Some(never_called), ptr::null_mut()) };
        assert_eq!(status, S::State as i32);
        // The first registration still owns the deliveries.
        let count = DELIVERED.lock().unwrap().len();
        invoke(mtm, &["/tmp/again.txt"]);
        let delivered = DELIVERED.lock().unwrap();
        assert_eq!(delivered.len(), count + 1);
        assert_eq!(delivered[count], (USER, vec!["/tmp/again.txt".to_string()]));
    }

    /// The event types the stand-in AvnWindow's own `sendEvent:` saw: what Avalonia would react to.
    static AVALONIA_SAW: Mutex<Vec<NSEventType>> = Mutex::new(Vec::new());

    unsafe extern "C-unwind" fn avalonia_send_event(_this: *mut AnyObject, _cmd: Sel, event: *mut NSEvent) {
        AVALONIA_SAW
            .lock()
            .unwrap()
            .push(unsafe { &*event }.r#type());
    }

    fn tracking_fix_without_avalonia(_mtm: MainThreadMarker) {
        assert!(AnyClass::get(c"AvnWindow").is_none());
        assert_eq!(unsafe { prisc_app_install_tracking_area_fix_macos() }, S::None as i32);
    }

    /// An enter or exit event for `area` (null: an event with no tracking area), as AppKit makes them.
    fn enter_exit(kind: NSEventType, area: *const AnyObject) -> Retained<NSEvent> {
        unsafe {
            msg_send![NSEvent::class(), enterExitEventWithType: kind, location: NSPoint::new(5.0, 5.0),
                modifierFlags: NSEventModifierFlags(0), timestamp: 0.0f64, windowNumber: 0isize,
                context: ptr::null_mut::<AnyObject>(), eventNumber: 0isize, trackingNumber: area as isize,
                userData: ptr::null_mut::<c_void>()]
        }
    }

    fn tracking_area(owner: &AnyObject) -> Retained<AnyObject> {
        let class = AnyClass::get(c"NSTrackingArea").expect("AppKit is loaded");
        // NSTrackingMouseEnteredAndExited | NSTrackingActiveAlways
        let options: usize = 0x01 | 0x80;
        unsafe {
            let area: Allocated<AnyObject> = msg_send![class, alloc];
            msg_send![area, initWithRect: NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(10.0, 10.0)),
                options: options, owner: owner, userInfo: ptr::null_mut::<AnyObject>()]
        }
    }

    fn tracking_fix(mtm: MainThreadMarker) {
        // Avalonia's two classes as the fix sees them: a window with its own sendEvent: and a view.
        let mut window_class = ClassBuilder::new(c"AvnWindow", NSWindow::class()).expect("no AvnWindow yet");
        unsafe { window_class.add_method(sel!(sendEvent:), avalonia_send_event as unsafe extern "C-unwind" fn(_, _, _)) };
        let window_class = window_class.register();
        ClassBuilder::new(c"AvnView", NSView::class())
            .expect("no AvnView yet")
            .register();

        assert_eq!(unsafe { prisc_app_install_tracking_area_fix_macos() }, OK);
        assert_eq!(
            unsafe { prisc_app_install_tracking_area_fix_macos() },
            OK,
            "a second install is a no-op"
        );

        let window: Retained<NSWindow> = unsafe {
            let window: Allocated<NSWindow> = msg_send![window_class, alloc];
            msg_send![window, initWithContentRect: NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(200.0, 100.0)),
                styleMask: 1usize, backing: 2usize, defer: true]
        };
        let avalonia_view: Retained<AnyObject> = unsafe { msg_send![AnyClass::get(c"AvnView").unwrap(), new] };
        // The stale-edge stand-in: an area owned by some other view (NSThemeFrame in the app).
        let other_view = NSView::new(mtm);
        let own_area = tracking_area(&avalonia_view);
        let foreign_area = tracking_area(&other_view);

        let send = |event: Retained<NSEvent>| window.sendEvent(&event);
        send(enter_exit(NSEventType::MouseExited, &*own_area));
        send(enter_exit(NSEventType::MouseEntered, &*own_area));
        send(enter_exit(NSEventType::MouseExited, &*foreign_area));
        send(enter_exit(NSEventType::MouseEntered, &*foreign_area));
        send(enter_exit(NSEventType::MouseExited, ptr::null()));

        assert_eq!(
            *AVALONIA_SAW.lock().unwrap(),
            vec![NSEventType::MouseExited, NSEventType::MouseEntered, NSEventType::MouseExited],
            "Avalonia sees its own view's areas and area-less events, never another view's"
        );
    }
}
