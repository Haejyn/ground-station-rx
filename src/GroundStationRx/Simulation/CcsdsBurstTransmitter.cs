using GroundStationRx.Coding;
using SpaceLink.ChannelCoding;

namespace GroundStationRx.Simulation;

/// <summary>
/// 합성 송신기 — 수신 체인의 거울. 정답을 아는 버스트를 만들어 녹음 없이도 수신기를 끝에서 끝까지 시험한다.
///
/// 스페이스 패킷 → KISS → 짧은 TM 프레임(223 바이트) → RS(관례 기저) → PN → ASM → NRZ-M → 길쌈 부호(K=7, G2 반전) → ±1 심볼.
/// ASRTU-1 실제 버스트처럼 앞에 전치 신호, 마지막 프레임 뒤에 **ASM 없는 PN 역순 채움**을 붙일 수 있다(수신기의 관성 블록 정책을 시험하려고).
/// </summary>
public static class CcsdsBurstTransmitter
{
    public const int FrameLength = ConventionalReedSolomon.DataLength;
    private const int ShortHeader = 5;
    private const byte Fend = 0xC0, Fesc = 0xDB, Tfend = 0xDC, Tfesc = 0xDD;

    /// <summary>패킷들을 KISS 로 감싸 짧은 TM 프레임에 차례로 채운다. 남는 자리는 FEND 로 채운다(실제 위성과 같은 모양).</summary>
    public static List<byte[]> ShortTmKissFrames(int spacecraftId, int virtualChannel, IEnumerable<byte[]> packets, int firstMc, int firstVcc)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var stream = new List<byte>();
        foreach (var p in packets)
        {
            stream.Add(Fend);
            foreach (byte b in p)
            {
                if (b == Fend) { stream.Add(Fesc); stream.Add(Tfend); }
                else if (b == Fesc) { stream.Add(Fesc); stream.Add(Tfesc); }
                else stream.Add(b);
            }
        }
        stream.Add(Fend);

        var frames = new List<byte[]>();
        int body = FrameLength - ShortHeader;
        for (int off = 0, k = 0; off < stream.Count; off += body, k++)
        {
            var f = new byte[FrameLength];
            f[0] = (byte)((spacecraftId >> 4) & 0x3F);
            f[1] = (byte)((spacecraftId & 0xF) << 4 | (virtualChannel & 7) << 1);
            f[2] = (byte)(firstMc + k);
            f[3] = (byte)(firstVcc + k);
            for (int i = 0; i < body; i++)
                f[ShortHeader + i] = off + i < stream.Count ? stream[off + i] : Fend;
            frames.Add(f);
        }
        return frames;
    }

    /// <summary>프레임들 → 채널 심볼(+1 = 비트 0, −1 = 비트 1).</summary>
    public static int[] Symbols(IReadOnlyList<byte[]> frames, int preambleBits, bool appendPnFill, int seed)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var rng = new Random(seed);
        var bytes = new List<byte>();
        var rs = new ConventionalReedSolomon();
        foreach (var f in frames)
        {
            var cb = rs.Encode(f);
            Pseudorandomizer.Apply(cb, PseudorandomSequence.Legacy255);
            bytes.AddRange([0x1A, 0xCF, 0xFC, 0x1D]);
            bytes.AddRange(cb);
        }
        if (appendPnFill)
        {
            var pn = Pseudorandomizer.Sequence(255, PseudorandomSequence.Legacy255);
            for (int j = 0; j < 4 + 255; j++) bytes.Add(pn[((45 - j) % 255 + 255) % 255]); // ASM 자리까지 PN 역순
        }

        var bits = new List<byte>();
        for (int i = 0; i < preambleBits; i++) bits.Add((byte)rng.Next(2));
        foreach (byte b in bytes)
            for (int k = 7; k >= 0; k--) bits.Add((byte)((b >> k) & 1));
        for (int i = 0; i < 64; i++) bits.Add((byte)rng.Next(2)); // 비터비 꼬리

        var coded = ConvolutionalCode.Encode(Nrzm.Encode([.. bits]));
        return [.. coded.Select(c => 1 - 2 * c)];
    }
}
