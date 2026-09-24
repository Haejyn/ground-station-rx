using System.Buffers.Binary;
using System.Globalization;
using GroundStationRx.Orbit;
using GroundStationRx.Receiver;
using GroundStationRx.Recording;
using GroundStationRx.Simulation;
using GroundStationRx.Tests.Orbit;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Receiver;

/// <summary>
/// 녹음 없이 도는 끝에서 끝까지 시험 — 합성 송신기(수신 체인의 거울)로 정답을 아는 SigMF 녹음을 만들어 수신기에 넣는다.
/// 실제 녹음 시험은 녹음이 있어야 돌고(없으면 건너뜀) 뮤테이션 시험에서는 너무 느리다. 이 시험이 그 빈자리를 메운다.
/// </summary>
public sealed class SyntheticEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gsrx-e2e-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTime Start = new(2024, 12, 9, 7, 58, 14, DateTimeKind.Utc);
    private const double CenterHz = 435.3e6, Fs = 1_000_000, TxOffsetHz = 221.0;

    private string WriteRecording(int[] symbols, double esN0Db, int seed, LinkGeometry link)
    {
        // 반송파 = 공칭 + 버스트 가운데의 예측 도플러 + 송신기 오차 (버스트 4 초 동안 도플러는 약 16 Hz 변한다 — 수신기가 따라가야 한다)
        var mid = Start.AddSeconds(0.5 + symbols.Length / 9600.0 / 2);
        double offset = 435.4e6 + link.At(mid).DopplerHz(435.4e6) + TxOffsetHz - CenterHz;
        var x = BpskSignalGenerator.Generate(symbols, new BpskChannel
        {
            SampleRateHz = Fs, SymbolRateHz = 9600.7, TimingOffsetSymbols = 0.3, CarrierOffsetHz = offset,
            CarrierPhaseRad = 0.4, EsN0Db = esN0Db, Seed = seed,
        }, leadSamples: 500_000, tailSamples: 500_000);
        if (double.IsPositiveInfinity(esN0Db))
        {
            // 잡음이 전혀 없으면 신호 없는 구간의 "잡음 바닥" 이 0 이 된다 — 아주 약한 잡음을 깐다
            var rng = new Random(seed);
            for (int i = 0; i < x.Length; i++) x[i] += new System.Numerics.Complex(1e-3 * BpskSignalGenerator.Gauss(rng), 1e-3 * BpskSignalGenerator.Gauss(rng));
        }

        string meta = Path.Combine(_dir, $"synthetic_{seed}.sigmf-meta");
        File.WriteAllText(meta, $$"""
            {"global":{"core:datatype":"cf32_le","core:sample_rate":{{Fs.ToString(CultureInfo.InvariantCulture)}}},
             "captures":[{"core:sample_start":0,"core:frequency":{{CenterHz.ToString(CultureInfo.InvariantCulture)}},"core:datetime":"{{Start:yyyy-MM-ddTHH:mm:ss}}Z"}]}
            """);
        var bytes = new byte[x.Length * 8];
        for (int i = 0; i < x.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8 * i), (float)x[i].Real);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8 * i + 4), (float)x[i].Imaginary);
        }
        File.WriteAllBytes(Path.ChangeExtension(meta, ".sigmf-data"), bytes);
        return meta;
    }

    [Theory]
    [Trait("Requirement", "REQ-RX-06")]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(0.0, 2)]
    public void SyntheticBurst_FramesAndPacketsComeBackExactly(double esN0Db, int seed)
    {
        var link = new LinkGeometry(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), LinkGeometryTests.Dwingeloo);
        var packets = TransportTests.Packets(10, seed);
        var frames = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, packets, firstMc: 0x15, firstVcc: 0x4F);
        var symbols = CcsdsBurstTransmitter.Symbols(frames, preambleBits: 4500, appendPnFill: true, seed);
        var rec = SigmfRecording.Open(WriteRecording(symbols, esN0Db, seed, link));

        var result = new BpskCcsdsReceiver(ReceiverTests.Asrtu1, link).Process(rec);
        var burst = Assert.Single(result.Bursts);
        output.WriteLine($"Es/N0 {esN0Db} dB: 프레임 {frames.Count} 보냄 → {result.Frames.Count} 받음 · RS 정정 {string.Join(",", result.Frames.Select(f => f.CorrectedSymbols))} · " +
                         $"품질 {burst.DemodQuality:F1} · 심볼율 {burst.SymbolRateHz:F2} · 관성 버림 {string.Join("/", burst.FlywheelDropped)}");

        Assert.Equal(frames.Count, result.Frames.Count);
        for (int i = 0; i < frames.Count; i++) Assert.Equal(frames[i], result.Frames[i].Bytes);
        // 끝의 PN 채움(과 잡음 조건이면 그 뒤 잡음 구간의 관성 블록)은 버려지고, 받은 프레임 수가 정확하니 가짜 프레임은 0
        Assert.True(burst.FlywheelDropped.Sum() >= 1);
        Assert.InRange(burst.SymbolRateHz, 9600.6, 9600.8);

        var t = new ShortTmKissTransport(50, [2]);
        var got = result.Frames.SelectMany(f => t.Process(f.Bytes)).Select(p => p.Raw).ToList();
        Assert.Equal(packets.Count, got.Count);
        for (int i = 0; i < packets.Count; i++) Assert.Equal(packets[i], got[i]);
    }
}
