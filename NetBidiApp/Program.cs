using System;
using UniBidi;
using Mono.Options;
using System.Text;

namespace NetBidiApp
{
    internal class Program
    {
        static readonly string USAGE_STRING = "[-h] FILENAME";

        static string ReadAllStdin() {
            using StreamReader sr = new(Console.OpenStandardInput(), Console.InputEncoding);
            return sr.ReadToEnd();
        }

        static string EncodeNonAsciiCharacters( string value ) {
            StringBuilder sb = new();
            foreach(char c in value) {
                // if( c > 127 ) {
                    // This character is too big for ASCII
                    sb.Append("\\u" + ((int) c).ToString("x4"));
                // }
                // else {
                //     sb.Append(c);
                // }
            }

            return sb.ToString();
        }


        static void PrintHelp() {
            Console.WriteLine($"Usage: netbidi {USAGE_STRING}");
        }

        static TextDirection StringToTextDirection(string text) {
            return text switch {
                "right" or "r" => TextDirection.RTL,
                "left" or "l" => TextDirection.LTR,
                "neutral" or "n" => TextDirection.NEUTRAL,
                _ => throw new Exception()
            };
        }

        static void Main(string[] args)
        {
            bool showHelp = false;
            string filename = "-";
            TextDirection textDirection = TextDirection.NEUTRAL;

            var optionSet = new OptionSet () {
                { "f|filename=", "The file to change the order of",
                    v => filename = v },
                { "d|direction=", "The direction of the text",
                    (string v) => textDirection = StringToTextDirection(v)},
                { "h|?|help",  "Show the program's usage string", 
                    v => showHelp = v != null },
            };

            // TODO: Detect invalid options and the like.
            optionSet.Parse(args);

            if (showHelp) {
                PrintHelp();
                return;
            }

            string inputText;
            if (filename == "-") {
                inputText = ReadAllStdin();
            }
            else {
                inputText = File.ReadAllText(filename);
            }

            uint[] logicalString = UniBidi.UniBidi.ConvertString(inputText);
            string displayString = UniBidi.UniBidi.BidiResolveString(logicalString, textDirection);
            Console.WriteLine(EncodeNonAsciiCharacters(displayString));
        }
    }
}