using System;
using System.Text.Json.Serialization;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// One clip on the timeline: a half-open span
/// <c>[TimelineStartTicks, TimelineStartTicks + DurationTicks)</c> of the output, filled by
/// <see cref="Content"/> and placed on the canvas by <see cref="Transform"/>. A "cut" is not a
/// model concept — cutting a recording produces multiple items back-to-back, each with its own
/// <see cref="MediaContent.SourceInTicks"/>.
///
/// Mutate items through <see cref="TimelineOps"/>, not directly: that is the only place the
/// time-link semantics live. What an item moves with follows from its row
/// (<see cref="Track.GroupId"/>) and its span; an item carries no group state of its own.
/// </summary>
public sealed class Item
{
    public Guid Id { get; set; }

    public Guid TrackId { get; set; }

    /// <summary>Where the item begins on the output timeline, in 100ns ticks.</summary>
    public long TimelineStartTicks { get; set; }

    /// <summary>Length on the output timeline, in 100ns ticks. Always positive; editing
    /// operations keep it at or above <see cref="TimelineOps.MinSegmentTicks"/>.</summary>
    public long DurationTicks { get; set; }

    /// <summary>Exclusive end of the item on the output timeline.</summary>
    [JsonIgnore]
    public long TimelineEndTicks => TimelineStartTicks + DurationTicks;

    /// <summary>Whether the item plays at <paramref name="timelineTicks"/>. The span is half-open,
    /// so two back-to-back items never both cover the instant they meet at.</summary>
    public bool Covers(long timelineTicks) =>
        timelineTicks >= TimelineStartTicks && timelineTicks < TimelineEndTicks;

    public ItemContent Content { get; set; }

    public Transform Transform { get; set; } = new Transform();

    /// <summary>What is drawn AROUND the item's silhouette (shadow / glow / outline), or null for a
    /// bare item. Read by the picture draws and the themed cursor glyph only. Deliberately not
    /// called an effect: an effect is applied to the item's own pixels (<see cref="Effect"/>),
    /// which is a different thing on a different side of the silhouette — see
    /// <see cref="Prisc.VideoSDK.Model.Surround"/>.</summary>
    public Surround Surround { get; set; }

    /// <summary>What is applied to the item's OWN pixels (blur / background blur / background
    /// removal), or null for an untouched item — the slot the <see cref="Surround"/> remarks
    /// reserved. Read by the picture draws only; see
    /// <see cref="Prisc.VideoSDK.Model.VideoEffect"/>.</summary>
    public VideoEffect Effect { get; set; }

    /// <summary>Transition played over the first part of the item, or null for a hard start.</summary>
    public Transition Entry { get; set; }

    /// <summary>Transition played over the last part of the item, or null for a hard end.</summary>
    public Transition Exit { get; set; }

    /// <summary>Linear gain applied to the item's audio, 1.0 = unity.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>The link group a project saved before track groups carries on each item, under
    /// the name those files used. <see cref="ProjectMigration"/> reads it on load, turns it into
    /// <see cref="Track.GroupId"/> and nulls it, so it is never written back and is null everywhere
    /// past <see cref="Project.FromJson(string)"/>.</summary>
    [JsonPropertyName("LinkGroupId")]
    public Guid? LegacyLinkGroupId { get; set; }

    /// <summary>A deep copy under a new <see cref="Id"/>: the same row, span and look, sharing no
    /// object with this item. What a particular copy must not keep (a transition on the edge a cut
    /// created) is the caller's to clear.</summary>
    public Item Clone() => new Item
    {
        Id = Guid.NewGuid(),
        TrackId = TrackId,
        TimelineStartTicks = TimelineStartTicks,
        DurationTicks = DurationTicks,
        Content = Content?.Clone(),
        Transform = Transform?.Clone() ?? new Transform(),
        Surround = Surround?.Clone(),
        Effect = Effect?.Clone(),
        Entry = Entry?.Clone(),
        Exit = Exit?.Clone(),
        Volume = Volume,
    };
}
