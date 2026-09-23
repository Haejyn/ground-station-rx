using System.Numerics;
using GroundStationRx.Recording;

namespace GroundStationRx.Dsp;

/// <summary>한 구간에서 잰 반송파 — 녹음 중심 주파수에 대한 편이(Hz)와 제곱 스펙트럼 선의 세기.</summary>
public readonly record struct CarrierMeasurement(DateTime Utc, double OffsetHz, double SnrDb);

/// <summary>
/// BPSK 반송파 주파수 측정 — 신호를 제곱하면 ±1 변조가 사라지고 2f 에 순수한 선이 남는다(심볼이 뭐든 (±1)² = 1).
/// 그 선을 FFT 로 찾아 반으로 나눈다. 복조기를 믿지 않고 반송파만 재는 독립 측정이라, 궤도 예측 도플러를 판정하는 데 쓴다.
///
/// 찾을 곳을 알면(예측 도플러) 그 주변 ±<c>searchHalfWidthHz</c> 만 본다 — 실제 녹음에는 다른 송신원도 있어서
/// 전 대역에서 가장 센 선을 고르면 엉뚱한 신호를 잰다(ASRTU-1 녹음의 −90 kHz 신호). 창 밖을 버리려고
/// 먼저 기대 주파수로 내려 옮기고 이동 평균으로 솎아 낸 뒤 제곱한다 — FFT 가 작아져 빨라지는 것은 덤이다.
///
/// 한계: 제곱하면 주파수가 두 배라 잴 수 있는 범위가 (솎아 낸) 표본율의 ±1/4 로 줄어든다.
/// 선의 위치는 Hann 창 스펙트럼의 로그 크기에 포물선을 맞춰 빈 사이 값까지 구한다.
/// </summary>
public static class BpskCarrierEstimator
{
    /// <param name="expectedOffsetHz">찾을 곳(녹음 중심 기준 Hz). null 이면 전 대역(±표본율/4)에서 찾는다.</param>
    public static CarrierMeasurement Measure(ReadOnlySpan<Complex> block, double sampleRateHz, DateTime utc,
        double? expectedOffsetHz = null, double searchHalfWidthHz = 5_000)
    {
        double center = expectedOffsetHz ?? 0.0;
        int decimation = expectedOffsetHz is null ? 1 : Math.Max(1, (int)(sampleRateHz / (8 * searchHalfWidthHz)));
        double rate = sampleRateHz / decimation;

        // 기대 주파수를 0 Hz 로 옮기고, decimation 개씩 평균 내 솎는다
        int n = block.Length / decimation;
        var x = new Complex[n];
        var rot = Complex.FromPolarCoordinates(1, -2 * Math.PI * center / sampleRateHz);
        var w = Complex.One;
        for (int i = 0, k = 0; i < n; i++)
        {
            var acc = Complex.Zero;
            for (int j = 0; j < decimation; j++, k++)
            {
                acc += block[k] * w;
                w *= rot;
            }
            x[i] = acc / decimation;
            if ((i & 1023) == 0) w /= w.Magnitude; // 누적 반올림으로 크기가 흐르지 않게
        }

        int fftSize = 1;
        while (fftSize < n * 4) fftSize <<= 1; // 4 배 영 채움 — 포물선 보간의 치우침을 줄인다
        var buf = new Complex[fftSize];
        for (int i = 0; i < n; i++)
        {
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            buf[i] = x[i] * x[i] * hann;
        }
        Fft.Forward(buf);

        var power = new double[fftSize];
        for (int k = 0; k < fftSize; k++)
            power[k] = buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;

        // 제곱 영역에서의 탐색 폭은 2 배
        int maxBin = expectedOffsetHz is null ? fftSize / 2 : (int)Math.Ceiling(2 * searchHalfWidthHz / rate * fftSize);
        int peak = 0;
        for (int d = -maxBin; d < maxBin; d++)
        {
            int k = (d + fftSize) % fftSize;
            if (power[k] > power[peak]) peak = k;
        }
        double a = Math.Log(power[(peak - 1 + fftSize) % fftSize] + 1e-300);
        double b = Math.Log(power[peak] + 1e-300);
        double c = Math.Log(power[(peak + 1) % fftSize] + 1e-300);
        double denom = a - 2 * b + c;
        double delta = denom == 0 ? 0 : 0.5 * (a - c) / denom;
        double bin = peak + delta;
        if (bin >= fftSize / 2.0) bin -= fftSize;

        var sorted = (double[])power.Clone();
        Array.Sort(sorted);
        double snrDb = 10 * Math.Log10(power[peak] / sorted[fftSize / 2]);
        return new CarrierMeasurement(utc, center + bin * rate / fftSize / 2.0, snrDb);
    }

    /// <summary>
    /// 녹음을 <paramref name="blockSeconds"/> 구간으로 나눠 재고, 선이 <paramref name="minSnrDb"/> 이상인 구간만 돌려준다.
    /// <paramref name="expectedOffsetHz"/> 는 구간 가운데 시각에서 찾을 곳(녹음 중심 기준) — 보통 궤도 예측 도플러.
    /// </summary>
    public static List<CarrierMeasurement> Scan(SigmfRecording recording, double blockSeconds, double minSnrDb,
        Func<DateTime, double> expectedOffsetHz, double searchHalfWidthHz = 5_000)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(expectedOffsetHz);
        int blockLen = (int)Math.Round(blockSeconds * recording.SampleRateHz);
        var block = new Complex[blockLen];
        var result = new List<CarrierMeasurement>();
        for (long start = 0; start + blockLen <= recording.SampleCount; start += blockLen)
        {
            recording.Read(start, block);
            var utc = recording.TimeOf(start + blockLen / 2);
            var m = Measure(block, recording.SampleRateHz, utc, expectedOffsetHz(utc), searchHalfWidthHz);
            if (m.SnrDb >= minSnrDb) result.Add(m);
        }
        return result;
    }
}
