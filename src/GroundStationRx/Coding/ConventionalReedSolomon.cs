using SpaceLink.ChannelCoding;

namespace GroundStationRx.Coding;

/// <summary>
/// 관례(conventional) 표현 RS(255,223) 복호 — SpaceLink 의 CCSDS RS 복호기를 그대로 쓰되 바이트 표현만 맞춘다.
///
/// SpaceLink v1.1 의 복호기는 CCSDS 표준대로 **이중 기저** 바이트를 받는다. ASRTU-1 은 같은 부호(근 α^(11·(112…143)))를
/// 관례 기저 바이트로 보낸다(gr-satellites 위성 정의 "RS basis: conventional"). 두 표현은 GF(2) 선형 변환 T 로 1:1 대응하므로,
/// 받은 바이트를 T 로 이중 기저로 옮겨 복호하고, 복원된 데이터를 T⁻¹ 로 되돌린다. SpaceLink 코드는 고치지 않는다(서브모듈 고정).
///
/// T 는 CCSDS 131.0-B-5 부속서 F 의 T_λ 행렬이다. SpaceLink 에는 같은 표가 있지만 internal 이라 이 저장소에 다시 적었다.
/// 맞는지는 이 코드가 아니라 **독립 구현** — 파이썬 reedsolo 로 만든 관례 기저 부호어(오류 0~16 개 주입) — 으로 판정한다.
/// </summary>
public sealed class ConventionalReedSolomon
{
    public const int CodeblockLength = 255;
    public const int DataLength = 223;

    private static readonly byte[] TRows = [0x8D, 0xEF, 0xEC, 0x86, 0xFA, 0x99, 0xAF, 0x7B];     // T_λ: α^7 … α^0 의 이중 기저 표현
    private static readonly byte[] TInvRows = [0xC5, 0x42, 0x2E, 0xFD, 0xF0, 0x79, 0xAC, 0xCC];  // T_λ⁻¹: l0 … l7 의 관례 기저 표현
    internal static readonly byte[] ToDual = Build(TRows);
    internal static readonly byte[] ToConventional = Build(TInvRows);

    private readonly ReedSolomonCodec _codec = new();

    /// <summary>255 바이트 코드블록(관례 기저) → 223 바이트 데이터. 정정 불가면 Succeeded = false.</summary>
    public ReedSolomonResult Decode(ReadOnlySpan<byte> codeblock, Span<byte> data)
    {
        if (codeblock.Length != CodeblockLength) throw new ArgumentException("코드블록은 255 바이트", nameof(codeblock));
        Span<byte> dual = stackalloc byte[CodeblockLength];
        for (int i = 0; i < CodeblockLength; i++) dual[i] = ToDual[codeblock[i]];
        var result = _codec.Decode(dual, data);
        for (int i = 0; i < DataLength; i++) data[i] = ToConventional[data[i]];
        return result;
    }

    /// <summary>223 바이트 데이터(관례 기저) → 255 바이트 코드블록(관례 기저). 합성 송신기와 시험용.</summary>
    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length != DataLength) throw new ArgumentException("데이터는 223 바이트", nameof(data));
        Span<byte> dual = stackalloc byte[DataLength];
        for (int i = 0; i < DataLength; i++) dual[i] = ToDual[data[i]];
        var cb = _codec.Encode(dual);
        for (int i = 0; i < cb.Length; i++) cb[i] = ToConventional[cb[i]];
        return cb;
    }

    private static byte[] Build(byte[] rows)
    {
        var table = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            byte o = 0;
            for (int r = 0; r < 8; r++)
                if (((v >> (7 - r)) & 1) != 0) o ^= rows[r];
            table[v] = o;
        }
        return table;
    }
}
