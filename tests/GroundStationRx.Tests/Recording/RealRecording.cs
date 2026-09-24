using GroundStationRx.Recording;

namespace GroundStationRx.Tests.Recording;

/// <summary>
/// 실제 위성 녹음 위치. 저장소에는 녹음을 넣지 않는다 — <c>python tools/fetch_recordings.py</c> 가 sha256 을 확인하며 받는다.
/// 환경 변수 <c>GSRX_RECORDINGS</c> 가 있으면 그 폴더, 없으면 저장소의 <c>recordings/</c>.
/// </summary>
internal static class RealRecording
{
    public const string Asrtu1Short = "asrtu_2024_12_09_07_57_51_435.300MHz_1.00Msps_ci16_le";
    /// <summary>짧은 녹음 바로 뒤(07:59:02)부터 패스 끝까지 560 초 — 도플러 +10 → −10 kHz.</summary>
    public const string Asrtu1FullPass = "asrtu_2024_12_09_07_59_02_435.300MHz_1.00Msps_ci16_le.chan0";

    public static string Directory
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("GSRX_RECORDINGS");
            if (!string.IsNullOrEmpty(env)) return env;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "fetch_recordings.py")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? ".", "recordings");
        }
    }

    public static string MetaPath(string name) => Path.Combine(Directory, name + ".sigmf-meta");

    public static SigmfRecording Open(string name) => SigmfRecording.Open(MetaPath(name));
}

/// <summary>녹음이 없으면 건너뛰는 시험 — 건너뛴 요구사항은 추적표에서 "검증됨" 이 되지 않는다(가짜 통과 없음).</summary>
public sealed class RecordingFactAttribute : FactAttribute
{
    public RecordingFactAttribute(string name)
    {
        if (!File.Exists(RealRecording.MetaPath(name)))
            Skip = $"녹음 없음 — python tools/fetch_recordings.py 로 받는다 ({RealRecording.Directory})";
    }
}
