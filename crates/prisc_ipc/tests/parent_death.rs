//! A helper whose parent dies must still quit (HELPER_PROTOCOL.md §3.3). A dead parent takes
//! the read ends of the helper's stdout and stderr pipes with it, so every diagnostic the quit
//! path writes meets a broken pipe; none of them may panic and lose the quit.
//!
//! This test binary re-runs itself as the helper: the `helper_role` test, with the role in an
//! environment variable, opens the channel, installs the quit path and reads commands like a
//! real helper's `main`.

use std::io::{BufRead, BufReader};
use std::ops::ControlFlow;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use prisc_ipc::catalog::job;

const ROLE_VAR: &str = "PRISC_IPC_PARENT_DEATH_ROLE";
/// The helper's quit grace; its hard-exit timer fires this long after the quit path.
const GRACE: Duration = Duration::from_secs(2);
/// How long the parent waits for the helper to exit before calling it a survivor.
const DEADLINE: Duration = Duration::from_secs(15);

/// The helper. `idle`: waits for the quit path. `emitting`: emits `progress` from the main
/// thread until the quit path fires, as prisc_vid2gif and prisc_scroll_driver do. Either way it
/// then exits 0, and a quit that never reaches it leaves the hard-exit timer to exit 6.
#[test]
fn helper_role() {
    let Ok(role) = std::env::var(ROLE_VAR) else {
        return;
    };
    let out = prisc_ipc::open::<job::Event>("parent_death_test");
    let (quit_tx, quit_rx) = mpsc::channel();
    prisc_ipc::lifetime::install(
        move |reason| {
            let _ = quit_tx.send(reason);
        },
        GRACE,
    );
    prisc_ipc::read_commands::<job::Command>(|_| ControlFlow::Continue(()));
    match role.as_str() {
        "idle" => {
            let _ = quit_rx.recv();
        }
        "emitting" => {
            let mut percent = 0;
            while quit_rx.try_recv().is_err() {
                out.emit(&job::Event::Progress {
                    percent,
                });
                percent = (percent + 1) % 100;
                std::thread::sleep(Duration::from_millis(20));
            }
        }
        other => panic!("unknown role {other}"),
    }
    // The terminal line, which nobody reads any more: dropped, not a panic.
    out.emit(&job::Event::Done {
        result: job::JobResult::Cancelled,
        path: None,
        bytes: None,
    });
    prisc_ipc::lifetime::exit(prisc_ipc::exit::OK)
}

/// Starts the helper as `role` with every stdio piped, and waits for its `hello`.
fn spawn(role: &str) -> (Child, BufReader<std::process::ChildStdout>) {
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args(["helper_role", "--exact", "--nocapture", "--test-threads=1"])
        .env(ROLE_VAR, role)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdout = BufReader::new(child.stdout.take().unwrap());
    let (hello_tx, hello_rx) = mpsc::channel();
    std::thread::spawn(move || {
        let mut line = String::new();
        // libtest's own "running 1 test" and "test helper_role ... " come first, the latter on
        // the same line as `hello`.
        while !line.contains(r#"{"type":"hello""#) {
            line.clear();
            if stdout.read_line(&mut line).unwrap_or(0) == 0 {
                return;
            }
        }
        let _ = hello_tx.send(stdout);
    });
    match hello_rx.recv_timeout(DEADLINE) {
        Ok(stdout) => (child, stdout),
        Err(_) => {
            let _ = child.kill();
            panic!("no hello from the helper within {DEADLINE:?}");
        }
    }
}

/// Waits for the helper to exit; kills a survivor and fails.
fn exit_code(mut child: Child) -> i32 {
    let started = Instant::now();
    while started.elapsed() < DEADLINE {
        if let Some(status) = child.try_wait().unwrap() {
            return status.code().unwrap_or(-1);
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let _ = child.kill();
    let _ = child.wait();
    panic!("the helper was still alive {DEADLINE:?} after its parent died");
}

/// The parent dies while the helper waits on stdin: stdout and stderr go first, then stdin
/// reaches EOF.
#[test]
fn a_helper_quits_on_stdin_eof_with_dead_stdout_and_stderr() {
    let (mut child, stdout) = spawn("idle");
    drop(stdout);
    drop(child.stderr.take());
    drop(child.stdin.take());
    assert_eq!(exit_code(child), prisc_ipc::exit::OK);
}

/// The parent dies while the helper is emitting, and stdin stays open: the failed emit is what
/// fires the quit path, on the helper's main thread.
#[test]
fn a_helper_quits_on_a_dead_stdout_with_a_dead_stderr() {
    let (mut child, stdout) = spawn("emitting");
    drop(stdout);
    drop(child.stderr.take());
    let _stdin = child.stdin.take();
    assert_eq!(exit_code(child), prisc_ipc::exit::OK);
}
