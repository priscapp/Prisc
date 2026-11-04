#!/usr/bin/env bash
# Prints a content fingerprint (40 hex digits) of everything one CI component's
# result depends on, so a job can reuse an earlier run's result for the same
# inputs instead of redoing it: a cached recorder payload, or a marker that the
# Rust gate already passed. ci.yml's plan job runs this once per component and
# hands the values to the jobs, which add their own toolchain to the cache keys.
#
#   tools/ci/fingerprint.sh recorder          the recorder stack (build-recorder*.yml)
#   tools/ci/fingerprint.sh recorder-natives  what decides the payload's FFmpeg/libobs
#                                             natives and layout (dotnet-test's fallback)
#   tools/ci/fingerprint.sh rust-test         ci.yml's rust-test gate
#
# It hashes `git ls-files -s` (mode, blob id and path of each file, the pinned
# commit of a submodule), so it reads the index, never the working tree, and is
# the same on every OS and line-ending setting. Including too much only costs a
# rebuild; leaving an input out would reuse a stale result, so when in doubt, add
# the path.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# Workspace crates the given packages reach through path dependencies (normal,
# build and dev), the packages included, as repo-relative directories.
crate_closure() {
  cargo metadata --no-deps --format-version 1 | jq -r --args '
    (.workspace_root | length) as $root
    | (.packages | map({key: .name, value: .}) | from_entries) as $pkgs
    | def closure($todo; $seen):
        if ($todo | length) == 0 then $seen
        elif any($seen[]; . == $todo[0]) then closure($todo[1:]; $seen)
        elif $pkgs[$todo[0]] == null then error("\($todo[0]) is not a workspace member")
        else closure($todo[1:] + [$pkgs[$todo[0]].dependencies[] | select(.path) | .name]; $seen + [$todo[0]])
        end;
      closure($ARGS.positional; [])[]
    | $pkgs[.].manifest_path[$root + 1:] | gsub("\\\\"; "/") | sub("/Cargo.toml$"; "")
  ' "$@" | tr -d '\r' # jq on Windows ends its lines with CRLF
}

# The FFmpeg/libobs natives (the obs-studio pin and the crates that build and
# fetch them) and the scripts and workflow that stage them into the payload.
recorder_natives() {
  git ls-files -s -- \
    crates/obs-studio crates/prisc_obs_sys crates/prisc_build_support crates/prisc_ffmpeg_sys \
    tools/staging/stage-recorder.sh tools/staging/shipped.sh \
    .github/workflows/build-recorder.yml .github/actions/setup-obs-build
}

case "${1:-}" in
  recorder)
    mapfile -t crates < <(crate_closure prisc_recorder prisc_share_region prisc_vid2gif)
    {
      recorder_natives
      git ls-files -s -- "${crates[@]}" \
        Cargo.toml Cargo.lock .cargo tools/staging tools/linux-build tools/ci/fingerprint.sh \
        .github/workflows/build-recorder-linux.yml
    } | git hash-object --stdin
    ;;
  recorder-natives)
    recorder_natives | git hash-object --stdin
    ;;
  rust-test)
    # Everything outside ui/, plus the ui/ files the Rust tests read: prisc_codegen
    # checks every generated *.g.cs (and lints ui/Prisc.Interop), and
    # prisc_interop's tests read the ABI hash it generates.
    {
      git ls-files -s -- ':(exclude)ui'
      git ls-files -s -- ui/Prisc.Interop ':(glob)ui/**/*.g.cs'
    } | git hash-object --stdin
    ;;
  *)
    echo "usage: $0 recorder|recorder-natives|rust-test" >&2
    exit 2
    ;;
esac
