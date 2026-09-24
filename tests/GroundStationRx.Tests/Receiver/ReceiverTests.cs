using GroundStationRx.Orbit;
using GroundStationRx.Receiver;
using GroundStationRx.Tests.Orbit;
using GroundStationRx.Tests.Recording;
using SpaceLink;
using Xunit.Abstractions;

namespace GroundStationRx.Tests.Receiver;

public class ReceiverTests(ITestOutputHelper output)
{
    internal static readonly BpskCcsdsConfig Asrtu1 = new() { NominalCarrierHz = 435.400e6, SymbolRateHz = 9600 };

    internal static ReceptionResult ReceiveAsrtu1Short()
    {
        var link = new LinkGeometry(new Sgp4(Tle.Parse(TleTests.Asrtu1Line1, TleTests.Asrtu1Line2)), LinkGeometryTests.Dwingeloo);
        return new BpskCcsdsReceiver(Asrtu1, link).Process(RealRecording.Open(RealRecording.Asrtu1Short));
    }

    /// <summary>TM 주 헤더를 직접 읽는다 — 이 위성은 데이터 필드 상태가 비표준이라 SpaceLink 의 엄격한 판정을 통과하지 못한다.</summary>
    internal static (int Version, int Scid, int Vcid, int Mc, int Vcc) TmHeader(byte[] f) =>
        (f[0] >> 6, (f[0] & 0x3F) << 4 | f[1] >> 4, (f[1] >> 1) & 7, f[2], f[3]);

    /// <summary>
    /// ASRTU-1 녹음 68 초(버스트 2 개)에서 코드블록을 전부 살린다. 판정은 이 저장소 밖의 사실로 한다 —
    /// RS 가 맞다는 것(우연히 맞을 확률 2⁻²⁵⁶), 그리고 위성이 붙인 **마스터 채널 프레임 카운트가 두 버스트에 걸쳐 빈틈없이 이어진다**는 것.
    /// 카운트가 이어지면 버스트 안에서도, 버스트 사이에서도 놓친 TM 프레임이 없다.
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1Short)]
    [Trait("Requirement", "REQ-RX-01")]
    public void Asrtu1Recording_EveryTmFrameIsRecovered()
    {
        var result = ReceiveAsrtu1Short();
        foreach (var b in result.Bursts)
            output.WriteLine($"버스트 {b.Index}: {b.StartUtc:HH:mm:ss.fff}–{b.EndUtc:HH:mm:ss.fff}  반송파 {b.CarrierOffsetHz:F1} Hz  " +
                             $"심볼율 {b.SymbolRateHz:F2}  품질 {b.DemodQuality:F1}  심볼 {b.Symbols}  코드블록 {b.Codeblocks}  RS 실패 {b.RsFailures}  프레임 {b.Frames}  " +
                             $"마커 a0 [{string.Join(",", b.MarkerBits[0])}] a1 [{string.Join(",", b.MarkerBits[1])}]  관성 {string.Join("/", b.FlywheelBlocks)} 버림 {string.Join("/", b.FlywheelDropped)}  경계 {string.Join(" ", b.BoundaryWords[0].Select(w => w.ToString("X8")))}");
        foreach (var f in result.Frames)
        {
            var h = TmHeader(f.Bytes);
            output.WriteLine($"  b{f.Burst} a{f.Alignment} RS 정정 {f.CorrectedSymbols,2}  v{h.Version} SCID {h.Scid} VC {h.Vcid} MC {h.Mc:X2} VCC {h.Vcc:X2}  " +
                             $"SpaceLink {f.TmError}  {(h.Version == 0 ? Convert.ToHexString(f.Bytes, 0, 12) : Convert.ToHexString(f.Bytes))}");
        }

        Assert.Equal(2, result.Bursts.Count);
        Assert.All(result.Bursts, b => Assert.Equal(0, b.RsFailures));
        // 버스트 끝의 PN 채움 블록(마커 없음, RS 는 통과)은 경계가 확인되지 않아 버린다
        Assert.All(result.Bursts, b => Assert.Equal(1, b.FlywheelDropped[b.MarkerBits[0].Length > 0 ? 0 : 1]));
        Assert.All(result.Frames, f => Assert.Equal(0, TmHeader(f.Bytes).Version));
        var tm = result.Frames.ToList();
        Assert.All(tm, f => Assert.Equal(50, TmHeader(f.Bytes).Scid));
        Assert.All(tm, f => Assert.Contains(TmHeader(f.Bytes).Vcid, new[] { 0, 2 })); // gr-satellites 위성 정의의 가상 채널
        var mc = tm.Select(f => TmHeader(f.Bytes).Mc).ToList();
        Assert.Equal(Enumerable.Range(mc[0], mc.Count), mc); // 빈틈없이 이어진다
        Assert.Equal(12, tm.Count);                          // 버스트마다 6 장
        // 이 위성은 동기 플래그 0 인데 세그먼트 길이 ID 를 00 으로 보낸다(CCSDS 132.0 은 11 을 요구) — SpaceLink 가 그걸 잡는다
        Assert.All(tm, f => Assert.Equal(FrameError.InvalidDataFieldStatus, f.TmError));
    }

    /// <summary>
    /// 짧은 TM → KISS → 스페이스 패킷. 판정: 패킷 헤더의 길이 필드가 KISS 로 구분된 실제 길이와 맞고(SpaceLink 가 확인),
    /// 위성이 붙인 순서 카운트가 빈틈없이 이어진다 — 이 위성은 APID 와 상관없이 카운트 하나를 같이 쓴다(4923 → 4934).
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1Short)]
    [Trait("Requirement", "REQ-RX-02")]
    public void Asrtu1Recording_SpacePacketsAreExtracted()
    {
        var result = ReceiveAsrtu1Short();
        var transport = new ShortTmKissTransport(50, [0, 2]);
        var packets = result.Frames.SelectMany(f => transport.Process(f.Bytes)).ToList();
        foreach (var p in packets)
            output.WriteLine($"VC {p.VirtualChannelId} APID {p.Packet.Apid} 순서 {p.Packet.SequenceCount} 길이 {p.Raw.Length}  {Convert.ToHexString(p.Raw, 0, Math.Min(24, p.Raw.Length))}");
        output.WriteLine($"패킷 {packets.Count} · 버린 패킷 {transport.PacketsDropped} · 거부 프레임 {transport.FramesRejected}");

        Assert.Equal(12, packets.Count);
        Assert.Equal(0, transport.PacketsDropped);
        var seq = packets.Select(p => (int)p.Packet.SequenceCount).ToList();
        Assert.Equal(Enumerable.Range(seq[0], seq.Count), seq); // 놓친 패킷 0
        Assert.Equal(0, transport.FramesRejected);
    }

    /// <summary>
    /// 관성 블록 정책 — 가운데 ASM 이 깨진 블록은 뒤 마커가 경계를 확인해 살리고, 끝의 마커 없는 블록은 버린다.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-RX-01")]
    public void FlywheelBlock_IsKeptOnlyWhenALaterMarkerConfirmsTheGrid()
    {
        var rng = new Random(4);
        byte[] Cadu(bool goodMarker)
        {
            var c = new byte[259];
            rng.NextBytes(c);
            uint asm = goodMarker ? 0x1ACFFC1Du : 0x1ACFFC1Du ^ 0x0F0F0000u; // 8 비트 오류 — 동기기 허용치(3) 밖
            c[0] = (byte)(asm >> 24); c[1] = (byte)(asm >> 16); c[2] = (byte)(asm >> 8); c[3] = (byte)asm;
            return c;
        }
        var stream = new List<byte>();
        stream.AddRange(new byte[37]);                    // 앞쪽 잡음 자리
        var cadus = new[] { Cadu(true), Cadu(true), Cadu(false), Cadu(true), Cadu(false) };
        foreach (var c in cadus) stream.AddRange(c);
        stream.AddRange(new byte[300]);                   // 끝 뒤에도 동기기가 한 블록을 더 읽을 만큼

        var blocks = BpskCcsdsReceiver.ConfirmedCodeblocks(stream.ToArray(), out int flywheel, out int dropped);
        output.WriteLine($"받은 블록 {blocks.Count} · 관성 {flywheel} · 버림 {dropped}");
        Assert.Equal(4, blocks.Count);                    // 앞 둘 + 가운데 관성(확인됨) + 넷째
        Assert.True(blocks[2].AsSpan().SequenceEqual(cadus[2].AsSpan(4)));
        Assert.True(dropped >= 1);                        // 끝의 마커 없는 블록(과 그 뒤 관성)은 확인되지 않는다
        Assert.All(blocks, b => Assert.False(b.AsSpan().SequenceEqual(cadus[4].AsSpan(4))));
    }

    /// <summary>
    /// 독립 구현과의 바이트 대조 — 같은 녹음을 gr-satellites(되먹임 루프 복조 · GNU Radio 비터비 · 매 프레임 ASM 탐색)로 복호한
    /// 12 장(<c>tools/gen_golden_grsatellites.py</c>)과 이 수신기의 12 장이 **순서까지 한 바이트도 다르지 않아야** 한다.
    /// </summary>
    [RecordingFact(RealRecording.Asrtu1Short)]
    [Trait("Requirement", "REQ-RX-03")]
    public void Asrtu1Recording_FramesMatchGrSatellitesByteForByte()
    {
        var reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", "asrtu1_grsatellites_frames.bin"))
            .Chunk(223).ToList();
        var ours = ReceiveAsrtu1Short().Frames.Select(f => f.Bytes).ToList();
        output.WriteLine($"gr-satellites {reference.Count} 장 · 이 수신기 {ours.Count} 장");
        Assert.Equal(12, reference.Count);
        Assert.Equal(reference.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
            Assert.True(reference[i].AsSpan().SequenceEqual(ours[i]), $"{i} 번째 프레임이 다르다");
    }
}
