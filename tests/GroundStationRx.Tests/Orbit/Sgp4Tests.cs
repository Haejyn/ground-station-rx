using System.Globalization;
using GroundStationRx.Orbit;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Orbit;

/// <summary>
/// SGP4 를 Vallado 의 공식 검증 자료와 대조한다 — <c>SGP4-VER.TLE</c>(입력)와 <c>tcppver.out</c>(Vallado C++ 기준 구현의 출력).
/// 두 파일은 python-sgp4 배포본에 들어 있는 것을 그대로 복사했다(수정 없음).
/// </summary>
public class Sgp4Tests(ITestOutputHelper output)
{
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden");

    internal sealed record VerCase(string Satnum, string Line1, string Line2);
    internal sealed record VerPoint(double Tsince, Vec3 R, Vec3 V);

    internal static List<VerCase> ReadVerTle()
    {
        var cases = new List<VerCase>();
        string? l1 = null;
        foreach (string raw in File.ReadLines(Path.Combine(Golden, "SGP4-VER.TLE")))
        {
            if (raw.StartsWith("1 ", StringComparison.Ordinal)) l1 = raw;
            else if (raw.StartsWith("2 ", StringComparison.Ordinal) && l1 is not null)
            {
                // 2 행 69 열 뒤에는 시작·끝·간격 열이 붙어 있다 — TLE 부분만 쓴다
                cases.Add(new VerCase(raw[2..7].Trim().TrimStart('0'), l1, raw[..Math.Min(69, raw.Length)]));
                l1 = null;
            }
        }
        return cases;
    }

    internal static Dictionary<string, List<VerPoint>> ReadTcppver()
    {
        var result = new Dictionary<string, List<VerPoint>>();
        List<VerPoint>? current = null;
        foreach (string raw in File.ReadLines(Path.Combine(Golden, "tcppver.out")))
        {
            string[] f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 2 && f[1] == "xx")
            {
                current = [];
                result[f[0]] = current; // 같은 번호가 두 번 나오면(20413) 뒤 것을 쓴다 — TLE 도 뒤 것이 이긴다
                continue;
            }
            if (current is null || f.Length < 7) continue;
            double[] d = f[..7].Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            current.Add(new VerPoint(d[0], new Vec3(d[1], d[2], d[3]), new Vec3(d[4], d[5], d[6])));
        }
        return result;
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-01")]
    public void NearEarthCases_MatchValladoReferenceOutput()
    {
        var expected = ReadTcppver();
        int compared = 0, satellites = 0;
        double worstR = 0, worstV = 0;
        foreach (var c in ReadVerTle())
        {
            Tle tle;
            try { tle = Tle.Parse(c.Line1, c.Line2); }
            catch (FormatException) { continue; } // 일부러 망가뜨린 사례 — 아래 시험이 따로 본다

            Sgp4 sgp4;
            try { sgp4 = new Sgp4(tle); }
            catch (NotSupportedException) { continue; } // 심우주 — 범위 밖

            satellites++;
            foreach (var p in expected[c.Satnum])
            {
                var s = sgp4.Propagate(p.Tsince);
                worstR = Math.Max(worstR, (s.PositionKm - p.R).Norm);
                worstV = Math.Max(worstV, (s.VelocityKmS - p.V).Norm);
                compared++;
            }
        }

        output.WriteLine($"위성 {satellites} · 시점 {compared} · 위치 최대 차이 {worstR:E2} km · 속도 최대 차이 {worstV:E2} km/s");
        // tcppver.out 은 위치를 소수 8 자리(10 μm), 속도를 9 자리로 찍는다 — 인쇄 반올림보다 조금 넉넉하게
        Assert.Equal(9, satellites); // SGP4-VER 의 근지구 사례 9 개 전부 (나머지는 심우주)
        Assert.Equal(158, compared);
        Assert.True(worstR < 1e-6, $"위치 최대 차이 {worstR:E3} km");
        Assert.True(worstV < 1e-8, $"속도 최대 차이 {worstV:E3} km/s");
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-01")]
    public void DeepSpaceCases_AreRejectedInsteadOfPropagatedWrongly()
    {
        int rejected = 0;
        foreach (var c in ReadVerTle())
        {
            Tle tle;
            try { tle = Tle.Parse(c.Line1, c.Line2); }
            catch (FormatException) { continue; }
            double periodMin = 1440.0 / tle.MeanMotionRevPerDay;
            if (periodMin < 220) continue; // Kozai→Brouwer 평균 운동 차이를 넘는 여유
            Assert.Throws<NotSupportedException>(() => new Sgp4(tle));
            rejected++;
        }
        Assert.True(rejected >= 10, $"심우주 거부 {rejected} 개");
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-01")]
    public void DecayedOrbit_ThrowsWithReason()
    {
        // 근지점 고도가 낮고 항력이 큰 TLE 를 한참 뒤로 전파하면 대기권에 떨어진다
        var tle = Tle.Parse(
            "1 61781U 24199AY  24343.80030339  .00013950  00000+0  57980-2 0  9998",
            "2 61781  97.3798 208.0613 0018899 119.8177 240.4942 15.23747191 49005");
        var sgp4 = new Sgp4(tle);
        var ex = Assert.ThrowsAny<Sgp4Exception>(() => sgp4.Propagate(365 * 1440.0));
        Assert.Equal(Sgp4Error.Decayed, ex.Error); // Vallado 판(python-sgp4)도 같은 시점에 오류 6
    }
}
