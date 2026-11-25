using Google.Protobuf;
using SeqTransit.Core;
using SeqTransit.Core.Gtfs;
using SeqTransit.Core.Ingestion;
using StopRelationship = TransitRealtime.TripUpdate.Types.StopTimeUpdate.Types.ScheduleRelationship;

namespace SeqTransit.Local;

/// <summary>Reads every raw snapshot in a Brisbane calendar day, without modifying the database.</summary>
internal static class FeedAudit
{
    public static async Task<int> WriteAsync(string directory, DateOnly date, CancellationToken cancellationToken)
    {
        var snapshotPath = Path.Combine(directory, SnapshotKeys.Prefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(snapshotPath))
        {
            Console.Error.WriteLine($"Snapshot directory not found: {snapshotPath}");
            return 1;
        }

        var files = Directory.EnumerateFiles(snapshotPath, "*.pb")
            .Select(path => (Path: path, Key: SnapshotKeys.Prefix + Path.GetFileName(path)))
            .Where(file => SnapshotKeys.TryParse(file.Key, out var timestamp) && BrisbaneTime.DateOf(timestamp) == date)
            .OrderBy(file => file.Key, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No snapshots for {date:yyyy-MM-dd}.");
            return 1;
        }

        var hours = new int[24];
        var relationships = new SortedDictionary<string, long>();
        var cancellationSightings = new Dictionary<string, (int Count, DateTimeOffset LastCancelled, DateTimeOffset? LaterRunning)>();
        long bytes = 0, trips = 0, stops = 0, missingDate = 0, missingSequence = 0, missingVehicle = 0;
        long skipped = 0, noData = 0, allSkipped = 0, timeWithoutDelay = 0, extracted = 0;
        var timestamps = new List<DateTimeOffset>();
        var corrupt = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await File.ReadAllBytesAsync(file.Path, cancellationToken);
            bytes += payload.Length;
            try
            {
                var feed = TripUpdateFeedParser.Parse(payload);
                if (feed.Header is not { HasTimestamp: true })
                {
                    corrupt++;
                    continue;
                }
                var timestamp = TripUpdateFeedParser.FeedTimestamp(feed, DateTimeOffset.MinValue);
                if (!SnapshotKeys.TryParse(file.Key, out var keyTimestamp) || timestamp != keyTimestamp)
                {
                    corrupt++;
                    continue;
                }
                timestamps.Add(timestamp);
                hours[timestamp.ToOffset(TimeSpan.FromHours(10)).Hour]++;
                extracted += TripUpdateFeedParser.Extract(feed, timestamp).Observations.Count;
                foreach (var entity in feed.Entity)
                {
                    if (entity.IsDeleted || entity.TripUpdate is not { Trip: { } trip } update) continue;
                    trips++;
                    var relationship = trip.ScheduleRelationship.ToString();
                    relationships[relationship] = relationships.GetValueOrDefault(relationship) + 1;
                    var tripKey = $"{trip.StartDate}/{trip.TripId}";
                    if (trip.ScheduleRelationship is TransitRealtime.TripDescriptor.Types.ScheduleRelationship.Canceled
                        or TransitRealtime.TripDescriptor.Types.ScheduleRelationship.Deleted)
                    {
                        var old = cancellationSightings.GetValueOrDefault(tripKey);
                        cancellationSightings[tripKey] = (old.Count + 1, timestamp, null);
                    }
                    else if (cancellationSightings.TryGetValue(tripKey, out var cancelled) && timestamp > cancelled.LastCancelled)
                    {
                        cancellationSightings[tripKey] = cancelled with { LaterRunning = timestamp };
                    }
                    if (!DateOnly.TryParseExact(trip.StartDate, "yyyyMMdd", out _)) missingDate++;
                    if (update.Vehicle is not { HasId: true, Id.Length: > 0 }) missingVehicle++;
                    if (update.StopTimeUpdate.Count > 0 &&
                        update.StopTimeUpdate.All(s => s.ScheduleRelationship == StopRelationship.Skipped)) allSkipped++;
                    foreach (var stop in update.StopTimeUpdate)
                    {
                        stops++;
                        if (!stop.HasStopSequence) missingSequence++;
                        if (stop.ScheduleRelationship == StopRelationship.Skipped) skipped++;
                        if (stop.ScheduleRelationship == StopRelationship.NoData) noData++;
                        if ((stop.Arrival is { HasTime: true } || stop.Departure is { HasTime: true }) &&
                            stop.Arrival is not { HasDelay: true } && stop.Departure is not { HasDelay: true }) timeWithoutDelay++;
                    }
                }
            }
            catch (InvalidProtocolBufferException)
            {
                corrupt++;
            }
        }

        Console.WriteLine($"# Raw feed audit: {date:yyyy-MM-dd} (Australia/Brisbane)");
        Console.WriteLine("Counts below are sightings across snapshots, not distinct trips or served stops.");
        Console.WriteLine($"Files: {files.Count}; invalid protobuf/header/key: {corrupt}");
        Console.WriteLine($"Total bytes: {bytes}; average bytes/file: {bytes / files.Count}");
        if (timestamps.Count > 0)
        {
            var first = timestamps.Min();
            var last = timestamps.Max();
            var gaps = timestamps.Order().Zip(timestamps.Order().Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(10));
            var boundaryGap = Math.Max((first - start).TotalSeconds, (start.AddDays(1) - last).TotalSeconds);
            var maxGap = Math.Max(boundaryGap, gaps.DefaultIfEmpty(86400).Max());
            Console.WriteLine($"First: {first.ToOffset(TimeSpan.FromHours(10)):O}; last: {last.ToOffset(TimeSpan.FromHours(10)):O}");
            Console.WriteLine($"Maximum gap including midnight boundaries: {maxGap:F0}s; mean internal gap: {gaps.DefaultIfEmpty(0).Average():F1}s");
            Console.WriteLine($"Full-day coverage (all hours, gaps <= 120s, no corrupt files): {hours.All(h => h > 0) && maxGap <= 120 && corrupt == 0}");
        }
        Console.WriteLine($"Trip sightings: {trips}; missing/invalid start_date: {missingDate}; missing vehicle_id: {missingVehicle}");
        Console.WriteLine($"Stop sightings: {stops}; missing stop_sequence: {missingSequence}; SKIPPED: {skipped}; NO_DATA: {noData}");
        Console.WriteLine($"Trips with every reported stop SKIPPED: {allSkipped}; stops with time but no delay: {timeWithoutDelay}");
        Console.WriteLine($"Extracted observations before folding: {extracted}");
        foreach (var (relationship, count) in relationships) Console.WriteLine($"{relationship}: {count}");
        foreach (var (tripKey, cancellation) in cancellationSightings)
            Console.WriteLine($"Cancellation {tripKey}: {cancellation.Count} sightings; last cancelled {cancellation.LastCancelled:O}; later running {cancellation.LaterRunning:O}");
        Console.WriteLine("Hour | snapshot files");
        for (var hour = 0; hour < 24; hour++) Console.WriteLine($"{hour:00} | {hours[hour]}");
        return corrupt == 0 ? 0 : 1;
    }
}
