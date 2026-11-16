using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.VideoSDK.Composition;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// Brings a project saved before track groups to the current shape, in place, as it loads
/// (<see cref="Project.FromJson(string)"/>, so every load path, the renderer and undo restore all
/// go through it). The marker is any item still carrying <see cref="Item.LegacyLinkGroupId"/>: the
/// old format required one on every overlay and never dissolved it, and nothing writes one now,
/// so a project without one costs a single loop here. Overlay clocks come first, because the host
/// rule they reproduce needs the legacy ids. Must never throw: an exception out of a load costs the
/// user's edit, and the autosave would then overwrite the file.
/// </summary>
internal static class ProjectMigration
{
    /// <summary>Migrates <paramref name="project"/> if it needs it, and returns whether it
    /// did.</summary>
    public static bool Upgrade(Project project)
    {
        var items = project?.Items;
        if (items == null)
            return false;

        var legacy = false;
        foreach (var item in items)
            legacy |= item?.LegacyLinkGroupId != null;
        if (!legacy)
            return false;

        DeriveOverlayClocks(project);
        GroupTracksByLegacyId(project);

        foreach (var item in items)
        {
            if (item != null)
                item.LegacyLinkGroupId = null;
        }

        return true;
    }

    /// <summary>
    /// Gives each cursor and keys clip the clock it was showing: an old overlay had none and asked
    /// the composer for the screen clip at every instant. That clip was the active screen-stream
    /// clip of the source on a video row carrying the overlay's own legacy id, otherwise the first
    /// in canonical order. A stretch with none keeps the default clock, which for a keys clip is
    /// exactly what the composer fell back to, and for a cursor clip draws nothing as it always did.
    /// </summary>
    private static void DeriveOverlayClocks(Project project)
    {
        var rowRanks = new Dictionary<Guid, (int Order, Guid Id)>();
        foreach (var track in project.Tracks ?? new List<Track>())
        {
            if (track?.Kind == TrackKind.Video)
                rowRanks[track.Id] = (track.Order, track.Id);
        }

        // the order Project.Normalize would leave them in
        var clips = project.Items
            .Where(i => i?.Content is MediaContent && rowRanks.ContainsKey(i.TrackId))
            .OrderBy(i => rowRanks[i.TrackId])
            .ThenBy(i => i.TimelineStartTicks)
            .ThenBy(i => i.Id)
            .ToList();

        foreach (var overlay in project.Items.ToList())
        {
            if (overlay?.Content is not InputOverlayContent { SourceId: var sourceId })
                continue;

            var source = project.Sources?.FirstOrDefault(s => s?.Id == sourceId);
            if (source == null)
                continue;

            var hosts = clips.Where(c => FrameComposer.PlaysScreen(source, c)).ToList();

            OverlayClock.Rederive(project, overlay, overlay.TimelineStartTicks, overlay.TimelineEndTicks, t =>
            {
                var active = hosts.Where(h => h.Covers(t)).ToList();
                return active.FirstOrDefault(h => h.LegacyLinkGroupId != null
                    && h.LegacyLinkGroupId == overlay.LegacyLinkGroupId) ?? active.FirstOrDefault();
            });
        }
    }

    /// <summary>Rows that shared a legacy id, directly or through another row, are one group. A
    /// component of two or more rows takes its smallest legacy id, so the same file always
    /// migrates to the same ids. A row alone in its component has nothing to stay in step with and
    /// stays ungrouped. Items on rows the project does not have are skipped.</summary>
    private static void GroupTracksByLegacyId(Project project)
    {
        if (project.Tracks == null)
            return;

        var parents = new Dictionary<Guid, Guid>();
        foreach (var track in project.Tracks)
        {
            if (track != null)
                parents[track.Id] = track.Id;
        }

        Guid Find(Guid track)
        {
            while (parents[track] != track)
                track = parents[track] = parents[parents[track]];
            return track;
        }

        // each legacy id joins the rows it appears on
        var firstRow = new Dictionary<Guid, Guid>();
        foreach (var item in project.Items)
        {
            if (item?.LegacyLinkGroupId is not Guid legacy || !parents.ContainsKey(item.TrackId))
                continue;

            if (firstRow.TryGetValue(legacy, out var row))
                parents[Find(item.TrackId)] = Find(row);
            else
                firstRow[legacy] = item.TrackId;
        }

        var smallest = new Dictionary<Guid, Guid>();
        foreach (var (legacy, row) in firstRow)
        {
            var root = Find(row);
            if (!smallest.TryGetValue(root, out var current) || legacy.CompareTo(current) < 0)
                smallest[root] = legacy;
        }

        var rowCounts = new Dictionary<Guid, int>();
        foreach (var trackId in parents.Keys)
        {
            var root = Find(trackId);
            rowCounts[root] = rowCounts.GetValueOrDefault(root) + 1;
        }

        foreach (var track in project.Tracks)
        {
            if (track == null)
                continue;

            var root = Find(track.Id);
            if (rowCounts[root] > 1 && smallest.TryGetValue(root, out var group))
                track.GroupId = group;
        }
    }
}
