using Xunit;

namespace FastFind.Windows.Tests.Helpers;

/// <summary>
/// 조건부 Fact 특성 - 조건이 false면 테스트 스킵
/// </summary>
public sealed class ConditionalFactAttribute : FactAttribute
{
    public ConditionalFactAttribute(string conditionPropertyName)
    {
        var conditionProperty = typeof(CIEnvironment).GetProperty(conditionPropertyName);
        if (conditionProperty?.GetValue(null) is bool condition && !condition)
        {
            Skip = CIEnvironment.GetSkipReason(conditionPropertyName.Replace("CanRun", "").Replace("Tests", ""));
        }
    }
}

/// <summary>
/// 조건부 Theory 특성 - 조건이 false면 테스트 스킵
/// </summary>
public sealed class ConditionalTheoryAttribute : TheoryAttribute
{
    public ConditionalTheoryAttribute(string conditionPropertyName)
    {
        var conditionProperty = typeof(CIEnvironment).GetProperty(conditionPropertyName);
        if (conditionProperty?.GetValue(null) is bool condition && !condition)
        {
            Skip = CIEnvironment.GetSkipReason(conditionPropertyName.Replace("CanRun", "").Replace("Tests", ""));
        }
    }
}

/// <summary>
/// Windows 전용 테스트 특성
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!CIEnvironment.CanRunWindowsSpecificTests)
        {
            Skip = CIEnvironment.GetSkipReason("Windows");
        }
    }
}

/// <summary>
/// 성능 테스트 특성 - CI에서는 자동 스킵
/// </summary>
public sealed class PerformanceTestFactAttribute : FactAttribute
{
    public PerformanceTestFactAttribute()
    {
        if (!CIEnvironment.CanRunPerformanceTests)
        {
            Skip = CIEnvironment.GetSkipReason("Performance");
        }
    }
}

/// <summary>
/// 스트레스 테스트 특성 - CI에서는 자동 스킵
/// </summary>
public sealed class StressTestFactAttribute : FactAttribute
{
    public StressTestFactAttribute()
    {
        if (!CIEnvironment.CanRunStressTests)
        {
            Skip = CIEnvironment.GetSkipReason("Stress");
        }
    }
}

/// <summary>
/// 통합 테스트 특성 - Linux CI에서는 자동 스킵
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!CIEnvironment.CanRunIntegrationTests)
        {
            Skip = CIEnvironment.GetSkipReason("Integration");
        }
    }
}