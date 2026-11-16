using System;

namespace Prisc.VideoSDK.Model;

public enum TrackKind
{
    Video,
    Audio,

    /// <summary>A row of effect items (<see cref="SpeedContent"/>/<see cref="ZoomContent"/>):
    /// never painted or mixed, it modulates how the rest of the timeline plays. Appended so the
    /// string-serialized enum stays wire-compatible.</summary>
    Effect,
}

/// <summary>One timeline row. Tracks carry presentation state only — the items on a track live in
/// the project's flat <see cref="Project.Items"/> list, keyed by <see cref="Item.TrackId"/>.
/// Video tracks composite bottom-up in ascending <see cref="Order"/> (higher order draws on top,
/// which is how the webcam PiP sits over the screen).</summary>
public sealed class Track
{
    public Guid Id { get; set; }

    public TrackKind Kind { get; set; }

    public string Name { get; set; }

    /// <summary>Stacking/row position. Ascending order composites later (on top) for video.</summary>
    public int Order { get; set; }

    /// <summary>The group this row belongs to, or null. A group is a tag shared by rows, with no
    /// other existence: dragging a clip on a grouped row brings the clips overlapping it on the
    /// group's rows (<see cref="TimelineOps.TimeLinked"/>). It only affects editing and nothing
    /// composes differently for it. A group with one row left holding clips dissolves
    /// (<see cref="TimelineOps.CollapseLoneGroups"/>).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>The row's clips cannot be moved, resized, rotated or cropped on the canvas
    /// (<see cref="Editing.EditorSession.EditPlacement"/> refuses). It says nothing about time:
    /// moves on the timeline, trims, splits and deletes all still apply, and so do undo and redo,
    /// which restore history. Offered on video rows, the only ones with a placement to protect.</summary>
    public bool PlacementLocked { get; set; }

    /// <summary>Video: excluded from the picture. Meaningless for audio (use <see cref="Muted"/>).</summary>
    public bool Hidden { get; set; }

    /// <summary>Audio: excluded from the mix. Meaningless for video (use <see cref="Hidden"/>).</summary>
    public bool Muted { get; set; }

    /// <summary>Audio: play the AI-denoised sidecar of the row's streams instead of the raw
    /// recording (see <c>Ai.DenoiseGenerator</c>). A missing or stale sidecar degrades to the raw
    /// audio rather than silencing the row. Meaningless for video.</summary>
    public bool Denoise { get; set; }

    /// <summary>Dry/wet mix for <see cref="Denoise"/>, validated to 0..1: 1 is the fully denoised
    /// sidecar, anything less blends the raw signal back in per sample. Read only while
    /// <see cref="Denoise"/> is on.</summary>
    public double DenoiseStrength { get; set; } = 1.0;

    /// <summary>The row's settings on a new row, named as a copy and in no group: a copy of a row
    /// is its own material, and a caller that wants the copies grouped says with what. Where it
    /// goes is <see cref="TimelineOps.InsertTrackAt"/>'s to say.</summary>
    public Track Clone() => new Track
    {
        Id = Guid.NewGuid(),
        Kind = Kind,
        Name = String.IsNullOrEmpty(Name) ? Name : Name + " copy",
        Order = Order,
        PlacementLocked = PlacementLocked,
        Hidden = Hidden,
        Muted = Muted,
        Denoise = Denoise,
        DenoiseStrength = DenoiseStrength,
    };
}
