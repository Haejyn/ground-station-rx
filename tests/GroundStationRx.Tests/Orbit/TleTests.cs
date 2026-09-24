using GroundStationRx.Orbit;

namespace GroundStationRx.Tests.Orbit;

public class TleTests
{
    internal const string Asrtu1Line1 = "1 61781U 24199AY  24343.80030339  .00013950  00000+0  57980-3 0  9999";
    internal const string Asrtu1Line2 = "2 61781  97.3798 208.0613 0018899 119.8177 240.4942 15.23747191 49005";

    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void Parse_ReadsEveryFieldAtItsColumn()
    {
        var tle = Tle.Parse(Asrtu1Line1, Asrtu1Line2);
        Assert.Equal("61781", tle.SatelliteNumber);
        Assert.Equal(2024, tle.EpochYear);
        Assert.Equal(343.80030339, tle.EpochDayOfYear, 12);
        // 343 번째 날 = 12 월 8 일, 0.80030339 일 = 19:12:26.213 UTC
        Assert.Equal(new DateTime(2024, 12, 8, 19, 12, 26, 213, DateTimeKind.Utc), tle.EpochUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0.00013950, tle.MeanMotionDot, 15);
        Assert.Equal(0.0, tle.MeanMotionDdot);
        Assert.Equal(0.57980e-3, tle.Bstar, 15);
        Assert.Equal(97.3798, tle.InclinationDeg, 12);
        Assert.Equal(208.0613, tle.RaanDeg, 12);
        Assert.Equal(0.0018899, tle.Eccentricity, 15);
        Assert.Equal(119.8177, tle.ArgPerigeeDeg, 12);
        Assert.Equal(240.4942, tle.MeanAnomalyDeg, 12);
        Assert.Equal(15.23747191, tle.MeanMotionRevPerDay, 12);
    }

    [Theory]
    [Trait("Requirement", "REQ-ORB-02")]
    [InlineData(" 00000-0", 0.0)]
    [InlineData(" 12345-3", 0.12345e-3)]
    [InlineData("-11606-4", -0.11606e-4)]
    [InlineData(" 28098-4", 0.28098e-4)]
    [InlineData(" 10000+1", 1.0)]
    public void ImpliedDecimalFields_AreDecodedWithSignedExponent(string field, double expected)
    {
        string line1 = Asrtu1Line1[..53] + field + Asrtu1Line1[61..68];
        line1 += Tle.Checksum(line1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, Tle.Parse(line1, Asrtu1Line2).Bstar, 15);
    }

    /// <summary>
    /// 체크섬(숫자 합 mod 10)은 숫자 하나가 바뀌는 오류를 **전부** 잡는다 — 두 행의 모든 숫자 자리 × 다른 숫자 9 개를 전수로 확인한다.
    /// 이런 오류가 통과하면 궤도가 조용히 수 km 옮겨 간다.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void EverySingleDigitCorruption_IsRejectedByChecksum()
    {
        int tried = 0;
        foreach (int which in new[] { 1, 2 })
        {
            string line = which == 1 ? Asrtu1Line1 : Asrtu1Line2;
            for (int col = 0; col < 68; col++)
            {
                if (!char.IsAsciiDigit(line[col])) continue;
                for (char d = '0'; d <= '9'; d++)
                {
                    if (d == line[col]) continue;
                    string bad = line[..col] + d + line[(col + 1)..];
                    if (which == 1) Assert.Throws<FormatException>(() => Tle.Parse(bad, Asrtu1Line2));
                    else Assert.Throws<FormatException>(() => Tle.Parse(Asrtu1Line1, bad));
                    tried++;
                }
            }
        }
        Assert.True(tried > 900, $"{tried} 가지");
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void ValladoDeliberatelyBrokenCases_FailChecksum()
    {
        // SGP4-VER.TLE 의 33333·33334·33335 는 Vallado 가 오류 처리를 보려고 손으로 고친 사례다
        var broken = Sgp4Tests.ReadVerTle().Where(c => c.Satnum.StartsWith("3333", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, broken.Count);
        foreach (var c in broken)
            Assert.Throws<FormatException>(() => Tle.Parse(c.Line1, c.Line2));
    }

    [Theory]
    [Trait("Requirement", "REQ-ORB-02")]
    [InlineData("", Asrtu1Line2)]
    [InlineData(Asrtu1Line2, Asrtu1Line2)]
    [InlineData(Asrtu1Line1, Asrtu1Line1)]
    [InlineData(Asrtu1Line1, "2 61781  97.3798")]
    public void MalformedLines_AreRejected(string l1, string l2)
    {
        Assert.Throws<FormatException>(() => Tle.Parse(l1, l2));
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void MismatchedSatelliteNumbers_AreRejected()
    {
        string other = "2 61782" + Asrtu1Line2[7..68];
        other += Tle.Checksum(other).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<FormatException>(() => Tle.Parse(Asrtu1Line1, other));
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void LinesWithoutChecksumColumn_AreAccepted()
    {
        // 체크섬 열(69)을 빼고 68 자로 주는 출처도 있다 — 그래도 읽는다
        var tle = Tle.Parse(Asrtu1Line1[..68], Asrtu1Line2[..68]);
        Assert.Equal(15.23747191, tle.MeanMotionRevPerDay, 12);
        Assert.Throws<FormatException>(() => Tle.Parse(Asrtu1Line1[..67], Asrtu1Line2));
        Assert.Throws<FormatException>(() => Tle.Parse(Asrtu1Line1, Asrtu1Line2[..67]));
        Assert.Throws<ArgumentNullException>(() => Tle.Parse(null!, Asrtu1Line2));
        Assert.Throws<ArgumentNullException>(() => Tle.Parse(Asrtu1Line1, null!));
    }

    [Theory]
    [Trait("Requirement", "REQ-ORB-02")]
    [InlineData("9 ", true)]
    [InlineData("1 ", false)]
    public void WrongLineNumber_IsRejectedEvenWithValidChecksum(string prefix, bool line1)
    {
        string l = prefix + (line1 ? Asrtu1Line1 : Asrtu1Line2)[2..68];
        l += Tle.Checksum(l).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<FormatException>(() => line1 ? Tle.Parse(l, Asrtu1Line2) : Tle.Parse(Asrtu1Line1, l));
    }

    /// <summary>두 자리 연도는 57 을 기준으로 가른다(첫 인공위성 1957) — 57 → 1957, 56 → 2056.</summary>
    [Theory]
    [Trait("Requirement", "REQ-ORB-02")]
    [InlineData("57", 1957)]
    [InlineData("99", 1999)]
    [InlineData("00", 2000)]
    [InlineData("56", 2056)]
    public void TwoDigitYear_PivotsAt57(string yy, int year)
    {
        string l = Asrtu1Line1[..18] + yy + Asrtu1Line1[20..68];
        l += Tle.Checksum(l).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tle = Tle.Parse(l, Asrtu1Line2);
        Assert.Equal(year, tle.EpochYear);
        Assert.Equal(year, tle.EpochUtc.Year);
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-02")]
    public void ImpliedDecimalWithoutExponent_IsReadAsIs()
    {
        string l = Asrtu1Line1[..53] + " 12345  " + Asrtu1Line1[61..68];
        l += Tle.Checksum(l).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0.12345, Tle.Parse(l, Asrtu1Line2).Bstar, 15);
    }
}
