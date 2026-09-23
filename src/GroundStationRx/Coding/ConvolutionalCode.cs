namespace GroundStationRx.Coding;

/// <summary>
/// CCSDS 131.0-B 길쌈 부호 — 구속 길이 7, 부호율 1/2, G1 = 1111001 (0x79), G2 = 1011011 (0x5B), **G2 출력 반전**, G1 먼저 보낸다.
/// 레지스터의 최상위 비트(비트 6)가 방금 들어온 비트다. 이 규약은 ASRTU-1 실제 녹음에서 부호화된 ASM 패턴을 찾아 확인했다
/// (G2 반전 없는 규약·순서 바꾼 규약·차동 없는 규약은 패턴이 나오지 않았다).
///
/// 두 생성 다항식의 무게가 홀수(5)라 입력 비트를 전부 뒤집으면 출력도 전부 뒤집힌다 — 복조기의 180° 모호성이
/// 비터비를 지나도 "모든 비트 반전" 으로만 남고, 그 뒤 NRZ-M 이 없앤다.
/// </summary>
public static class ConvolutionalCode
{
    public const int ConstraintLength = 7;
    private const int States = 64;
    private const int G1 = 0x79, G2 = 0x5B;

    /// <summary>비트(0/1) → 채널 심볼(0/1), 길이 2 배. 레지스터는 0 에서 시작한다.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> bits)
    {
        var o = new byte[bits.Length * 2];
        int reg = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            reg = ((bits[i] & 1) << 6 | reg >> 1) & 0x7F;
            o[2 * i] = (byte)Parity(reg & G1);
            o[2 * i + 1] = (byte)(Parity(reg & G2) ^ 1);
        }
        return o;
    }

    // 다음 상태 ns 의 두 이전 상태와, 그 가지에서 기대하는 두 심볼(+1 = 비트 0, −1 = 비트 1)
    private static readonly int[,] Pred = new int[States, 2];
    private static readonly float[,] Exp1 = new float[States, 2];
    private static readonly float[,] Exp2 = new float[States, 2];

#pragma warning disable CA1810 // 표를 한 번에 만드는 정적 생성자가 더 읽기 쉽다
    static ConvolutionalCode()
#pragma warning restore CA1810
    {
        for (int ns = 0; ns < States; ns++)
        {
            int u = ns >> 5;
            for (int l = 0; l < 2; l++)
            {
                int p = (ns & 31) << 1 | l;
                int reg = u << 6 | p;
                Pred[ns, l] = p;
                Exp1[ns, l] = 1 - 2 * Parity(reg & G1);
                Exp2[ns, l] = 1 - 2 * (Parity(reg & G2) ^ 1);
            }
        }
    }

    /// <summary>
    /// 연판정 비터비 복호 — 입력은 심볼 쌍(G1, G2)의 연판정 값(양수 = 비트 0 쪽). 시작·끝 상태를 모른다고 보고
    /// (버스트 중간부터 받는다), 끝에서 경로 척도가 가장 좋은 상태부터 거슬러 올라간다.
    /// </summary>
    /// <param name="soft">심볼 연판정 값, 짝수 번째가 G1</param>
    public static byte[] Decode(ReadOnlySpan<double> soft)
    {
        int n = soft.Length / 2;
        var decisions = new ulong[n];   // 비트 ns = 상태 ns 로 들어온 가지(l)
        Span<double> pm = stackalloc double[States];
        Span<double> next = stackalloc double[States];
        pm.Clear();
        for (int t = 0; t < n; t++)
        {
            double r1 = soft[2 * t], r2 = soft[2 * t + 1];
            ulong d = 0;
            double best = double.NegativeInfinity;
            for (int ns = 0; ns < States; ns++)
            {
                double c0 = pm[Pred[ns, 0]] + Exp1[ns, 0] * r1 + Exp2[ns, 0] * r2;
                double c1 = pm[Pred[ns, 1]] + Exp1[ns, 1] * r1 + Exp2[ns, 1] * r2;
                if (c1 > c0) { next[ns] = c1; d |= 1UL << ns; }
                else next[ns] = c0;
                if (next[ns] > best) best = next[ns];
            }
            for (int s = 0; s < States; s++) pm[s] = next[s] - best; // 척도가 끝없이 커지지 않게
            decisions[t] = d;
        }

        int st = 0;
        for (int s = 1; s < States; s++) if (pm[s] > pm[st]) st = s;
        var bits = new byte[n];
        for (int t = n - 1; t >= 0; t--)
        {
            bits[t] = (byte)(st >> 5);
            int l = (int)((decisions[t] >> st) & 1);
            st = (st & 31) << 1 | l;
        }
        return bits;
    }

    private static int Parity(int v) => System.Numerics.BitOperations.PopCount((uint)v) & 1;
}

/// <summary>NRZ-M(차동) — 보낼 때 "1 이면 준위를 바꾼다", 받을 때 이웃 두 비트의 XOR. 모든 비트가 뒤집혀도 결과는 같다.</summary>
public static class Nrzm
{
    public static byte[] Decode(ReadOnlySpan<byte> bits)
    {
        var o = new byte[bits.Length];
        byte prev = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            o[i] = (byte)(bits[i] ^ prev);
            prev = bits[i];
        }
        return o;
    }

    public static byte[] Encode(ReadOnlySpan<byte> bits)
    {
        var o = new byte[bits.Length];
        byte level = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            level ^= bits[i];
            o[i] = level;
        }
        return o;
    }
}
