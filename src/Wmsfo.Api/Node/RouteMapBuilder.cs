using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Node;

// The three route map settings (contracts 6) with their defaults.
public sealed record RouteMapSettings(int SimplifyToleranceM, int MaxPoints, int DefaultDurationMinutes)
{
    public const string SimplifyToleranceKey = "route_map_simplify_tolerance_m";
    public const string MaxPointsKey = "route_map_max_points";
    public const string DefaultDurationKey = "route_map_default_duration_minutes";

    public const int DefaultSimplifyToleranceM = 30;
    public const int DefaultMaxPoints = 1200;
    public const int DefaultDefaultDurationMinutes = 120;

    public static readonly RouteMapSettings Defaults =
        new(DefaultSimplifyToleranceM, DefaultMaxPoints, DefaultDefaultDurationMinutes);

    // Reads the three keys from `app_setting`; a missing row or a non-integer
    // value keeps the default.
    public static async Task<RouteMapSettings> ReadAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var tolerance = DefaultSimplifyToleranceM;
        var maxPoints = DefaultMaxPoints;
        var duration = DefaultDefaultDurationMinutes;
        await using var cmd = new NpgsqlCommand(
            "select key, value from app_setting where key = any($1);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = new[] { SimplifyToleranceKey, MaxPointsKey, DefaultDurationKey },
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            using var doc = JsonDocument.Parse(reader.GetString(1));
            if (doc.RootElement.ValueKind != JsonValueKind.Number || !doc.RootElement.TryGetInt32(out var parsed))
                continue;
            switch (key)
            {
                case SimplifyToleranceKey: tolerance = parsed; break;
                case MaxPointsKey:         maxPoints = parsed; break;
                case DefaultDurationKey:   duration = parsed; break;
            }
        }
        return new RouteMapSettings(tolerance, maxPoints, duration);
    }
}

// Builds the RouteMap of contracts 1.3 from a recording's points (the route
// object of contracts 1.4, the same source as event.flightHistory):
//   1. Douglas-Peucker simplification at the tolerance in metres.
//   2. Chaikin corner cutting, two passes, keeping the first and last point.
//   3. Even thinning to the vertex cap, keeping the first and last point.
//   4. Coordinates rounded to 6 decimals.
// Every raw point has a distance along the recording and a time in minutes
// from the start. The simplified vertices keep the distances of the raw
// points they came from, and Chaikin mixes distances with the same weights as
// positions, so every drawn vertex has a nondecreasing distance along the
// recording. A timeline minute becomes a distance through the raw points' time
// model, and the position is read off the drawn path at that distance, so
// smoothing never shifts when the route passes an anchor. The result depends
// only on the points and the settings, so two builds produce the same bytes.
public static class RouteMapBuilder
{
    public const int TimelineStepMinutes = 5;
    public const int MinDurationMinutes = 5;
    private const int SmoothingPasses = 2;
    private const int CoordinateDecimals = 6;
    private const double EarthRadiusM = 6371008.8;

    public static RouteMap? Build(IList<RoutePoint> points, RouteMapSettings settings)
    {
        var n = points.Count;
        if (n == 0) return null;

        var (xs, ys) = Project(points);
        var cumulative = new double[n];
        for (var i = 1; i < n; i++)
            cumulative[i] = cumulative[i - 1] + Distance(xs[i - 1], ys[i - 1], xs[i], ys[i]);

        var (times, durationExact, timed) = TimeModel(points, cumulative, settings.DefaultDurationMinutes);
        var durationMinutes = timed
            ? Math.Max(MinDurationMinutes, (int)Math.Ceiling(durationExact))
            : Math.Max(MinDurationMinutes, settings.DefaultDurationMinutes);

        var kept = Simplify(xs, ys, cumulative, Math.Max(0, settings.SimplifyToleranceM));
        var vertices = new List<Vertex>(kept.Count);
        foreach (var i in kept) vertices.Add(new Vertex(points[i].Lat, points[i].Lng, cumulative[i]));

        for (var pass = 0; pass < SmoothingPasses; pass++) vertices = Chaikin(vertices);
        vertices = ThinEvenly(vertices, settings.MaxPoints);

        var path = new List<RouteMapPoint>(vertices.Count);
        var rounded = new List<Vertex>(vertices.Count);
        foreach (var v in vertices)
        {
            var lat = Round(v.Lat);
            var lng = Round(v.Lng);
            path.Add(new RouteMapPoint { Lat = lat, Lng = lng });
            rounded.Add(new Vertex(lat, lng, v.Along));
        }

        var timeline = new List<RouteMapTimelineEntry>();
        for (var m = 0; m < durationMinutes; m += TimelineStepMinutes)
            timeline.Add(TimelineEntry(rounded, m, AlongAt(times, cumulative, m)));
        var end = rounded[^1];
        timeline.Add(new RouteMapTimelineEntry { Minutes = durationMinutes, Lat = end.Lat, Lng = end.Lng });

        return new RouteMap
        {
            Path = path,
            Timeline = timeline,
            DurationMinutes = durationMinutes,
            Timed = timed,
        };
    }

    // A drawn vertex and its distance in metres along the raw recording.
    private readonly record struct Vertex(double Lat, double Lng, double Along);

    // Local equirectangular projection in metres around the first point; the
    // routes cover a region, so the distortion is well under the tolerance.
    private static (double[] Xs, double[] Ys) Project(IList<RoutePoint> points)
    {
        var n = points.Count;
        var xs = new double[n];
        var ys = new double[n];
        var lat0 = points[0].Lat;
        var lng0 = points[0].Lng;
        var metresPerDegree = EarthRadiusM * Math.PI / 180.0;
        var cosLat0 = Math.Cos(lat0 * Math.PI / 180.0);
        for (var i = 0; i < n; i++)
        {
            xs[i] = (points[i].Lng - lng0) * metresPerDegree * cosLat0;
            ys[i] = (points[i].Lat - lat0) * metresPerDegree;
        }
        return (xs, ys);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // The time of every raw point in minutes from the start. Anchors are the
    // points whose recordedAt is set and not earlier than the previous anchor.
    // With two or more anchors the route is timed: time is piecewise linear in
    // cumulative distance between the surrounding anchors (by index when the
    // anchors share a position), points before the first anchor sit at 0 and
    // points after the last at the duration. With fewer the route is untimed
    // and time is proportional to cumulative distance over the default duration
    // (by index when the route has no length).
    private static (double[] Times, double Duration, bool Timed) TimeModel(
        IList<RoutePoint> points, double[] cumulative, int defaultDurationMinutes)
    {
        var n = points.Count;
        var times = new double[n];
        var anchors = new List<(int Index, long Ticks)>();
        for (var i = 0; i < n; i++)
        {
            if (points[i].RecordedAt is not DateTimeOffset at) continue;
            var ticks = at.UtcTicks;
            if (anchors.Count > 0 && ticks < anchors[^1].Ticks) continue;
            anchors.Add((i, ticks));
        }

        if (anchors.Count >= 2)
        {
            var t0 = anchors[0].Ticks;
            double Minutes(long ticks) => (ticks - t0) / (double)TimeSpan.TicksPerMinute;
            var duration = Minutes(anchors[^1].Ticks);
            for (var i = 0; i <= anchors[0].Index; i++) times[i] = 0;
            for (var k = 0; k + 1 < anchors.Count; k++)
            {
                var (a, ta) = anchors[k];
                var (b, tb) = anchors[k + 1];
                var ma = Minutes(ta);
                var mb = Minutes(tb);
                var span = cumulative[b] - cumulative[a];
                for (var i = a; i <= b; i++)
                {
                    var f = span > 0 ? (cumulative[i] - cumulative[a]) / span : (double)(i - a) / (b - a);
                    times[i] = ma + (mb - ma) * f;
                }
            }
            for (var i = anchors[^1].Index; i < n; i++) times[i] = duration;
            return (times, duration, true);
        }

        var total = cumulative[n - 1];
        for (var i = 0; i < n; i++)
        {
            var f = total > 0 ? cumulative[i] / total : (n > 1 ? (double)i / (n - 1) : 0);
            times[i] = f * defaultDurationMinutes;
        }
        return (times, defaultDurationMinutes, false);
    }

    // Douglas-Peucker over the projected points with an explicit stack, so a
    // 50,000 point route does not recurse deeply. A point's distance from a
    // segment is measured to where the segment puts the point's distance along
    // the recording. That is never less than the perpendicular distance, and it
    // keeps a turn back along the same line, so a position read off the
    // simplified path by distance stays within the tolerance too. Returns the
    // kept indices in route order; the first and last are always kept.
    private static List<int> Simplify(double[] xs, double[] ys, double[] along, double tolerance)
    {
        var n = xs.Length;
        if (n <= 2) return Enumerable.Range(0, n).ToList();
        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end - start < 2) continue;
            var maxDistance = -1.0;
            var index = -1;
            for (var i = start + 1; i < end; i++)
            {
                var d = SynchronizedDistance(i, start, end, xs, ys, along);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    index = i;
                }
            }
            if (maxDistance > tolerance)
            {
                keep[index] = true;
                stack.Push((index, end));
                stack.Push((start, index));
            }
        }
        var kept = new List<int>();
        for (var i = 0; i < n; i++) if (keep[i]) kept.Add(i);
        return kept;
    }

    // The distance from point i to the position on segment start..end at the
    // point's share of the recording's distance between the two; the plain
    // distance to the segment when the recording does not move between them.
    private static double SynchronizedDistance(int i, int start, int end, double[] xs, double[] ys, double[] along)
    {
        var span = along[end] - along[start];
        if (span <= 0)
            return SegmentDistance(xs[i], ys[i], xs[start], ys[start], xs[end], ys[end]);
        var f = (along[i] - along[start]) / span;
        var x = xs[start] + (xs[end] - xs[start]) * f;
        var y = ys[start] + (ys[end] - ys[start]) * f;
        return Distance(xs[i], ys[i], x, y);
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0) return Distance(px, py, ax, ay);
        var t = ((px - ax) * dx + (py - ay) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        return Distance(px, py, ax + t * dx, ay + t * dy);
    }

    // One pass of Chaikin corner cutting on an open polyline: every segment is
    // replaced by its points at one quarter and three quarters, and the first
    // and last vertex stay where they are.
    private static List<Vertex> Chaikin(List<Vertex> source)
    {
        if (source.Count < 3) return source;
        var result = new List<Vertex>(source.Count * 2);
        result.Add(source[0]);
        for (var i = 0; i + 1 < source.Count; i++)
        {
            var a = source[i];
            var b = source[i + 1];
            result.Add(Mix(a, b, 0.25));
            result.Add(Mix(a, b, 0.75));
        }
        result.Add(source[^1]);
        return result;
    }

    private static Vertex Mix(Vertex a, Vertex b, double f) => new(
        a.Lat + (b.Lat - a.Lat) * f,
        a.Lng + (b.Lng - a.Lng) * f,
        a.Along + (b.Along - a.Along) * f);

    // Even thinning to at most `max` vertices: vertex i of the result is source
    // vertex floor(i * (n - 1) / (max - 1)), so the first and last are kept and
    // the gaps differ by at most one.
    public static List<T> ThinEvenly<T>(IReadOnlyList<T> source, int max)
    {
        var n = source.Count;
        if (n <= max || max < 2) return source.ToList();
        var result = new List<T>(max);
        for (long i = 0; i < max; i++)
            result.Add(source[(int)(i * (n - 1) / (max - 1))]);
        return result;
    }

    // The distance along the recording at `minutes`: the first raw point whose
    // time reaches it, interpolated from the point before, so a stop (time
    // passing with no distance) holds the position; before the start is 0 and
    // past the end is the full distance.
    private static double AlongAt(double[] times, double[] cumulative, double minutes)
    {
        var j = Array.FindIndex(times, t => t >= minutes);
        if (j < 0) return cumulative[^1];
        if (j == 0) return cumulative[0];
        var f = (minutes - times[j - 1]) / (times[j] - times[j - 1]);
        return cumulative[j - 1] + (cumulative[j] - cumulative[j - 1]) * f;
    }

    // The position on the drawn path at distance `along`: the first vertex
    // whose distance reaches it, interpolated from the vertex before; past the
    // end is the last vertex. The final entry at durationMinutes is always the
    // last vertex.
    private static RouteMapTimelineEntry TimelineEntry(List<Vertex> path, int minutes, double along)
    {
        double lat;
        double lng;
        var j = path.FindIndex(v => v.Along >= along);
        if (j < 0)
        {
            lat = path[^1].Lat;
            lng = path[^1].Lng;
        }
        else if (j == 0)
        {
            lat = path[0].Lat;
            lng = path[0].Lng;
        }
        else
        {
            var a = path[j - 1];
            var b = path[j];
            var f = (along - a.Along) / (b.Along - a.Along);
            lat = a.Lat + (b.Lat - a.Lat) * f;
            lng = a.Lng + (b.Lng - a.Lng) * f;
        }
        return new RouteMapTimelineEntry { Minutes = minutes, Lat = Round(lat), Lng = Round(lng) };
    }

    private static double Round(double value) =>
        Math.Round(value, CoordinateDecimals, MidpointRounding.AwayFromZero);
}
