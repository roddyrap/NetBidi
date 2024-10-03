using NetBidi;

namespace NetBidiTests;

public class BidiCharacterTestData : TheoryData<uint[], TextDirection, uint[], uint[]> {
    static TextDirection IntToDirection(uint input) {
        return input switch {
            0 => TextDirection.LTR,
            1 => TextDirection.RTL,
            2 => TextDirection.NEUTRAL,
            _ => throw new Exception()
        };
    }

    public BidiCharacterTestData() {
        string bidiCharTestsString = TestUtils.GetWebFile("https://www.unicode.org/Public/UCD/latest/ucd/BidiCharacterTest.txt");

        foreach (string line in bidiCharTestsString.Split("\n")) {
            if (line.StartsWith('#')) continue;

            string[] splitLine = line.Split(";");
            if (splitLine.Length < 2) continue;

            uint[] embeddingLevels = splitLine[3].Split(" ").Where(x => x != "x").Select(x => Convert.ToUInt32(x)).ToArray();

            TextDirection textDirection = IntToDirection(Convert.ToUInt32(splitLine[1], 16));

            uint[] testInput = splitLine[0].Split(" ").Select(uniCharString => Convert.ToUInt32(uniCharString, 16)).ToArray();
            int[] displayIndices = splitLine[4].Split(" ").Select(visualStringIndex => Convert.ToInt32(visualStringIndex)).ToArray();

            uint[] expectedOutput = displayIndices.Select(x => testInput[x]).ToArray();

            Add(testInput, textDirection, embeddingLevels.ToArray(), expectedOutput);
        }
    }
}