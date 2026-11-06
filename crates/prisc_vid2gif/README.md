# prisc_vid2gif

A companion CLI to [prisc_recorder](../prisc_recorder/README.md) (`prisc_vid2gif`, built from `crates/prisc_vid2gif`) that converts a recording (or any video FFmpeg can decode) into an optimized palette-based GIF or an animated WebP. It links the FFmpeg libraries already bundled next to `prisc_recorder` (`avformat`/`avcodec`/`avfilter`/`avutil`, via the bindgen-based `crates/prisc_ffmpeg_sys`) and runs the whole conversion in process: no subprocesses, no separate ffmpeg binary, on **every** supported platform. Bindings are generated from the obs-deps headers at build time, so an FFmpeg version bump that changes the ABI fails the build instead of the runtime. The bundled FFmpeg has no WebP encoder, so WebP frames go to libwebp's animation encoder, compiled from source and linked statically through the `libwebp-sys` crate. The test suite is self-contained (committed fixtures, generated raw-video inputs, and GIF and WebP byte-stream validators) and the release staging fails hard if `prisc_vid2gif` or its libraries are missing from the bundle.

```sh
prisc_vid2gif input.mkv                          # writes input.gif
prisc_vid2gif input.mkv out.webp                 # the extension picks the format
prisc_vid2gif input.mkv --format webp            # writes input.webp
prisc_vid2gif input.mp4 out.gif --quality best   # quality: best | good | fair (default good)
prisc_vid2gif input.mkv --max-width 480 --fps 12 # aspect-preserving clamps; never upscales
```

- `--format` is `gif` or `webp`. Without it the output's extension decides (`.webp` is WebP, anything else GIF), and without an output path it is GIF.
- `--quality` sets frame rate and fidelity. GIF: `best` (20 fps, sierra2_4a dithering), `good` (15 fps, bayer), `fair` (10 fps, coarse bayer). WebP: the same frame rates at libwebp quality 92, 85 and 75. On screen recordings each WebP preset is sharper, truer in color and 25% to 12x smaller than the GIF preset of the same name. `--fps` overrides the preset.
- `--max-width` / `--max-height` cap the output size like prisc_recorder's recording clamps: aspect preserved, the more restrictive wins, never upscales.
- It speaks the `job` catalog of Prisc's helper protocol, specified in [`crates/prisc_capture/HELPER_PROTOCOL.md`](../prisc_capture/HELPER_PROTOCOL.md) (§4.3): JSON lines on stdout, `hello`, monotonic `progress` lines, then `done` with the output's `path` and `bytes`, or a fatal `error` (exit 1; exit 2 for arguments that parsed but cannot be used).
- `{"type":"quit"}` on stdin, closing stdin, or a signal cancels the conversion: the in-flight pass stops within one packet, any partial output is removed, and prisc_vid2gif emits `done` with `result` `cancelled` and exits 0. Run by hand, keep stdin open (a terminal does).

A GIF is two in-process passes (fps/scale + `palettegen`, then `paletteuse` into the GIF encoder) with the palette kept in memory, no temp files. A WebP is one pass (fps/scale, then BGRA frames into libwebp), and the file is written in one go once the last frame is encoded. Progress derives from input frame timestamps, so it streams smoothly through every pass.

The bundled FFmpeg cannot decode an animated WebP, so a `.webp` is not a usable input, and Prisc's Recent page draws a WebP entry's tile with Skia's codec instead of FFmpeg.

## License

`GPL-3.0-or-later`, like the rest of Prisc; see [LICENSE](../../LICENSE).
