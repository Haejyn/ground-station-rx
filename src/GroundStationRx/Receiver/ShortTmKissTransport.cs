using SpaceLink;

namespace GroundStationRx.Receiver;

/// <summary>짧은 TM 헤더(5 바이트) — CCSDS 132.0 의 6 바이트 주 헤더에서 데이터 필드 상태 2 바이트 대신 포인터 1 바이트만 둔 변형.</summary>
public readonly record struct ShortTmHeader(int Version, int SpacecraftId, int VirtualChannelId, bool OcfFlag,
    int MasterChannelFrameCount, int VirtualChannelFrameCount, int FirstHeaderPointer)
{
    public const int Length = 5;

    public static ShortTmHeader Read(ReadOnlySpan<byte> f) => new(
        f[0] >> 6, (f[0] & 0x3F) << 4 | f[1] >> 4, (f[1] >> 1) & 7, (f[1] & 1) != 0, f[2], f[3], f[4]);
}

/// <summary>꺼낸 스페이스 패킷 — 어느 가상 채널에서 왔는지와 함께.</summary>
public sealed record ExtractedPacket(int VirtualChannelId, SpacePacket Packet, byte[] Raw);

/// <summary>
/// 짧은 TM 프레임의 데이터 필드에 KISS(FEND 0xC0 로 구분, 0xDB 이스케이프)로 감싼 스페이스 패킷 — ASRTU-1 형식.
/// gr-satellites 의 "TM short KISS" 전송과 같은 해석이다(헤더 5 바이트, 가상 채널별 KISS 상태).
///
/// 다른 점 하나 — **손상 가능성이 있는 패킷은 내보내지 않는다**(SpaceLink 의 원칙). 가상 채널 프레임 카운트가 건너뛰면
/// 그 사이 바이트를 잃은 것이므로 조립 중인 패킷을 버린다. gr-satellites 는 이어 붙인다 — 그래서 대조할 때
/// 카운트가 이어지는 구간에서만 두 결과가 같아야 한다.
/// 스페이스 패킷 헤더의 버전·길이 필드가 실제와 다르면 버린다(SpaceLink <see cref="SpacePacket.Decode"/> 가 판정).
/// </summary>
public sealed class ShortTmKissTransport(int spacecraftId, IReadOnlyCollection<int> virtualChannels)
{
    private const byte Fend = 0xC0, Fesc = 0xDB, Tfend = 0xDC, Tfesc = 0xDD;

    private sealed class ChannelState
    {
        public readonly List<byte> Buffer = [];
        public bool Escape;
        public int? LastCount;
        public bool Damaged = true; // 처음 받는 FEND 전의 바이트는 앞부분이 없다
    }

    private readonly Dictionary<int, ChannelState> _channels = [];

    public int FramesRejected { get; private set; }
    public int PacketsDropped { get; private set; }

    /// <summary>프레임 하나를 넣고, 완성된 패킷을 돌려준다.</summary>
    public List<ExtractedPacket> Process(ReadOnlySpan<byte> frame)
    {
        var output = new List<ExtractedPacket>();
        var h = ShortTmHeader.Read(frame);
        if (h.Version != 0 || h.SpacecraftId != spacecraftId || !virtualChannels.Contains(h.VirtualChannelId))
        {
            FramesRejected++;
            return output;
        }
        if (!_channels.TryGetValue(h.VirtualChannelId, out var st))
            _channels[h.VirtualChannelId] = st = new ChannelState();
        if (st.LastCount is int last && ((last + 1) & 0xFF) != h.VirtualChannelFrameCount)
        {
            st.Damaged = true; // 사이 프레임을 잃었다
            st.Buffer.Clear();
            st.Escape = false;
        }
        st.LastCount = h.VirtualChannelFrameCount;

        foreach (byte b in frame[ShortTmHeader.Length..])
        {
            if (b == Fend)
            {
                if (st.Buffer.Count > 0)
                {
                    if (st.Damaged) PacketsDropped++;
                    else if (TryPacket(st.Buffer, out var p)) output.Add(new ExtractedPacket(h.VirtualChannelId, p!, [.. st.Buffer]));
                    else PacketsDropped++;
                }
                st.Buffer.Clear();
                st.Escape = false;
                st.Damaged = false;
                continue;
            }
            if (st.Escape)
            {
                st.Buffer.Add(b == Tfend ? Fend : b == Tfesc ? Fesc : b);
                st.Escape = false;
            }
            else if (b == Fesc) st.Escape = true;
            else st.Buffer.Add(b);
        }
        return output;
    }

    private static bool TryPacket(List<byte> bytes, out SpacePacket? packet)
    {
        try
        {
            packet = SpacePacket.Decode([.. bytes]);
            return true;
        }
        catch (FormatException)
        {
            packet = null;
            return false;
        }
    }
}
