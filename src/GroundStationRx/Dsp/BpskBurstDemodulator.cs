using System.Numerics;

namespace GroundStationRx.Dsp;

/// <summary>버스트 하나의 복조 결과.</summary>
/// <param name="Soft">연판정 심볼(평균 |값| = 1 로 정규화, 부호가 비트). 180° 모호성은 남아 있다 — 차동 부호화가 푼다.</param>
/// <param name="SymbolRateHz">버스트에서 잰 심볼율</param>
/// <param name="FirstSymbolSample">첫 심볼의 입력 표본 위치(소수)</param>
/// <param name="Quality">평균 |soft| / 표준편차 |soft| — 잡음이 없으면 무한대, 판정 경계가 흐리면 작아진다</param>
public sealed record DemodulatedBurst(double[] Soft, double SymbolRateHz, double FirstSymbolSample, double Quality);

/// <summary>
/// BPSK 버스트 복조기 — 녹음 후처리용 **블록 전방 추정(feedforward)** 방식.
///
/// 되먹임 루프(Gardner·Costas)는 한 번 미끄러지면 심볼이 끼거나 빠지고, 그 뒤 비터비 짝이 어긋나 프레임을 통째로 잃는다
/// (파이썬 타당성 시험에서 실제로 겪었다: 창마다 타이밍을 따로 고르자 4 초 버스트에서 심볼이 세 번 미끄러졌다).
/// 버스트 전체를 이미 가지고 있으므로, 추정값을 창 단위로 구해 **연속되게 펼친 뒤** 보간한다 — 미끄러짐이 구조적으로 없다.
///
/// 1. 정합 필터(RRC)
/// 2. 심볼율 — |y|² 의 심볼율 선을 FFT 로 찾는다(송신 클럭 오차: ASRTU-1 은 9600.7 baud 로 쟀다)
/// 3. 타이밍 — Oerder &amp; Meyr (1988): 창마다 Σ|y|²·e^(−j2πRn/fs) 의 위상이 심볼 중심 위치다. 위상을 펼쳐(unwrap) 잇는다
/// 4. 심볼 시각의 값을 4 점 Lagrange(3 차) 보간으로 구한다
/// 5. 반송파 위상 — Viterbi &amp; Viterbi: BPSK 는 s² 의 위상이 2θ 다. 창마다 구해 mod π 로 펼친다
/// </summary>
public static class BpskBurstDemodulator
{
    public static DemodulatedBurst Demodulate(ReadOnlySpan<Complex> baseband, double sampleRateHz, double nominalSymbolRateHz,
        double rollOff = 0.35, int timingWindowSymbols = 256, int phaseWindowSymbols = 32)
    {
        double nominalSps = sampleRateHz / nominalSymbolRateHz;
        if (baseband.Length < timingWindowSymbols * nominalSps)
            throw new ArgumentException($"버스트({baseband.Length} 표본)가 타이밍 창({timingWindowSymbols} 심볼)보다 짧다", nameof(baseband));
        var mf = FirFilter.RootRaisedCosine(nominalSps, rollOff, 10);
        var y = FirFilter.FilterDecimate(baseband, mf, 1);
        int n = y.Length;

        var power = new double[n];
        for (int i = 0; i < n; i++) power[i] = y[i].Real * y[i].Real + y[i].Imaginary * y[i].Imaginary;

        double rate = MeasureSymbolRate(power, sampleRateHz, nominalSymbolRateHz);
        // 신호가 없으면(전부 0 등) 선이 없어 NaN 이 된다 — 그대로 두면 창 길이가 음수가 되어 창을 끝없이 만들다 메모리가 바닥난다(시험이 잡은 결함)
        if (!double.IsFinite(rate) || Math.Abs(rate / nominalSymbolRateHz - 1) > 0.02)
            throw new ArgumentException($"심볼율 선을 찾지 못했다(측정 {rate} Hz) — 신호가 없는 구간", nameof(baseband));
        double sps = sampleRateHz / rate;

        // Oerder & Meyr: 창 가운데 표본 위치 → 타이밍 위상(주기 단위)
        int win = (int)(timingWindowSymbols * sps);
        var centers = new List<double>();
        var phases = new List<double>();
        for (int start = 0; start + win <= n; start += win / 2)
        {
            double re = 0, im = 0;
            for (int i = start; i < start + win; i++)
            {
                double ang = -2 * Math.PI * i / sps;
                re += power[i] * Math.Cos(ang);
                im += power[i] * Math.Sin(ang);
            }
            centers.Add(start + win / 2.0);
            phases.Add(Math.Atan2(im, re));
        }
        if (phases.Count == 0) throw new ArgumentException("버스트가 타이밍 창보다 짧다", nameof(baseband));
        var unwrapped = Unwrap(phases, 2 * Math.PI);

        // |y|² ≈ A + B·cos(2π(i − t0)/sps) 이면 Σ|y|²·e^(−j2πi/sps) 의 위상은 −2π·t0/sps
        // → 심볼 중심은 t = (k − φ/2π)·sps. φ 는 펼쳐 두었으므로 t 가 k 에 따라 끊김 없이 이어진다
        var soft = new List<Complex>();
        double firstSample = double.NaN;
        for (int k = 0; ; k++)
        {
            double phi = Interp(centers, unwrapped, k * sps);
            double t = (k - phi / (2 * Math.PI)) * sps;
            if (t < 2) continue;
            if (t > n - 3) break;
            if (double.IsNaN(firstSample)) firstSample = t;
            soft.Add(Lagrange4(y, t));
        }

        // Viterbi & Viterbi: 창마다 θ = ½·arg Σ s², mod π 로 펼친다
        int m = soft.Count;
        var pc = new List<double>();
        var pp = new List<double>();
        for (int start = 0; start + phaseWindowSymbols <= m; start += phaseWindowSymbols / 2)
        {
            var acc = Complex.Zero;
            for (int i = start; i < start + phaseWindowSymbols; i++) acc += soft[i] * soft[i];
            pc.Add(start + phaseWindowSymbols / 2.0);
            pp.Add(acc.Phase);
        }
        var theta2 = Unwrap(pp, 2 * Math.PI);
        var result = new double[m];
        for (int i = 0; i < m; i++)
        {
            double th = Interp(pc, theta2, i) / 2;
            result[i] = (soft[i] * Complex.FromPolarCoordinates(1, -th)).Real;
        }

        double meanAbs = result.Average(Math.Abs);
        for (int i = 0; i < m; i++) result[i] /= meanAbs;
        double var = result.Select(v => (Math.Abs(v) - 1) * (Math.Abs(v) - 1)).Average();
        return new DemodulatedBurst(result, rate, firstSample, 1 / Math.Sqrt(var));
    }

    /// <summary>|y|² 의 스펙트럼에서 공칭 심볼율 ±1 % 안의 선을 찾는다.</summary>
    internal static double MeasureSymbolRate(double[] power, double sampleRateHz, double nominalHz)
    {
        int fftSize = 1;
        while (fftSize < power.Length * 2) fftSize <<= 1;
        var buf = new Complex[fftSize];
        double mean = power.Average();
        for (int i = 0; i < power.Length; i++)
        {
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (power.Length - 1));
            buf[i] = (power[i] - mean) * hann;
        }
        Fft.Forward(buf);
        int lo = (int)(nominalHz * 0.99 / sampleRateHz * fftSize), hi = (int)Math.Ceiling(nominalHz * 1.01 / sampleRateHz * fftSize);
        int peak = lo;
        for (int k = lo; k <= hi; k++)
            if (buf[k].Magnitude > buf[peak].Magnitude) peak = k;
        double a = Math.Log(buf[peak - 1].Magnitude), b = Math.Log(buf[peak].Magnitude), c = Math.Log(buf[peak + 1].Magnitude);
        double denom = a - 2 * b + c;
        double delta = denom == 0 ? 0 : 0.5 * (a - c) / denom;
        return (peak + delta) * sampleRateHz / fftSize;
    }

    /// <summary>주기 <paramref name="period"/> 로 접힌 위상을 이웃과 이어지게 편다.</summary>
    internal static double[] Unwrap(IReadOnlyList<double> wrapped, double period)
    {
        var r = new double[wrapped.Count];
        if (r.Length == 0) return r;
        r[0] = wrapped[0];
        for (int i = 1; i < r.Length; i++)
        {
            double d = wrapped[i] - wrapped[i - 1];
            d -= period * Math.Round(d / period);
            r[i] = r[i - 1] + d;
        }
        return r;
    }

    /// <summary>선형 보간, 양 끝 밖은 끝값.</summary>
    private static double Interp(List<double> xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        int i = xs.BinarySearch(x);
        if (i >= 0) return ys[i];
        i = ~i;
        double f = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
        return ys[i - 1] + f * (ys[i] - ys[i - 1]);
    }

    /// <summary>4 점 Lagrange 보간 — 소수 위치 t 의 값.</summary>
    internal static Complex Lagrange4(Complex[] y, double t)
    {
        int i = (int)Math.Floor(t);
        double mu = t - i;
        double c0 = -mu * (mu - 1) * (mu - 2) / 6;
        double c1 = (mu + 1) * (mu - 1) * (mu - 2) / 2;
        double c2 = -(mu + 1) * mu * (mu - 2) / 2;
        double c3 = (mu + 1) * mu * (mu - 1) / 6;
        return c0 * y[i - 1] + c1 * y[i] + c2 * y[i + 1] + c3 * y[i + 2];
    }
}
