using System.Numerics;
using GroundStationRx.Dsp;
using GroundStationRx.Orbit;
using GroundStationRx.Recording;
using GroundStationRx.Tests.Orbit;
using GroundStationRx.Tests.Recording;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Dsp;

public class CarrierTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Requirement", "REQ-DOP-01")]
    public void Fft_MatchesDirectDft()
    {
        var rng = new Random(7);
        var x = Enumerable.Range(0, 256).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var y = (Complex[])x.Clone();
        Fft.Forward(y);
        for (int k = 0; k < x.Length; k++)
        {
            var sum = Complex.Zero;
            for (int n = 0; n < x.Length; n++)
                sum += x[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * n / x.Length);
            Assert.True((sum - y[k]).Magnitude < 1e-9, $"bin {k}");
        }
        Assert.Throws<ArgumentException>(() => Fft.Forward(new Complex[100]));
    }

    /// <summary>무작위 BPSK(9600 baud, 사각 펄스) + 잡음에서 알고 있는 반송파를 잰다 — 정답을 아는 합성 신호.</summary>
    [Theory]
    [Trait("Requirement", "REQ-DOP-01")]
    [InlineData(110_218.3, 20.0)]
    [InlineData(-73_411.7, 10.0)]
    [InlineData(1_234.56, 5.0)]
    [InlineData(-240_000.0, 20.0)]
    public void SyntheticBpsk_CarrierIsMeasuredWithinHalfHertz(double offsetHz, double snrDb)
    {
        var x = SyntheticBpsk(offsetHz, snrDb, amplitude: 1.0, seed: 42);
        var m = BpskCarrierEstimator.Measure(x, Fs, DateTime.UnixEpoch);
        output.WriteLine($"{offsetHz} Hz, SNR {snrDb} dB → {m.OffsetHz:F3} Hz (선 {m.SnrDb:F1} dB)");
        Assert.True(Math.Abs(m.OffsetHz - offsetHz) < 0.5, $"측정 {m.OffsetHz:F3} Hz, 정답 {offsetHz} Hz");
    }

    /// <summary>
    /// 창 밖의 **더 센** BPSK 가 있어도 기대 주파수 ±5 kHz 안의 목표만 잰다 — 실제 녹음의 −90 kHz 방해 신호를 흉내 낸다.
    /// 창 없이 재면 방해 신호를 고른다는 것도 함께 확인한다(창이 실제로 일을 한다는 증거).
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-DOP-01")]
    public void SearchWindow_IgnoresStrongerSignalOutsideIt()
    {
        var target = SyntheticBpsk(110_220.0, 20.0, amplitude: 1.0, seed: 1);
        var interferer = SyntheticBpsk(-95_000.0, 60.0, amplitude: 4.0, seed: 2);
        var x = target.Zip(interferer, (a, b) => a + b).ToArray();

        var windowed = BpskCarrierEstimator.Measure(x, Fs, DateTime.UnixEpoch, expectedOffsetHz: 110_000.0);
        var blind = BpskCarrierEstimator.Measure(x, Fs, DateTime.UnixEpoch);
        Assert.True(Math.Abs(windowed.OffsetHz - 110_220.0) < 0.5, $"창 안 측정 {windowed.OffsetHz:F2} Hz");
        Assert.True(Math.Abs(blind.OffsetHz - -95_000.0) < 0.5, $"창 없는 측정 {blind.OffsetHz:F2} Hz");
    }

    private const double Fs = 1_000_000;

    /// <summary>무작위 ±1 심볼(9600 baud, 사각 펄스)을 반송파에 싣고 복소 가우스 잡음을 더한다. snrDb 는 진폭 1 기준.</summary>
    private static Complex[] SyntheticBpsk(double offsetHz, double snrDb, double amplitude, int seed)
    {
        const double baud = 9600;
        int n = 200_000;
        var rng = new Random(seed);
        var x = new Complex[n];
        double noise = Math.Sqrt(Math.Pow(10, -snrDb / 10) / 2);
        int symbol = 1;
        double phase0 = rng.NextDouble() * 2 * Math.PI;
        for (int i = 0; i < n; i++)
        {
            if ((int)(i * baud / Fs) != (int)((i - 1) * baud / Fs)) symbol = rng.Next(2) * 2 - 1;
            double ph = phase0 + 2 * Math.PI * offsetHz * i / Fs;
            x[i] = amplitude * symbol * Complex.FromPolarCoordinates(1, ph) + new Complex(Gauss(rng) * noise, Gauss(rng) * noise);
        }
        return x;
    }

    /// <summary>
    /// 궤도 예측과 실제 녹음의 대조 — ASRTU-1 녹음의 반송파를 0.2 초마다 재고, 같은 시각에 SGP4 로 예측한 도플러를 뺀다.
    /// 남는 값은 송신기(와 수신기) 주파수 오차다. 이 위성은 버스트마다 켜질 때 발진기가 데워지며 약 12 Hz 흘러가는데,
    /// 그 모양이 버스트마다 같으므로 **버스트별 중앙값은 같아야** 한다. 두 버스트 사이 도플러는 약 120 Hz 변하므로,
    /// 예측 도플러의 모양이 틀리거나 녹음 시각이 어긋나면 중앙값이 벌어진다.
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1Short)]
    [Trait("Requirement", "REQ-ORB-04")]
    public void RealRecording_CarrierFollowsPredictedDoppler()
    {
        var rec = RealRecording.Open(RealRecording.Asrtu1Short);
        var link = new LinkGeometry(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), LinkGeometryTests.Dwingeloo);
        const double nominalHz = 435.400e6; // SatNOGS DB 송신기 주파수
        double Predicted(DateTime utc) => nominalHz + link.At(utc).DopplerHz(nominalHz);

        var measured = BpskCarrierEstimator.Scan(rec, 0.2, 30.0, utc => Predicted(utc) - rec.CenterFrequencyHz);
        var residuals = measured.Select(m => (m.Utc, Residual: rec.CenterFrequencyHz + m.OffsetHz - Predicted(m.Utc))).ToList();

        // 1 초 넘게 비면 다른 버스트
        var bursts = new List<List<(DateTime Utc, double Residual)>> { new() };
        foreach (var r in residuals)
        {
            if (bursts[^1].Count > 0 && (r.Utc - bursts[^1][^1].Utc).TotalSeconds > 1.0) bursts.Add([]);
            bursts[^1].Add(r);
        }
        foreach (var b in bursts)
        {
            double doppler = link.At(b[b.Count / 2].Utc).DopplerHz(nominalHz);
            output.WriteLine($"버스트 {b[0].Utc:HH:mm:ss.f}–{b[^1].Utc:HH:mm:ss.f}  {b.Count} 구간  예측 도플러 {doppler:F1} Hz  " +
                             $"잔차 중앙값 {Median(b):F2} Hz  (버스트 안 {b.Min(r => r.Residual):F1}…{b.Max(r => r.Residual):F1})");
        }

        Assert.Equal(2, bursts.Count); // 4 초 버스트 두 번, 30 초 간격
        Assert.All(bursts, b => Assert.True(b.Count >= 18, $"버스트 구간 {b.Count} 개"));
        double predictedChange = link.At(bursts[1][0].Utc).DopplerHz(nominalHz) - link.At(bursts[0][0].Utc).DopplerHz(nominalHz);
        double medianGap = Median(bursts[1]) - Median(bursts[0]);
        output.WriteLine($"두 버스트 사이 예측 도플러 변화 {predictedChange:F1} Hz · 잔차 중앙값 차이 {medianGap:F2} Hz");
        // 송신기 주파수 오차는 두 버스트에 공통이라 빠지고, 남는 차이가 도플러 모양의 오차다
        Assert.True(Math.Abs(medianGap) < 2.0, $"잔차 중앙값이 버스트마다 {medianGap:F2} Hz 다르다 — 예측 도플러 변화 {predictedChange:F1} Hz 와 안 맞는다");
        Assert.True(Math.Abs(predictedChange) > 50, "두 버스트 사이 도플러가 거의 안 변하면 이 판정은 아무것도 보지 못한다");
    }

    private static double Median(List<(DateTime Utc, double Residual)> b)
    {
        var s = b.Select(r => r.Residual).Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : 0.5 * (s[s.Length / 2 - 1] + s[s.Length / 2]);
    }

    private static double Gauss(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
}
