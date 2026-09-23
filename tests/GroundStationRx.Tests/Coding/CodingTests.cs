using GroundStationRx.Coding;
using GroundStationRx.Simulation;
using GroundStationRx.Tests.Dsp;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Coding;

public class CodingTests(ITestOutputHelper output)
{
    private static byte[] RandomBits(int n, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (byte)rng.Next(2)).ToArray();
    }

    private static double[] ToSoft(byte[] symbols, double esN0Db, int seed)
    {
        // 비트 0 → +1, 비트 1 → −1, 복소 기저대역의 실수축 잡음 분산 N0/2 (Es = 1)
        var rng = new Random(seed);
        double sigma = double.IsPositiveInfinity(esN0Db) ? 0 : Math.Sqrt(1 / Math.Pow(10, esN0Db / 10) / 2);
        return symbols.Select(s => (1.0 - 2 * s) + sigma * BpskSignalGenerator.Gauss(rng)).ToArray();
    }

    [Fact]
    [Trait("Requirement", "REQ-FEC-01")]
    public void Viterbi_NoiselessRoundTrip_AndInversionTransparency()
    {
        var bits = RandomBits(10_000, 1);
        var soft = ToSoft(ConvolutionalCode.Encode(bits), double.PositiveInfinity, 0);
        Assert.Equal(bits, ConvolutionalCode.Decode(soft));
        // 채널 심볼이 전부 뒤집히면(180° 모호성) 복호 비트도 전부 뒤집힌다 — 두 생성 다항식의 무게가 홀수라서
        var inverted = ConvolutionalCode.Decode(soft.Select(s => -s).ToArray());
        Assert.Equal(bits.Select(b => (byte)(b ^ 1)).ToArray(), inverted);
        // 그리고 NRZ-M 을 거치면 뒤집힘이 사라진다(첫 비트만 기준 준위를 모른다)
        var diffA = Nrzm.Decode(Nrzm.Encode(bits));
        var diffB = Nrzm.Decode(Nrzm.Encode(bits).Select(b => (byte)(b ^ 1)).ToArray());
        Assert.Equal(bits, diffA);
        Assert.Equal(bits[1..], diffB[1..]);
    }

    /// <summary>
    /// 연판정 비터비의 부호 이득 — K=7 r=1/2 는 Eb/N0 3 dB 에서 BER 1e-3 수준, 4.5 dB 에서 1e-5 수준이 교과서 값이다.
    /// 부호화하지 않은 BPSK(3 dB 에서 2.3e-2)보다 한 자릿수 이상 좋아야 한다.
    /// </summary>
    [Theory]
    [Trait("Requirement", "REQ-FEC-01")]
    [InlineData(3.0, 200_000, 1.5e-3)]
    [InlineData(4.5, 400_000, 3e-5)]
    public void Viterbi_CodingGain(double ebN0Db, int nbits, double maxBer)
    {
        var bits = RandomBits(nbits, 2);
        double esN0 = ebN0Db + 10 * Math.Log10(0.5); // 부호율 1/2 → 심볼당 에너지는 비트당의 절반
        var decoded = ConvolutionalCode.Decode(ToSoft(ConvolutionalCode.Encode(bits), esN0, 3));
        int errors = bits.Zip(decoded).Count(p => p.First != p.Second);
        double ber = (double)errors / nbits;
        double uncoded = DemodulatorTests.TheoryBer(ebN0Db);
        output.WriteLine($"Eb/N0 {ebN0Db} dB: 부호화 BER {ber:E2} ({errors}/{nbits}) · 부호화 안 한 BPSK {uncoded:E2}");
        Assert.True(ber <= maxBer, $"BER {ber:E2} > {maxBer:E2}");
        Assert.True(ber < uncoded / 10);
    }

    [Fact]
    [Trait("Requirement", "REQ-FEC-02")]
    public void BasisTransform_RoundTripsAll256Values()
    {
        for (int v = 0; v < 256; v++)
            Assert.Equal(v, ConventionalReedSolomon.ToConventional[ConventionalReedSolomon.ToDual[v]]);
        Assert.Equal(256, ConventionalReedSolomon.ToDual.Distinct().Count());
    }

    /// <summary>
    /// 독립 구현(파이썬 reedsolo, 관례 기저)이 만든 부호어 680 개 — 오류 0~16 개 각 40 개. 16 개까지는 전부 원래 데이터로 돌아와야 한다.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-FEC-02")]
    public void ConventionalCodewordsFromReedsolo_AreCorrectedUpTo16Errors()
    {
        var blob = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "rs_conventional.bin"));
        const int record = 1 + 255 + 223;
        Assert.Equal(0, blob.Length % record);
        var rs = new ConventionalReedSolomon();
        var data = new byte[223];
        int cases = 0;
        for (int off = 0; off < blob.Length; off += record)
        {
            int nerr = blob[off];
            var result = rs.Decode(blob.AsSpan(off + 1, 255), data);
            Assert.True(result.Succeeded, $"사례 {cases} (오류 {nerr}) 복호 실패");
            Assert.Equal(nerr, result.CorrectedSymbols);
            Assert.True(blob.AsSpan(off + 256, 223).SequenceEqual(data), $"사례 {cases} (오류 {nerr}) 데이터 불일치");
            cases++;
        }
        Assert.Equal(680, cases);
    }

    [Fact]
    [Trait("Requirement", "REQ-FEC-02")]
    public void SeventeenErrors_AreNotSilentlyReturnedAsOriginal()
    {
        var blob = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "rs_conventional.bin"));
        var rng = new Random(5);
        var rs = new ConventionalReedSolomon();
        var data = new byte[223];
        int failures = 0;
        for (int c = 0; c < 200; c++)
        {
            var cw = blob.AsSpan(1, 255).ToArray(); // 오류 0 개 사례의 깨끗한 부호어
            foreach (int pos in Enumerable.Range(0, 255).OrderBy(_ => rng.Next()).Take(17))
                cw[pos] ^= (byte)rng.Next(1, 256);
            var r = rs.Decode(cw, data);
            if (!r.Succeeded) failures++;
            else Assert.False(blob.AsSpan(256, 223).SequenceEqual(data), "17 개 오류인데 원래 데이터라고 했다");
        }
        output.WriteLine($"오류 17 개 200 회: 실패 선언 {failures}, 다른 부호어로 오정정 {200 - failures}");
        Assert.True(failures >= 190);
    }
}
