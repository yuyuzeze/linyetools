using KikuCaption.Core.Enums;
using KikuCaption.Infrastructure.Diagnostics;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

public class DiskSpaceEvaluatorTests
{
    [Fact]
    public void Evaluate_BelowMinimum_ReturnsWarning()
    {
        long oneGb = (long)DiskSpaceEvaluator.BytesPerGb;

        var status = DiskSpaceEvaluator.Evaluate(oneGb, minimumGb: 2);

        Assert.Equal(EnvironmentCheckStatus.Warning, status);
    }

    [Fact]
    public void Evaluate_AtOrAboveMinimum_ReturnsOk()
    {
        long fiveGb = (long)(5 * DiskSpaceEvaluator.BytesPerGb);

        var status = DiskSpaceEvaluator.Evaluate(fiveGb, minimumGb: 2);

        Assert.Equal(EnvironmentCheckStatus.Ok, status);
    }

    [Fact]
    public void ToGigabytes_ConvertsCorrectly()
    {
        long threeGb = (long)(3 * DiskSpaceEvaluator.BytesPerGb);

        Assert.Equal(3d, DiskSpaceEvaluator.ToGigabytes(threeGb), precision: 3);
    }
}
