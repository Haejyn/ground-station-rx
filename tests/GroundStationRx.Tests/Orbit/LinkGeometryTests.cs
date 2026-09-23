using System.Globalization;
using GroundStationRx.Orbit;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Orbit;

public class LinkGeometryTests(ITestOutputHelper output)
{
    /// <summary>SatNOGS 지상국 PI9RD (CAMRAS 드빙겔로 25 m) — 관측 10736393 의 좌표.</summary>
    internal static readonly GroundSite Dwingeloo = new("PI9RD Dwingeloo", 52.812, 6.396, 0.010);

    [Fact]
    [Trait("Requirement", "REQ-ORB-03")]
    public void JulianDate_OfJ2000Epoch()
    {
        Assert.Equal(2451545.0, EarthFrames.JulianDate(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)), 9);
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-03")]
    public void Gmst_MatchesValladoExample3_5()
    {
        // Vallado, Fundamentals of Astrodynamics, 예제 3-5: 1992-08-20 12:14:00 UT1 → GMST 152.578787810°
        double jd = EarthFrames.JulianDate(new DateTime(1992, 8, 20, 12, 14, 0, DateTimeKind.Utc));
        Assert.Equal(152.578787810, EarthFrames.Gmst(jd) * 180.0 / Math.PI, 6);
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-03")]
    public void LocalTime_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EarthFrames.JulianDate(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    /// <summary>
    /// Skyfield(세차·장동·UT1 까지 넣은 독립 구현)와 ASRTU-1 실제 패스 12 분을 10 초 간격으로 대조한다.
    /// 차이는 이 저장소가 뺀 항(UT1, 극운동, 장동)에서 온다. 실측 거리 10 m · 거리 변화율 58 mm/s(435 MHz 에서 0.08 Hz),
    /// 허용 오차는 그 약 두 배(100 mm/s)로 둔다 — 넘으면 변환 경로가 바뀐 것이다.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-ORB-03")]
    public void RealPass_MatchesSkyfield()
    {
        var link = new LinkGeometry(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), Dwingeloo);
        double worstRange = 0, worstRate = 0, worstEl = 0;
        int rows = 0;
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "golden", "link_skyfield.csv")).Skip(1))
        {
            string[] f = line.Split(',');
            var utc = DateTime.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var s = link.At(utc);
            worstRange = Math.Max(worstRange, Math.Abs(s.RangeKm - double.Parse(f[1], CultureInfo.InvariantCulture)));
            worstRate = Math.Max(worstRate, Math.Abs(s.RangeRateKmS - double.Parse(f[2], CultureInfo.InvariantCulture)));
            worstEl = Math.Max(worstEl, Math.Abs(s.ElevationDeg - double.Parse(f[3], CultureInfo.InvariantCulture)));
            rows++;
        }
        output.WriteLine($"{rows} 점 · 거리 {worstRange * 1000:F2} m · 거리 변화율 {worstRate * 1e6:F3} mm/s · 고도각 {worstEl:E2}°");
        Assert.Equal(73, rows);
        Assert.True(worstRange < 0.5, $"거리 최대 차이 {worstRange * 1000:F1} m");
        Assert.True(worstRate < 1e-4, $"거리 변화율 최대 차이 {worstRate * 1e6:F1} mm/s");
        Assert.True(worstEl < 0.02, $"고도각 최대 차이 {worstEl:F4}°");
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-03")]
    public void Doppler_IsPositiveWhileApproachingAndSignFlipsAtClosestApproach()
    {
        var link = new LinkGeometry(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), Dwingeloo);
        var start = new DateTime(2024, 12, 9, 7, 57, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 721).Select(k => link.At(start.AddSeconds(k))).ToList();
        Assert.True(samples[0].DopplerHz(435.4e6) > 9_000);
        Assert.True(samples[^1].DopplerHz(435.4e6) < -9_000);
        // 최근접(거리 최소) 시각에서 거리 변화율 부호가 바뀐다
        int closest = samples.IndexOf(samples.MinBy(s => s.RangeKm));
        Assert.True(samples[closest - 1].RangeRateKmS < 0 && samples[closest + 1].RangeRateKmS > 0);
    }
}
