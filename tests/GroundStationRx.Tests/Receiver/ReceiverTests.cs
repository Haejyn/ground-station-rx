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
                             $"심볼율 {b.SymbolRateHz:F2}  품질 {b.DemodQuality:F1}  심볼 {b.Symbols}  코드블록 {b.Codeblocks}  RS 실패 {b.RsFailures}  프레임 {b.Frames}");
        foreach (var f in result.Frames)
        {
            var h = TmHeader(f.Bytes);
            output.WriteLine($"  b{f.Burst} a{f.Alignment} RS 정정 {f.CorrectedSymbols,2}  v{h.Version} SCID {h.Scid} VC {h.Vcid} MC {h.Mc:X2} VCC {h.Vcc:X2}  " +
                             $"SpaceLink {f.TmError}  {Convert.ToHexString(f.Bytes, 0, 12)}");
        }

        Assert.Equal(2, result.Bursts.Count);
        Assert.All(result.Bursts, b => Assert.Equal(0, b.RsFailures));
        var tm = result.Frames.Where(f => TmHeader(f.Bytes).Version == 0).ToList();
        Assert.All(tm, f => Assert.Equal(50, TmHeader(f.Bytes).Scid));
        Assert.All(tm, f => Assert.Contains(TmHeader(f.Bytes).Vcid, new[] { 0, 2 })); // gr-satellites 위성 정의의 가상 채널
        var mc = tm.Select(f => TmHeader(f.Bytes).Mc).ToList();
        Assert.Equal(Enumerable.Range(mc[0], mc.Count), mc); // 빈틈없이 이어진다
        Assert.Equal(12, tm.Count);                          // 버스트마다 6 장
        // 이 위성은 동기 플래그 0 인데 세그먼트 길이 ID 를 00 으로 보낸다(CCSDS 132.0 은 11 을 요구) — SpaceLink 가 그걸 잡는다
        Assert.All(tm, f => Assert.Equal(FrameError.InvalidDataFieldStatus, f.TmError));
    }
}
