//! Animated WebP encoding against libwebp's `WebPAnimEncoder` (statically
//! linked through libwebp-sys; the bundled FFmpeg has no libwebp encoder).
//! FFmpeg still does the decoding, resampling and scaling: this module only
//! sees the finished BGRA frames and their timestamps.
//!
//! The animation encoder buffers the encoded frames in memory and assembles
//! the whole file at the end, so the output is written in one go by
//! [`AnimEncoder::finish`].

use std::ffi::CStr;
use std::path::Path;
use std::ptr;

use anyhow::{bail, Context, Result};
use libwebp_sys as webp;

/// Per-frame encoder settings for one quality preset.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Settings {
    /// Lossy quality, 0-100.
    pub quality: f32,
    /// Speed/size trade-off, 0 (fast) to 6 (slowest, smallest).
    pub method: i32,
}

/// Owned `WebPAnimEncoder` plus the per-frame config it encodes with.
pub struct AnimEncoder {
    enc: *mut webp::WebPAnimEncoder,
    config: webp::WebPConfig,
    width: i32,
    height: i32,
}

impl Drop for AnimEncoder {
    fn drop(&mut self) {
        unsafe { webp::WebPAnimEncoderDelete(self.enc) }
    }
}

impl AnimEncoder {
    pub fn new(width: i32, height: i32, settings: Settings) -> Result<AnimEncoder> {
        unsafe {
            let mut config = std::mem::MaybeUninit::<webp::WebPConfig>::uninit();
            if webp::WebPConfigInitInternal(
                config.as_mut_ptr(),
                webp::WebPPreset::WEBP_PRESET_DEFAULT,
                settings.quality,
                webp::WEBP_ENCODER_ABI_VERSION as i32,
            ) == 0
            {
                bail!("libwebp version mismatch (encoder config)");
            }
            let mut config = config.assume_init();
            config.method = settings.method;
            config.thread_level = 1;
            if webp::WebPValidateConfig(&config) == 0 {
                bail!("invalid webp encoder settings: {settings:?}");
            }

            let mut options = std::mem::MaybeUninit::<webp::WebPAnimEncoderOptions>::uninit();
            if webp::WebPAnimEncoderOptionsInitInternal(
                options.as_mut_ptr(),
                webp::WEBP_MUX_ABI_VERSION as i32,
            ) == 0
            {
                bail!("libwebp version mismatch (animation options)");
            }
            let mut options = options.assume_init();
            options.anim_params.loop_count = 0; // loop forever, like the GIF output
            // Lets each frame pick lossless where it is smaller: flat UI and text in a
            // screen recording often are.
            options.allow_mixed = 1;

            let enc = webp::WebPAnimEncoderNewInternal(
                width,
                height,
                &options,
                webp::WEBP_MUX_ABI_VERSION as i32,
            );
            if enc.is_null() {
                bail!("could not create webp animation encoder ({width}x{height})");
            }
            Ok(AnimEncoder {
                enc,
                config,
                width,
                height,
            })
        }
    }

    /// Adds one frame of packed BGRA rows `stride` bytes apart, shown from
    /// `timestamp_ms` until the next frame's timestamp.
    pub fn add(&mut self, bgra: &[u8], stride: i32, timestamp_ms: i32) -> Result<()> {
        let needed = stride as usize * (self.height as usize - 1) + self.width as usize * 4;
        if stride < self.width * 4 || bgra.len() < needed {
            bail!("frame buffer too small for {}x{}", self.width, self.height);
        }
        unsafe {
            let mut pic = std::mem::MaybeUninit::<webp::WebPPicture>::uninit();
            if webp::WebPPictureInitInternal(pic.as_mut_ptr(), webp::WEBP_ENCODER_ABI_VERSION as i32) == 0 {
                bail!("libwebp version mismatch (picture)");
            }
            let mut pic = pic.assume_init();
            pic.width = self.width;
            pic.height = self.height;
            pic.use_argb = 1;
            if webp::WebPPictureImportBGRA(&mut pic, bgra.as_ptr(), stride) == 0 {
                webp::WebPPictureFree(&mut pic);
                bail!("could not import frame into webp picture");
            }
            let ok = webp::WebPAnimEncoderAdd(self.enc, &mut pic, timestamp_ms, &self.config);
            webp::WebPPictureFree(&mut pic);
            if ok == 0 {
                bail!("could not encode webp frame: {}", self.error());
            }
        }
        Ok(())
    }

    /// Closes the last frame at `end_ms`, assembles the animation and writes
    /// it to `output`.
    pub fn finish(self, end_ms: i32, output: &Path) -> Result<()> {
        unsafe {
            if webp::WebPAnimEncoderAdd(self.enc, ptr::null_mut(), end_ms, ptr::null()) == 0 {
                bail!("could not finish webp animation: {}", self.error());
            }
            let mut data = webp::WebPData::default();
            if webp::WebPAnimEncoderAssemble(self.enc, &mut data) == 0 {
                bail!("could not assemble webp animation: {}", self.error());
            }
            let bytes = std::slice::from_raw_parts(data.bytes, data.size);
            let written = std::fs::write(output, bytes);
            webp::WebPDataClear(&mut data);
            written.with_context(|| format!("could not write {}", output.display()))
        }
    }

    fn error(&self) -> String {
        unsafe {
            let msg = webp::WebPAnimEncoderGetError(self.enc);
            if msg.is_null() {
                "unknown error".to_owned()
            } else {
                CStr::from_ptr(msg).to_string_lossy().into_owned()
            }
        }
    }
}
