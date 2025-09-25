using System.Runtime.InteropServices;

namespace FastFind.Windows.Tests.Helpers;

/// <summary>
/// CI/CD 환경 감지 및 테스트 스킵 조건 제공
/// </summary>
public static class CIEnvironment
{
    /// <summary>
    /// CI/CD 환경에서 실행 중인지 확인
    /// </summary>
    public static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JENKINS_URL")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    /// <summary>
    /// Windows 플랫폼에서 실행 중인지 확인
    /// </summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Linux 플랫폼에서 실행 중인지 확인
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// macOS 플랫폼에서 실행 중인지 확인
    /// </summary>
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// 사용 가능한 메모리가 충분한지 확인 (CI 환경은 메모리 제한됨)
    /// </summary>
    public static bool HasSufficientMemory
    {
        get
        {
            try
            {
                var gc = GC.GetTotalMemory(false);
                var available = GC.GetTotalMemory(true);
                // CI 환경에서는 100MB 이하일 수 있음
                return available > 50_000_000; // 50MB 최소
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 성능 테스트 실행 가능 여부 (CI에서는 불안정하므로 스킵)
    /// </summary>
    public static bool CanRunPerformanceTests => !IsRunningInCI && HasSufficientMemory;

    /// <summary>
    /// Windows 전용 기능 테스트 실행 가능 여부
    /// </summary>
    public static bool CanRunWindowsSpecificTests => IsWindows;

    /// <summary>
    /// 스트레스 테스트 실행 가능 여부 (CI에서는 시간 제한으로 스킵)
    /// </summary>
    public static bool CanRunStressTests => !IsRunningInCI;

    /// <summary>
    /// 통합 테스트 실행 가능 여부 (Linux CI에서는 Windows 구성요소 없음)
    /// </summary>
    public static bool CanRunIntegrationTests => IsWindows || !IsRunningInCI;

    /// <summary>
    /// 테스트 스킵 이유 메시지 생성
    /// </summary>
    public static string GetSkipReason(string testType)
    {
        return testType switch
        {
            "Performance" => IsRunningInCI ?
                "Performance tests are unreliable in CI environment" :
                "Insufficient memory for performance testing",
            "Windows" => IsWindows ?
                "Not skipped" :
                "Windows-specific functionality not available on this platform",
            "Stress" => IsRunningInCI ?
                "Stress tests exceed CI time limits" :
                "Not skipped",
            "Integration" => (!IsWindows && IsRunningInCI) ?
                "Integration tests require Windows components not available in Linux CI" :
                "Not skipped",
            _ => "Test conditions not met"
        };
    }
}