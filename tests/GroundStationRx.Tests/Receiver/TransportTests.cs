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
}
