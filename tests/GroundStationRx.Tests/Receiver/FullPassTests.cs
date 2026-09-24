using GroundStationRx.Dsp;
using GroundStationRx.Orbit;
using GroundStationRx.Receiver;
using GroundStationRx.Tests.Orbit;
using GroundStationRx.Tests.Recording;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Receiver;

public class FullPassTests(ITestOutputHelper output)
{
    private const double NominalHz = 435.4e6;

    private static LinkGeometry Link() =>
        new(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), LinkGeometryTests.Dwingeloo);

    /// <summary>
    /// 패스 전체 560 초(도플러 +9.9 → −10.1 kHz, 고도 10.5° → 2°)에서 녹음에 신호가 있는 프레임을 전부 살린다.
    /// 판정은 위성이 붙인 마스터 채널 카운트 — 빈 곳은 한 군데뿐이어야 하고, 그 자리는 녹음에 반송파가 없는 버스트(30 초 간격이 60 초로 벌어짐)여야 한다.
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1FullPass)]
    [Trait("Requirement", "REQ-RX-04")]
    public void FullPass_EveryFramePresentInTheSignalIsRecovered()
    {
        var link = Link();
        var rec = RealRecording.Open(RealRecording.Asrtu1FullPass);
        var result = new BpskCcsdsReceiver(ReceiverTests.Asrtu1, link).Process(rec);
        foreach (var b in result.Bursts)
        {
            var l = link.At(b.StartUtc + (b.EndUtc - b.StartUtc) / 2);
            output.WriteLine($"버스트 {b.Index,2}: {b.StartUtc:HH:mm:ss}  고도 {l.ElevationDeg,5:F1}°  반송파 {b.CarrierOffsetHz,9:F1} Hz  " +
                             $"품질 {b.DemodQuality,5:F1}  심볼율 {b.SymbolRateHz:F2}  블록 {b.Codeblocks}  RS 실패 {b.RsFailures}  프레임 {b.Frames}");
        }
        var mc = result.Frames.Select(f => ShortTmHeader.Read(f.Bytes).MasterChannelFrameCount).ToList();
        var gaps = mc.Zip(mc.Skip(1)).Where(p => ((p.First + 1) & 0xFF) != p.Second).ToList();
        output.WriteLine($"프레임 {result.Frames.Count} · MC {mc[0]:X2}→{mc[^1]:X2} · 빈 곳 {string.Join(", ", gaps.Select(g => $"{g.First:X2}→{g.Second:X2}"))}");

        Assert.Equal(17, result.Bursts.Count);
        Assert.All(result.Frames, f => Assert.Equal(50, ShortTmHeader.Read(f.Bytes).SpacecraftId));
        var gap = Assert.Single(gaps);
        int missing = ((gap.Second - gap.First) & 0xFF) - 1;
        Assert.Equal(mc.Count + missing, ((mc[^1] - mc[0]) & 0xFF) + 1);
        // 빈 곳은 버스트 하나가 통째로 없는 자리 — 앞뒤 버스트 간격이 평소(30 초)의 두 배
        int before = result.Frames.First(f => ShortTmHeader.Read(f.Bytes).MasterChannelFrameCount == gap.First).Burst;
        double spacing = (result.Bursts[before + 1].StartUtc - result.Bursts[before].StartUtc).TotalSeconds;
        output.WriteLine($"빈 곳 앞뒤 버스트 간격 {spacing:F1} 초 · 잃은 프레임 {missing}");
        Assert.InRange(spacing, 58, 62);
        Assert.InRange(missing, 6, 7);
    }

    [Fact]
    [Trait("Requirement", "REQ-ORB-05")]
    public void DopplerFit_RecoversInjectedTimeAndFrequencyOffsets()
    {
        var link = Link();
        var start = new DateTime(2024, 12, 9, 7, 59, 10, DateTimeKind.Utc);
        var rng = new Random(3);
        var pts = Enumerable.Range(0, 500).Select(k =>
        {
            var t = start.AddSeconds(k);
            double truth = NominalHz + link.At(t.AddSeconds(0.23)).DopplerHz(NominalHz) + 221.0;
            return (t, truth + (rng.NextDouble() - 0.5) * 10); // ±5 Hz 균일 잡음(발진기 가열 흐름 정도)
        }).ToList();
        var fit = DopplerResidualFit.Fit(pts, link, NominalHz);
        output.WriteLine($"c {fit.FrequencyOffsetHz:F2} Hz · Δt {fit.TimeOffsetSeconds * 1000:F1} ms · RMS {fit.RmsBeforeHz:F1} → {fit.RmsAfterHz:F2} Hz");
        Assert.InRange(fit.FrequencyOffsetHz, 220.0, 222.0);
        Assert.InRange(fit.TimeOffsetSeconds, 0.21, 0.25);
    }

    /// <summary>
    /// 실제 패스 전체의 반송파(0.2 초 구간, 선 30 dB 이상)로 고정 주파수 오차와 시각 어긋남을 함께 푼다.
    ///
    /// 처음 세운 가설은 "최근접 근처 버스트의 잔차가 18 Hz 튀는 것은 시각 어긋남(약 0.17 초) 때문" 이었다. 구간 단위로 풀자
    /// 시각 항은 잔차를 거의 설명하지 못했다(RMS 5.12 → 4.73 Hz, Δt ≈ −53 ms) — 18 Hz 는 버스트 요약값을 만들 때
    /// 중앙값 시각과 예측 시각이 어긋난 탓(도플러 변화율 −100 Hz/s × 약 0.18 초)이었다. 그래서 판정은 데이터가 지지하는 쪽으로 둔다:
    /// 20 kHz 를 쓸어 가는 패스 전체에서 예측과 실측의 차이가 **고정 오차 하나와 발진기 가열 흐름(±7 Hz) 수준으로 설명**되고,
    /// 시각 어긋남은 0.2 초보다 작다(TLE 진행 방향 오차 1.5 km 미만에 해당).
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1FullPass)]
    [Trait("Requirement", "REQ-ORB-05")]
    public void FullPass_PredictedDopplerMatchesWithinTransmitterDrift()
    {
        var link = Link();
        var rec = RealRecording.Open(RealRecording.Asrtu1FullPass);
        var scan = BpskCarrierEstimator.Scan(rec, 0.2, 30.0, u => NominalHz + link.At(u).DopplerHz(NominalHz) - rec.CenterFrequencyHz);
        var pts = scan.Select(m => (m.Utc, rec.CenterFrequencyHz + m.OffsetHz)).ToList();
        var fit = DopplerResidualFit.Fit(pts, link, NominalHz);
        double sweep = pts.Max(p => p.Item2) - pts.Min(p => p.Item2);
        output.WriteLine($"{fit.Points} 구간 · 실측 스윕 {sweep / 1000:F2} kHz · 고정 오차 {fit.FrequencyOffsetHz:F1} Hz · " +
                         $"시각 어긋남 {fit.TimeOffsetSeconds * 1000:F0} ± {fit.TimeOffsetStdErrSeconds * 1000:F0} ms (진행 방향 {fit.TimeOffsetSeconds * 7.6:F2} km) · " +
                         $"잔차 RMS {fit.RmsBeforeHz:F2} → {fit.RmsAfterHz:F2} Hz");

        Assert.True(fit.Points > 300);
        Assert.True(sweep > 19_000, "패스가 최근접을 지나 도플러를 쓸어 가야 판정이 의미 있다");
        Assert.True(fit.RmsBeforeHz < 7.0, $"고정 오차만 뺀 잔차 RMS {fit.RmsBeforeHz:F2} Hz");
        Assert.InRange(fit.FrequencyOffsetHz, 215.0, 235.0); // 짧은 녹음에서 잰 221 Hz 와 같은 송신기
        Assert.InRange(Math.Abs(fit.TimeOffsetSeconds), 0.0, 0.2);
    }

    /// <summary>
    /// 독립 복호 결과 둘과 대조한다 — 둘 다 gr-satellites, 입력만 다르다.
    ///  (1) 원시 IQ 를 **도플러 보정 없이**(FLL 이 따라감) 복호한 것 (2) SatNOGS 가 자기 궤도 예측으로 도플러를 보정해 기록한 IQ 를 복호한 것.
    /// (2) 는 이 저장소의 궤도·도플러 코드를 한 줄도 거치지 않는다. 두 기준이 낸 프레임은 **전부** 이 수신기 결과에 같은 바이트로 있어야 한다.
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1FullPass)]
    [Trait("Requirement", "REQ-RX-05")]
    public void FullPass_EveryFrameFromIndependentDecodersIsReproduced()
    {
        var link = Link();
        var ours = new List<byte[]>();
        foreach (string name in new[] { RealRecording.Asrtu1Short, RealRecording.Asrtu1FullPass })
            ours.AddRange(new BpskCcsdsReceiver(ReceiverTests.Asrtu1, link).Process(RealRecording.Open(name)).Frames.Select(f => f.Bytes));
        var ourSet = ours.Select(Convert.ToHexString).ToHashSet();

        foreach (var (file, label) in new[]
        {
            ("asrtu1_grsatellites_frames.bin", "gr-satellites · 짧은 녹음"),
            ("asrtu1_fullpass_grsatellites_frames.bin", "gr-satellites · 전체 패스(도플러 보정 없음)"),
            ("asrtu1_satnogs_iq_grsatellites_frames.bin", "gr-satellites · SatNOGS IQ(SatNOGS 도플러 보정, 포화)"),
        })
        {
            var theirs = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", file)).Chunk(223)
                .Select(Convert.ToHexString).ToHashSet();
            var missing = theirs.Where(h => !ourSet.Contains(h)).ToList();
            output.WriteLine($"{label}: {theirs.Count} 장(중복 제거) · 이 수신기에 없는 것 {missing.Count}");
            foreach (var m in missing)
                output.WriteLine($"   없음: MC {m[4..6]} {m[..24]}");
            Assert.Empty(missing);
        }
        var reference = new[] { "asrtu1_grsatellites_frames.bin", "asrtu1_fullpass_grsatellites_frames.bin", "asrtu1_satnogs_iq_grsatellites_frames.bin" }
            .SelectMany(f => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", f)).Chunk(223).Select(Convert.ToHexString)).ToHashSet();
        var onlyOurs = ourSet.Where(h => !reference.Contains(h)).Select(h => h[4..6]).Order().ToList();
        output.WriteLine($"이 수신기 {ourSet.Count} 장 · 기준 합집합 {reference.Count} 장 · 이 수신기만 받은 것 {onlyOurs.Count} (MC {string.Join(" ", onlyOurs)})");
    }
}
