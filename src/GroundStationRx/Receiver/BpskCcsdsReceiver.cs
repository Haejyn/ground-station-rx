using System.Numerics;
using GroundStationRx.Coding;
using GroundStationRx.Dsp;
using GroundStationRx.Orbit;
using GroundStationRx.Recording;
using SpaceLink;
using SpaceLink.ChannelCoding;

namespace GroundStationRx.Receiver;

/// <summary>수신기 설정 — 위성마다 다른 값.</summary>
public sealed record BpskCcsdsConfig
{
    /// <summary>위성 송신 공칭 주파수(Hz). 도플러 예측의 기준.</summary>
    public required double NominalCarrierHz { get; init; }
    public required double SymbolRateHz { get; init; }
    public double RollOff { get; init; } = 0.35;
    /// <summary>RS 복호 뒤 전송 프레임 길이(바이트).</summary>
    public int FrameLength { get; init; } = ConventionalReedSolomon.DataLength;
    /// <summary>예측 도플러 주변에서 반송파를 찾을 폭(±Hz). 송신기 주파수 오차 + TLE 오차를 덮어야 한다.</summary>
    public double SearchHalfWidthHz { get; init; } = 5_000;
    /// <summary>
    /// 버스트로 볼 반송파 선 세기(제곱 스펙트럼 선 / 중앙값, 0.2 초 구간). 신호 없는 구간은 실측 10~12.6 dB(ASRTU-1 녹음 전체),
    /// 합성 BPSK 는 Es/N0 0 dB 에서 약 22 dB — 그 사이인 18 dB. 처음 둔 30 dB 는 Es/N0 6 dB 이하 버스트를 통째로 놓쳤다
    /// (비터비 + RS 는 그 조건에서도 복호한다 — <c>SyntheticEndToEndTests</c>).
    /// </summary>
    public double MinCarrierSnrDb { get; init; } = 18;
}

/// <summary>복원한 전송 프레임 하나.</summary>
public sealed record ReceivedFrame(DateTime Utc, int Burst, int Alignment, byte[] Bytes, int CorrectedSymbols, TransferFrame? Tm, FrameError TmError);

/// <summary>버스트 하나의 수신 기록 — 어디서 무엇을 쟀고 몇 장을 살렸는지(실패도 남긴다).</summary>
/// <param name="MarkerBits">정렬별로 ASM(비트 오류 3 개 이하)이 보인 비트 위치 — 코드블록 경계가 어디서 왔는지 보려고 남긴다</param>
/// <param name="FlywheelBlocks">정렬별로 동기기가 마커 없이 관성(flywheel)으로 낸 코드블록 수</param>
/// <param name="FlywheelDropped">그중 뒤 마커로 경계가 확인되지 않아 버린 수</param>
/// <param name="BoundaryWords">정렬별로 첫 마커부터 코드블록 길이 간격의 자리에 실제로 있던 32 비트 — 마커가 깨진 자리를 들여다보려고</param>
/// <param name="Soft">복조기가 낸 연판정 심볼(평균 |값| = 1) — 품질을 그림으로 보려고 남긴다</param>
public sealed record BurstReport(int Index, DateTime StartUtc, DateTime EndUtc, double CarrierOffsetHz, double SymbolRateHz,
    double DemodQuality, int Symbols, int Codeblocks, int RsFailures, int Frames, int[][] MarkerBits, int[] FlywheelBlocks,
    int[] FlywheelDropped, uint[][] BoundaryWords, double[] Soft);

public sealed record ReceptionResult(IReadOnlyList<ReceivedFrame> Frames, IReadOnlyList<BurstReport> Bursts);

/// <summary>
/// 원시 IQ 녹음 → 전송 프레임. 저궤도 큐브샛의 BPSK + CCSDS 연접 부호(길쌈 + RS) 다운링크.
///
/// 1. 궤도 예측 도플러 ±창 안에서 반송파를 0.2 초마다 잰다 → 신호가 있는 구간이 버스트다
/// 2. 버스트마다 잰 반송파(구간 사이 선형 보간)로 0 Hz 로 옮기고 40 kHz 로 솎는다
/// 3. 복조(<see cref="BpskBurstDemodulator"/>) → 연판정 심볼
/// 4. 비터비는 심볼 짝의 시작을 모른다 — 두 정렬 모두 복호한다(gr-satellites 와 같은 방식). 같은 코드블록은 한 번만 낸다
/// 5. NRZ-M → ASM 동기(SpaceLink) → PN 제거(SpaceLink) → RS(관례 기저, SpaceLink 를 기저 변환으로 감쌈) → TM 프레임(SpaceLink)
/// </summary>
public sealed class BpskCcsdsReceiver(BpskCcsdsConfig config, LinkGeometry link)
{
    private const double BasebandRateHz = 40_000;

    public ReceptionResult Process(SigmfRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        double Expected(DateTime utc) => config.NominalCarrierHz + link.At(utc).DopplerHz(config.NominalCarrierHz) - recording.CenterFrequencyHz;

        var carrier = BpskCarrierEstimator.Scan(recording, 0.2, config.MinCarrierSnrDb, Expected, config.SearchHalfWidthHz);
        var groups = new List<List<CarrierMeasurement>>();
        foreach (var m in carrier)
        {
            if (groups.Count == 0 || (m.Utc - groups[^1][^1].Utc).TotalSeconds > 1.0) groups.Add([]);
            groups[^1].Add(m);
        }

        var frames = new List<ReceivedFrame>();
        var reports = new List<BurstReport>();
        for (int b = 0; b < groups.Count; b++)
        {
            var (burstFrames, report) = ProcessBurst(recording, groups[b], b);
            frames.AddRange(burstFrames);
            reports.Add(report);
        }
        return new ReceptionResult(frames, reports);
    }

    private (List<ReceivedFrame>, BurstReport) ProcessBurst(SigmfRecording rec, List<CarrierMeasurement> group, int index)
    {
        double fs = rec.SampleRateHz;
        // 앞뒤로 잡음 구간을 넉넉히 넣는다 — BurstEdges 가 잡음 바닥을 여기서 잰다
        long start = Math.Max(0, SampleAt(rec, group[0].Utc) - (long)(0.6 * fs));
        long end = Math.Min(rec.SampleCount, SampleAt(rec, group[^1].Utc) + (long)(0.6 * fs));
        var x = new Complex[end - start];
        rec.Read(start, x);

        // 잰 반송파를 따라 0 Hz 로 — 구간 가운데 시각 사이는 선형 보간, 양 끝 밖은 끝값
        var ts = group.Select(m => (double)(SampleAt(rec, m.Utc) - start)).ToArray();
        var fsHz = group.Select(m => m.OffsetHz).ToArray();
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            x[i] *= Complex.FromPolarCoordinates(1, -phase);
            phase += 2 * Math.PI * InterpolateClamped(ts, fsHz, i) / fs;
            if (Math.Abs(phase) > Math.PI) phase -= 2 * Math.PI * Math.Round(phase / (2 * Math.PI));
        }

        int decimation = (int)Math.Round(fs / BasebandRateHz);
        double rate = fs / decimation;
        var baseband = FirFilter.FilterDecimate(x, FirFilter.LowPass(fs, 14_000, 301), decimation);
        var (s0, s1) = BurstEdges(baseband, rate / config.SymbolRateHz);
        var burst = baseband.AsSpan(s0, s1 - s0);
        var demod = BpskBurstDemodulator.Demodulate(burst, rate, config.SymbolRateHz, config.RollOff);

        var frames = new List<ReceivedFrame>();
        var seen = new HashSet<string>();
        int codeblocks = 0, rsFailures = 0;
        var rs = new ConventionalReedSolomon();
        var markerBits = new int[2][];
        var flywheel = new int[2];
        var flywheelDropped = new int[2];
        var boundary = new uint[2][];
        for (int align = 0; align < 2; align++)
        {
            var bits = Nrzm.Decode(ConvolutionalCode.Decode(demod.Soft.AsSpan(align)));
            markerBits[align] = FindMarkers(bits, 3);
            boundary[align] = BoundaryWords(bits, markerBits[align]);
            foreach (var cb in ConfirmedCodeblocks(PackBits(bits), out flywheel[align], out flywheelDropped[align]))
            {
                codeblocks++;
                Pseudorandomizer.Apply(cb, PseudorandomSequence.Legacy255);
                var data = new byte[ConventionalReedSolomon.DataLength];
                var r = rs.Decode(cb, data);
                if (!r.Succeeded) { rsFailures++; continue; }
                if (!seen.Add(Convert.ToHexString(data))) continue; // 다른 정렬이 이미 살린 프레임
                var tm = TransferFrame.Decode(data.AsSpan(0, config.FrameLength), new FrameConfig(config.FrameLength, hasFrameErrorControl: false));
                frames.Add(new ReceivedFrame(rec.TimeOf(start + (long)Math.Round((s0 + demod.FirstSymbolSample) * decimation)), index, align,
                    data, r.CorrectedSymbols, tm.Frame, tm.Error));
            }
        }

        var report = new BurstReport(index, rec.TimeOf(start + (long)s0 * decimation), rec.TimeOf(start + (long)s1 * decimation),
            group.Select(m => m.OffsetHz).Order().ElementAt(group.Count / 2), demod.SymbolRateHz, demod.Quality,
            demod.Soft.Length, codeblocks, rsFailures, frames.Count, markerBits, flywheel, flywheelDropped, boundary, demod.Soft);
        return (frames, report);
    }

    /// <summary>
    /// SpaceLink 동기기의 코드블록 중, 마커 없이 관성(flywheel)으로 낸 블록은 **뒤에 마커가 다시 나와 같은 격자가 확인될 때만** 받는다.
    ///
    /// 왜: ASRTU-1 은 마지막 프레임 뒤에 PN 을 거꾸로 읽은 채움 신호를 보낸다. 잠긴 동기기는 마커가 없어도 한 블록을 더 읽는데,
    /// PN 류 수열은 GF(256) 스펙트럼이 8 점에만 있고 그 점들이 RS 의 근을 모두 피해 가서 **RS 가 오류 0 으로 통과한다**
    /// (<c>CodingTests.PnFill_IsAValidReedSolomonCodeword</c>). RS 통과만으로는 진짜 프레임과 구별할 수 없다.
    /// 버스트 한가운데서 잡음으로 마커 하나가 깨진 경우는 뒤 마커가 경계를 확인해 주므로 그대로 살아난다.
    /// </summary>
    internal static List<byte[]> ConfirmedCodeblocks(ReadOnlySpan<byte> stream, out int flywheelBlocks, out int flywheelDropped)
    {
        var sync = new FrameSynchronizer(ConventionalReedSolomon.CodeblockLength);
        var confirmed = new List<byte[]>();
        var pending = new List<byte[]>();
        flywheelBlocks = 0;
        flywheelDropped = 0;
        long missed = 0, resyncs = 0;
        for (int i = 0; i < stream.Length; i++)
        {
            // 한 바이트씩 넣으면 한 번에 많아야 한 블록이 나와서, 그 블록이 관성 블록인지 MarkersMissed 로 가릴 수 있다
            var blocks = sync.Process(stream.Slice(i, 1));
            if (sync.Resyncs != resyncs)
            {
                flywheelDropped += pending.Count; // 격자를 잃었다 — 관성 블록의 경계는 끝내 확인되지 않는다
                pending.Clear();
                resyncs = sync.Resyncs;
            }
            foreach (var b in blocks)
            {
                if (sync.MarkersMissed != missed)
                {
                    missed = sync.MarkersMissed;
                    flywheelBlocks++;
                    pending.Add(b);
                }
                else
                {
                    confirmed.AddRange(pending);
                    pending.Clear();
                    confirmed.Add(b);
                }
            }
        }
        flywheelDropped += pending.Count;
        return confirmed;
    }

    /// <summary>
    /// 버스트의 시작·끝 — 64 심볼 이동 평균 전력이 잡음 바닥(하위 2 %)보다 10 dB 높은 가장 긴 구간.
    /// 앞뒤 잡음을 복조기에 넣으면 정규화와 타이밍 추정이 흐려진다.
    /// 처음에는 하위 10 % 를 바닥으로 잡았는데, 버스트가 구간의 90 % 넘게 차면 "바닥" 이 신호 안에 들어가
    /// 넘는 구간이 없어졌다(합성 시험이 잡은 예외). 그래도 못 찾으면 구간 전체를 쓴다.
    /// </summary>
    internal static (int Start, int End) BurstEdges(Complex[] y, double sps)
    {
        int w = Math.Max(1, (int)(64 * sps));
        var p = new double[y.Length];
        double acc = 0;
        for (int i = 0; i < y.Length; i++)
        {
            acc += y[i].Real * y[i].Real + y[i].Imaginary * y[i].Imaginary;
            if (i >= w) acc -= y[i - w].Real * y[i - w].Real + y[i - w].Imaginary * y[i - w].Imaginary;
            p[i] = acc / Math.Min(i + 1, w);
        }
        var sorted = (double[])p.Clone();
        Array.Sort(sorted);
        double threshold = sorted[sorted.Length / 50] * 10;
        int bestStart = 0, bestLen = 0, runStart = -1;
        for (int i = 0; i <= p.Length; i++)
        {
            bool on = i < p.Length && p[i] > threshold;
            if (on && runStart < 0) runStart = i;
            if (!on && runStart >= 0)
            {
                if (i - runStart > bestLen) { bestLen = i - runStart; bestStart = runStart; }
                runStart = -1;
            }
        }
        if (bestLen < w) return (0, y.Length);
        // 이동 평균은 뒤를 본다 — 켜지는 쪽은 창 길이만큼 늦게 넘는다
        int s = Math.Max(0, bestStart - w / 2);
        int e = Math.Min(y.Length, bestStart + bestLen - w / 2);
        return (s, e);
    }

    /// <summary>ASM 0x1ACFFC1D 와 해밍 거리 <paramref name="maxErrors"/> 이하인 비트 위치(겹치지 않게).</summary>
    internal static int[] FindMarkers(ReadOnlySpan<byte> bits, int maxErrors)
    {
        const uint asm = FrameSynchronizer.AttachedSyncMarker;
        var hits = new List<int>();
        uint window = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            window = window << 1 | bits[i];
            if (i < 31) continue;
            int start = i - 31;
            if (System.Numerics.BitOperations.PopCount(window ^ asm) <= maxErrors && (hits.Count == 0 || start - hits[^1] >= 32))
                hits.Add(start);
        }
        return [.. hits];
    }

    private static uint[] BoundaryWords(ReadOnlySpan<byte> bits, int[] markers)
    {
        if (markers.Length == 0) return [];
        const int caduBits = (4 + ConventionalReedSolomon.CodeblockLength) * 8;
        var words = new List<uint>();
        for (int pos = markers[0]; pos + 32 <= bits.Length; pos += caduBits)
        {
            uint w = 0;
            for (int i = 0; i < 32; i++) w = w << 1 | bits[pos + i];
            words.Add(w);
        }
        return [.. words];
    }

    private static long SampleAt(SigmfRecording rec, DateTime utc) => (long)Math.Round((utc - rec.StartUtc).TotalSeconds * rec.SampleRateHz);

    private static double InterpolateClamped(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        int i = Array.BinarySearch(xs, x);
        if (i >= 0) return ys[i];
        i = ~i;
        return ys[i - 1] + (x - xs[i - 1]) / (xs[i] - xs[i - 1]) * (ys[i] - ys[i - 1]);
    }

    /// <summary>비트(0/1) → 바이트, MSB 먼저.</summary>
    internal static byte[] PackBits(ReadOnlySpan<byte> bits)
    {
        var o = new byte[bits.Length / 8];
        for (int i = 0; i < o.Length; i++)
        {
            int v = 0;
            for (int j = 0; j < 8; j++) v = v << 1 | bits[8 * i + j];
            o[i] = (byte)v;
        }
        return o;
    }
}
