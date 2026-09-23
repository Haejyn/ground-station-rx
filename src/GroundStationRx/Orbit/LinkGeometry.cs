namespace GroundStationRx.Orbit;

/// <summary>한 시각의 위성–지상국 기하.</summary>
/// <param name="RangeKm">경사 거리</param>
/// <param name="RangeRateKmS">거리 변화율 — 양수면 멀어진다</param>
/// <param name="ElevationDeg">측지 고도각</param>
public readonly record struct LinkSample(DateTime Utc, double RangeKm, double RangeRateKmS, double ElevationDeg)
{
    /// <summary>
    /// 송신 주파수 <paramref name="carrierHz"/> 가 지상국에 도착했을 때의 도플러 편이(Hz).
    /// 1차 근사 −f·ṙ/c — 저궤도 속도(8 km/s)에서 2차 항은 435 MHz 에서 0.2 Hz 미만이라 뺐다.
    /// </summary>
    public double DopplerHz(double carrierHz) => -carrierHz * RangeRateKmS / SpeedOfLightKmS;

    public const double SpeedOfLightKmS = 299792.458;
}

/// <summary>SGP4 궤도와 지상국으로 거리·거리 변화율·고도각을 계산한다.</summary>
public sealed class LinkGeometry
{
    private readonly Sgp4 _orbit;
    private readonly Vec3 _siteEcef;
    private readonly Vec3 _up;

    public GroundSite Site { get; }

    public LinkGeometry(Sgp4 orbit, GroundSite site)
    {
        ArgumentNullException.ThrowIfNull(orbit);
        ArgumentNullException.ThrowIfNull(site);
        _orbit = orbit;
        Site = site;
        _siteEcef = site.Ecef;
        _up = site.Up;
    }

    public LinkSample At(DateTime utc)
    {
        var (r, v) = EarthFrames.TemeToEcef(_orbit.At(utc), utc);
        Vec3 los = r - _siteEcef;
        double range = los.Norm;
        // 지상국은 지구 고정 좌표에서 정지해 있으므로 거리 변화율은 위성 속도의 시선 방향 성분뿐이다
        double rangeRate = los.Dot(v) / range;
        double elevation = Math.Asin(los.Dot(_up) / range) * 180.0 / Math.PI;
        return new LinkSample(utc, range, rangeRate, elevation);
    }
}
