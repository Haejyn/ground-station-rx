using System.Numerics;

namespace GroundStationRx.Simulation;

/// <summary>합성 BPSK 신호의 조건 — 수신기가 겪는 손상을 하나씩 켜고 끌 수 있게 나눴다.</summary>
public sealed record BpskChannel
{
    public double SampleRateHz { get; init; } = 40_000;
    public double SymbolRateHz { get; init; } = 9600;
    public double RollOff { get; init; } = 0.35;
    /// <summary>첫 심볼의 시각(심볼 단위, 소수) — 수신기는 이것을 모른다.</summary>
    public double TimingOffsetSymbols { get; init; }
    public double CarrierOffsetHz { get; init; }
    public double CarrierPhaseRad { get; init; }
    /// <summary>심볼당 에너지 대 잡음 밀도(dB). 무한대면 잡음 없음.</summary>
    public double EsN0Db { get; init; } = double.PositiveInfinity;
    public int Seed { get; init; } = 1;
}

/// <summary>
/// 정답을 아는 BPSK 신호 — RRC 펄스를 **연속 시간 식**으로 직접 계산해 소수 표본 위치의 타이밍·심볼율 오차를 정확히 넣는다.
/// 펄스 에너지가 심볼 시간 기준 1 이라, 표본당 복소 잡음 분산 N0 = sps / 10^(Es/N0 / 10) 로 두면 정합 필터 출력의 SNR 이 Es/N0 가 된다.
/// </summary>
public static class BpskSignalGenerator
{
    private const int SpanSymbols = 8;

    public static Complex[] Generate(ReadOnlySpan<int> symbols, BpskChannel ch, int leadSamples = 0, int tailSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(ch);
        double sps = ch.SampleRateHz / ch.SymbolRateHz;
        int n = leadSamples + (int)Math.Ceiling((symbols.Length + ch.TimingOffsetSymbols) * sps) + tailSamples;
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double u = (i - leadSamples) / sps - ch.TimingOffsetSymbols; // 심볼 단위 시각
            int k0 = (int)Math.Floor(u) - SpanSymbols, k1 = (int)Math.Floor(u) + SpanSymbols;
            double v = 0;
            for (int k = Math.Max(0, k0); k <= Math.Min(symbols.Length - 1, k1); k++)
                v += symbols[k] * Rrc(u - k, ch.RollOff);
            double ph = ch.CarrierPhaseRad + 2 * Math.PI * ch.CarrierOffsetHz * i / ch.SampleRateHz;
            x[i] = v * Complex.FromPolarCoordinates(1, ph);
        }

        if (!double.IsPositiveInfinity(ch.EsN0Db))
        {
            double sigma = Math.Sqrt(sps / Math.Pow(10, ch.EsN0Db / 10) / 2);
            var rng = new Random(ch.Seed);
            for (int i = 0; i < n; i++)
                x[i] += new Complex(Gauss(rng) * sigma, Gauss(rng) * sigma);
        }
        return x;
    }

    /// <summary>단위 에너지 RRC 펄스(심볼 시간 T = 1).</summary>
    public static double Rrc(double t, double a)
    {
        if (Math.Abs(t) < 1e-12) return 1 - a + 4 * a / Math.PI;
        if (a > 0 && Math.Abs(Math.Abs(4 * a * t) - 1) < 1e-9)
            return a / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * a)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * a)));
        return (Math.Sin(Math.PI * t * (1 - a)) + 4 * a * t * Math.Cos(Math.PI * t * (1 + a))) / (Math.PI * t * (1 - 16 * a * a * t * t));
    }

    public static double Gauss(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
    }
}
