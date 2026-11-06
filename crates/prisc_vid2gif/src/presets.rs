//! Output formats, quality presets, output sizing, and the filter-graph
//! strings for the conversion passes. The graph strings use the same syntax as
//! the ffmpeg CLI: `avfilter_graph_parse_ptr` accepts them verbatim.

use std::path::Path;

use clap::ValueEnum;

use crate::webp;

#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum Format {
    /// Palette-based GIF: plays everywhere.
    Gif,
    /// Animated WebP: full color and much smaller than GIF at every preset.
    Webp,
}

impl Format {
    /// The format an output path asks for by its extension: `.webp` is WebP,
    /// anything else (or nothing) is GIF.
    pub fn from_path(path: &Path) -> Format {
        match path.extension() {
            Some(ext) if ext.eq_ignore_ascii_case("webp") => Format::Webp,
            _ => Format::Gif,
        }
    }

    pub fn extension(self) -> &'static str {
        match self {
            Format::Gif => "gif",
            Format::Webp => "webp",
        }
    }
}

/// One preset per level, per format. A WebP preset is higher quality and
/// smaller than the GIF preset of the same name.
#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum Quality {
    /// GIF: 20 fps, error-diffusion dithering. WebP: 20 fps, quality 92.
    Best,
    /// GIF: 15 fps, fine ordered dithering. WebP: 15 fps, quality 85 (default).
    Good,
    /// GIF: 10 fps, coarse ordered dithering. WebP: 10 fps, quality 75.
    Fair,
}

impl Quality {
    /// Output frame rate when `--fps` is not given.
    pub fn fps(self) -> u32 {
        match self {
            Quality::Best => 20,
            Quality::Good => 15,
            Quality::Fair => 10,
        }
    }

    /// libwebp settings for the WebP output. Benchmarked on screen recordings
    /// against the GIF preset of the same name: every level came out sharper,
    /// truer in color, and 25% to 12x smaller. Method 4 is libwebp's default;
    /// 6 took 25x as long for 1% less.
    pub fn webp(self) -> webp::Settings {
        let quality = match self {
            Quality::Best => 92.0,
            Quality::Good => 85.0,
            Quality::Fair => 75.0,
        };
        webp::Settings { quality, method: 4 }
    }

    /// `paletteuse` dithering — with fps, the main quality/size lever.
    fn dither(self) -> &'static str {
        match self {
            Quality::Best => "sierra2_4a",
            Quality::Good => "bayer:bayer_scale=3",
            Quality::Fair => "bayer:bayer_scale=5",
        }
    }
}

/// Resolves the `--max-width`/`--max-height` clamps against the source size:
/// aspect is preserved, the more restrictive clamp wins, and the result is
/// `None` when no downscale is needed (never upscales — same contract as
/// prisc_recorder's recording resolution cap). The width is floored to even,
/// which keeps output dimensions stable and predictable.
pub fn clamp_width(src_w: u32, src_h: u32, max_w: Option<u32>, max_h: Option<u32>) -> Option<u32> {
    let fw = max_w.map(|m| m as f64 / src_w as f64);
    let fh = max_h.map(|m| m as f64 / src_h as f64);
    let f = match (fw, fh) {
        (Some(a), Some(b)) => a.min(b),
        (Some(a), None) => a,
        (None, Some(b)) => b,
        (None, None) => return None,
    };
    if f >= 1.0 {
        return None;
    }
    let w = (src_w as f64 * f) as u32 & !1;
    Some(w.max(2))
}

/// The shared fps/scale front of both passes.
fn chain(fps: u32, scale_width: Option<u32>) -> String {
    match scale_width {
        Some(w) => format!("fps={fps},scale={w}:-1:flags=lanczos"),
        None => format!("fps={fps}"),
    }
}

/// Pass 1 (single input → single output, default `in`/`out` labels): resample
/// and build one global 256-color palette. `stats_mode=diff` weights pixels
/// that change between frames, which favors the moving content of a screen
/// recording over its static background.
pub fn pass1_graph(fps: u32, scale_width: Option<u32>) -> String {
    format!("{},palettegen=stats_mode=diff", chain(fps, scale_width))
}

/// Pass 2 (video + palette inputs, labeled `vid`/`pal` → `out`): resample and
/// map frames onto the palette. `diff_mode=rectangle` re-dithers only the
/// changed region of each frame, which both shrinks the file and avoids
/// shimmering in static areas.
pub fn pass2_graph(fps: u32, scale_width: Option<u32>, quality: Quality) -> String {
    format!(
        "[vid]{}[x];[x][pal]paletteuse=dither={}:diff_mode=rectangle[out]",
        chain(fps, scale_width),
        quality.dither()
    )
}

/// The single WebP pass (default `in`/`out` labels): resample, then hand
/// libwebp the packed BGRA it imports directly.
pub fn webp_graph(fps: u32, scale_width: Option<u32>) -> String {
    format!("{},format=bgra", chain(fps, scale_width))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn format_from_path_extension() {
        assert_eq!(Format::from_path(Path::new("a/out.webp")), Format::Webp);
        assert_eq!(Format::from_path(Path::new("OUT.WEBP")), Format::Webp);
        assert_eq!(Format::from_path(Path::new("out.gif")), Format::Gif);
        assert_eq!(Format::from_path(Path::new("out")), Format::Gif);
    }

    #[test]
    fn webp_graph_with_and_without_scale() {
        assert_eq!(webp_graph(15, None), "fps=15,format=bgra");
        assert_eq!(webp_graph(20, Some(480)), "fps=20,scale=480:-1:flags=lanczos,format=bgra");
    }

    #[test]
    fn webp_quality_ordering() {
        assert!(Quality::Best.webp().quality > Quality::Good.webp().quality);
        assert!(Quality::Good.webp().quality > Quality::Fair.webp().quality);
    }

    #[test]
    fn preset_fps_ordering() {
        assert!(Quality::Best.fps() > Quality::Good.fps());
        assert!(Quality::Good.fps() > Quality::Fair.fps());
    }

    #[test]
    fn pass1_graph_without_scale() {
        assert_eq!(pass1_graph(15, None), "fps=15,palettegen=stats_mode=diff");
    }

    #[test]
    fn pass1_graph_with_scale() {
        assert_eq!(
            pass1_graph(20, Some(480)),
            "fps=20,scale=480:-1:flags=lanczos,palettegen=stats_mode=diff"
        );
    }

    #[test]
    fn pass2_graph_per_quality() {
        assert_eq!(
            pass2_graph(15, None, Quality::Good),
            "[vid]fps=15[x];[x][pal]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle[out]"
        );
        assert_eq!(
            pass2_graph(20, Some(480), Quality::Best),
            "[vid]fps=20,scale=480:-1:flags=lanczos[x];[x][pal]paletteuse=dither=sierra2_4a:diff_mode=rectangle[out]"
        );
        assert!(pass2_graph(10, None, Quality::Fair).contains("bayer_scale=5"));
    }

    #[test]
    fn clamp_width_none_when_unconstrained() {
        assert_eq!(clamp_width(1920, 1080, None, None), None);
    }

    #[test]
    fn clamp_width_never_upscales() {
        assert_eq!(clamp_width(320, 240, Some(5000), None), None);
        assert_eq!(clamp_width(320, 240, None, Some(240)), None);
        assert_eq!(clamp_width(320, 240, Some(320), Some(9999)), None);
    }

    #[test]
    fn clamp_width_by_width() {
        assert_eq!(clamp_width(320, 240, Some(120), None), Some(120));
    }

    #[test]
    fn clamp_width_by_height() {
        // 240 -> 60 is a factor of 0.25: width 320 -> 80.
        assert_eq!(clamp_width(320, 240, None, Some(60)), Some(80));
    }

    #[test]
    fn clamp_width_takes_more_restrictive() {
        // height clamp (0.25) beats width clamp (0.375)
        assert_eq!(clamp_width(320, 240, Some(120), Some(60)), Some(80));
        // width clamp beats height clamp when it is tighter
        assert_eq!(clamp_width(320, 240, Some(80), Some(120)), Some(80));
    }

    #[test]
    fn clamp_width_is_even_and_at_least_two() {
        // 1920 * (500/1080) = 888.9 -> floored to even 888
        assert_eq!(clamp_width(1920, 1080, None, Some(500)), Some(888));
        assert_eq!(clamp_width(100, 100, Some(1), None), Some(2));
    }
}
