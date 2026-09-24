namespace GroundStationRx.Orbit;

/// <summary>도플러 잔차 적합 결과.</summary>
/// <param name="FrequencyOffsetHz">송신기(와 수신기) 고정 주파수 오차</param>
/// <param name="TimeOffsetSeconds">예측 궤도가 실제보다 이만큼 늦다(양수) — TLE 진행 방향 오차와 녹음 시계 오차의 합</param>
/// <param name="RmsBeforeHz">적합 전 잔차의 제곱평균제곱근(고정 오차만 뺀 것)</param>
/// <param name="RmsAfterHz">적합 뒤 잔차의 제곱평균제곱근</param>
/// <param name="TimeOffsetStdErrSeconds">Δt 의 표준오차 — 잔차가 서로 독립이라는 가정의 값이라, 버스트마다 되풀이되는 흐름이 있으면 실제보다 작게 나온다</param>
public sealed record DopplerFitResult(double FrequencyOffsetHz, double TimeOffsetSeconds, double RmsBeforeHz, double RmsAfterHz, int Points,
    double TimeOffsetStdErrSeconds);

/// <summary>
/// 실측 반송파 − 예측 도플러 = c + Δt·ḟ(t) 를 최소제곱으로 푼다.
///
/// 예측 궤도가 실제보다 Δt 늦으면(또는 녹음 시각이 Δt 빠르면) 실측은 예측을 Δt 만큼 앞서 간다 — f(t+Δt) ≈ f(t) + Δt·ḟ(t).
/// ḟ 는 최근접에서 가장 크므로(저궤도 435 MHz 에서 약 −100 Hz/s) 시각 어긋남은 최근접 부근 잔차로 드러나고,
/// 고정 오차 c 는 ḟ 가 작은 패스 앞뒤에서 정해진다. 둘이 잘 분리되려면 패스가 최근접을 지나야 한다.
/// 저궤도 속도 약 7.6 km/s 를 곱하면 진행 방향 위치 오차로 읽을 수 있다.
/// </summary>
public static class DopplerResidualFit
{
    public static DopplerFitResult Fit(IReadOnlyList<(DateTime Utc, double MeasuredHz)> measurements, LinkGeometry link, double carrierHz)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(link);
        int n = measurements.Count;
        if (n < 3) throw new ArgumentException("점이 셋 이상 있어야 두 미지수를 푼다", nameof(measurements));

        var r = new double[n];
        var d = new double[n];
        for (int i = 0; i < n; i++)
        {
            var t = measurements[i].Utc;
            double predicted = carrierHz + link.At(t).DopplerHz(carrierHz);
            r[i] = measurements[i].MeasuredHz - predicted;
            // ḟ — 중앙 차분 ±0.5 초
            d[i] = link.At(t.AddSeconds(0.5)).DopplerHz(carrierHz) - link.At(t.AddSeconds(-0.5)).DopplerHz(carrierHz);
        }

        // 정규 방정식 [n Σd; Σd Σd²][c; Δt] = [Σr; Σdr]
        double sd = d.Sum(), sdd = d.Sum(x => x * x), sr = r.Sum(), sdr = d.Zip(r, (a, b) => a * b).Sum();
        double det = n * sdd - sd * sd;
        if (Math.Abs(det) < 1e-9) throw new ArgumentException("도플러 변화율이 한결같아 시각 어긋남을 고정 오차와 가를 수 없다", nameof(measurements));
        double c = (sr * sdd - sd * sdr) / det;
        double dt = (n * sdr - sd * sr) / det;

        double mean = sr / n;
        double before = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / n);
        double rss = r.Select((x, i) => x - c - dt * d[i]).Sum(e => e * e);
        double after = Math.Sqrt(rss / n);
        double stdErr = Math.Sqrt(rss / (n - 2) * n / det);
        return new DopplerFitResult(c, dt, before, after, n, stdErr);
    }
}
