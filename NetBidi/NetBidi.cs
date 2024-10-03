using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Text;

using BidiReturnData = (uint[] visualString, uint[] embeddingLevels);

[assembly: InternalsVisibleTo("NetBidiTests")]

namespace NetBidi;

public enum TextDirection {
    LTR,
    RTL,
    NEUTRAL
}

// According to the 3.3.2 specification of applying the X rules.
class DirectionalStatus(uint embeddingLevel, TextDirection directionalOverrideStatus, bool directionalIsolateStatus)
{
    public uint embeddingLevel = embeddingLevel;
    public TextDirection directionalOverrideStatus = directionalOverrideStatus;
    public bool directionalIsolateStatus = directionalIsolateStatus;
}

class BidiStringData {
    public uint[] logicalString;

    public uint paragraphEmbeddingLevel;

    public uint[] embeddingLevels;
    public BidiClass[] bidiClasses;

    public BidiStringData(uint[] logicalString, uint paragraphEmbeddingLevel, uint[] embeddingLevels, BidiClass[] bidiClasses) {
        this.logicalString = logicalString;
        this.paragraphEmbeddingLevel = paragraphEmbeddingLevel;

        this.embeddingLevels = embeddingLevels;
        this.bidiClasses = bidiClasses;
    }

    public BidiClass GetBidiClass(int characterIndex) {
        return BidiMap.GetBidiClass(this.logicalString[characterIndex]);
    }
}

class IsolatingRunSequence {
    public List<int> isolatingRunIndices;
    public BidiClass startOfSequene;
    public BidiClass endOfSequence;

    // BD13 explicitly mentions that all level runs in an isolating run sequences have the same embedding level, so
    // there's no reason not to expose it to the user.
    public uint embdeddingLevel;

    public int GetRelativeIndex(int wantedAbsoluteIndex) {
        return this.isolatingRunIndices.FindIndex(absoluteIndex => absoluteIndex == wantedAbsoluteIndex);
    }

    // TODO: Change to take constref of BidiData because at this point I use most of it.
    public IsolatingRunSequence(uint[] embeddingLevels, BidiClass[] bidiClasses, List<ArraySegment<uint>> runLevelSequence, uint paragraphEmbeddingLevel) {
        // "Unpack" the run level sequence to a one-dimensional array of the isolating level run sequence indices.
        this.isolatingRunIndices = new();
        foreach(var runLevelArray in runLevelSequence) {
            for (int i = runLevelArray.Offset; i < runLevelArray.Offset + runLevelArray.Count; ++i) {
                this.isolatingRunIndices.Add(i);
            }
        }

        // Ensure that the indices are in order.
        this.isolatingRunIndices.Sort();

        // This assignment should work because all level runs have the same embedding level.
        this.embdeddingLevel = embeddingLevels[this.isolatingRunIndices[0]];

        // Calculate the start-of-sequence (sos) and end-of-sequence (eos) values according to X10.
        int runStartIndex = this.isolatingRunIndices[0];
        int runEndIndex = this.isolatingRunIndices.Last();

        int higherEmbeddingLevel;
        if (runStartIndex == 0) {
            higherEmbeddingLevel = (int)Math.Max(paragraphEmbeddingLevel, embeddingLevels[runStartIndex]);
        } else {
            higherEmbeddingLevel = (int)Math.Max(embeddingLevels[runStartIndex], embeddingLevels[runStartIndex - 1]);
        }

        startOfSequene = higherEmbeddingLevel % 2 == 0 ? BidiClass.L : BidiClass.R;

        // It can be inferred from the notes of BD13 that if an isolate initiator has a matching PDI it should always
        // continue its level run (As a matching PDI by design wil always be right after an isolation sequence end and will
        // always have the same embedding level as it's initiator). Therefore, if an isolate initiator is at the end of an
        // isolating run sequence we can assume that it has no matching PDI without needing to check.
        if (runEndIndex == embeddingLevels.Length - 1 || bidiClasses[runEndIndex].IsIsolateInitiator()) {
            higherEmbeddingLevel = (int)Math.Max(paragraphEmbeddingLevel, embeddingLevels[runEndIndex]);
        } else {
            higherEmbeddingLevel = (int)Math.Max(embeddingLevels[runEndIndex], embeddingLevels[runEndIndex + 1]);
        }

        endOfSequence = higherEmbeddingLevel % 2 == 0 ? BidiClass.L : BidiClass.R;
    }
}

public static class NetBidi
{
    // According to BD2.
    public const uint MAX_DPETH = 125;

    // According to BD16.
    public const uint MAX_BRACKET_PAIRS = 63;

    // Not part of spec pre se, but are related to P3.
    const uint LTR_DEFAULT_EMBEDDING_LEVEL = 0;
    const uint RTL_DEFAULT_EMBEDDING_LEVEL = 1;

    public static string BidiResolveString(string logicalString) {
        return BidiResolveString(ConvertString(logicalString));
    }

    public static string BidiResolveString(uint[] logicalString, TextDirection givenParagraphEmbedding = TextDirection.NEUTRAL, bool mirrorCharacters = true) {
        return ConvertUInts(BidiResolveStringEx(logicalString, givenParagraphEmbedding, mirrorCharacters).visualString);
    }

    // TODO: This method currenly copies the enumerable, which is rather wasteful.
    // TODO: It might be worth it to make it less generic and use a span.
    public static IEnumerable<T[]> SplitAndKeep<T>(IEnumerable<T> input, T splitChar) {
        List<T> currentSplit = new();
        foreach (T currentValue in input) {
            currentSplit.Add(currentValue);

            if (EqualityComparer<T>.Default.Equals(currentValue, splitChar)) {
                yield return currentSplit.ToArray();
                currentSplit.Clear();
            }
        }

        if (currentSplit.Count > 0) {
            yield return currentSplit.ToArray();
        }
    }

    public static BidiReturnData BidiResolveStringEx(uint[] logicalString, TextDirection givenParagraphEmbedding = TextDirection.NEUTRAL, bool mirrorCharacters = true) {
        // TODO: Move paragraph seprator constant to a normal place.
        const uint PARAGRAPH_SEPARATOR_CODEPOINT = 0x2029;
    
        // If a paragraph separtor is found and it's not the last character in the sequence, split the given sequence to
        // all of the seuquences in which paragraph separators are the last character, and run this method on them.
        // According to rule P1.
        List<uint> visualString = new();
        List<uint> embeddingLevels = new();

        foreach (var splitLine in SplitAndKeep(logicalString, PARAGRAPH_SEPARATOR_CODEPOINT)) {
            var (newVisualString, newEmbeddingLevels) = BidiResolvePargraph(splitLine, givenParagraphEmbedding, mirrorCharacters);
            visualString.AddRange(newVisualString);
            embeddingLevels.AddRange(newEmbeddingLevels);
        }

        return (visualString.ToArray(), embeddingLevels.ToArray());
    }

    static BidiReturnData BidiResolvePargraph(uint[] logicalString, TextDirection givenParagraphEmbedding = TextDirection.NEUTRAL, bool mirrorCharacters = true) {
        // TODO: Check for Paragraph separators? Maybe?

        uint paragraphEmbeddingLevel = givenParagraphEmbedding switch {
            TextDirection.LTR => LTR_DEFAULT_EMBEDDING_LEVEL,
            TextDirection.RTL => RTL_DEFAULT_EMBEDDING_LEVEL,
            TextDirection.NEUTRAL => GetParagraphEmbeddingLevel(logicalString),
            _ => throw new InvalidOperationException()
        };

        Console.WriteLine($"Paragraph embedding level: {paragraphEmbeddingLevel}");

        BidiStringData bidiData = ResolveExplicit(logicalString, paragraphEmbeddingLevel);

        Console.WriteLine($"Isolating runs class values: {string.Join(", ", bidiData.bidiClasses)}");
        Console.WriteLine($"Isolating runs embedding values: {string.Join(", ", bidiData.embeddingLevels)}");

        // If there are only explicit characters in the logical string, X9 might strip all of them and have an
        // empty string. There's no point in reordering empty strings.
        if (bidiData.logicalString.Length == 0) return (Array.Empty<uint>(), Array.Empty<uint>());

        List<IsolatingRunSequence> isolatingRuns = GetIsolatingRunSequences(bidiData);

        // A part of X10.
        ResolveWX(isolatingRuns, bidiData);

        Console.WriteLine($"Intermediate class values: {string.Join(", ", bidiData.bidiClasses)}");

        // A part of X10.
        ResolveNX(isolatingRuns, bidiData);

        uint[] outputLogicalString = ReorderString(bidiData, mirrorCharacters);
        return (outputLogicalString, bidiData.embeddingLevels);
    }

    // The L rules implementation. TODO: Linebreaking size support?
    static uint[] ReorderString(BidiStringData bidiData, bool mirrorCharacters = true) {
        for (int absoluteCharIndex = 0; absoluteCharIndex < bidiData.logicalString.Length; ++absoluteCharIndex) {
            BidiClass bidiClassValue = BidiMap.GetBidiClass(bidiData.logicalString[absoluteCharIndex]);
            if (bidiClassValue == BidiClass.S || bidiClassValue == BidiClass.B) {
                bidiData.embeddingLevels[absoluteCharIndex] = bidiData.paragraphEmbeddingLevel;

                for (int iteratedCharIndex = absoluteCharIndex - 1; iteratedCharIndex >= 0; --iteratedCharIndex) {
                    BidiClass iteratedBidiClassValue = BidiMap.GetBidiClass(bidiData.logicalString[iteratedCharIndex]);
                    if (iteratedBidiClassValue == BidiClass.WS || iteratedBidiClassValue.IsIsolateInitiator() || iteratedBidiClassValue == BidiClass.PDI) {
                        bidiData.embeddingLevels[iteratedCharIndex] = bidiData.paragraphEmbeddingLevel;
                    } else {
                        break;
                    }
                }
            }
        }

        // L1 section 4.
        for (int absoluteCharIndex = bidiData.logicalString.Length - 1; absoluteCharIndex >= 0; --absoluteCharIndex) {
            BidiClass iteratedBidiClassValue = BidiMap.GetBidiClass(bidiData.logicalString[absoluteCharIndex]);
            if (iteratedBidiClassValue == BidiClass.WS || iteratedBidiClassValue.IsIsolateInitiator() || iteratedBidiClassValue == BidiClass.PDI) {
                bidiData.embeddingLevels[absoluteCharIndex] = bidiData.paragraphEmbeddingLevel;
            } else {
                break;
            }
        }

        uint[] newString = new uint[bidiData.logicalString.Length];
        Array.Copy(bidiData.logicalString, newString, newString.Length);

        uint highestEmbeddingLevel = bidiData.embeddingLevels.Max();
        // uint lowestOddEmbeddingLevel = bidiData.embeddingLevels.Where(level => level % 2 == 1).Min();
        for (uint minReversedLevel = highestEmbeddingLevel; minReversedLevel > 0; --minReversedLevel) {
            int reverseStartIndex = int.MaxValue;
            for (int currentIndex = 0; currentIndex < newString.Length; ++currentIndex) {
                if (bidiData.embeddingLevels[currentIndex] >= minReversedLevel) {
                    if (reverseStartIndex == int.MaxValue) {
                        reverseStartIndex = currentIndex;
                    }
                    // Handle the scenario in which the last character in the string should be reversed.
                    else if (currentIndex == newString.Length - 1) {
                        Array.Reverse(newString, reverseStartIndex, currentIndex - reverseStartIndex + 1);
                        reverseStartIndex = int.MaxValue;
                    }
                }
                else if (bidiData.embeddingLevels[currentIndex] < minReversedLevel && reverseStartIndex != int.MaxValue) {
                    Array.Reverse(newString, reverseStartIndex, currentIndex - reverseStartIndex);
                    reverseStartIndex = int.MaxValue;
                }
            }
        }

        // TODO: L3 implementation.

        // L4.
        if (mirrorCharacters) {
            for (int absoluteCharIndex = 0; absoluteCharIndex < newString.Length; ++absoluteCharIndex) {
                if (bidiData.embeddingLevels[absoluteCharIndex] % 2 == RTL_DEFAULT_EMBEDDING_LEVEL) {
                    newString[absoluteCharIndex] = BidiMap.GetMirror(newString[absoluteCharIndex]);
                }
            }
        }

        Console.WriteLine($"Input: {string.Join(", ", bidiData.logicalString.Select(x => x.ToString("X4")))}");
        Console.WriteLine($"Output: {string.Join(", ", newString.Select(x => x.ToString("X4")))}");
        Console.WriteLine($"Embedding Values: {string.Join(", ", bidiData.embeddingLevels)}");
        return newString;
    }

    // According to BD16.
    static List<(int, int)> GetBracketPairs(IsolatingRunSequence isolatingRunSequence, Span<uint> logicalString, BidiClass[] bidiClasses) {
        // Holds the the index of the opening bracket and the bracket that is paired to it, at that order.
        List<(int, uint)> bracketStack = new();

        List<(int, int)> bracketPairs = new();

        foreach (int currentAbsoluteIndex in isolatingRunSequence.isolatingRunIndices) {
            // BD 14 & 15 specify that bracket pair matching should only be applied to Other Neutral bidi class characters.
            if (bidiClasses[currentAbsoluteIndex] != BidiClass.ON) continue;

            uint currentChar = logicalString[currentAbsoluteIndex];
            BracketType currentBracketType = BidiMap.GetBracketType(currentChar);
            if (currentBracketType == BracketType.OPEN) {
                // BD16 specifies that if the bracket stack is too small (need a 64th entry) then processing is immediatetly stopped.
                if (bracketStack.Count < MAX_BRACKET_PAIRS) {
                    uint pairedBracket = BidiMap.GetPairedBracket(currentChar);

                    // Handle Unicode's equivalence of U+3008/U+3009, and U+2329/U+232A.
                    if (pairedBracket == 0x3009) pairedBracket = 0x232A;

                    bracketStack.Add((currentAbsoluteIndex, pairedBracket));
                }
                else {
                    return bracketPairs;
                }
            } else if (currentBracketType == BracketType.CLOSE) {
                // Handle Unicode's equivalence of U+3008/U+3009, and U+2329/U+232A.
                uint checkedBracket = currentChar;
                if (checkedBracket == 0x3009) checkedBracket = 0x232A;

                for (int bracketStackIndex = bracketStack.Count -1; bracketStackIndex >= 0; --bracketStackIndex) {
                    if (checkedBracket == bracketStack[bracketStackIndex].Item2) {
                        bracketPairs.Add((bracketStack[bracketStackIndex].Item1, currentAbsoluteIndex));
                        bracketStack.RemoveRange(bracketStackIndex, bracketStack.Count - bracketStackIndex);

                        break;
                    }
                }
            }
        }

        return bracketPairs;
    }

    static BidiClass NXResolveStrongBidiClass(BidiClass bidiClass) {
        if (bidiClass == BidiClass.L) {
            return BidiClass.L;
        }

        // N0 mentions that EN and AN are supposed to be treated as strong Rs.
        // AL shouldn't appear by this stage in the algorithm, but I am including it for correctness.
        else if (bidiClass == BidiClass.R || bidiClass == BidiClass.EN ||
                 bidiClass == BidiClass.AN || bidiClass == BidiClass.AL) {
            return BidiClass.R;
        }

        return BidiClass.ON;
    }

    // NX rules.
    static void ResolveNX(List<IsolatingRunSequence> isolatingRuns, BidiStringData bidiData) {
        foreach (IsolatingRunSequence isolatingRunSequence in isolatingRuns) {
            // Rule N0.
            List<(int, int)> bracketPairs = GetBracketPairs(isolatingRunSequence, bidiData.logicalString, bidiData.bidiClasses);

            // N0 mentions that the bracket pairs need to be processed sequentially by the order of the opening paired brackets.
            bracketPairs.Sort((first_pair, second_pair) => first_pair.Item1.CompareTo(second_pair.Item1));

            Console.WriteLine(string.Join(", ", bracketPairs));
            Console.WriteLine($"Isolating run {isolatingRunSequence.isolatingRunIndices.First()} - {isolatingRunSequence.isolatingRunIndices.Last()}; EL: {isolatingRunSequence.embdeddingLevel}");

            foreach ((int, int) bracketIndices in bracketPairs) {
                // I need to know if the matching strong has been found, not just the first strong one found.
                bool foundEmbeddingBidiValue = false;
                bool foundInvertedBidiValue = false;

                BidiClass embeddingBidiClass = isolatingRunSequence.embdeddingLevel % 2 == LTR_DEFAULT_EMBEDDING_LEVEL? BidiClass.L : BidiClass.R;

                int startingRelativeIndex = isolatingRunSequence.GetRelativeIndex(bracketIndices.Item1);
                int endingRelativeIndex = isolatingRunSequence.GetRelativeIndex(bracketIndices.Item2);
                for (int relativeInsideCharIndex = startingRelativeIndex; relativeInsideCharIndex <= endingRelativeIndex; ++relativeInsideCharIndex) {
                    int absoluteIndexCharIndex = isolatingRunSequence.isolatingRunIndices[relativeInsideCharIndex];
                    BidiClass resolvedClass = NXResolveStrongBidiClass(bidiData.bidiClasses[absoluteIndexCharIndex]);

                    if (resolvedClass == embeddingBidiClass) {
                        foundEmbeddingBidiValue = true;
                    } else if (resolvedClass != BidiClass.ON) {
                        foundInvertedBidiValue = true;
                    }
                }

                if (foundEmbeddingBidiValue) {
                    bidiData.bidiClasses[bracketIndices.Item1] = embeddingBidiClass;
                    bidiData.bidiClasses[bracketIndices.Item2] = embeddingBidiClass;
                // According to N0 section c.
                } else if (foundInvertedBidiValue) {
                    BidiClass preceedingStrongBidiClass = isolatingRunSequence.startOfSequene;
                    for (int beforeBracketIndex = bracketIndices.Item1 - 1; beforeBracketIndex >= 0; --beforeBracketIndex) {
                        BidiClass resolvedBidiClassValue = NXResolveStrongBidiClass(bidiData.bidiClasses[beforeBracketIndex]);
                        if (resolvedBidiClassValue != BidiClass.ON) {
                            preceedingStrongBidiClass = resolvedBidiClassValue;
                            break;
                        }
                    }

                    BidiClass internalBidiClass = embeddingBidiClass;
                    if (preceedingStrongBidiClass != embeddingBidiClass) {
                        internalBidiClass = embeddingBidiClass == BidiClass.L? BidiClass.R : BidiClass.L;
                    }

                    bidiData.bidiClasses[bracketIndices.Item1] = internalBidiClass;
                    bidiData.bidiClasses[bracketIndices.Item2] = internalBidiClass;
                }

                // TODO: Isolating run sequence considerations...
                // As mentioned in N0:
                // "Any number of characters that had original bidirectional character type NSM prior to the application of
                // W1 that immediately follow a paired bracket which changed to L or R under N0 should change to match the
                // type of their preceding bracket."
                if (bidiData.bidiClasses[bracketIndices.Item2].IsStrongBidiClass()) {
                    for (int followingBracketIndex = bracketIndices.Item2; followingBracketIndex < bidiData.logicalString.Length; ++followingBracketIndex) {
                        if (bidiData.GetBidiClass(followingBracketIndex) == BidiClass.NSM) {
                            bidiData.bidiClasses[followingBracketIndex] = bidiData.bidiClasses[bracketIndices.Item2];
                        }
                    }

                    for (int followingBracketIndex = bracketIndices.Item1; followingBracketIndex < bracketIndices.Item2; ++followingBracketIndex) {
                        if (bidiData.GetBidiClass(followingBracketIndex) == BidiClass.NSM) {
                            bidiData.bidiClasses[followingBracketIndex] = bidiData.bidiClasses[bracketIndices.Item1];
                        }
                    }
                }
            }

            Console.WriteLine($"After N0 class values: {string.Join(", ", bidiData.bidiClasses)}");
            Console.WriteLine($"Isolating run start/end: {isolatingRunSequence.startOfSequene}/{isolatingRunSequence.endOfSequence}");

            // Rule N1. TODO: This is an extremely bad implementation that I MUST change in the future.
            BidiClass startBidiClass = NXResolveStrongBidiClass(isolatingRunSequence.startOfSequene);
            (int, BidiClass)? lastStrongChar = (-1, startBidiClass);
            
            for (int relativeCharIndex = 0; relativeCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++relativeCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[relativeCharIndex];

                BidiClass currentBidiClass = bidiData.bidiClasses[absoluteCharIndex];
                BidiClass resolvedBidiClass = NXResolveStrongBidiClass(currentBidiClass);

                if (currentBidiClass.IsNeutralOrIsolate()) {
                    if (relativeCharIndex == isolatingRunSequence.isolatingRunIndices.Count - 1) {
                        relativeCharIndex += 1;
                        currentBidiClass = isolatingRunSequence.endOfSequence;
                        resolvedBidiClass = NXResolveStrongBidiClass(isolatingRunSequence.endOfSequence);
                    }
                    else {
                        continue;
                    }
                }

                if (resolvedBidiClass.IsStrongBidiClass()) {
                    if (lastStrongChar.HasValue && lastStrongChar?.Item2 == resolvedBidiClass) {
                        for (int iteratedIndex = lastStrongChar.Value.Item1 + 1; iteratedIndex < relativeCharIndex; ++ iteratedIndex) {
                            int absoluteIteratedIndex = isolatingRunSequence.isolatingRunIndices[iteratedIndex];
                            bidiData.bidiClasses[absoluteIteratedIndex] = resolvedBidiClass;
                        }
                    }

                    lastStrongChar = (relativeCharIndex, resolvedBidiClass);
                }
                else {
                    lastStrongChar = null;
                }
            }

            Console.WriteLine($"After N1 class values: {string.Join(", ", bidiData.bidiClasses)}");

            // Rule N2.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                if (bidiData.bidiClasses[absoluteCharIndex].IsNeutralOrIsolate()) {
                    bidiData.bidiClasses[absoluteCharIndex] = bidiData.embeddingLevels[absoluteCharIndex] % 2 == LTR_DEFAULT_EMBEDDING_LEVEL ? BidiClass.L : BidiClass.R;
                }
            }

            Console.WriteLine($"After N2 class values: {string.Join(", ", bidiData.bidiClasses)}");
            Console.WriteLine($"After N2 embedding values: {string.Join(", ", bidiData.embeddingLevels)}");

            // Rules I1 & I2.
            // Important: From now on we can't use the isolating run sequence's embedding value, because it may not represent all characters inside of it.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                BidiClass currentBidiClassValue = bidiData.bidiClasses[absoluteCharIndex];
                uint currentEmbeddingLevel = bidiData.embeddingLevels[absoluteCharIndex];

                // I1.
                if (currentEmbeddingLevel % 2 == LTR_DEFAULT_EMBEDDING_LEVEL) {
                    if (currentBidiClassValue == BidiClass.R) {
                        bidiData.embeddingLevels[absoluteCharIndex] += 1;
                    } else if (currentBidiClassValue == BidiClass.AN || currentBidiClassValue == BidiClass.EN) {
                        bidiData.embeddingLevels[absoluteCharIndex] += 2;
                    }
                }
                // I2.
                else {
                    if (currentBidiClassValue == BidiClass.L || currentBidiClassValue == BidiClass.EN || currentBidiClassValue == BidiClass.AN) {
                        bidiData.embeddingLevels[absoluteCharIndex] += 1;
                    }
                }
            }

            Console.WriteLine($"After I1/2 class values: {string.Join(", ", bidiData.bidiClasses)}");
            Console.WriteLine($"After I1/2 embedding values: {string.Join(", ", bidiData.embeddingLevels)}");
        }
    }

    // Really simple (and wasteful) algorithm, according to BD7.
    // TODO: The special case for 1-length strings is bullshit and I want to reimplement this method.
    static List<ArraySegment<uint>> GetLevelRuns(BidiStringData bidiData) {
        List<ArraySegment<uint>> levelRuns = new();
        if (bidiData.logicalString.Length != bidiData.embeddingLevels.Length || bidiData.logicalString.Length == 0) return levelRuns;

        // If the string is of length 1, the for loop bellow will never run, and no level run will be created.
        if (bidiData.logicalString.Length == 1) {
            levelRuns.Add(new ArraySegment<uint>(bidiData.logicalString, 0, 1));
            return levelRuns;
        }

        int currentLevelRunStartIndex = 0;
        uint currentLevelRunEmbeddingLevel = bidiData.embeddingLevels[0];

        for (int currentIndex = 1; currentIndex < bidiData.logicalString.Length; ++currentIndex) {
            uint currentEmbeddingLevel = bidiData.embeddingLevels[currentIndex];

            if (currentEmbeddingLevel != currentLevelRunEmbeddingLevel) {
                levelRuns.Add(new ArraySegment<uint>(bidiData.logicalString, currentLevelRunStartIndex, currentIndex - currentLevelRunStartIndex));

                currentLevelRunStartIndex = currentIndex;
                currentLevelRunEmbeddingLevel = currentEmbeddingLevel;
            }

            // Handle the last character of the string in a level run.
            if (currentIndex == bidiData.logicalString.Length -1) {
                levelRuns.Add(new ArraySegment<uint>(bidiData.logicalString, currentLevelRunStartIndex, currentIndex - currentLevelRunStartIndex + 1));
            }
        }

        return levelRuns;
    }

    // According to BD13, using values calculated from X1-X9.
    static List<IsolatingRunSequence> GetIsolatingRunSequences(BidiStringData bidiData) {
        List<ArraySegment<uint>> levelRuns = GetLevelRuns(bidiData);

        Console.WriteLine("Level Runs:");
        foreach (ArraySegment<uint> levelRun in levelRuns) {
            Console.WriteLine($"{levelRun.Offset} - {levelRun.Count + levelRun.Offset - 1}");
        }

        // TODO: Finding PDI vailidity is dumb and really wasteful because I am doing it in the explicit resolve already.
        // TODO: I should REALLY use it instead of doing it AGAIN here.

        // Store the level run index of each valid PDI.
        Dictionary<int, int> pdiLevelRuns = new();

        // Store the index of the matching PDI to the index of every valid isolate initiator.
        Dictionary<int, int> isolateInitiatorToPDI = new();

        for (int currentLevelRunIndex = 0; currentLevelRunIndex < levelRuns.Count; ++currentLevelRunIndex) {
            for (int currentCharIndex = 0; currentCharIndex < levelRuns[currentLevelRunIndex].Count; ++currentCharIndex) {
                int absoluteCharIndex = levelRuns[currentLevelRunIndex].Offset + currentCharIndex;

                Console.WriteLine($"Level Run char index: {absoluteCharIndex} {bidiData.bidiClasses[absoluteCharIndex]} {GetMatchingPDIIndex(bidiData.logicalString, absoluteCharIndex)} {currentCharIndex}/{currentLevelRunIndex}");

                // Dumb way to add valid PDIs. The current bidi char check happens inside the method, so I don't need to do it here.
                // TODO: Replace PDI recognition because it's dumb.
                int matchingPdiIndex = GetMatchingPDIIndex(bidiData.logicalString, absoluteCharIndex);
                if (matchingPdiIndex != int.MaxValue) {
                    isolateInitiatorToPDI.Add(absoluteCharIndex, matchingPdiIndex);
                }
                // TODO: I use BidiMap.GetBidiClass directly because PDIs get overwritten in bidiClasses.
                // TODO: I am not sure if this is correct. If I need to change this then change it below too.
                else if (bidiData.GetBidiClass(absoluteCharIndex) == BidiClass.PDI && isolateInitiatorToPDI.ContainsValue(absoluteCharIndex)) {
                    pdiLevelRuns.Add(absoluteCharIndex, currentLevelRunIndex);
                }
            }
        }

        Console.WriteLine($"Isolate Initiators to PDI: {string.Join(", ", isolateInitiatorToPDI)}");
        Console.WriteLine($"PDI Level runs: {string.Join(", ", pdiLevelRuns)}");

        List<IsolatingRunSequence> isolationRunSequences = new();

        foreach (ArraySegment<uint> levelRun in levelRuns) {
            int startIndex = levelRun.Offset;

            if (bidiData.GetBidiClass(startIndex) != BidiClass.PDI || !pdiLevelRuns.ContainsValue(startIndex)) {
                List<ArraySegment<uint>> currentIsolationRun = [levelRun];

                while (true) {
                    ArraySegment<uint> lastLevelRun = currentIsolationRun.Last();
                    int currentEndIndex = lastLevelRun.Offset + lastLevelRun.Count - 1;
                    if (isolateInitiatorToPDI.TryGetValue(currentEndIndex, out int endPdiLevelRunIndex)) {
                        currentIsolationRun.Add(levelRuns[pdiLevelRuns[endPdiLevelRunIndex]]);
                    } else {
                        break;
                    }
                }

                isolationRunSequences.Add(new IsolatingRunSequence(bidiData.embeddingLevels, bidiData.bidiClasses, currentIsolationRun, bidiData.paragraphEmbeddingLevel));
            }
        }

        return isolationRunSequences;
    }

    static void ResolveW1(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
            int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];

            if (bidiClasses[absoluteCharIndex] == BidiClass.NSM) {
                if (currentCharIndex == 0) {
                    bidiClasses[absoluteCharIndex] = isolatingRunSequence.startOfSequene;
                } else {
                    int previousCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex - 1];
                    BidiClass previousCharType = bidiClasses[previousCharAbsoluteIndex];
                    if (previousCharType.IsIsolateInitiator() || previousCharType == BidiClass.PDI) {
                        bidiClasses[absoluteCharIndex] = BidiClass.ON;
                    } else {
                        bidiClasses[absoluteCharIndex] = previousCharType;
                    }
                }
            }
        }
    }

    static void ResolveW2(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        BidiClass lastStrongBidiClassValue = isolatingRunSequence.startOfSequene;
        for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
            int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
            BidiClass currentBidiClassValue = bidiClasses[absoluteCharIndex];

            if (currentBidiClassValue.IsStrongBidiClass()) {
                lastStrongBidiClassValue = currentBidiClassValue;
            }
            else if (currentBidiClassValue == BidiClass.EN) {
                if (lastStrongBidiClassValue == BidiClass.AL) {
                    bidiClasses[absoluteCharIndex] = BidiClass.AN;
                }
            }
        }
    }

    static void ResolveW3(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
            if (bidiClasses[absoluteCharIndex] == BidiClass.AL) {
                bidiClasses[absoluteCharIndex] = BidiClass.R;
            }
        }
    }

    static void ResolveW4(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        for (int currentCharIndex = 1; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count - 1; ++currentCharIndex) {
            int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
            BidiClass currentCharType = bidiClasses[absoluteCharIndex];

            if (currentCharType != BidiClass.ES && currentCharType != BidiClass.CS) continue;

            int previousCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex - 1];
            int nextCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex + 1];

            BidiClass previousCharType = bidiClasses[previousCharAbsoluteIndex];
            BidiClass nextCharType = bidiClasses[nextCharAbsoluteIndex];

            if (previousCharType != nextCharType) continue;

            if (nextCharType == BidiClass.EN) {
                bidiClasses[absoluteCharIndex] = BidiClass.EN;
            } else if (currentCharType == BidiClass.CS && nextCharType == BidiClass.AN) {
                bidiClasses[absoluteCharIndex] = BidiClass.AN;
            }
        }
    }

    static void ResolveW5(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
            int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
            if (bidiClasses[absoluteCharIndex] != BidiClass.EN) continue;

            for (int iteratedCharIndex = currentCharIndex - 1; iteratedCharIndex >= 0; --iteratedCharIndex) {
                int absoluteIteratedCharIndex = isolatingRunSequence.isolatingRunIndices[iteratedCharIndex];
                if (bidiClasses[absoluteIteratedCharIndex] == BidiClass.ET) {
                    bidiClasses[absoluteIteratedCharIndex] = BidiClass.EN;
                } else break;
            }
            for (int iteratedCharIndex = currentCharIndex + 1; iteratedCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++iteratedCharIndex) {
                int absoluteIteratedCharIndex = isolatingRunSequence.isolatingRunIndices[iteratedCharIndex];
                if (bidiClasses[absoluteIteratedCharIndex] == BidiClass.ET) {
                    bidiClasses[absoluteIteratedCharIndex] = BidiClass.EN;
                } else break;
            }
        }
    }

    static void ResolveW6(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
            if (bidiClasses[absoluteCharIndex].IsSeparator() || bidiClasses[absoluteCharIndex] == BidiClass.ET) {
                bidiClasses[absoluteCharIndex] = BidiClass.ON;
            }
        }
    }

    static void ResolveW7(IsolatingRunSequence isolatingRunSequence, BidiClass[] bidiClasses) {
        BidiClass lastStrongBidiClassValue = isolatingRunSequence.startOfSequene;
        for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
            int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
            BidiClass currentBidiClassValue = bidiClasses[absoluteCharIndex];

            if (currentBidiClassValue.IsStrongBidiClass()) {
                lastStrongBidiClassValue = currentBidiClassValue;
            }
            else if (currentBidiClassValue == BidiClass.EN && lastStrongBidiClassValue == BidiClass.L) {
                bidiClasses[absoluteCharIndex] = BidiClass.L;
            }
        }
    }

    // Apply the W1-W7 rules on the bidi data.
    static void ResolveWX(List<IsolatingRunSequence> isolatingRuns, BidiStringData bidiData) {
        foreach (IsolatingRunSequence isolatingRunSequence in isolatingRuns) {
            ResolveW1(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW2(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW3(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW4(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW5(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW6(isolatingRunSequence, bidiData.bidiClasses);

            ResolveW7(isolatingRunSequence, bidiData.bidiClasses);
        }
    }

    static void HandleEmbedded(BidiClass embeddedChar, Stack<DirectionalStatus> directionalStack, ref uint overflowIsolateCount, ref uint overflowEmbeddingCount) {
        bool isEven;
        TextDirection newDirectionalOverride;

        if (embeddedChar == BidiClass.RLE) {
            isEven = false;
            newDirectionalOverride = TextDirection.NEUTRAL;
        } else if (embeddedChar == BidiClass.LRE) {
            isEven = true;
            newDirectionalOverride = TextDirection.NEUTRAL;
        } else if (embeddedChar == BidiClass.RLO) {
            isEven = false;
            newDirectionalOverride = TextDirection.RTL;

        } else if (embeddedChar == BidiClass.LRO) {
            isEven = true;
            newDirectionalOverride = TextDirection.LTR;
        } else {
            return;
        }

        uint newEmbeddingLevel = GetLargerParityThan(directionalStack.Peek().embeddingLevel, isEven);
        if (newEmbeddingLevel <= MAX_DPETH && overflowIsolateCount == 0 && overflowEmbeddingCount == 0) {
            directionalStack.Push(new DirectionalStatus(newEmbeddingLevel, newDirectionalOverride, false));
        } else {
            if (overflowIsolateCount == 0) {
                overflowEmbeddingCount += 1;
            }
        }
    }

    static void HandleIsolate(ref BidiClass isolateChar, Stack<DirectionalStatus> directionalStack, ref uint overflowIsolateCount, ref uint overflowEmbeddingCount, ref uint validIsolateCount, ref uint newCurrentEmbeddedLevel) {
        newCurrentEmbeddedLevel = directionalStack.Peek().embeddingLevel;

        bool isEven;
        if (isolateChar == BidiClass.RLI) {
            isEven = false;
        } else if (isolateChar == BidiClass.LRI) {
            isEven = true;
        } else {
            return;
        }

        if (directionalStack.Peek().directionalOverrideStatus == TextDirection.LTR) {
            isolateChar = BidiClass.L;
        } else if (directionalStack.Peek().directionalOverrideStatus == TextDirection.RTL) {
            isolateChar = BidiClass.R;
        }


        uint newEmbeddingLevel = GetLargerParityThan(directionalStack.Peek().embeddingLevel, isEven);
        if (newEmbeddingLevel <= MAX_DPETH && overflowIsolateCount == 0 && overflowEmbeddingCount == 0) {
            validIsolateCount += 1;
            directionalStack.Push(new DirectionalStatus(newEmbeddingLevel, TextDirection.NEUTRAL, true));
        } else {
            overflowIsolateCount += 1;
        }
    }

    // Trying to separate X1 - X9 rules to a different scope.
    static BidiStringData ResolveExplicit(Span<uint> inString, uint paragraphEmbeddingLevel) {
        // TODO: Make max size be MAX_SIZE (125) + 2 according to 3.3.2.
        Stack<DirectionalStatus> directionalStack = new();

        // According to X1.
        directionalStack.Push(new DirectionalStatus(paragraphEmbeddingLevel, TextDirection.NEUTRAL, false));

        // According to X1.
        uint overflowIsolateCount = 0;
        uint overflowEmbeddingCount = 0;
        uint validIsolateCount = 0;

        // Instantiate lists and not arrays because X9 character removal is merged with this passthrough,
        // so it's not possible to know the size of the string in advance. Still, we have a minor
        // optimization by knowing that the capacity should not be more than inString.Length.
        List<uint> embeddingLevels = new(inString.Length);
        List<BidiClass> bidiClasses = new(inString.Length);
        List<uint> filteredString = new(inString.Length);

        for (int currentIndex = 0; currentIndex < inString.Length; ++currentIndex) {
            uint currentChar = inString[currentIndex];
            BidiClass currentBidiClass = BidiMap.GetBidiClass(currentChar);
            uint newCurrentEmbeddedLevel = uint.MaxValue;

            Console.WriteLine($"{currentIndex} Directional stack status: {directionalStack.Peek().embeddingLevel}, {directionalStack.Peek().directionalOverrideStatus}, {directionalStack.Peek().directionalIsolateStatus}");

            switch (currentBidiClass) {
            // According to X2 - X5.
            case BidiClass.RLE:
            case BidiClass.LRE:
            case BidiClass.RLO:
            case BidiClass.LRO:
                HandleEmbedded(currentBidiClass, directionalStack, ref overflowIsolateCount, ref overflowEmbeddingCount);
                break;
            // According to X5a - X5b.
            case BidiClass.LRI:
            case BidiClass.RLI:
                HandleIsolate(ref currentBidiClass, directionalStack, ref overflowIsolateCount, ref overflowEmbeddingCount,
                                ref validIsolateCount, ref newCurrentEmbeddedLevel);
                break;
            // According to X5c.
            case BidiClass.FSI:
                // If there is no matching PDI use the span until the end of the string.
                int matchingPDIIndex = GetMatchingPDIIndex(inString, currentIndex);
                matchingPDIIndex = matchingPDIIndex == int.MaxValue ? inString.Length - 1 : matchingPDIIndex;

                Span<uint> isolatedStringSpan = inString.Slice(currentIndex + 1, matchingPDIIndex - currentIndex);
                uint nextEmbeddingLevel = GetParagraphEmbeddingLevel(isolatedStringSpan);
                if (nextEmbeddingLevel == RTL_DEFAULT_EMBEDDING_LEVEL) {
                    currentBidiClass = BidiClass.RLI;
                } else if (nextEmbeddingLevel == LTR_DEFAULT_EMBEDDING_LEVEL) {
                    currentBidiClass = BidiClass.LRI;
                } else {
                    throw new InvalidEnumArgumentException();
                }

                Console.WriteLine($"FSI Information: {currentIndex}, {GetMatchingPDIIndex(inString, currentIndex)}, Direction: {currentBidiClass}");
                HandleIsolate(ref currentBidiClass, directionalStack, ref overflowIsolateCount, ref overflowEmbeddingCount,
                            ref validIsolateCount, ref newCurrentEmbeddedLevel);
                break;
            // These values are completely ignored by the X rules except for X9, which mentions they are to be removed.
            case BidiClass.BN:
                break;
            // According to X7.
            case BidiClass.PDF:
                if (overflowIsolateCount > 0) {}
                else if (overflowEmbeddingCount > 0) overflowEmbeddingCount -= 1;
                else if (!directionalStack.Peek().directionalIsolateStatus && directionalStack.Count >= 2) {
                    directionalStack.Pop();
                }
                break;
            // According to X6a.
            case BidiClass.PDI:
                // The PDI matches an overflow isolate, so it should do nothing.
                if (overflowIsolateCount > 0) {
                    overflowIsolateCount -= 1;
                }
                // The PDI doesn't match an isolate, so it should do nothing.
                else if (validIsolateCount == 0) {}
                // Actually close an isolate.
                else {
                    // Embeds are invalidated when exiting an isolated scope (Because it's isolated).
                    overflowEmbeddingCount = 0;
                    
                    // Exit out of the first isolated scope found, and all scopes between.
                    DirectionalStatus popped;
                    do {
                        popped = directionalStack.Pop();
                    } while (!popped.directionalIsolateStatus);
                    validIsolateCount -= 1;
                }

                goto default;
            // According to X8.
            case BidiClass.B:
                newCurrentEmbeddedLevel = paragraphEmbeddingLevel;
                break;
            // According to X6c.
            default:
                newCurrentEmbeddedLevel = directionalStack.Peek().embeddingLevel;

                TextDirection directionalOverrideStatus = directionalStack.Peek().directionalOverrideStatus;
                if (directionalOverrideStatus == TextDirection.LTR) {
                    currentBidiClass = BidiClass.L;
                } else if (directionalOverrideStatus == TextDirection.RTL) {
                    currentBidiClass = BidiClass.R;
                }
                break;
            };

            if (newCurrentEmbeddedLevel != uint.MaxValue) {
                bidiClasses.Add(currentBidiClass);
                embeddingLevels.Add(newCurrentEmbeddedLevel);
                filteredString.Add(currentChar);
            }
        }

        return new BidiStringData(filteredString.ToArray(), paragraphEmbeddingLevel, embeddingLevels.ToArray(), bidiClasses.ToArray());
    }

    public static uint GetLargerParityThan(uint minNum, bool isEven) {
        if (isEven) {
            return minNum % 2 == 0 ? minNum + 2 : minNum + 1; 
        }

        return minNum % 2 == 0 ? minNum + 1 : minNum + 2;
    }

    // According to PD2.
    public static uint GetParagraphEmbeddingLevel(Span<uint> logicalString) {
        int currentCharIndex = 0;
        while (currentCharIndex < logicalString.Length) {
            BidiClass currentBidiClass = BidiMap.GetBidiClass(logicalString[currentCharIndex]);
            if (currentBidiClass == BidiClass.L) {
                return LTR_DEFAULT_EMBEDDING_LEVEL;
            } else if (currentBidiClass == BidiClass.R || currentBidiClass == BidiClass.AL) {
                return RTL_DEFAULT_EMBEDDING_LEVEL;
            } else if (currentBidiClass.IsIsolateInitiator()) {
                currentCharIndex = GetMatchingPDIIndex(logicalString, currentCharIndex);
            } else {
                currentCharIndex += 1;
            }
        }

        return LTR_DEFAULT_EMBEDDING_LEVEL;
    }

    public static uint[] ConvertString(string baseString) {
        byte[] stringBytes = Encoding.UTF32.GetBytes(baseString);
        LinkedList<uint> stringUTF32 = new();

        for (int i = 0; i < stringBytes.Length; i += sizeof(uint)) {
            stringUTF32.AddLast(BitConverter.ToUInt32(stringBytes, i));
        }

        return stringUTF32.ToArray();
    }

    public static string ConvertUInts(Span<uint> baseString) {
        LinkedList<byte> resultBytes = new();
        foreach (uint char32 in baseString) {
            foreach (byte charByte in BitConverter.GetBytes(char32))
            {
                resultBytes.AddLast(charByte);
            }
        }

        return System.Text.Encoding.UTF32.GetString(resultBytes.ToArray());
    }

    // In accordance with BD9.
    public static int GetMatchingPDIIndex(Span<uint> logicalString, int startPosition) {
        BidiClass startBidiType = BidiMap.GetBidiClass(logicalString[startPosition]);
        if (!startBidiType.IsIsolateInitiator()) {
            return int.MaxValue;
        }

        int scopeCounter = 1;
        int currentIndex;

        for (currentIndex = startPosition + 1; currentIndex < logicalString.Length; ++currentIndex) {
            BidiClass currentBidiType = BidiMap.GetBidiClass(logicalString[currentIndex]);

            if (currentBidiType.IsIsolateInitiator()) scopeCounter += 1;
            if (currentBidiType == BidiClass.PDI) scopeCounter -= 1;

            if (scopeCounter == 0) {
                return currentIndex;
            }
        }

        return int.MaxValue;
    }

    // According to BD10 & BD11.
    public static int GetMatchingPDFIndex(Span<uint> logicalString, int startPosition) {
        BidiClass startBidiType = BidiMap.GetBidiClass(logicalString[startPosition]);
        if (!startBidiType.IsEmbeddingInitiator()) {
            return int.MaxValue;
        }

        int scopeCounter = 1;
        int currentIndex = startPosition;

        while (scopeCounter > 0 && currentIndex < logicalString.Length - 1) {
            BidiClass currentBidiType = BidiMap.GetBidiClass(logicalString[currentIndex]);

            // TODO: Handle PDI characters that close an isolate initiator before the embedding initiator,
            // TODO: in accordance with BD11. Should stop the search.
            if (currentBidiType == BidiClass.PDF) scopeCounter -= 1;
            if (currentBidiType.IsEmbeddingInitiator()) scopeCounter += 1;
            if (currentBidiType.IsIsolateInitiator()) {
                currentIndex = GetMatchingPDFIndex(logicalString, currentIndex);
            }
            else {
                currentIndex += 1;
            }
        }

        return currentIndex;
    }
}
