using System.CommandLine;
using System.Text;
using NetBidi;

namespace NetBidiApp
{
    enum UnicodeEscape {
        ALL,
        UNICODE,
        NONE
    }

    internal class Program
    {
        static string ReadFile(string filePath) {
            using StreamReader sr = filePath == "-" ? new(Console.OpenStandardInput(), Console.InputEncoding) : new(filePath);
            return sr.ReadToEnd();
        }

        static void WriteFile(string filePath, string fileContents) {
            using StreamWriter sw = filePath == "-" ? new(Console.OpenStandardOutput(), Console.OutputEncoding) : new(filePath);
            sw.Write(fileContents);
        }

        static string EscapeText(string text, UnicodeEscape unicodeEscaping) {
            if (unicodeEscaping == UnicodeEscape.NONE) {
                return text;
            }

            StringBuilder escapedBuilder = new();
            foreach(char currentChar in text) {
                // If the character cannot be displayed in 7 bits (Which is sbyte max) then it's not
                // ASCII.
                if (unicodeEscaping == UnicodeEscape.ALL || currentChar > sbyte.MaxValue) {
                    escapedBuilder.Append($"\\u{(int)currentChar:x4}");
                }
                else {
                    escapedBuilder.Append(currentChar);
                }
            }

            return escapedBuilder.ToString();
        }

        static int Main(string[] args)
        {
            Argument<string> inputFileArgument = new(
                name: "input_file",
                description: "The file to read and parse the value of. Use '-' for stdin.",
                getDefaultValue: () => "-"
            );

            Option<string> outputFileOption = new(
                name: "--output",
                description: "The file to write the converted string to. Use '-' for stdout.",
                getDefaultValue: () => "-"
            );
            outputFileOption.AddAlias("-o");

            Option<TextDirection> directionOption = new(
                name: "--direction",
                description: "The paragraph default direction.",
                getDefaultValue: () => TextDirection.NEUTRAL
            );
            directionOption.AddAlias("-d");

            Option<UnicodeEscape> unicodeEscapingOption = new(
                name: "--unicode-escape",
                description: "Escape characters to the format of \\uXXXX.",
                getDefaultValue: () => UnicodeEscape.NONE
            );
            unicodeEscapingOption.AddAlias("-u");

            RootCommand rootCommand = new("Use NetBidi to reorder logical strings");
            rootCommand.AddArgument(inputFileArgument);
            rootCommand.AddOption(outputFileOption);
            rootCommand.AddOption(directionOption);
            rootCommand.AddOption(unicodeEscapingOption);

            rootCommand.SetHandler((inputFile, outputFile, direction, unicodeEscaping) => 
                { 
                    uint[] logicalString = Bidi.ConvertString(ReadFile(inputFile));

                    BidiString bidiString = Bidi.CreateBidiString(logicalString, direction);
                    string visualString = bidiString.GetReorderedString();
                    visualString = EscapeText(visualString, unicodeEscaping);

                    WriteFile(outputFile, visualString); 
                },
                inputFileArgument, outputFileOption, directionOption, unicodeEscapingOption);

            return rootCommand.Invoke(args);
        }
    }
}