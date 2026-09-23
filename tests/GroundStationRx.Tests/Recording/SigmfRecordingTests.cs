using System.Buffers.Binary;
using System.Numerics;
using GroundStationRx.Recording;
using GroundStationRx.Tests.Recording;

namespace GroundStationRx.Tests.RecordingFormat;

public sealed class SigmfRecordingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gsrx-sigmf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string datatype, byte[] data, string captures =
        """[{"core:sample_start":0,"core:frequency":435300000,"core:datetime":"2024-12-09T07:57:51.440039"}]""")
    {
        string meta = Path.Combine(_dir, $"{datatype}.sigmf-meta");
        File.WriteAllText(meta, $$"""
            {"global":{"core:datatype":"{{datatype}}","core:sample_rate":1000000,"vrt:time_source":"internal"},
             "annotations":[],"captures":{{captures}}}
            """);
        File.WriteAllBytes(Path.ChangeExtension(meta, ".sigmf-data"), data);
        return meta;
    }

    [Fact]
    [Trait("Requirement", "REQ-REC-01")]
    public void Ci16Le_MetadataAndSamples()
    {
        var bytes = new byte[4 * 5];
        for (int i = 0; i < 5; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), (short)(i * 1000 - 2000));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), (short)(-i * 7));
        }
        var rec = SigmfRecording.Open(Write("ci16_le", bytes));
        Assert.Equal(SigmfDatatype.Ci16Le, rec.Datatype);
        Assert.Equal(5, rec.SampleCount);
        Assert.Equal(1e6, rec.SampleRateHz);
        Assert.Equal(435.3e6, rec.CenterFrequencyHz);
        Assert.Equal("internal", rec.TimeSource);
        Assert.Equal(new DateTime(2024, 12, 9, 7, 57, 51, DateTimeKind.Utc).AddTicks(4_400_390), rec.StartUtc);
        Assert.Equal(DateTimeKind.Utc, rec.StartUtc.Kind);
        Assert.Equal(rec.StartUtc.AddTicks(30), rec.TimeOf(3)); // 1 Msps → 표본 3 개 = 3 μs = 30 틱

        var buf = new Complex[10];
        Assert.Equal(3, rec.Read(2, buf)); // 끝을 넘으면 읽은 수만
        Assert.Equal(new Complex(0, -14), buf[0]);
        Assert.Equal(new Complex(2000, -28), buf[2]);
        Assert.Equal(0, rec.Read(5, buf));
    }

    [Fact]
    [Trait("Requirement", "REQ-REC-01")]
    public void Cf32LeAndCu8_AreDecoded()
    {
        var f = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(f, 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(4), -1.5f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(8), 3f);
        BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(12), 0f);
        var cf = SigmfRecording.Open(Write("cf32_le", f));
        var buf = new Complex[2];
        cf.Read(0, buf);
        Assert.Equal(new Complex(0.25, -1.5), buf[0]);
        Assert.Equal(new Complex(3, 0), buf[1]);

        var cu = SigmfRecording.Open(Write("cu8", [0, 255, 128, 127]));
        cu.Read(0, buf);
        Assert.Equal(new Complex(-127.5, 127.5), buf[0]);
        Assert.Equal(new Complex(0.5, -0.5), buf[1]);
    }

    [Fact]
    [Trait("Requirement", "REQ-REC-01")]
    public void UnsupportedOrAmbiguousRecordings_AreRejected()
    {
        Assert.Throws<NotSupportedException>(() => SigmfRecording.Open(Write("ri8", [1, 2])));
        string twoCaptures = """
            [{"core:sample_start":0,"core:frequency":1,"core:datetime":"2024-01-01T00:00:00Z"},
             {"core:sample_start":10,"core:frequency":2,"core:datetime":"2024-01-01T00:00:01Z"}]
            """;
        Assert.Throws<NotSupportedException>(() => SigmfRecording.Open(Write("cf32_le", new byte[8], twoCaptures)));
        string meta = Write("ci16_le", new byte[4]);
        File.Delete(Path.ChangeExtension(meta, ".sigmf-data"));
        Assert.Throws<FileNotFoundException>(() => SigmfRecording.Open(meta));
    }

    [RecordingFact(RealRecording.Asrtu1Short)]
    [Trait("Requirement", "REQ-REC-01")]
    public void RealAsrtu1Recording_Metadata()
    {
        var rec = RealRecording.Open(RealRecording.Asrtu1Short);
        Assert.Equal(67_720_000, rec.SampleCount); // 270,880,000 바이트 / 4
        Assert.Equal(435.3e6, rec.CenterFrequencyHz);
        Assert.Equal(new DateTime(2024, 12, 9, 7, 57, 51, DateTimeKind.Utc), rec.StartUtc, TimeSpan.FromSeconds(1));
    }
}
