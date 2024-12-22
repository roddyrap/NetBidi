using Xunit.Abstractions;

using System.Diagnostics;
using NetBidi;

namespace NetBidiTests;

// TODO: The fact that I control the debug prints means I can make an actually informative
// TODO: file print hierarchy. Might be cool.

class TestTraceWrite(ITestOutputHelper output) : TraceListener
{
    readonly ITestOutputHelper output = output;

    public override void Write(string? message)
    {
        output.WriteLine(message);
    }

    public override void WriteLine(string? message)
    {
        output.WriteLine(message);
    }
}

public class BidiTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Theory]
    [ClassData(typeof(BidiTestData))]
    [ClassData(typeof(BidiCharacterTestData))]
    public void TestResolveString(uint[] input, TextDirection textDirection, uint[] expectedEmbeddingLevels, uint[] expectedOutput)
    {
        TestTraceWrite traceWriter = new(output);
        Trace.Listeners.Add(traceWriter);
        // output.WriteLine($"Test Input: {string.Join(" ", input.Select(x => x.ToString("X4")))}");
        // output.WriteLine($"Expected output: {string.Join(" ", testOutput.Select(x => x.ToString("X4")))}");

        BidiString bidiString = Bidi.CreateBidiString(input, textDirection);

        Assert.Equal(expectedEmbeddingLevels, bidiString.GetReolvedEmbeddingLevels());

        // The unicode test don't support character mirroring.
        Assert.Equal(expectedOutput, bidiString.GetReorderedCodePoints(false));

        Trace.Listeners.Remove(traceWriter);
    }
}
