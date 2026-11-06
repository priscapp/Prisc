//! Pure logic shared by Prisc's Rust binaries (the capture overlay
//! `prisc_capture`, the scrolling-capture driver `prisc_scroll_driver`, the AI
//! inference binary `prisc_ai` and the recorder stack) and by Prisc.Ui and
//! Prisc.VideoRender through `prisc_interop`.
//!
//! The bar for living here is that **two processes must agree**, so a change
//! made in one place and not the other would be a bug:
//!
//! - [`geometry`] — the virtual-desktop coordinate space every capture
//!   coordinate, CLI flag and Win32 call is expressed in.
//! - [`session`] — the `session.json` contract, which `Prisc.Ui` reads
//!   (`SessionInfo`) and both binaries write.
//! - [`ocr`] — the recognition request/response contract the overlay and
//!   `prisc_ai ocr` speak across their process boundary.
//! - [`color`], [`share_region`], [`encoding`]: pure logic that a Rust
//!   helper and Prisc.Ui (through prisc_interop) must compute identically.
//! - [`contract`]: typed constant tables both sides read; prisc_codegen
//!   renders them into C# (`cargo codegen`).
//!
//! Anything that only one binary needs stays in that binary. What the binaries
//! share but that reaches the OS or Sentry has a crate of its own: `prisc_desktop`
//! and `prisc_telemetry`.

pub mod color;
pub mod contract;
pub mod encoding;
pub mod geometry;
pub mod ocr;
pub mod session;
pub mod share_region;
