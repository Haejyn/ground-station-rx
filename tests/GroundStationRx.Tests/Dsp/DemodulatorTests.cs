using GroundStationRx.Dsp;
using GroundStationRx.Simulation;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Dsp;

public class DemodulatorTests(ITestOutputHelper output)
{
    /// <summary>ASRTU-1 에서 잰 조건을 흉내 낸 채널 — 심볼율 9600.7 baud, 소수 타이밍, 잔여 반송파(포착 뒤), 임의 위상.</summary>
    private static BpskChannel RealisticChannel(double esN0Db, int seed) => new()
    {
        SampleRateHz = 40_000,
        SymbolRateHz = 9600.7,
        TimingOffsetSymbols = 0.37,
        CarrierOffsetHz = 0.8,
        CarrierPhaseRad = 1.1,
        EsN0Db = esN0Db,
        Seed = seed,
    };

    internal static int[] RandomSymbols(int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => rng.Next(2) * 2 - 1).ToArray();
    }

    /// <summary>
    /// 복조 결과를 보낸 심볼과 맞춘다 — 첫 심볼 위치(앞쪽 몇 개를 건너뛸 수 있다)와 180° 모호성은 수신기가 모르는 게 정상이다.
    /// 모호성은 2,000 심볼 구간마다 따로 고른다(위상을 펼치다 π 만큼 넘어가도 차동 부호화가 풀어 주는 것과 같은 조건).
    /// </summary>
    internal static (int Errors, int Compared) CountErrors(double[] soft, int[] sent)
    {
        int bestShift = 0;
        double best = -1;
        for (int shift = -4; shift <= 4; shift++)
        {
            double c = 0;
            for (int i = 0; i < 2000; i++)
            {
                int j = i + shift;
                if (j >= 0 && j < sent.Length && i < soft.Length) c += soft[i] * sent[j];
            }
            if (Math.Abs(c) > best) { best = Math.Abs(c); bestShift = shift; }
        }
        int errors = 0, compared = 0;
        for (int seg = 0; seg < soft.Length; seg += 2000)
        {
            int e0 = 0, e1 = 0, n = 0;
            for (int i = seg; i < Math.Min(seg + 2000, soft.Length); i++)
            {
                int j = i + bestShift;
                if (j < 0 || j >= sent.Length) continue;
                int decided = soft[i] >= 0 ? 1 : -1;
                if (decided != sent[j]) e0++; else e1++;
                n++;
            }
            errors += Math.Min(e0, e1);
            compared += n;
        }
        return (errors, compared);
    }

    [Fact]
    [Trait("Requirement", "REQ-DEM-01")]
    public void Noiseless_AllSymbolsRecovered_UnderTimingRateAndCarrierErrors()
    {
        foreach (double offset in new[] { -10.0, 0.0, 10.0 })
        {
            var sent = RandomSymbols(20_000, 3);
            var x = BpskSignalGenerator.Generate(sent, RealisticChannel(double.PositiveInfinity, 1) with { CarrierOffsetHz = offset });
            var burst = BpskBurstDemodulator.Demodulate(x, 40_000, 9600);
            var (errors, compared) = CountErrors(burst.Soft, sent);
            output.WriteLine($"반송파 {offset} Hz: {compared} 심볼 중 오류 {errors}, 심볼율 {burst.SymbolRateHz:F3}, 품질 {burst.Quality:F1}");
            Assert.Equal(0, errors);
            Assert.True(compared > 19_900, $"비교한 심볼 {compared}");
            Assert.True(Math.Abs(burst.SymbolRateHz - 9600.7) < 0.05, $"심볼율 {burst.SymbolRateHz:F4}");
        }
    }

    /// <summary>
    /// 비트 오류율을 이론 곡선 ½·erfc(√(Eb/N0)) 와 비교한다 — 구현 손실(이론보다 몇 dB 더 필요한가)이 0.5 dB 를 넘으면 실패.
    /// 이론보다 **좋게** 나와도 실패다(측정이나 잡음 발생이 틀렸다는 뜻).
    /// </summary>
    [Theory]
    [Trait("Requirement", "REQ-DEM-01")]
    [InlineData(2.0, 60_000)]
    [InlineData(4.0, 100_000)]
    [InlineData(6.0, 200_000)]
    public void BitErrorRate_WithinHalfDecibelOfTheory(double ebN0Db, int symbols)
    {
        var sent = RandomSymbols(symbols, (int)ebN0Db);
        var x = BpskSignalGenerator.Generate(sent, RealisticChannel(ebN0Db, 11 + (int)ebN0Db));
        var burst = BpskBurstDemodulator.Demodulate(x, 40_000, 9600);
        var (errors, compared) = CountErrors(burst.Soft, sent);
        double ber = (double)errors / compared;
        double theory = TheoryBer(ebN0Db), worse = TheoryBer(ebN0Db - 0.5), better = TheoryBer(ebN0Db + 0.5);
        output.WriteLine($"Eb/N0 {ebN0Db} dB: BER {ber:E2} ({errors}/{compared}) · 이론 {theory:E2} · −0.5 dB {worse:E2} · +0.5 dB {better:E2}");
        Assert.True(ber <= worse, $"BER {ber:E2} > 이론(Eb/N0 − 0.5 dB) {worse:E2}");
        Assert.True(ber >= better, $"BER {ber:E2} < 이론(Eb/N0 + 0.5 dB) {better:E2} — 이론보다 좋을 수 없다");
    }

    [Fact]
    [Trait("Requirement", "REQ-DEM-01")]
    public void Unwrap_KeepsPhaseContinuousAcrossTheCut()
    {
        double[] wrapped = [3.0, -3.1, 3.05, -3.0];
        var r = BpskBurstDemodulator.Unwrap(wrapped, 2 * Math.PI);
        for (int i = 1; i < r.Length; i++) Assert.True(Math.Abs(r[i] - r[i - 1]) < 0.3);
    }

    /// <summary>합성 신호의 펄스 — t=0 의 값은 닫힌 식 1 − α + 4α/π, 에너지는 심볼 시간 기준 1 (수치 적분).</summary>
    [Fact]
    [Trait("Requirement", "REQ-DEM-01")]
    public void RrcPulse_PeakAndUnitEnergy()
    {
        Assert.Equal(1 - 0.35 + 4 * 0.35 / Math.PI, BpskSignalGenerator.Rrc(0.0, 0.35), 12);
        double e = 0;
        for (double t = -40; t <= 40; t += 0.001) e += Math.Pow(BpskSignalGenerator.Rrc(t, 0.35), 2) * 0.001;
        Assert.Equal(1.0, e, 3);
        Assert.True(double.IsFinite(BpskSignalGenerator.Rrc(1 / (4 * 0.35), 0.35))); // 1/(4α) 특이점
    }

    internal static double TheoryBer(double ebN0Db) => 0.5 * Erfc(Math.Sqrt(Math.Pow(10, ebN0Db / 10)));

    /// <summary>상보 오차 함수 — Numerical Recipes erfcc(체비셰프 근사, 상대 오차 1.2e-7).</summary>
    internal static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806
                   + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2 - r;
    }

    /// <summary>
    /// RRC 식은 t = ±1/(4α) 에서 0/0 이라 따로 적은 극한값을 쓴다. 그 값이 틀려도 "유한하다" 는 통과했다(뮤테이션 시험이 지적) —
    /// 이웃 점과 이어지는지로 판정한다. 합성 신호 발생기와 정합 필터 두 곳 모두.
    /// </summary>
    [Theory]
    [Trait("Requirement", "REQ-DEM-01")]
    [InlineData(0.35)]
    [InlineData(0.5)]
    public void RrcSingularPoint_IsContinuousWithNeighbours(double a)
    {
        double t0 = 1 / (4 * a);
        double left = BpskSignalGenerator.Rrc(t0 - 1e-6, a), right = BpskSignalGenerator.Rrc(t0 + 1e-6, a);
        Assert.Equal(0.5 * (left + right), BpskSignalGenerator.Rrc(t0, a), 5);

        // 정합 필터: 심볼당 표본 수를 t0 가 정확히 표본에 걸리게 고른다(sps = 4 → t = 1/(4·0.5) = 0.5 = 표본 2 개)
        double sps = 1 / t0;
        var h = FirFilter.RootRaisedCosine(sps, a, 8);
        int m = h.Length / 2;
        var dense = FirFilter.RootRaisedCosine(sps * 1000, a, 8); // 같은 펄스를 1000 배 촘촘히
        int md = dense.Length / 2;
        double scale = h[m] / dense[md];
        Assert.Equal(0.5 * (dense[md + 999] + dense[md + 1001]) * scale, h[m + 1], 4);
    }

    [Fact]
    [Trait("Requirement", "REQ-DEM-01")]
    public void FilterDesign_AndDemodulator_RejectBadInputs()
    {
        Assert.Throws<ArgumentException>(() => FirFilter.LowPass(40_000, 5_000, 100)); // 짝수 탭 — 지연이 정수가 아니다
        Assert.Equal(1.0, FirFilter.LowPass(40_000, 5_000, 101).Sum(), 12);           // DC 이득 1
        Assert.Throws<ArgumentException>(() => BpskBurstDemodulator.Demodulate(new System.Numerics.Complex[500], 40_000, 9600)); // 타이밍 창보다 짧다
        // 길이는 충분한데 신호가 없다(전부 0 — 녹음이 끊긴 구간). 처음에는 심볼율이 NaN 이 되어 무한 반복 끝에 메모리가 바닥났다
        Assert.Throws<ArgumentException>(() => BpskBurstDemodulator.Demodulate(new System.Numerics.Complex[40_000], 40_000, 9600));
    }
}
