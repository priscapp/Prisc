//! End-to-end CLI tests. prisc_vid2gif links the bundled FFmpeg libraries directly,
//! so these tests are fully self-contained: container inputs come from small
//! committed fixtures (tests/fixtures/), raw inputs are generated on the fly
//! as Y4M, and outputs are validated by parsing the GIF or WebP byte stream,
//! with no external tools involved.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

fn fixture(name: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join(name)
}

/// Fresh per-test temp dir (removed up-front so reruns start clean).
fn test_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("vid2gif-test-{name}"));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Writes a raw Y4M clip (moving gradient bands) — decodable by FFmpeg on
/// every platform with no codec involved.
fn write_y4m(path: &Path, w: usize, h: usize, fps: u32, seconds: u32) {
    use std::io::BufWriter;
    let mut f = BufWriter::new(std::fs::File::create(path).unwrap());
    writeln!(f, "YUV4MPEG2 W{w} H{h} F{fps}:1 Ip A1:1 C420mpeg2").unwrap();
    let (cw, ch) = (w / 2, h / 2);
    let mut y_plane = vec![0u8; w * h];
    let mut u_plane = vec![0u8; cw * ch];
    let mut v_plane = vec![0u8; cw * ch];
    for t in 0..(fps as usize * seconds as usize) {
        for yy in 0..h {
            for xx in 0..w {
                y_plane[yy * w + xx] = ((xx / 2 + yy / 2 + t * 7) & 0xFF) as u8;
            }
        }
        for yy in 0..ch {
            for xx in 0..cw {
                u_plane[yy * cw + xx] = ((xx * 2 + t * 5) & 0xFF) as u8;
                v_plane[yy * cw + xx] = ((yy * 2 + t * 3) & 0xFF) as u8;
            }
        }
        f.write_all(b"FRAME\n").unwrap();
        f.write_all(&y_plane).unwrap();
        f.write_all(&u_plane).unwrap();
        f.write_all(&v_plane).unwrap();
    }
}

/// Runs prisc_vid2gif to completion and returns (exit code, parsed stdout
/// protocol messages). stdin stays open until the process exits: closing it
/// is a cancel.
fn run_vid2gif(args: &[&std::ffi::OsStr]) -> (Option<i32>, Vec<Msg>) {
    let mut child = Command::new(env!("CARGO_BIN_EXE_prisc_vid2gif"))
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawn prisc_vid2gif");
    let stdin = child.stdin.take();
    let mut stdout = String::new();
    child
        .stdout
        .take()
        .unwrap()
        .read_to_string(&mut stdout)
        .expect("read stdout");
    let status = child.wait().expect("wait for prisc_vid2gif");
    drop(stdin);
    (status.code(), parse_protocol(&stdout))
}

#[derive(Debug)]
enum Msg {
    Progress(u32),
    Done(PathBuf, u64),
    Error { code: String, message: String, fatal: bool },
    Cancelled,
}

/// Parses the stdout protocol strictly: the first line must be the `job`
/// catalog's `hello`, and any line that is not a known protocol message is a
/// violation that fails the test.
fn parse_protocol(stdout: &str) -> Vec<Msg> {
    let mut lines = stdout.lines();
    let hello: serde_json::Value = serde_json::from_str(lines.next().expect("no hello")).expect("hello is JSON");
    assert_eq!(hello["type"], "hello", "{hello}");
    assert_eq!(hello["catalog"], "job", "{hello}");
    assert_eq!(hello["helper"], "prisc_vid2gif", "{hello}");
    let mut msgs = Vec::new();
    for line in lines {
        let v: serde_json::Value = serde_json::from_str(line)
            .unwrap_or_else(|_| panic!("protocol violation on stdout: {line:?}"));
        msgs.push(match v["type"].as_str() {
            Some("progress") => Msg::Progress(v["percent"].as_u64().expect("percent") as u32),
            Some("done") if v["result"] == "ok" => Msg::Done(
                PathBuf::from(v["path"].as_str().expect("path")),
                v["bytes"].as_u64().expect("bytes"),
            ),
            Some("done") if v["result"] == "cancelled" => Msg::Cancelled,
            Some("error") => Msg::Error {
                code: v["code"].as_str().expect("code").to_owned(),
                message: v["message"].as_str().expect("message").to_owned(),
                fatal: v["fatal"].as_bool().expect("fatal"),
            },
            _ => panic!("protocol violation on stdout: {line:?}"),
        });
    }
    msgs
}

/// Asserts a run ended with exactly one fatal error of `code` and returns
/// its message.
fn expect_fatal(code: Option<i32>, msgs: &[Msg], expected_code: &str, expected_exit: i32) -> String {
    assert_eq!(code, Some(expected_exit), "exit code: {msgs:?}");
    match msgs {
        [Msg::Error { code, message, fatal: true }] => {
            assert_eq!(code, expected_code, "{message}");
            message.clone()
        }
        other => panic!("expected a single fatal error, got {other:?}"),
    }
}

/// Asserts the happy-path protocol shape and returns the `done` payload:
/// progress lines strictly increasing from 0 to exactly 100, then one final
/// `done{ok}`, and no `error`/cancelled lines anywhere.
fn expect_success(msgs: &[Msg]) -> (PathBuf, u64) {
    let mut progress = Vec::new();
    for (i, m) in msgs.iter().enumerate() {
        match m {
            Msg::Progress(p) => {
                assert!(i < msgs.len() - 1, "progress after done: {msgs:?}");
                progress.push(*p);
            }
            Msg::Done(..) => assert_eq!(i, msgs.len() - 1, "done must be last: {msgs:?}"),
            Msg::Error { message, .. } => panic!("unexpected error message: {message}"),
            Msg::Cancelled => panic!("unexpected cancelled message: {msgs:?}"),
        }
    }
    assert_eq!(
        progress.first(),
        Some(&0),
        "must start at progress 0: {msgs:?}"
    );
    assert_eq!(
        progress.last(),
        Some(&100),
        "must end at progress 100: {msgs:?}"
    );
    assert!(
        progress.windows(2).all(|w| w[0] < w[1]),
        "progress must be strictly increasing: {progress:?}"
    );
    assert!(progress.iter().all(|p| *p <= 100));
    match msgs.last() {
        Some(Msg::Done(path, bytes)) => (path.clone(), *bytes),
        other => panic!("expected done as final message, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// GIF validation (byte-level, no external tools)
// ---------------------------------------------------------------------------

/// GIF header magic + logical screen dimensions.
fn gif_dims(data: &[u8]) -> (u16, u16) {
    assert!(data.len() > 13, "gif too small: {} bytes", data.len());
    assert_eq!(&data[..6], b"GIF89a", "bad gif magic");
    (
        u16::from_le_bytes([data[6], data[7]]),
        u16::from_le_bytes([data[8], data[9]]),
    )
}

/// Walks the GIF block structure and counts image frames. Panics on any
/// malformed block, so it doubles as a structural integrity check down to the
/// trailer byte.
fn gif_frame_count(data: &[u8]) -> usize {
    gif_dims(data);
    let mut pos = 6;
    let flags = data[pos + 4];
    pos += 7; // logical screen descriptor
    if flags & 0x80 != 0 {
        pos += 3 << ((flags & 0x07) as usize + 1); // global color table
    }
    let mut frames = 0;
    loop {
        match data[pos] {
            0x21 => {
                // extension: introducer + label, then length-prefixed sub-blocks
                pos += 2;
                while data[pos] != 0 {
                    pos += data[pos] as usize + 1;
                }
                pos += 1;
            }
            0x2C => {
                // image descriptor
                let f = data[pos + 9];
                pos += 10;
                if f & 0x80 != 0 {
                    pos += 3 << ((f & 0x07) as usize + 1); // local color table
                }
                pos += 1; // LZW minimum code size
                while data[pos] != 0 {
                    pos += data[pos] as usize + 1;
                }
                pos += 1;
                frames += 1;
            }
            0x3B => break, // trailer
            other => panic!("unexpected GIF block 0x{other:02X} at offset {pos}"),
        }
    }
    frames
}

// ---------------------------------------------------------------------------
// WebP validation (byte-level, no external tools)
// ---------------------------------------------------------------------------

/// What an animated WebP declares: canvas size, loop count, and per frame
/// its rectangle and duration.
#[derive(Debug)]
struct WebpAnim {
    width: u32,
    height: u32,
    loop_count: u16,
    frames: Vec<WebpFrame>,
}

#[derive(Debug)]
struct WebpFrame {
    x: u32,
    y: u32,
    width: u32,
    height: u32,
    duration_ms: u32,
}

impl WebpAnim {
    fn total_ms(&self) -> u32 {
        self.frames.iter().map(|f| f.duration_ms).sum()
    }
}

fn u24(b: &[u8]) -> u32 {
    u32::from_le_bytes([b[0], b[1], b[2], 0])
}

/// Walks the RIFF container chunk by chunk and requires the extended,
/// animated layout: `VP8X` (animation flag set) first, one `ANIM`, then
/// `ANMF` frames that each hold a `VP8 `/`VP8L` bitstream and fit the canvas.
/// Panics on anything malformed, so it doubles as a structural check down to
/// the last byte.
fn parse_webp(data: &[u8]) -> WebpAnim {
    assert!(data.len() > 30, "webp too small: {} bytes", data.len());
    assert_eq!(&data[..4], b"RIFF", "bad riff magic");
    assert_eq!(&data[8..12], b"WEBP", "bad webp magic");
    let riff_size = u32::from_le_bytes(data[4..8].try_into().unwrap()) as usize;
    assert_eq!(riff_size + 8, data.len(), "RIFF size must cover the whole file");

    let mut anim = None;
    let mut loop_count = None;
    let mut frames = Vec::new();
    let mut pos = 12;
    while pos < data.len() {
        let id = &data[pos..pos + 4];
        let size = u32::from_le_bytes(data[pos + 4..pos + 8].try_into().unwrap()) as usize;
        let payload = &data[pos + 8..pos + 8 + size];
        match id {
            b"VP8X" => {
                assert_eq!(pos, 12, "VP8X must be the first chunk");
                assert!(payload[0] & 0x02 != 0, "VP8X animation flag not set");
                anim = Some((u24(&payload[4..7]) + 1, u24(&payload[7..10]) + 1));
            }
            b"ANIM" => {
                loop_count = Some(u16::from_le_bytes([payload[4], payload[5]]));
            }
            b"ANMF" => {
                let frame = WebpFrame {
                    x: u24(&payload[0..3]) * 2,
                    y: u24(&payload[3..6]) * 2,
                    width: u24(&payload[6..9]) + 1,
                    height: u24(&payload[9..12]) + 1,
                    duration_ms: u24(&payload[12..15]),
                };
                // the frame's own bitstream, optionally after an ALPH chunk
                let mut inner = 16;
                let mut saw_image = false;
                while inner < payload.len() {
                    let sub = &payload[inner..inner + 4];
                    let sub_size = u32::from_le_bytes(payload[inner + 4..inner + 8].try_into().unwrap()) as usize;
                    match sub {
                        b"ALPH" => {}
                        b"VP8 " | b"VP8L" => saw_image = true,
                        other => panic!("unexpected chunk {:?} inside ANMF", String::from_utf8_lossy(other)),
                    }
                    inner += 8 + sub_size + (sub_size & 1);
                }
                assert!(saw_image, "ANMF frame without a VP8/VP8L bitstream");
                frames.push(frame);
            }
            other => panic!("unexpected chunk {:?}", String::from_utf8_lossy(other)),
        }
        pos += 8 + size + (size & 1);
    }
    assert_eq!(pos, data.len(), "chunks must end exactly at the end of the file");

    let (width, height) = anim.expect("no VP8X chunk");
    let anim = WebpAnim {
        width,
        height,
        loop_count: loop_count.expect("no ANIM chunk"),
        frames,
    };
    assert!(!anim.frames.is_empty(), "no frames");
    for f in &anim.frames {
        assert!(
            f.x + f.width <= width && f.y + f.height <= height,
            "frame outside the canvas: {f:?} in {width}x{height}"
        );
    }
    anim
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[test]
fn converts_mp4_with_valid_protocol_and_output() {
    let dir = test_dir("mp4");
    let clip = dir.join("clip.mp4");
    std::fs::copy(fixture("clip.mp4"), &clip).unwrap();

    let (code, msgs) = run_vid2gif(&[clip.as_os_str(), "--quality".as_ref(), "fair".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);

    // Default output path: input with .gif extension.
    assert_eq!(path, dir.join("clip.gif"));
    let data = std::fs::read(&path).expect("read output gif");
    assert_eq!(
        bytes,
        data.len() as u64,
        "done line must report the real size"
    );
    assert_eq!(gif_dims(&data), (160, 120), "no scaling requested");

    // 2 s at the `fair` preset's 10 fps ≈ 20 frames.
    let frames = gif_frame_count(&data);
    assert!(
        (15..=25).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn mkv_input_explicit_output_with_spaces_best_quality() {
    let dir = test_dir("mkv");

    // Exercise the done-line parsing with a space in the output path.
    let out = dir.join("my clips").join("out put.gif");
    let input = fixture("clip.mkv");
    let (code, msgs) = run_vid2gif(&[
        input.as_os_str(),
        out.as_os_str(),
        "--quality".as_ref(),
        "best".as_ref(),
    ]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);
    assert_eq!(path, out);
    assert_eq!(bytes, std::fs::metadata(&out).unwrap().len());

    // 2 s at the `best` preset's 20 fps ≈ 40 frames.
    let data = std::fs::read(&out).unwrap();
    let frames = gif_frame_count(&data);
    assert!(
        (30..=50).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn max_width_and_height_clamp_most_restrictive() {
    let dir = test_dir("clamp");
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 15, 1);

    let convert = |extra: &[&str], out: &str| -> (u16, u16) {
        let out_path = dir.join(out);
        let mut args: Vec<&std::ffi::OsStr> = vec![clip.as_os_str(), out_path.as_os_str()];
        args.extend(extra.iter().map(|s| -> &std::ffi::OsStr { s.as_ref() }));
        let (code, msgs) = run_vid2gif(&args);
        assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
        let (path, _) = expect_success(&msgs);
        gif_dims(&std::fs::read(path).unwrap())
    };

    // Width clamp alone.
    assert_eq!(convert(&["--max-width", "120"], "w.gif"), (120, 90));
    // Height clamp alone (factor 0.25 -> 80x60).
    assert_eq!(convert(&["--max-height", "60"], "h.gif"), (80, 60));
    // Both: the height clamp is more restrictive here.
    assert_eq!(
        convert(&["--max-width", "120", "--max-height", "60"], "wh.gif"),
        (80, 60)
    );
    // Clamps larger than the source never upscale.
    assert_eq!(
        convert(&["--max-width", "5000", "--max-height", "5000"], "big.gif"),
        (320, 240)
    );
}

#[test]
fn fps_override_changes_frame_count() {
    let dir = test_dir("fps");
    let out = dir.join("out.gif");
    let input = fixture("clip.mp4");
    let (code, msgs) = run_vid2gif(&[
        input.as_os_str(),
        out.as_os_str(),
        "--fps".as_ref(),
        "5".as_ref(),
    ]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    expect_success(&msgs);
    let frames = gif_frame_count(&std::fs::read(&out).unwrap());
    assert!(
        (8..=12).contains(&frames),
        "unexpected frame count {frames}"
    );
}

#[test]
fn missing_input_is_a_fatal_io_error() {
    let (code, msgs) = run_vid2gif(&["C:/definitely/not/here.mp4".as_ref()]);
    let message = expect_fatal(code, &msgs, "io_failed", 1);
    assert!(message.contains("not found"), "unexpected message: {message}");
}

#[test]
fn corrupt_input_is_a_fatal_init_error() {
    let dir = test_dir("corrupt");
    let clip = dir.join("clip.mp4");
    std::fs::write(&clip, b"this is not a video file").unwrap();

    let (code, msgs) = run_vid2gif(&[clip.as_os_str()]);
    let message = expect_fatal(code, &msgs, "init_failed", 1);
    assert!(message.contains("could not read"), "unexpected message: {message}");
}

#[test]
fn gif_input_without_explicit_output_is_a_usage_error() {
    let dir = test_dir("gifin");
    let input = dir.join("clip.gif");
    std::fs::write(&input, b"GIF89a").unwrap();

    let (code, msgs) = run_vid2gif(&[input.as_os_str()]);
    let message = expect_fatal(code, &msgs, "usage", 2);
    assert!(message.contains("equals input"), "unexpected message: {message}");
    // Nothing was written, so nothing is cleaned up: the input survives.
    assert!(input.exists());
}

#[test]
fn webp_output_extension_selects_webp() {
    let dir = test_dir("webp-ext");
    let out = dir.join("out.webp");
    let input = fixture("clip.mp4");
    let (code, msgs) = run_vid2gif(&[input.as_os_str(), out.as_os_str(), "--quality".as_ref(), "fair".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    let (path, bytes) = expect_success(&msgs);
    assert_eq!(path, out);

    let data = std::fs::read(&out).unwrap();
    assert_eq!(bytes, data.len() as u64, "done line must report the real size");
    let anim = parse_webp(&data);
    assert_eq!((anim.width, anim.height), (160, 120), "no scaling requested");
    assert_eq!(anim.loop_count, 0, "must loop forever, like the GIF");
    // 2 s of input. libwebp folds identical frames into the previous one's
    // duration, so the frame count can drop but the running time cannot.
    assert!(
        (1800..=2200).contains(&anim.total_ms()),
        "unexpected running time {} ms: {anim:?}",
        anim.total_ms()
    );
}

#[test]
fn format_flag_without_output_writes_webp_beside_input() {
    let dir = test_dir("webp-flag");
    let clip = dir.join("clip.mkv");
    std::fs::copy(fixture("clip.mkv"), &clip).unwrap();

    let (code, msgs) = run_vid2gif(&[clip.as_os_str(), "--format".as_ref(), "webp".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    let (path, _) = expect_success(&msgs);
    assert_eq!(path, dir.join("clip.webp"));
    parse_webp(&std::fs::read(&path).unwrap());
    assert!(!dir.join("clip.gif").exists());
}

#[test]
fn format_flag_overrides_the_extension() {
    let dir = test_dir("webp-override");
    let out = dir.join("out.gif");
    let input = fixture("clip.mp4");
    let (code, msgs) = run_vid2gif(&[input.as_os_str(), out.as_os_str(), "--format".as_ref(), "webp".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    expect_success(&msgs);
    parse_webp(&std::fs::read(&out).unwrap());

    // and the other way round: --format gif writes a GIF into a .webp path
    let out = dir.join("out.webp");
    let (code, msgs) = run_vid2gif(&[input.as_os_str(), out.as_os_str(), "--format".as_ref(), "gif".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    expect_success(&msgs);
    gif_frame_count(&std::fs::read(&out).unwrap());
}

#[test]
fn webp_every_frame_and_timestamp_survives() {
    // the gradient moves every frame, so no frame can be folded into another:
    // 2 s at --fps 12 is exactly 24 frames of 1000/12 ms each.
    let dir = test_dir("webp-frames");
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 24, 2);
    let out = dir.join("out.webp");
    let (code, msgs) = run_vid2gif(&[clip.as_os_str(), out.as_os_str(), "--fps".as_ref(), "12".as_ref()]);
    assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
    expect_success(&msgs);

    let anim = parse_webp(&std::fs::read(&out).unwrap());
    assert_eq!((anim.width, anim.height), (320, 240));
    assert_eq!(anim.frames.len(), 24, "{anim:?}");
    for f in &anim.frames {
        // 83.3 ms, rounded per timestamp to 83 or 84
        assert!((83..=84).contains(&f.duration_ms), "frame duration {f:?}");
    }
    assert!((1999..=2001).contains(&anim.total_ms()), "running time {}", anim.total_ms());
}

#[test]
fn webp_max_width_and_height_clamp_most_restrictive() {
    let dir = test_dir("webp-clamp");
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 15, 1);

    let convert = |extra: &[&str], out: &str| -> (u32, u32) {
        let out_path = dir.join(out);
        let mut args: Vec<&std::ffi::OsStr> = vec![clip.as_os_str(), out_path.as_os_str()];
        args.extend(extra.iter().map(|s| -> &std::ffi::OsStr { s.as_ref() }));
        let (code, msgs) = run_vid2gif(&args);
        assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
        let (path, _) = expect_success(&msgs);
        let anim = parse_webp(&std::fs::read(path).unwrap());
        (anim.width, anim.height)
    };

    assert_eq!(convert(&["--max-width", "120"], "w.webp"), (120, 90));
    assert_eq!(convert(&["--max-height", "60"], "h.webp"), (80, 60));
    assert_eq!(convert(&["--max-width", "120", "--max-height", "60"], "wh.webp"), (80, 60));
    assert_eq!(convert(&["--max-width", "5000"], "big.webp"), (320, 240));
    // an odd height after scaling (100x75) is fine for WebP
    assert_eq!(convert(&["--max-width", "100"], "odd.webp"), (100, 75));
}

#[test]
fn webp_presets_order_by_size() {
    let dir = test_dir("webp-presets");
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 20, 2);

    let size = |quality: &str| -> u64 {
        let out = dir.join(format!("{quality}.webp"));
        let (code, msgs) = run_vid2gif(&[clip.as_os_str(), out.as_os_str(), "--quality".as_ref(), quality.as_ref()]);
        assert_eq!(code, Some(0), "prisc_vid2gif failed: {msgs:?}");
        expect_success(&msgs).1
    };
    let (best, good, fair) = (size("best"), size("good"), size("fair"));
    assert!(best > good && good > fair, "best {best}, good {good}, fair {fair}");
}

#[test]
fn webp_input_without_explicit_output_is_a_usage_error() {
    let dir = test_dir("webpin");
    let input = dir.join("clip.webp");
    std::fs::write(&input, b"RIFF").unwrap();

    let (code, msgs) = run_vid2gif(&[input.as_os_str(), "--format".as_ref(), "webp".as_ref()]);
    let message = expect_fatal(code, &msgs, "usage", 2);
    assert!(message.contains("equals input"), "unexpected message: {message}");
    assert!(input.exists());
}

/// How a mid-conversion cancel is delivered.
enum CancelBy {
    Quit,
    Eof,
}

/// Starts a conversion long enough that it cannot finish first, waits until it
/// is demonstrably in flight, sends `bad` (if any) and then cancels; requires a
/// prompt `done{cancelled}`, exit 0, and no partial output.
fn cancel_mid_conversion(name: &str, by: CancelBy, bad: Option<&[u8]>) -> Vec<Msg> {
    cancel_mid_conversion_as(name, by, bad, "gif")
}

/// [`cancel_mid_conversion`] for the output `format` (`gif` or `webp`).
fn cancel_mid_conversion_as(name: &str, by: CancelBy, bad: Option<&[u8]>, format: &str) -> Vec<Msg> {
    use std::io::{BufRead, BufReader};
    use std::time::Instant;

    let dir = test_dir(name);
    let clip = dir.join("clip.y4m");
    write_y4m(&clip, 320, 240, 15, 90);

    let mut child = Command::new(env!("CARGO_BIN_EXE_prisc_vid2gif"))
        .arg(&clip)
        .args(["--format", format])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .expect("spawn prisc_vid2gif");
    let mut stdin = Some(child.stdin.take().unwrap());
    let stdout = BufReader::new(child.stdout.take().unwrap());

    let mut lines = Vec::new();
    let mut sent_at = None;
    for line in stdout.lines() {
        let line = line.expect("read stdout");
        if sent_at.is_none() {
            let v: serde_json::Value = serde_json::from_str(&line).unwrap();
            let percent = v["percent"].as_u64().unwrap_or(0);
            if v["type"] == "progress" && percent > 0 && percent < 100 {
                let mut pipe = stdin.take().unwrap();
                if let Some(bad) = bad {
                    pipe.write_all(bad).unwrap();
                }
                match by {
                    CancelBy::Quit => {
                        pipe.write_all(b"{\"type\":\"quit\"}\n").expect("write quit");
                        pipe.flush().unwrap();
                        stdin = Some(pipe);
                    }
                    CancelBy::Eof => drop(pipe),
                }
                sent_at = Some(Instant::now());
            }
        }
        lines.push(line);
    }
    let latency = sent_at
        .expect("never saw mid-conversion progress")
        .elapsed();

    let status = child.wait().expect("wait for prisc_vid2gif");
    drop(stdin);
    assert!(status.success(), "cancellation must exit 0");
    let msgs = parse_protocol(&lines.join("\n"));
    assert!(
        matches!(msgs.last(), Some(Msg::Cancelled)),
        "done{{cancelled}} must be the final message: {msgs:?}"
    );
    assert!(
        !msgs.iter().any(|m| matches!(m, Msg::Done(..) | Msg::Error { fatal: true, .. })),
        "no done{{ok}}/fatal error after cancellation: {msgs:?}"
    );
    assert!(
        latency.as_secs() < 5,
        "cancel must stop the conversion promptly, took {latency:?}"
    );
    assert!(
        !dir.join(format!("clip.{format}")).exists(),
        "partial output must be removed"
    );
    msgs
}

#[test]
fn quit_mid_conversion_cancels_promptly_and_cleans_up() {
    cancel_mid_conversion("cancel", CancelBy::Quit, None);
}

#[test]
fn quit_mid_webp_conversion_cancels_promptly_and_cleans_up() {
    cancel_mid_conversion_as("cancel-webp", CancelBy::Quit, None, "webp");
}

#[test]
fn stdin_eof_mid_conversion_cancels_like_quit() {
    cancel_mid_conversion("cancel-eof", CancelBy::Eof, None);
}

#[test]
fn a_bad_command_is_reported_and_never_fatal() {
    let msgs = cancel_mid_conversion("bad-command", CancelBy::Quit, Some(b"{\"type\":\"pause\"}\nnot json\n"));
    let errors: Vec<(&str, &str)> = msgs
        .iter()
        .filter_map(|m| match m {
            Msg::Error { code, message, fatal: false } => Some((code.as_str(), message.as_str())),
            _ => None,
        })
        .collect();
    assert_eq!(errors.len(), 2, "{msgs:?}");
    assert_eq!(errors[0], ("bad_command", "unknown command 'pause'"));
    assert_eq!(errors[1].0, "bad_command");
}
