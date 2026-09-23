using System.Numerics;

namespace GroundStationRx.Dsp;

/// <summary>FIR 필터 설계와 적용 — 창 함수 저역 통과, 제곱근 올림 코사인(RRC), 솎아 내며 거르기.</summary>
public static class FirFilter
{
    /// <summary>Blackman 창 sinc 저역 통과. <paramref name="cutoffHz"/> 에서 −6 dB, DC 이득 1.</summary>
    public static double[] LowPass(double sampleRateHz, double cutoffHz, int taps)
    {
        if (taps % 2 == 0) throw new ArgumentException("탭 수는 홀수여야 지연이 정수다", nameof(taps));
        var h = new double[taps];
        int m = taps / 2;
        double fc = cutoffHz / sampleRateHz;
        for (int n = 0; n < taps; n++)
        {
            int k = n - m;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (taps - 1));
            h[n] = sinc * w;
        }
        double sum = h.Sum();
        for (int n = 0; n < taps; n++) h[n] /= sum;
        return h;
    }

    /// <summary>
    /// 제곱근 올림 코사인(RRC) — 심볼당 표본 수가 정수가 아니어도 된다(t = n / sps 로 계산).
    /// 송신 필터와 짝을 이루는 정합 필터라 두 개를 거치면 심볼 간 간섭이 없는 올림 코사인이 된다. 에너지 1 로 정규화.
    /// </summary>
    public static double[] RootRaisedCosine(double samplesPerSymbol, double rollOff, int spanSymbols)
    {
        int m = (int)Math.Ceiling(spanSymbols * samplesPerSymbol / 2);
        var h = new double[2 * m + 1];
        double a = rollOff;
        for (int n = -m; n <= m; n++)
        {
            double t = n / samplesPerSymbol;
            double v;
            if (Math.Abs(t) < 1e-12)
                v = 1 - a + 4 * a / Math.PI;
            else if (a > 0 && Math.Abs(Math.Abs(4 * a * t) - 1) < 1e-9)
                v = a / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * a)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * a)));
            else
                v = (Math.Sin(Math.PI * t * (1 - a)) + 4 * a * t * Math.Cos(Math.PI * t * (1 + a)))
                    / (Math.PI * t * (1 - 16 * a * a * t * t));
            h[n + m] = v;
        }
        double energy = Math.Sqrt(h.Sum(x => x * x));
        for (int i = 0; i < h.Length; i++) h[i] /= energy;
        return h;
    }

    /// <summary>
    /// 거르고 <paramref name="decimation"/> 개마다 하나씩 남긴다. 출력 i 는 입력 i·D 를 중심으로 한 값이다
    /// (탭 지연을 되돌려 입출력 시각이 맞는다 — 시각이 도플러 보정과 타이밍에 그대로 쓰이므로 중요하다).
    /// </summary>
    public static Complex[] FilterDecimate(ReadOnlySpan<Complex> x, double[] taps, int decimation)
    {
        ArgumentNullException.ThrowIfNull(taps);
        int m = taps.Length / 2;
        int outLen = (x.Length + decimation - 1) / decimation;
        var y = new Complex[outLen];
        for (int i = 0; i < outLen; i++)
        {
            int c = i * decimation;
            double re = 0, im = 0;
            int lo = Math.Max(0, c - m), hi = Math.Min(x.Length - 1, c + m);
            for (int k = lo; k <= hi; k++)
            {
                double t = taps[k - c + m];
                re += x[k].Real * t;
                im += x[k].Imaginary * t;
            }
            y[i] = new Complex(re, im);
        }
        return y;
    }
}
