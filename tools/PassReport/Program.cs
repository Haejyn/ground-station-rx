// README 그림용 수치 — 실제 녹음을 이 저장소의 수신기로 처리해 build/report/*.csv 로 낸다.
// 사용: GSRX_RECORDINGS=<녹음 폴더> dotnet run -c Release --project tools/PassReport
using System.Globalization;
using System.Numerics;
using GroundStationRx.Dsp;
using GroundStationRx.Orbit;
using GroundStationRx.Receiver;
using GroundStationRx.Recording;
using GroundStationRx.Simulation;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
string root = FindRoot();
string recDir = Environment.GetEnvironmentVariable("GSRX_RECORDINGS") ?? Path.Combine(root, "recordings");
string outDir = Path.Combine(root, "build", "report");
Directory.CreateDirectory(outDir);

const double nominal = 435.4e6;
var tle = Tle.Parse("1 61781U 24199AY  24343.80030339  .00013950  00000+0  57980-3 0  9999",
                    "2 61781  97.3798 208.0613 0018899 119.8177 240.4942 15.23747191 49005");
var link = new LinkGeometry(new Sgp4(tle), new GroundSite("PI9RD Dwingeloo", 52.812, 6.396, 0.010));
var config = new BpskCcsdsConfig { NominalCarrierHz = nominal, SymbolRateHz = 9600 };
string[] recordings =
[
    "asrtu_2024_12_09_07_57_51_435.300MHz_1.00Msps_ci16_le",
    "asrtu_2024_12_09_07_59_02_435.300MHz_1.00Msps_ci16_le.chan0",
];

using var carrier = new StreamWriter(Path.Combine(outDir, "carrier.csv"));
using var bursts = new StreamWriter(Path.Combine(outDir, "bursts.csv"));
using var frames = new StreamWriter(Path.Combine(outDir, "frames.csv"));
carrier.WriteLine("utc,measured_hz,predicted_hz,elevation_deg,snr_db");
bursts.WriteLine("recording,index,start_utc,end_utc,elevation_deg,quality,symbol_rate,codeblocks,rs_failures,frames");
frames.WriteLine("recording,burst,mc,vc,vcc,corrected,hex");
double[]? sampleSoft = null;

for (int r = 0; r < recordings.Length; r++)
{
    var rec = SigmfRecording.Open(Path.Combine(recDir, recordings[r] + ".sigmf-meta"));
    double Expected(DateTime u) => nominal + link.At(u).DopplerHz(nominal) - rec.CenterFrequencyHz;
    foreach (var m in BpskCarrierEstimator.Scan(rec, 0.2, 30, Expected))
    {
        var l = link.At(m.Utc);
        carrier.WriteLine($"{m.Utc:O},{rec.CenterFrequencyHz + m.OffsetHz:F2},{nominal + l.DopplerHz(nominal):F2},{l.ElevationDeg:F3},{m.SnrDb:F1}");
    }
    var result = new BpskCcsdsReceiver(config, link).Process(rec);
    foreach (var b in result.Bursts)
    {
        var mid = b.StartUtc + (b.EndUtc - b.StartUtc) / 2;
        bursts.WriteLine($"{r},{b.Index},{b.StartUtc:O},{b.EndUtc:O},{link.At(mid).ElevationDeg:F2},{b.DemodQuality:F2},{b.SymbolRateHz:F3},{b.Codeblocks},{b.RsFailures},{b.Frames}");
        if (r == 0 && b.Index == 0) sampleSoft = b.Soft;
    }
    foreach (var f in result.Frames)
    {
        var h = ShortTmHeader.Read(f.Bytes);
        frames.WriteLine($"{r},{f.Burst},{h.MasterChannelFrameCount},{h.VirtualChannelId},{h.VirtualChannelFrameCount},{f.CorrectedSymbols},{Convert.ToHexString(f.Bytes)}");
    }
    Console.WriteLine($"{recordings[r]}: 버스트 {result.Bursts.Count} · 프레임 {result.Frames.Count}");
}
File.WriteAllLines(Path.Combine(outDir, "soft.csv"), ["soft", .. sampleSoft!.Select(v => v.ToString("F4"))]);

// 합성 신호 BER — 시험(REQ-DEM-01)과 같은 채널 조건
using var ber = new StreamWriter(Path.Combine(outDir, "ber.csv"));
ber.WriteLine("ebn0_db,ber,errors,symbols");
foreach (double ebn0 in new[] { 0.0, 1, 2, 3, 4, 5, 6, 7 })
{
    int n = ebn0 < 5 ? 100_000 : 400_000;
    var rng = new Random(100 + (int)ebn0);
    var sent = Enumerable.Range(0, n).Select(_ => rng.Next(2) * 2 - 1).ToArray();
    var x = BpskSignalGenerator.Generate(sent, new BpskChannel
    {
        SampleRateHz = 40_000, SymbolRateHz = 9600.7, TimingOffsetSymbols = 0.37, CarrierOffsetHz = 0.8,
        CarrierPhaseRad = 1.1, EsN0Db = ebn0, Seed = 200 + (int)ebn0,
    });
    var soft = BpskBurstDemodulator.Demodulate(x, 40_000, 9600).Soft;
    var (errors, compared) = CountErrors(soft, sent);
    ber.WriteLine($"{ebn0},{(double)errors / compared:E4},{errors},{compared}");
    Console.WriteLine($"Eb/N0 {ebn0} dB: {errors}/{compared}");
}

static (int Errors, int Compared) CountErrors(double[] soft, int[] sent)
{
    int bestShift = 0;
    double best = -1;
    for (int shift = -4; shift <= 4; shift++)
    {
        double c = 0;
        for (int i = 0; i < 2000; i++)
        {
            int j = i + shift;
            if (j >= 0 && j < sent.Length) c += soft[i] * sent[j];
        }
        if (Math.Abs(c) > best) { best = Math.Abs(c); bestShift = shift; }
    }
    int errors = 0, compared = 0;
    for (int seg = 0; seg < soft.Length; seg += 2000)
    {
        int e0 = 0, e1 = 0;
        for (int i = seg; i < Math.Min(seg + 2000, soft.Length); i++)
        {
            int j = i + bestShift;
            if (j < 0 || j >= sent.Length) continue;
            if ((soft[i] >= 0 ? 1 : -1) != sent[j]) e0++; else e1++;
            compared++;
        }
        errors += Math.Min(e0, e1);
    }
    return (errors, compared);
}

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "tools", "fetch_recordings.py"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("저장소 루트를 찾지 못했다");
}
