namespace WorldGen.Core.Simulation;

/// <summary>
/// Bounds observer-facing history. Simulation state carries current facts;
/// old journal rows are provenance, not a second copy of the world.
/// </summary>
public static class WorldHistory
{
    public const int JournalRetentionDays = 3_600;
    public const int JournalSoftLimit = 4_096;
    public const int JournalTrimBatch = 512;
    public const int ObservationSoftLimitPerCity = 1_024;

    public static void Compact(WorldState world, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!force && world.Day % 30 != 0 && world.Journal.Count <= JournalSoftLimit + JournalTrimBatch) return;

        var cutoffDay = world.Day - JournalRetentionDays;
        var firstRecent = world.Journal.FindIndex(evt => evt.Day >= cutoffDay);
        if (firstRecent < 0) firstRecent = world.Journal.Count;
        var overLimit = Math.Max(0, world.Journal.Count - JournalSoftLimit);
        // Never discard a row that Information.Advance has not consumed yet.
        var removeCount = Math.Min(Math.Max(firstRecent, overLimit), world.Information.LastJournalIndex);
        if (removeCount > 0)
        {
            var archive = world.JournalArchive;
            foreach (var evt in world.Journal.Take(removeCount))
            {
                archive.RemovedEvents++;
                archive.FirstDay = archive.FirstDay is null ? evt.Day : Math.Min(archive.FirstDay.Value, evt.Day);
                archive.ThroughDay = archive.ThroughDay is null ? evt.Day : Math.Max(archive.ThroughDay.Value, evt.Day);
                archive.CountsByType[evt.Type] = archive.CountsByType.GetValueOrDefault(evt.Type) + 1;
            }
            world.Journal.RemoveRange(0, removeCount);
            // Information.Advance has already consumed this prefix. Keeping the
            // cursor relative to the shortened list makes restored worlds safe.
            world.Information.LastJournalIndex = Math.Max(0, world.Information.LastJournalIndex - removeCount);
        }

        CompactObservations(world, cutoffDay);
    }

    private static void CompactObservations(WorldState world, int cutoffDay)
    {
        // A journal row and a city's awareness of that row are separate data.
        // Only reports still in transit must pin an observation; otherwise a
        // noisy local settlement would make the observation cap meaningless.
        var retainedEvents = world.Information.Reports.Select(report => report.EventId).ToHashSet(StringComparer.Ordinal);

        foreach (var city in world.Cities.Values)
        {
            var observations = city.KnowledgeState.Observations;
            var removable = observations.Values
                .Where(item => !retainedEvents.Contains(item.EventId))
                .OrderBy(item => item.ReceivedDay)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToArray();
            var excess = Math.Max(0, observations.Count - ObservationSoftLimitPerCity);
            var remove = removable.Where(item => item.ReceivedDay < cutoffDay).Select(item => item.EventId)
                .Concat(removable.Take(excess).Select(item => item.EventId))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (var id in remove) observations.Remove(id);
        }
    }
}
