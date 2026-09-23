namespace GroundStationRx.Orbit;

/// <summary>
/// 시각 척도와 지구 고정 좌표 변환.
///
/// 정밀도 예산 — 무엇을 빼고 얼마나 틀리는지 적어 둔다(수신기 도플러가 목적이라 거리 변화율 오차가 기준):
/// - UT1 대신 UTC: |UT1−UTC| ≤ 0.9 s → 지구 자전으로 지상국이 최대 약 0.4 km 옮겨진 것과 같다.
/// - 세차·장동 없이 TEME → 의사 지구 고정(PEF) 을 GMST 한 번 회전으로 — SGP4 의 TEME 정의가 원래 그렇다(Vallado 2006 §3.4).
/// - 극운동 무시 — 0.3″ 수준, 지상에서 약 10 m.
/// 실측(ASRTU-1 실제 패스 12 분, 세차·장동·UT1 을 다 넣은 Skyfield 대비): 거리 최대 10 m, 거리 변화율 최대 58 mm/s
/// — 435 MHz 도플러로 0.08 Hz 다. 수신기의 주파수 추적 오차보다 두 자릿수 작다(<c>LinkGeometryTests</c>).
/// </summary>
public static class EarthFrames
{
    /// <summary>지구 자전 각속도(rad/s) — Vallado 가 TEME 변환에 쓰는 값.</summary>
    public const double EarthRotationRadPerSec = 7.292115146706979e-5;

    // WGS-84 타원체 (지상국 위치에 쓴다. SGP4 내부 상수 WGS-72 와는 별개)
    public const double Wgs84EquatorialRadiusKm = 6378.137;
    public const double Wgs84Flattening = 1.0 / 298.257223563;

    /// <summary>UTC 시각의 율리우스일.</summary>
    public static double JulianDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("UTC 시각이어야 한다", nameof(utc));
        // 0001-01-01 00:00 UTC 는 JD 1721425.5
        return 1721425.5 + utc.Ticks / (double)TimeSpan.TicksPerDay;
    }

    /// <summary>그리니치 평균 항성시(rad) — IAU-82 식, Vallado gstime 과 같다.</summary>
    public static double Gmst(double julianDateUt1)
    {
        double tut1 = (julianDateUt1 - 2451545.0) / 36525.0;
        double seconds = -6.2e-6 * tut1 * tut1 * tut1 + 0.093104 * tut1 * tut1
                         + (876600.0 * 3600.0 + 8640184.812866) * tut1 + 67310.54841;
        double rad = seconds * (Math.PI / 180.0) / 240.0 % (2.0 * Math.PI);
        return rad < 0.0 ? rad + 2.0 * Math.PI : rad;
    }

    /// <summary>TEME 상태 → 지구 고정 좌표(위치 km, 지구에 대한 속도 km/s).</summary>
    public static (Vec3 Position, Vec3 Velocity) TemeToEcef(TemeState teme, DateTime utc)
    {
        double theta = Gmst(JulianDate(utc));
        double c = Math.Cos(theta), s = Math.Sin(theta);
        Vec3 r = Rz(teme.PositionKm, c, s);
        Vec3 v = Rz(teme.VelocityKmS, c, s);
        // 회전하는 좌표계에서 본 속도: v_ecef = R v_teme − ω × r_ecef
        var omega = new Vec3(0, 0, EarthRotationRadPerSec);
        return (r, v - omega.Cross(r));
    }

    private static Vec3 Rz(Vec3 a, double c, double s) => new(c * a.X + s * a.Y, -s * a.X + c * a.Y, a.Z);
}

/// <summary>지상국 — WGS-84 측지 좌표(위도·경도 도, 타원체 고도 km).</summary>
public sealed record GroundSite(string Name, double LatitudeDeg, double LongitudeDeg, double AltitudeKm)
{
    public Vec3 Ecef
    {
        get
        {
            double lat = LatitudeDeg * Math.PI / 180.0, lon = LongitudeDeg * Math.PI / 180.0;
            double f = EarthFrames.Wgs84Flattening;
            double e2 = f * (2.0 - f);
            double n = EarthFrames.Wgs84EquatorialRadiusKm / Math.Sqrt(1.0 - e2 * Math.Sin(lat) * Math.Sin(lat));
            return new Vec3(
                (n + AltitudeKm) * Math.Cos(lat) * Math.Cos(lon),
                (n + AltitudeKm) * Math.Cos(lat) * Math.Sin(lon),
                (n * (1.0 - e2) + AltitudeKm) * Math.Sin(lat));
        }
    }

    /// <summary>측지 천정 방향 단위 벡터(타원체 법선).</summary>
    public Vec3 Up
    {
        get
        {
            double lat = LatitudeDeg * Math.PI / 180.0, lon = LongitudeDeg * Math.PI / 180.0;
            return new Vec3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        }
    }
}
