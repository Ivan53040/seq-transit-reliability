using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SeqTransit.Data;

/// <summary>
/// EF Core mapping onto the tables created by db/migrations. Used for reads; bulk writes go
/// through <see cref="ObservationWriter"/>, and the SQL files (not EF) own the schema.
/// </summary>
public sealed class TransitDbContext(DbContextOptions<TransitDbContext> options) : DbContext(options)
{
    public DbSet<FeedSnapshotRow> FeedSnapshots => Set<FeedSnapshotRow>();

    public DbSet<StopDelayObservationRow> StopDelayObservations => Set<StopDelayObservationRow>();

    public DbSet<TripCancellationRow> TripCancellations => Set<TripCancellationRow>();
}

[Table("feed_snapshot")]
[PrimaryKey(nameof(FeedTimestamp))]
public sealed class FeedSnapshotRow
{
    [Column("feed_timestamp")] public DateTimeOffset FeedTimestamp { get; set; }
    [Column("s3_key")] public string S3Key { get; set; } = "";
    [Column("loaded_at")] public DateTimeOffset LoadedAt { get; set; }
    [Column("trip_update_count")] public int TripUpdateCount { get; set; }
    [Column("stop_update_count")] public int StopUpdateCount { get; set; }
    [Column("cancelled_trip_count")] public int CancelledTripCount { get; set; }
}

[Table("stop_delay_observation")]
[PrimaryKey(nameof(ServiceDate), nameof(TripId), nameof(StopSequence))]
public sealed class StopDelayObservationRow
{
    [Column("service_date")] public DateOnly ServiceDate { get; set; }
    [Column("trip_id")] public string TripId { get; set; } = "";
    [Column("stop_sequence")] public int StopSequence { get; set; }
    [Column("route_id")] public string RouteId { get; set; } = "";
    [Column("stop_id")] public string StopId { get; set; } = "";
    [Column("vehicle_id")] public string? VehicleId { get; set; }
    [Column("arrival_delay_s")] public int? ArrivalDelaySeconds { get; set; }
    [Column("departure_delay_s")] public int? DepartureDelaySeconds { get; set; }
    [Column("event_time")] public DateTimeOffset? EventTime { get; set; }
    [Column("first_seen_at")] public DateTimeOffset FirstSeenAt { get; set; }
    [Column("last_feed_at")] public DateTimeOffset LastFeedAt { get; set; }
}

[Table("trip_cancellation")]
[PrimaryKey(nameof(ServiceDate), nameof(TripId))]
public sealed class TripCancellationRow
{
    [Column("service_date")] public DateOnly ServiceDate { get; set; }
    [Column("trip_id")] public string TripId { get; set; } = "";
    [Column("route_id")] public string RouteId { get; set; } = "";
    [Column("first_seen_at")] public DateTimeOffset FirstSeenAt { get; set; }
    [Column("last_feed_at")] public DateTimeOffset LastFeedAt { get; set; }
}
