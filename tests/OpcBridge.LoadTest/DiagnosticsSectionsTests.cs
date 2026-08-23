using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DiagnosticsSectionsTests
{
    private sealed record Section(int Value);

    [Fact]
    public void Safe_ReturnsBuiltValue_WhenBuilderSucceeds()
    {
        object? result = DiagnosticsSections.Safe("runtime", () => new Section(42), _ => Assert.Fail("must not report"));

        var section = Assert.IsType<Section>(result);
        Assert.Equal(42, section.Value);
    }

    [Fact]
    public void Safe_ReturnsNull_AndReportsError_WhenBuilderThrows()
    {
        Exception? reported = null;

        object? result = DiagnosticsSections.Safe("problems", () => throw new InvalidOperationException("boom"), e => reported = e);

        Assert.Null(result);
        var failure = Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal("boom", failure.Message);
    }
}
