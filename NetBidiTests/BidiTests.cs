using Xunit;
using Xunit.Abstractions;

using System;
using NetBidiTests;
using NetBidi;

namespace NetBidiTests;

public class BidiTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Theory]
    [ClassData(typeof(BidiTestData))]
    [ClassData(typeof(BidiCharacterTestData))]
    public void TestResolveString(uint[] input, TextDirection paragraphEmbeddingLevel, uint[] expectedEmbeddingLevels, uint[] expectedOutput)
    {
        // output.WriteLine($"Test Input: {string.Join(" ", input.Select(x => x.ToString("X4")))}");
        // output.WriteLine($"Expected output: {string.Join(" ", testOutput.Select(x => x.ToString("X4")))}");

        // The unicode test don't support character mirroring.
        var (visualString, embeddingLevels) = NetBidi.NetBidi.BidiResolveStringEx(input, paragraphEmbeddingLevel, false);

        Assert.Equal(expectedEmbeddingLevels, embeddingLevels);
        Assert.Equal(expectedOutput, visualString);
    }
}
