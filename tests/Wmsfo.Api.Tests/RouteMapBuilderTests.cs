using System.Globalization;
using System.Text.Json;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// RouteMapBuilder (contracts 1.3 event.routeMap): determinism, the timed and
// untimed time models, the 5 minute timeline with its exact end, timeline
// positions on the drawn path, and the even vertex cap.
public class RouteMapBuilderTests
{
    private const double Lat0 = 46.87;
    private const double Lng0 = -114.0;
    private const double MetresPerDegreeLat = 6371008.8 * Math.PI / 180.0;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2025-12-22T01:00:00.000Z", CultureInfo.InvariantCulture);

    [Fact]
    public void Two_builds_from_the_same_recording_are_byte_identical()
    {
        var recording = CanonicalJson.SerializeToUtf8Bytes(new RouteObject { Name = "loop", Points = Spiral(3000, timed: true) });

        var first = BuildFromBytes(recording, RouteMapSettings.Defaults);
        byte[] second;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            second = BuildFromBytes(recording, RouteMapSettings.Defaults);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        Assert.Equal(first, second);
        Assert.Contains("\"timed\":true", System.Text.Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void Timed_timeline_hits_zero_every_five_minutes_and_the_exact_end()
    {
        // 62.5 minutes of recording: durationMinutes rounds up to 63.
        var points = Spiral(251, timed: true, secondsPerPoint: 15);
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.True(map.Timed);
        Assert.Equal(63, map.DurationMinutes);
        var expected = Enumerable.Range(0, 13).Select(i => i * 5).Append(63).ToArray();
        Assert.Equal(expected, map.Timeline.Select(t => t.Minutes).ToArray());

        Assert.Equal(map.Path[0].Lat, map.Timeline[0].Lat);
        Assert.Equal(map.Path[0].Lng, map.Timeline[0].Lng);
        Assert.Equal(map.Path[^1].Lat, map.Timeline[^1].Lat);
        Assert.Equal(map.Path[^1].Lng, map.Timeline[^1].Lng);
    }

    [Fact]
    public void Timed_duration_that_is_a_multiple_of_five_ends_once()
    {
        var points = Spiral(241, timed: true, secondsPerPoint: 15); // exactly 60 minutes
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.Equal(60, map.DurationMinutes);
        Assert.Equal(Enumerable.Range(0, 13).Select(i => i * 5).ToArray(), map.Timeline.Select(t => t.Minutes).ToArray());
    }

    [Fact]
    public void Short_timed_recording_spans_at_least_five_minutes()
    {
        var points = Spiral(9, timed: true, secondsPerPoint: 15); // 2 minutes
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.Equal(5, map.DurationMinutes);
        Assert.Equal(new[] { 0, 5 }, map.Timeline.Select(t => t.Minutes).ToArray());
    }

    [Fact]
    public void Position_halfway_in_time_between_two_anchors_sits_between_them_on_the_ground()
    {
        // Three anchors on a line heading north: A to B is 1 km in 10 minutes,
        // B to C is 4 km in 10 minutes. Minute 5 is halfway from A to B, not a
        // quarter of the whole distance.
        var a = Point(0, 0, 0);
        var b = Point(1000, 0, 10);
        var c = Point(5000, 0, 20);
        var map = RouteMapBuilder.Build(new List<RoutePoint> { a, b, c }, RouteMapSettings.Defaults)!;

        Assert.True(map.Timed);
        Assert.Equal(20, map.DurationMinutes);
        var five = map.Timeline.Single(t => t.Minutes == 5);
        var midAb = (Lat: (a.Lat + b.Lat) / 2, Lng: (a.Lng + b.Lng) / 2);
        Assert.True(Metres(five.Lat, five.Lng, midAb.Lat, midAb.Lng) < 5,
            $"minute 5 is {Metres(five.Lat, five.Lng, midAb.Lat, midAb.Lng):F1} m from the midpoint of A and B");
        Assert.True(five.Lat > a.Lat && five.Lat < b.Lat);

        var ten = map.Timeline.Single(t => t.Minutes == 10);
        Assert.True(Metres(ten.Lat, ten.Lng, b.Lat, b.Lng) < 5);
    }

    [Fact]
    public void A_stop_holds_the_position_while_time_passes()
    {
        // 10 minutes to B, 10 minutes stopped at B, 10 minutes on to C.
        var points = new List<RoutePoint>
        {
            Point(0, 0, 0),
            Point(2000, 0, 10),
            Point(2000, 0, 20),
            Point(4000, 0, 30),
        };
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        foreach (var minute in new[] { 10, 15, 20 })
        {
            var entry = map.Timeline.Single(t => t.Minutes == minute);
            Assert.True(Metres(entry.Lat, entry.Lng, points[1].Lat, points[1].Lng) < 5, $"minute {minute} left the stop");
        }
        var five = map.Timeline.Single(t => t.Minutes == 5);
        Assert.True(Metres(five.Lat, five.Lng, Lat0 + 1000 / MetresPerDegreeLat, Lng0) < 5);
    }

    [Fact]
    public void A_decreasing_anchor_is_treated_as_null()
    {
        var points = new List<RoutePoint>
        {
            Point(0, 0, 0),
            Point(1000, 0, 10),
            Point(2000, 0, 5),
            Point(3000, 0, 20),
        };
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.True(map.Timed);
        Assert.Equal(20, map.DurationMinutes);
        // Between B (minute 10) and D (minute 20) time follows distance, so
        // minute 15 sits at the dropped point's position.
        var fifteen = map.Timeline.Single(t => t.Minutes == 15);
        Assert.True(Metres(fifteen.Lat, fifteen.Lng, points[2].Lat, points[2].Lng) < 5);
    }

    [Fact]
    public void Untimed_recording_spans_the_default_duration_by_distance()
    {
        var points = Enumerable.Range(0, 11).Select(i => Point(i * 500, 0, null)).ToList();
        points[3].RecordedAt = Start; // one anchor alone still leaves the route untimed
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.False(map.Timed);
        Assert.Equal(120, map.DurationMinutes);
        Assert.Equal(Enumerable.Range(0, 25).Select(i => i * 5).ToArray(), map.Timeline.Select(t => t.Minutes).ToArray());
        var half = map.Timeline.Single(t => t.Minutes == 60);
        Assert.True(Metres(half.Lat, half.Lng, points[5].Lat, points[5].Lng) < 5);
        var quarter = map.Timeline.Single(t => t.Minutes == 30);
        var quarterNorth = (quarter.Lat - Lat0) * MetresPerDegreeLat;
        Assert.InRange(quarterNorth, 1245, 1255);

        var custom = RouteMapBuilder.Build(points, RouteMapSettings.Defaults with { DefaultDurationMinutes = 47 })!;
        Assert.Equal(47, custom.DurationMinutes);
        Assert.Equal(new[] { 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 47 }, custom.Timeline.Select(t => t.Minutes).ToArray());
    }

    [Fact]
    public void Every_timeline_position_lies_on_the_smoothed_path()
    {
        var map = RouteMapBuilder.Build(Spiral(2000, timed: true), RouteMapSettings.Defaults)!;
        Assert.True(map.Timeline.Count > 10);
        foreach (var entry in map.Timeline)
        {
            var nearest = DistanceToPath(map.Path, entry.Lat, entry.Lng);
            Assert.True(nearest < 2, $"minute {entry.Minutes} is {nearest:F2} m from the path");
        }
    }

    [Fact]
    public void Path_is_simplified_smoothed_and_rounded()
    {
        var points = Spiral(2000, timed: true);
        var map = RouteMapBuilder.Build(points, RouteMapSettings.Defaults)!;

        Assert.Equal(Math.Round(points[0].Lat, 6), map.Path[0].Lat);
        Assert.Equal(Math.Round(points[^1].Lng, 6), map.Path[^1].Lng);
        foreach (var p in map.Path)
        {
            Assert.Equal(Math.Round(p.Lat, 6), p.Lat);
            Assert.Equal(Math.Round(p.Lng, 6), p.Lng);
        }
        // Smoothing cuts corners: no interior vertex turns by more than 90 degrees.
        for (var i = 1; i + 1 < map.Path.Count; i++)
        {
            var (ax, ay) = Local(map.Path[i - 1].Lat, map.Path[i - 1].Lng);
            var (bx, by) = Local(map.Path[i].Lat, map.Path[i].Lng);
            var (cx, cy) = Local(map.Path[i + 1].Lat, map.Path[i + 1].Lng);
            var dot = (bx - ax) * (cx - bx) + (by - ay) * (cy - by);
            Assert.True(dot >= -1e-6, $"vertex {i} turns back on itself");
        }
    }

    [Fact]
    public void Cap_thins_evenly_and_keeps_both_ends()
    {
        var source = Enumerable.Range(0, 10000).ToList();
        var thinned = RouteMapBuilder.ThinEvenly(source, 100);
        Assert.Equal(100, thinned.Count);
        Assert.Equal(0, thinned[0]);
        Assert.Equal(9999, thinned[^1]);
        var gaps = thinned.Zip(thinned.Skip(1), (x, y) => y - x).ToList();
        Assert.True(gaps.Max() - gaps.Min() <= 1, $"gaps range from {gaps.Min()} to {gaps.Max()}");

        Assert.Equal(source.Take(50), RouteMapBuilder.ThinEvenly(source.Take(50).ToList(), 100));

        // A jagged recording smoothed well past the cap comes back at the cap
        // with the recording's own first and last point.
        var jagged = Enumerable.Range(0, 5000)
            .Select(i => Point(i * 20, i % 2 == 0 ? 0 : 200, null))
            .ToList();
        var map = RouteMapBuilder.Build(jagged, RouteMapSettings.Defaults with { MaxPoints = 100 })!;
        Assert.Equal(100, map.Path.Count);
        Assert.Equal(Math.Round(jagged[0].Lat, 6), map.Path[0].Lat);
        Assert.Equal(Math.Round(jagged[0].Lng, 6), map.Path[0].Lng);
        Assert.Equal(Math.Round(jagged[^1].Lat, 6), map.Path[^1].Lat);
        Assert.Equal(Math.Round(jagged[^1].Lng, 6), map.Path[^1].Lng);
    }

    [Fact]
    public void Serialized_shape_matches_contracts_1_3()
    {
        var map = RouteMapBuilder.Build(Spiral(50, timed: true), RouteMapSettings.Defaults)!;
        using var doc = JsonDocument.Parse(CanonicalJson.SerializeToUtf8Bytes(map));
        var root = doc.RootElement;
        Assert.Equal(new[] { "path", "timeline", "durationMinutes", "timed" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "lat", "lng" }, root.GetProperty("path")[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "minutes", "lat", "lng" }, root.GetProperty("timeline")[0].EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---------- helpers ----------

    private static byte[] BuildFromBytes(byte[] recording, RouteMapSettings settings)
    {
        var route = JsonSerializer.Deserialize<RouteObject>(recording, CanonicalJson.Options)!;
        return CanonicalJson.SerializeToUtf8Bytes(RouteMapBuilder.Build(route.Points, settings)!);
    }

    // A point `north` and `east` metres from the origin, at `minutes` after the
    // start (null leaves recordedAt null).
    private static RoutePoint Point(double north, double east, double? minutes) => new()
    {
        Lat = Lat0 + north / MetresPerDegreeLat,
        Lng = Lng0 + east / (MetresPerDegreeLat * Math.Cos(Lat0 * Math.PI / 180.0)),
        RecordedAt = minutes is double m ? Start.AddMinutes(m) : null,
    };

    // A widening spiral with a little GPS jitter, one point every
    // `secondsPerPoint` seconds when timed.
    private static List<RoutePoint> Spiral(int count, bool timed, int secondsPerPoint = 5)
    {
        var points = new List<RoutePoint>(count);
        for (var i = 0; i < count; i++)
        {
            var angle = i * 0.02;
            var radius = 500 + i * 3.0;
            var jitter = ((i * 7919) % 11 - 5) * 1.5;
            var p = Point(radius * Math.Sin(angle) + jitter, radius * Math.Cos(angle) - jitter, null);
            if (timed) p.RecordedAt = Start.AddSeconds(i * secondsPerPoint);
            points.Add(p);
        }
        return points;
    }

    private static (double X, double Y) Local(double lat, double lng) => (
        (lng - Lng0) * MetresPerDegreeLat * Math.Cos(Lat0 * Math.PI / 180.0),
        (lat - Lat0) * MetresPerDegreeLat);

    private static double Metres(double lat1, double lng1, double lat2, double lng2)
    {
        var (x1, y1) = Local(lat1, lng1);
        var (x2, y2) = Local(lat2, lng2);
        return Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
    }

    private static double DistanceToPath(IList<RouteMapPoint> path, double lat, double lng)
    {
        var (px, py) = Local(lat, lng);
        var best = double.MaxValue;
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (ax, ay) = Local(path[i].Lat, path[i].Lng);
            var (bx, by) = Local(path[i + 1].Lat, path[i + 1].Lng);
            var dx = bx - ax;
            var dy = by - ay;
            var len = dx * dx + dy * dy;
            var t = len == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len, 0, 1);
            var qx = ax + t * dx - px;
            var qy = ay + t * dy - py;
            best = Math.Min(best, Math.Sqrt(qx * qx + qy * qy));
        }
        return best;
    }
}
