using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GroundStationRx.Recording;

/// <summary>SigMF 표본 형식 중 지원하는 것 — 드빙겔로 원시 녹음(ci16_le), GNU Radio·SatDump(cf32_le), RTL-SDR(cu8).</summary>
public enum SigmfDatatype { Ci16Le, Cf32Le, Cu8 }

/// <summary>
/// SigMF 녹음(.sigmf-meta + .sigmf-data) — 복소 기저대역 표본을 원하는 위치부터 읽는다.
/// 녹음이 수백 MB~수 GB 라 한 번에 올리지 않고 구간 단위로 읽는다.
/// </summary>
public sealed class SigmfRecording
{
    public string DataPath { get; }
    public SigmfDatatype Datatype { get; }
    public double SampleRateHz { get; }
    public double CenterFrequencyHz { get; }
    /// <summary>첫 표본의 시각(UTC). SigMF 는 core:datetime 을 UTC 로 정한다.</summary>
    public DateTime StartUtc { get; }
    public long SampleCount { get; }
    /// <summary>녹음 장비가 밝힌 시각 출처(예: "internal") — 절대 시각을 얼마나 믿을지 판단하는 데 쓴다.</summary>
    public string? TimeSource { get; }

    private int BytesPerSample => Datatype switch
    {
        SigmfDatatype.Ci16Le => 4,
        SigmfDatatype.Cf32Le => 8,
        _ => 2,
    };

    private SigmfRecording(string dataPath, SigmfDatatype datatype, double sampleRate, double center, DateTime start, string? timeSource)
    {
        DataPath = dataPath;
        Datatype = datatype;
        SampleRateHz = sampleRate;
        CenterFrequencyHz = center;
        StartUtc = start;
        TimeSource = timeSource;
        SampleCount = new FileInfo(dataPath).Length / BytesPerSample;
    }

    public static SigmfRecording Open(string metaPath)
    {
        ArgumentNullException.ThrowIfNull(metaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var global = doc.RootElement.GetProperty("global");
        var datatype = global.GetProperty("core:datatype").GetString() switch
        {
            "ci16_le" => SigmfDatatype.Ci16Le,
            "cf32_le" => SigmfDatatype.Cf32Le,
            "cu8" => SigmfDatatype.Cu8,
            var other => throw new NotSupportedException($"SigMF datatype '{other}' 는 지원하지 않는다"),
        };
        double rate = global.GetProperty("core:sample_rate").GetDouble();
        string? timeSource = global.TryGetProperty("vrt:time_source", out var ts) ? ts.GetString() : null;

        var captures = doc.RootElement.GetProperty("captures");
        if (captures.GetArrayLength() != 1)
            throw new NotSupportedException("capture 구간이 여러 개인 녹음은 지원하지 않는다 — 중심 주파수가 중간에 바뀐다");
        var cap = captures[0];
        double center = cap.GetProperty("core:frequency").GetDouble();
        var start = DateTime.Parse(cap.GetProperty("core:datetime").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        string dataPath = Path.ChangeExtension(metaPath, ".sigmf-data");
        if (!File.Exists(dataPath))
            throw new FileNotFoundException("짝이 되는 .sigmf-data 가 없다", dataPath);
        return new SigmfRecording(dataPath, datatype, rate, center, start, timeSource);
    }

    public DateTime TimeOf(long sampleIndex) => StartUtc.AddTicks((long)Math.Round(sampleIndex / SampleRateHz * TimeSpan.TicksPerSecond));

    /// <summary><paramref name="start"/> 표본부터 <paramref name="destination"/> 길이만큼 읽는다. 끝을 넘으면 읽은 수만 돌려준다.</summary>
    public int Read(long start, Span<Complex> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        long available = Math.Max(0, SampleCount - start);
        int count = (int)Math.Min(destination.Length, available);
        if (count == 0) return 0;

        int bps = BytesPerSample;
        byte[] raw = new byte[count * bps];
        using (var fs = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            fs.Position = start * bps;
            fs.ReadExactly(raw);
        }
        for (int i = 0; i < count; i++)
        {
            var s = raw.AsSpan(i * bps, bps);
            destination[i] = Datatype switch
            {
                SigmfDatatype.Ci16Le => new Complex(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..])),
                SigmfDatatype.Cf32Le => new Complex(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..])),
                _ => new Complex(s[0] - 127.5, s[1] - 127.5),
            };
        }
        return count;
    }
}
