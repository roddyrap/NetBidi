using NetBidi;

namespace NetBidiTests;

public class BidiTestData : TheoryData<uint[], TextDirection, uint[], uint[]> {
    private const string UNICODE_TESTS_URL = "https://www.unicode.org/Public/UCD/latest/ucd/BidiTest.txt";
    private static readonly char[] UNICODE_TEST_CHAR_SEPARATORS = ['\t', ' '];

    // The tests specify that random characters for each specified class in the tests a random
    // character should be chosen. This algorithm doesn't group characters this way, so a predefined list
    // is used. This also makes the tests more predictable, which can be good or bad.
    static uint StringToBidiChar(string input) {
        return input switch {
            "L" =>   0x0061 /* a */,
            "R" =>   0x05d0 /* Hebrew Alef */,
            "AL" =>  0x0627 /* Arabic Alif */,
            "EN" =>  0x0030 /* 0 */,
            "ES" =>  0x002B /* + */,
            "ET" =>  0x0023 /* # */,
            "AN" =>  0x0661 /* Arabic number one */,
            "CS" =>  0x002C /* , */,
            "NSM" => 0x0300 /* Combining grave accent */,
            "BN" =>  0x0007 /* Bell */,
            "B" =>   0x2029 /* Paragraph separator */,
            "S" =>   0x0009 /* Tab */,
            "WS" =>  0x0020 /* Space */,
            "ON" =>  0x0021 /* ! */,
            "LRE" => 0x202A /* LRE */,
            "RLE" => 0x202B /* RLE */,
            "PDF" => 0x202C /* PDF */,
            "LRO" => 0x202D /* LRO */,
            "RLO" => 0x202E /* RLO */,
            "LRI" => 0x2066 /* LRI */,
            "RLI" => 0x2067 /* RLI */,
            "FSI" => 0x2068 /* FSI */,
            "PDI" => 0x2069 /* PDI */,
            _ => throw new Exception()
        };
    }

    static IEnumerable<TextDirection> BitsetToTextDirections(uint bitset) {
        if ((bitset & 1) > 0) {
            yield return TextDirection.NEUTRAL;
        }

        if ((bitset & 2) > 0) {
            yield return TextDirection.LTR;
        }

        if ((bitset & 4) > 0) {
            yield return TextDirection.RTL;
        }
    }

    public BidiTestData() {
        // TODO: I don't want to download the file each run. If I am doing it like this, I should download it at
        // compile-time and read from it at runtime.
        string bidiTestsString = TestUtils.GetWebFile("https://www.unicode.org/Public/UCD/latest/ucd/BidiTest.txt");

        int[] reorderIndices = [];
        uint[] embeddingLevels = [];
        foreach (var testLine in bidiTestsString.Split("\n")) {
            // Skip comment lines.
            if (testLine.StartsWith('#')) {
                continue;
            }

            if (testLine.StartsWith('@')) {
                string[] splitControlLine = testLine.Split('\t');
                if (splitControlLine.Length < 2) continue;
    
                if (testLine.StartsWith("@Reorder")) {
                    reorderIndices = splitControlLine[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x)).ToArray();
                    // Console.WriteLine($"Reorder line \"{testLine}\" -> {string.Join(", ", reorderIndices)}");
                }
                else if (testLine.StartsWith("@Levels")) {
                    embeddingLevels = splitControlLine[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x != "x").Select(x => Convert.ToUInt32(x)).ToArray();
                }

                // Ignore all other @ lines.
                continue;
            }

            string[] splitLine = testLine.Split(";", StringSplitOptions.RemoveEmptyEntries);
            if (splitLine.Length < 2) continue;

            uint[] testChars = splitLine[0].Split(UNICODE_TEST_CHAR_SEPARATORS, StringSplitOptions.RemoveEmptyEntries).Select(StringToBidiChar).ToArray();

            uint[] finalString = reorderIndices.Select(index => testChars[index]).ToArray();
            // Console.WriteLine($"Test Data: {string.Join(", ", testChars.Select(x => x.ToString("X4")))}; {string.Join(", ", reorderIndices)};{string.Join(", ", finalString.Select(x => x.ToString("X4")))}");

            foreach (TextDirection testDirection in BitsetToTextDirections(Convert.ToUInt32(splitLine[1]))) {
                this.Add(testChars, testDirection, embeddingLevels, finalString);
            }
        }
    }
}