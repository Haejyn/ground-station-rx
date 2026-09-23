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
    public double MinCarrierSnrDb { get; init; } = 30;
}

/// <summary>복원한 전송 프레임 하나.</summary>
public sealed record ReceivedFrame(DateTime Utc, int Burst, int Alignment, byte[] Bytes, int CorrectedSymbols, TransferFrame? Tm, FrameError TmError);

/// <summary>버스트 하나의 수신 기록 — 어디서 무엇을 쟀고 몇 장을 살렸는지(실패도 남긴다).</summary>
public sealed record BurstReport(int Index, DateTime StartUtc, DateTime EndUtc, double CarrierOffsetHz, double SymbolRateHz,
    double DemodQuality, int Symbols, int Codeblocks, int RsFailures, int Frames);

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
        long start = Math.Max(0, SampleAt(rec, group[0].Utc) - (long)(0.3 * fs));
        long end = Math.Min(rec.SampleCount, SampleAt(rec, group[^1].Utc) + (long)(0.3 * fs));
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
        for (int align = 0; align < 2; align++)
        {
            var bits = Nrzm.Decode(ConvolutionalCode.Decode(demod.Soft.AsSpan(align)));
            var sync = new FrameSynchronizer(ConventionalReedSolomon.CodeblockLength);
            foreach (var cb in sync.Process(PackBits(bits)))
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
            demod.Soft.Length, codeblocks, rsFailures, frames.Count);
        return (frames, report);
    }

    /// <summary>
    /// 버스트의 시작·끝 — 64 심볼 이동 평균 전력이 잡음 바닥(하위 10 %)보다 10 dB 높은 가장 긴 구간.
    /// 앞뒤 잡음을 복조기에 넣으면 정규화와 타이밍 추정이 흐려진다.
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
        double threshold = sorted[sorted.Length / 10] * 10;
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
        // 이동 평균은 뒤를 본다 — 켜지는 쪽은 창 길이만큼 늦게 넘는다
        int s = Math.Max(0, bestStart - w / 2);
        int e = Math.Min(y.Length, bestStart + bestLen - w / 2);
        return (s, e);
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
