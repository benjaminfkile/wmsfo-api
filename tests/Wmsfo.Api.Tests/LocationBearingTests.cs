using Wmsfo.Api.Endpoints;

namespace Wmsfo.Api.Tests;

// A139: the derived heading is the initial great-circle bearing, 0 to 360,
// clockwise from true north.
public class LocationBearingTests
{
    [Theory]
    [InlineData(46.87, -114.0, 46.88, -114.0, 0)]
    [InlineData(46.87, -114.0, 46.87, -113.99, 90)]
    [InlineData(46.87, -114.0, 46.86, -114.0, 180)]
    [InlineData(46.87, -114.0, 46.87, -114.01, 270)]
    public void Cardinal_directions(double lat1, double lng1, double lat2, double lng2, double expected)
    {
        var bearing = LocationIngest.InitialBearingDegrees(lat1, lng1, lat2, lng2);
        Assert.InRange(bearing, expected - 0.01, expected + 0.01);
    }

    [Fact]
    public void Bearing_is_never_negative()
    {
        // Just west of due north lands near 360, not near 0 below zero.
        var bearing = LocationIngest.InitialBearingDegrees(46.87, -114.0, 46.88, -114.0001);
        Assert.InRange(bearing, 359.0, 360.0);
    }
}
