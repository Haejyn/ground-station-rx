using GroundStationRx.Receiver;
using GroundStationRx.Simulation;
using SpaceLink;

namespace GroundStationRx.Tests.Receiver;

public class TransportTests
{
    /// <summary>KISS 이스케이프가 필요한 바이트(0xC0 · 0xDB)를 일부러 많이 넣은 패킷들 — 프레임 경계를 여러 번 넘는다.</summary>
    internal static List<byte[]> Packets(int count, int seed, int firstSeq = 100)
    {
        var rng = new Random(seed);
        var list = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            var data = new byte[rng.Next(1, 300)];
            rng.NextBytes(data);
            for (int k = 0; k < data.Length; k += 7) data[k] = rng.Next(2) == 0 ? (byte)0xC0 : (byte)0xDB;
            list.Add(new SpacePacket((ushort)rng.Next(0, 2048), (ushort)(firstSeq + i), data).Encode());
        }
        return list;
    }

    [Fact]
    [Trait("Requirement", "REQ-RX-02")]
    public void Packets_SurviveKissFramingAcrossFrameBoundaries()
    {
        var sent = Packets(40, 1);
        var frames = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, sent, firstMc: 250, firstVcc: 250); // 카운트가 255 → 0 으로 돈다
        var t = new ShortTmKissTransport(50, [0, 2]);
        var got = frames.SelectMany(f => t.Process(f)).ToList();
        Assert.Equal(sent.Count, got.Count);
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.Equal(sent[i], got[i].Raw);
            Assert.Equal(2, got[i].VirtualChannelId);
        }
        Assert.Equal(0, t.PacketsDropped);
    }

    [Fact]
    [Trait("Requirement", "REQ-RX-02")]
    public void LostFrame_DropsOnlyThePacketsItTouched()
    {
        var sent = Packets(40, 2);
        var frames = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, sent, 0, 0);
        int lost = frames.Count / 2;
        var t = new ShortTmKissTransport(50, [2]);
        var got = frames.Where((_, i) => i != lost).SelectMany(f => t.Process(f)).Select(p => p.Raw).ToList();

        // 잃은 프레임에 한 바이트라도 걸친 패킷은 나오면 안 되고, 나머지는 전부 나와야 한다
        int body = CcsdsBurstTransmitter.FrameLength - ShortTmHeader.Length;
        long lostStart = (long)lost * body, lostEnd = lostStart + body;
        var expected = new List<byte[]>();
        long pos = 0;
        foreach (var p in sent)
        {
            long escaped = 1 + p.Sum(b => b is 0xC0 or 0xDB ? 2 : 1); // 앞 FEND + 이스케이프한 본문
            long start = pos, end = pos + escaped + 1;                // 뒤 FEND 까지 읽어야 끝난다
            if (end <= lostStart || start >= lostEnd) expected.Add(p);
            pos += escaped;
        }
        Assert.Equal(expected.Count, got.Count);
        for (int i = 0; i < got.Count; i++) Assert.Equal(expected[i], got[i]);
        Assert.True(t.PacketsDropped >= 1);
    }

    [Fact]
    [Trait("Requirement", "REQ-RX-02")]
    public void ForeignFramesAndBrokenPackets_AreRejected()
    {
        var sent = Packets(3, 3);
        var t = new ShortTmKissTransport(50, [2]);
        Assert.Empty(t.Process(CcsdsBurstTransmitter.ShortTmKissFrames(51, 2, sent, 0, 0)[0])); // 다른 위성
        Assert.Empty(t.Process(CcsdsBurstTransmitter.ShortTmKissFrames(50, 3, sent, 0, 0)[0])); // 다른 가상 채널
        var v1 = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, sent, 0, 0)[0];
        v1[0] |= 0x40;                                                                             // 버전 1
        Assert.Empty(t.Process(v1));
        Assert.Equal(3, t.FramesRejected);

        // 길이 필드와 실제 길이가 다른 패킷은 버린다
        var bad = (byte[])sent[0].Clone();
        bad[5] ^= 0x01;
        var t2 = new ShortTmKissTransport(50, [2]);
        var out2 = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, [bad, sent[1]], 0, 0).SelectMany(f => t2.Process(f)).ToList();
        Assert.Equal(sent[1], Assert.Single(out2).Raw);
        Assert.Equal(1, t2.PacketsDropped);
    }

    [Fact]
    [Trait("Requirement", "REQ-RX-02")]
    public void BytesBeforeTheFirstDelimiter_AreNotAPacket()
    {
        // 수신을 패킷 한가운데서 시작하면 첫 FEND 앞의 바이트는 앞부분이 없는 조각이다
        var sent = Packets(6, 4);
        var frames = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, sent, 0, 0);
        var t = new ShortTmKissTransport(50, [2]);
        var got = frames.Skip(1).SelectMany(f => t.Process(f)).Select(p => p.Raw).ToList();
        Assert.All(got, g => Assert.Contains(sent, s => s.AsSpan().SequenceEqual(g)));
        Assert.DoesNotContain(got, g => g.AsSpan().SequenceEqual(sent[0]));
    }

    /// <summary>
    /// 잃은 프레임 바로 뒤 조각이 **우연히 올바른 패킷 모양**이어도 내보내지 않는다 — 큰 패킷 P 안에 정상 모양의 작은 패킷 Q 를 숨겨,
    /// P 의 앞부분이 든 프레임을 잃으면 뒤 프레임은 Q 로 시작해 FEND 로 끝난다. 길이 검사로는 못 가려낸다(Q 는 길이가 맞다).
    /// 가상 채널 카운트의 빈틈을 보고 "앞부분 없는 조각" 이라고 표시하는 것만이 막는다 — 표시를 끄면 위조 패킷 Q 가 조용히 나간다.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-RX-02")]
    public void FragmentAfterLostFrame_IsNotEmittedEvenIfItLooksLikeAPacket()
    {
        const int body = CcsdsBurstTransmitter.FrameLength - ShortTmHeader.Length; // 218
        // 순서 플래그 "분할 없음"(11)은 헤더 셋째 바이트를 0xC0(=FEND)로 만들어 KISS 이스케이프로 길이가 바뀐다 — 위치를 맞추려고 "첫 조각"(01)
        var r = new SpacePacket(10, 1, Enumerable.Repeat((byte)0x22, 94).ToArray(), SequenceFlags.First).Encode();   // 100 바이트
        var q = new SpacePacket(99, 7, Enumerable.Repeat((byte)0x11, 44).ToArray(), SequenceFlags.First).Encode();   // 50 바이트, 숨길 패킷
        // 흐름: FEND R FEND P ... — Q 가 셋째 프레임 본문의 첫 바이트(2·218)에서 시작해 P 의 끝과 같이 끝나게 P 를 짠다
        int pStart = 1 + r.Length + 1;
        int filler = 2 * body - pStart - SpacePacket.PrimaryHeaderLength;
        var pData = Enumerable.Repeat((byte)0x33, filler).Concat(q).ToArray();
        var p = new SpacePacket(20, 2, pData, SequenceFlags.First).Encode();
        var frames = CcsdsBurstTransmitter.ShortTmKissFrames(50, 2, [r, p], 0, 0);
        Assert.True(frames[2].AsSpan(ShortTmHeader.Length, q.Length).SequenceEqual(q)); // 셋째 프레임이 Q 로 시작한다

        var t = new ShortTmKissTransport(50, [2]);
        var got = frames.Where((_, i) => i != 1).SelectMany(f => t.Process(f)).Select(x => x.Raw).ToList();
        Assert.Equal(r, Assert.Single(got));                  // R 만 나오고
        Assert.DoesNotContain(got, g => g.AsSpan().SequenceEqual(q)); // 숨긴 Q 는 나오지 않는다
        Assert.Equal(1, t.PacketsDropped);
    }
}
