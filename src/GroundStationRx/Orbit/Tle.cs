using System.Globalization;

namespace GroundStationRx.Orbit;

/// <summary>
/// NORAD 2행 궤도 요소(TLE). 값은 TLE 단위 그대로 두고, 단위 변환은 <see cref="Sgp4"/> 가 한다.
/// 고정 폭 열 위치를 따르며, 체크섬이 틀리면 거부한다 — 요소의 숫자 하나가 바뀌면 위성이 조용히 수 km 옮겨 간다.
/// </summary>
public sealed record Tle
{
    public required string SatelliteNumber { get; init; }
    public required DateTime EpochUtc { get; init; }
    /// <summary>TLE 의 연도 + 연중 일수(소수). SGP4 에포크 계산에 그대로 쓴다.</summary>
    public required int EpochYear { get; init; }
    public required double EpochDayOfYear { get; init; }
    public required double MeanMotionDot { get; init; }        // rev/day² (1차 도함수 / 2)
    public required double MeanMotionDdot { get; init; }       // rev/day³ (2차 도함수 / 6)
    public required double Bstar { get; init; }                // 1/지구 반지름
    public required double InclinationDeg { get; init; }
    public required double RaanDeg { get; init; }
    public required double Eccentricity { get; init; }
    public required double ArgPerigeeDeg { get; init; }
    public required double MeanAnomalyDeg { get; init; }
    public required double MeanMotionRevPerDay { get; init; }

    public static Tle Parse(string line1, string line2)
    {
        ArgumentNullException.ThrowIfNull(line1);
        ArgumentNullException.ThrowIfNull(line2);
        line1 = line1.TrimEnd();
        line2 = line2.TrimEnd();
        if (line1.Length < 68 || !line1.StartsWith("1 ", StringComparison.Ordinal))
            throw new FormatException($"TLE line 1 malformed: '{line1}'");
        if (line2.Length < 68 || !line2.StartsWith("2 ", StringComparison.Ordinal))
            throw new FormatException($"TLE line 2 malformed: '{line2}'");
        VerifyChecksum(line1);
        VerifyChecksum(line2);

        string satnum = line1[2..7];
        if (satnum != line2[2..7])
            throw new FormatException("TLE lines 1 and 2 have different satellite numbers");

        int twoDigitYear = int.Parse(line1[18..20], CultureInfo.InvariantCulture);
        int year = twoDigitYear < 57 ? 2000 + twoDigitYear : 1900 + twoDigitYear;
        double dayOfYear = D(line1[20..32]);
        // AddDays 는 반올림하므로 틱으로 더해 100 ns 해상도를 지킨다
        var epoch = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddTicks((long)Math.Round((dayOfYear - 1.0) * TimeSpan.TicksPerDay));

        return new Tle
        {
            SatelliteNumber = satnum.Trim(),
            EpochUtc = epoch,
            EpochYear = year,
            EpochDayOfYear = dayOfYear,
            MeanMotionDot = D(line1[33..43]),
            MeanMotionDdot = ImpliedDecimal(line1[44..52]),
            Bstar = ImpliedDecimal(line1[53..61]),
            InclinationDeg = D(line2[8..16]),
            RaanDeg = D(line2[17..25]),
            Eccentricity = D("0." + line2[26..33].Replace(' ', '0')),
            ArgPerigeeDeg = D(line2[34..42]),
            MeanAnomalyDeg = D(line2[43..51]),
            MeanMotionRevPerDay = D(line2[52..63]),
        };
    }

    /// <summary>숫자 합의 10 나머지, '-' 는 1 로 센다(69 열).</summary>
    internal static int Checksum(string line)
    {
        int sum = 0;
        foreach (char c in line.AsSpan(0, 68))
        {
            if (char.IsAsciiDigit(c)) sum += c - '0';
            else if (c == '-') sum += 1;
        }
        return sum % 10;
    }

    private static void VerifyChecksum(string line)
    {
        if (line.Length < 69 || !char.IsAsciiDigit(line[68])) return; // 체크섬을 빼고 주는 출처도 있다
        int expected = line[68] - '0';
        int actual = Checksum(line);
        if (expected != actual)
            throw new FormatException($"TLE checksum {expected} but line sums to {actual}: '{line}'");
    }

    private static double D(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>"-11606-4" → -0.11606e-4 (앞에 소수점이 있다고 보고, 부호 있는 지수).</summary>
    private static double ImpliedDecimal(string s)
    {
        string mantissa = s[..6].Trim();
        string exponent = s[6..].Trim();
        if (mantissa.Length == 0) return 0.0;
        char sign = mantissa[0] is '-' or '+' ? mantissa[0] : '+';
        string digits = mantissa.TrimStart('-', '+').Trim();
        double value = D($"{sign}0.{digits}");
        return exponent.Length == 0 ? value : value * Math.Pow(10.0, int.Parse(exponent, CultureInfo.InvariantCulture));
    }
}
