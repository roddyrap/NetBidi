using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Text;

[assembly: InternalsVisibleTo("UniBidiTests")]

namespace UniBidi;

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

    public IsolatingRunSequence(uint[] embeddingLevels, List<ArraySegment<uint>> runLevelSequence, uint paragraphEmbeddingLevel) {
        // "Unpack" the run level sequence to a one-dimensional array of the isolating level run sequence indices.
        this.isolatingRunIndices = new();
        foreach(var runLevelArray in runLevelSequence) {
            for (int i = runLevelArray.Offset; i < runLevelArray.Offset + runLevelArray.Count; ++i) {
                this.isolatingRunIndices.Add(i);
            }
        }

        // TODO: I don't think it's needed.
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

        if (runEndIndex == embeddingLevels.Length - 1) {
            higherEmbeddingLevel = (int)Math.Max(paragraphEmbeddingLevel, embeddingLevels[runEndIndex]);
        } else {
            higherEmbeddingLevel = (int)Math.Max(embeddingLevels[runEndIndex], embeddingLevels[runEndIndex + 1]);
        }

        endOfSequence = higherEmbeddingLevel % 2 == 0 ? BidiClass.L : BidiClass.R;
    }
}

public static class UniBidi
{
    // According to BD2.
    public const uint MAX_DPETH = 125;

    // According to BD16.
    public const uint MAX_BRACKET_PAIRS = 63;

    // Not part of spec pre se, but are related to P3.
    const uint LTR_DEFAULT_EMBEDDING_LEVEL = 0;
    const uint RTL_DEFAULT_EMBEDDING_LEVEL = 1;

    public static string BidiResolveString(uint[] logicalString, TextDirection givenParagraphEmbedding = TextDirection.NEUTRAL, bool mirrorCharacters = true) {
        // TODO: Fulfill P1 - Split by paragraph for the algorithm without discarding the paragraph breaks.
        // TODO: Also implement X8 according to how paragraphs are implemented.

        uint paragraphEmbeddingLevel = givenParagraphEmbedding switch {
            TextDirection.LTR => LTR_DEFAULT_EMBEDDING_LEVEL,
            TextDirection.RTL => RTL_DEFAULT_EMBEDDING_LEVEL,
            TextDirection.NEUTRAL => GetParagraphEmbeddingLevel(logicalString)
        };

        Console.WriteLine($"Paragraph embedding level: {paragraphEmbeddingLevel}");

        uint[] filterredLogicalString = ResolveExplicit(logicalString, paragraphEmbeddingLevel,
                                                      out uint[] embeddingValues, out BidiClass[] bidiClassValues);

        Console.WriteLine($"Isolating runs class values: {string.Join(", ", bidiClassValues)}");
        Console.WriteLine($"Isolating runs embedding values: {string.Join(", ", embeddingValues)}");

        List<IsolatingRunSequence> isolatingRuns = GetIsolatingRunLevels(filterredLogicalString, embeddingValues, bidiClassValues, paragraphEmbeddingLevel);
        ResolveX10(isolatingRuns, bidiClassValues);

        Console.WriteLine($"Intermediate class values: {string.Join(", ", bidiClassValues)}");

        ResolveNX(isolatingRuns, filterredLogicalString, embeddingValues, bidiClassValues);

        uint[] outputLogicalString = ReorderString(filterredLogicalString, embeddingValues, bidiClassValues, paragraphEmbeddingLevel, mirrorCharacters);
        return ConvertUInts(outputLogicalString);
    }

    public static string BidiResolveString(string logicalString) {
        return BidiResolveString(ConvertString(logicalString));
    }

    // The L rules implementation. TODO: Linebreaking size support?
    static uint[] ReorderString(uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues, uint paragraphEmbeddingLevel, bool mirrorCharacters = true) {
        for (int absoluteCharIndex = 0; absoluteCharIndex < inString.Length; ++absoluteCharIndex) {
            BidiClass bidiClassValue = BidiMap.GetBidiClass(inString[absoluteCharIndex]);
            if (bidiClassValue == BidiClass.S || bidiClassValue == BidiClass.B) {
                embeddingValues[absoluteCharIndex] = paragraphEmbeddingLevel;

                for (int iteratedCharIndex = absoluteCharIndex - 1; iteratedCharIndex >= 0; --iteratedCharIndex) {
                    BidiClass iteratedBidiClassValue = bidiClassValues[iteratedCharIndex];
                    if (iteratedBidiClassValue == BidiClass.WS || iteratedBidiClassValue.IsIsolateInitiator() || iteratedBidiClassValue == BidiClass.PDI) {
                        embeddingValues[iteratedCharIndex] = paragraphEmbeddingLevel;
                    } else {
                        break;
                    }
                }
            }
        }

        // L1 section 4.
        for (int absoluteCharIndex = inString.Length - 1; absoluteCharIndex >= 0; --absoluteCharIndex) {
            BidiClass iteratedBidiClassValue = bidiClassValues[absoluteCharIndex];
            if (iteratedBidiClassValue == BidiClass.WS || iteratedBidiClassValue.IsIsolateInitiator() || iteratedBidiClassValue == BidiClass.PDI) {
                embeddingValues[absoluteCharIndex] = paragraphEmbeddingLevel;
            } else {
                break;
            }
        }

        uint[] newString = new uint[inString.Length];
        Array.Copy(inString, newString, newString.Length);

        uint highestEmbeddingLevel = embeddingValues.Max();
        // uint lowestOddEmbeddingLevel = embeddingValues.Where(level => level % 2 == 1).Min();
        for (uint minReversedLevel = highestEmbeddingLevel; minReversedLevel > 0; --minReversedLevel) {
            int reverseStartIndex = int.MaxValue;
            for (int currentIndex = 0; currentIndex < newString.Length; ++currentIndex) {
                if (embeddingValues[currentIndex] >= minReversedLevel) {
                    if (reverseStartIndex == int.MaxValue) {
                        reverseStartIndex = currentIndex;
                    }
                    // Handle the scenario in which the last character in the string should be reversed.
                    else if (currentIndex == newString.Length - 1) {
                        Array.Reverse(newString, reverseStartIndex, currentIndex - reverseStartIndex + 1);
                        reverseStartIndex = int.MaxValue;
                    }
                }
                else if (embeddingValues[currentIndex] < minReversedLevel && reverseStartIndex != int.MaxValue) {
                    Array.Reverse(newString, reverseStartIndex, currentIndex - reverseStartIndex);
                    reverseStartIndex = int.MaxValue;
                }
            }
        }

        // TODO: L3 implementation.

        // L4.
        if (mirrorCharacters) {
            for (int absoluteCharIndex = 0; absoluteCharIndex < newString.Length; ++absoluteCharIndex) {
                if (embeddingValues[absoluteCharIndex] % 2 == RTL_DEFAULT_EMBEDDING_LEVEL) {
                    newString[absoluteCharIndex] = BidiMap.GetMirror(newString[absoluteCharIndex]);
                }
            }
        }

        Console.WriteLine($"Input: {string.Join(", ", inString.Select(x => x.ToString("X4")))}");
        Console.WriteLine($"Output: {string.Join(", ", newString.Select(x => x.ToString("X4")))}");
        Console.WriteLine($"Embedding Values: {string.Join(", ", embeddingValues)}");
        return newString;
    }

    // According to BD16.
    static List<(int, int)> GetBracketPairs(IsolatingRunSequence isolatingRunSequence, uint[] inString, BidiClass[] bidiClassValues) {
        // Holds the the index of the opening bracket and the bracket that is paired to it, at that order.
        List<(int, uint)> bracketStack = new();

        List<(int, int)> bracketPairs = new();

        foreach (int currentAbsoluteIndex in isolatingRunSequence.isolatingRunIndices) {
            // BD 14 & 15 specify that bracket pair matching should only be applied to Other Neutral bidi class characters.
            if (bidiClassValues[currentAbsoluteIndex] != BidiClass.ON) continue;

            uint currentChar = inString[currentAbsoluteIndex];
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
    static void ResolveNX(List<IsolatingRunSequence> isolatingRuns, uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues) {
        foreach (IsolatingRunSequence isolatingRunSequence in isolatingRuns) {
            // Rule N0.
            List<(int, int)> bracketPairs = GetBracketPairs(isolatingRunSequence, inString, bidiClassValues);

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
                    BidiClass resolvedClass = NXResolveStrongBidiClass(bidiClassValues[absoluteIndexCharIndex]);

                    if (resolvedClass == embeddingBidiClass) {
                        foundEmbeddingBidiValue = true;
                    } else if (resolvedClass != BidiClass.ON) {
                        foundInvertedBidiValue = true;
                    }
                }

                if (foundEmbeddingBidiValue) {
                    bidiClassValues[bracketIndices.Item1] = embeddingBidiClass;
                    bidiClassValues[bracketIndices.Item2] = embeddingBidiClass;
                // According to N0 section c.
                } else if (foundInvertedBidiValue) {
                    BidiClass preceedingStrongBidiClass = isolatingRunSequence.startOfSequene;
                    for (int beforeBracketIndex = bracketIndices.Item1 - 1; beforeBracketIndex >= 0; --beforeBracketIndex) {
                        BidiClass resolvedBidiClassValue = NXResolveStrongBidiClass(bidiClassValues[beforeBracketIndex]);
                        if (resolvedBidiClassValue != BidiClass.ON) {
                            preceedingStrongBidiClass = resolvedBidiClassValue;
                            break;
                        }
                    }

                    BidiClass internalBidiClass = embeddingBidiClass;
                    if (preceedingStrongBidiClass != embeddingBidiClass) {
                        internalBidiClass = embeddingBidiClass == BidiClass.L? BidiClass.R : BidiClass.L;
                    }

                    bidiClassValues[bracketIndices.Item1] = internalBidiClass;
                    bidiClassValues[bracketIndices.Item2] = internalBidiClass;
                }

                // TODO: Isolating run sequence considerations...
                // As mentioned in N0:
                // "Any number of characters that had original bidirectional character type NSM prior to the application of
                // W1 that immediately follow a paired bracket which changed to L or R under N0 should change to match the
                // type of their preceding bracket."
                if (bidiClassValues[bracketIndices.Item2].IsStrongBidiClass()) {
                    for (int followingBracketIndex = bracketIndices.Item2; followingBracketIndex < inString.Length; ++followingBracketIndex) {
                        if (BidiMap.GetBidiClass(inString[followingBracketIndex]) == BidiClass.NSM) {
                            bidiClassValues[followingBracketIndex] = bidiClassValues[bracketIndices.Item2];
                        }
                    }

                    for (int followingBracketIndex = bracketIndices.Item1; followingBracketIndex < bracketIndices.Item2; ++followingBracketIndex) {
                        if (BidiMap.GetBidiClass(inString[followingBracketIndex]) == BidiClass.NSM) {
                            bidiClassValues[followingBracketIndex] = bidiClassValues[bracketIndices.Item1];
                        }
                    }
                }
            }

            Console.WriteLine($"After N0 class values: {string.Join(", ", bidiClassValues)}");

            // Rule N1.
            BidiClass startBidiClass = NXResolveStrongBidiClass(isolatingRunSequence.startOfSequene);
            int niStartIndex = int.MaxValue;
            for (int relativeCharIndex = 0; relativeCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++relativeCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[relativeCharIndex];
                BidiClass resolvedBidiClass = NXResolveStrongBidiClass(bidiClassValues[absoluteCharIndex]);

                // TODO: Consider the EOS bidi value at the end of the isolating run sequence.

                if (resolvedBidiClass.IsStrongBidiClass()) {
                    if (niStartIndex != int.MaxValue && startBidiClass == resolvedBidiClass) {
                        for (int iteratedCharIndex = niStartIndex; iteratedCharIndex < relativeCharIndex; ++iteratedCharIndex) {
                            bidiClassValues[isolatingRunSequence.isolatingRunIndices[iteratedCharIndex]] = resolvedBidiClass;
                        }
                    }

                    startBidiClass = resolvedBidiClass;
                    niStartIndex = int.MaxValue;
                }
                else if (resolvedBidiClass.IsNeutralOrIsolate()) {
                    if (niStartIndex == int.MaxValue) {
                        niStartIndex = relativeCharIndex;
                    }
                }
                else {
                    niStartIndex = int.MaxValue;
                }
            }

            Console.WriteLine($"After N1 class values: {string.Join(", ", bidiClassValues)}");

            // Rule N2.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                if (bidiClassValues[absoluteCharIndex].IsNeutralOrIsolate()) {
                    bidiClassValues[absoluteCharIndex] = embeddingValues[absoluteCharIndex] % 2 == LTR_DEFAULT_EMBEDDING_LEVEL ? BidiClass.L : BidiClass.R;
                }
            }

            Console.WriteLine($"After N2 class values: {string.Join(", ", bidiClassValues)}");

            // Rules I1 & I2.
            // Important: From now on we can't use the isolating run sequence's embedding value, because it may not represent all characters inside of it.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                BidiClass currentBidiClassValue = bidiClassValues[absoluteCharIndex];
                uint currentEmbeddingValue = embeddingValues[absoluteCharIndex];

                // I1.
                if (currentEmbeddingValue % 2 == LTR_DEFAULT_EMBEDDING_LEVEL) {
                    if (currentBidiClassValue == BidiClass.R) {
                        embeddingValues[absoluteCharIndex] += 1;
                    } else if (currentBidiClassValue == BidiClass.AN || currentBidiClassValue == BidiClass.EN) {
                        embeddingValues[absoluteCharIndex] += 2;
                    }
                }
                // I2.
                else {
                    if (currentBidiClassValue == BidiClass.L || currentBidiClassValue == BidiClass.EN || currentBidiClassValue == BidiClass.AN) {
                        embeddingValues[absoluteCharIndex] += 1;
                    }
                }
            }

            Console.WriteLine($"After I1/2 class values: {string.Join(", ", bidiClassValues)}");
        }
    }

    // Really simple (and wasteful) algorithm, according to BD7.
    static List<ArraySegment<uint>> GetLevelRuns(uint[] inString, uint[] embeddingValues) {
        List<ArraySegment<uint>> levelRuns = new();
        if (inString.Length != embeddingValues.Length) return levelRuns;

        int currentLevelRunStartIndex = 0;
        uint currentLevelRunEmbeddingValue = embeddingValues[0];

        for (int currentIndex = 1; currentIndex < inString.Length; ++currentIndex) {
            uint currentEmbeddingValue = embeddingValues[currentIndex];

            if (currentEmbeddingValue != currentLevelRunEmbeddingValue) {
                levelRuns.Add(new ArraySegment<uint>(inString, currentLevelRunStartIndex, currentIndex - currentLevelRunStartIndex));

                currentLevelRunStartIndex = currentIndex;
                currentLevelRunEmbeddingValue = currentEmbeddingValue;
            }
            // Handle the last character of the string in a level run.
            else if (currentIndex == inString.Length -1) {
                levelRuns.Add(new ArraySegment<uint>(inString, currentLevelRunStartIndex, currentIndex - currentLevelRunStartIndex + 1));
            }
        }

        return levelRuns;
    }

    // According to BD13, using values calculated from X1-X9. TODO: Probably got a bug here lol.
    static List<IsolatingRunSequence> GetIsolatingRunLevels(uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues, uint paragraphEmbeddingLevel) {
        List<ArraySegment<uint>> levelRuns = GetLevelRuns(inString, embeddingValues);

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

                Console.WriteLine($"Level Run char index: {absoluteCharIndex} {bidiClassValues[absoluteCharIndex]} {GetMatchingPDIIndex(inString, absoluteCharIndex)} {currentCharIndex}/{currentLevelRunIndex}");

                // Dumb way to add valid PDIs. The current bidi char check happens inside the method, so I don't need to do it here.
                // TODO: Replace PDI recognition because it's dumb.
                int matchingPdiIndex = GetMatchingPDIIndex(inString, absoluteCharIndex);
                if (matchingPdiIndex != int.MaxValue) {
                    isolateInitiatorToPDI.Add(absoluteCharIndex, matchingPdiIndex);
                }
                // TODO: I use BidiMap.GetBidiClass directly because PDIs get overwritten in bidiClassValues.
                // TODO: I am not sure if this is correct. If I need to change this then change it below too.
                else if (BidiMap.GetBidiClass(inString[absoluteCharIndex]) == BidiClass.PDI && isolateInitiatorToPDI.ContainsValue(absoluteCharIndex)) {
                    pdiLevelRuns.Add(absoluteCharIndex, currentLevelRunIndex);
                }
            }
        }

        Console.WriteLine($"Isolate Initiators to PDI: {string.Join(", ", isolateInitiatorToPDI)}");
        Console.WriteLine($"PDI Level runs: {string.Join(", ", pdiLevelRuns)}");

        List<IsolatingRunSequence> isolationRunSequences = new();

        foreach (ArraySegment<uint> levelRun in levelRuns) {
            int startIndex = levelRun.Offset;

            if (BidiMap.GetBidiClass(inString[startIndex]) != BidiClass.PDI || !pdiLevelRuns.ContainsValue(startIndex)) {
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

                isolationRunSequences.Add(new IsolatingRunSequence(embeddingValues, currentIsolationRun, paragraphEmbeddingLevel));
            }
        }

        return isolationRunSequences;
    }

    // X10 seems compilcated, I better do it in its own scope.
    static void ResolveX10(List<IsolatingRunSequence> isolatingRuns, BidiClass[] bidiClassValues) {
        // The specification explicitly mentions that each W rule must be fully applied on the sequence before the next rule is
        // evaluated. This means that we need to loop separately for each rule.

        // TODO: Generally The N and I rules are also part of X10. Putting them in the method is counter-intuitive, so separate
        // TODO: X10's methods to apply W, N and I rules and call them from here or from the original method.

        // Rule W1.
        foreach (var isolatingRunSequence in isolatingRuns) {
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];

                if (bidiClassValues[absoluteCharIndex] == BidiClass.NSM) {
                    if (currentCharIndex == 0) {
                        bidiClassValues[absoluteCharIndex] = isolatingRunSequence.startOfSequene;
                    } else {
                        int previousCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex - 1];
                        BidiClass previousCharType = bidiClassValues[previousCharAbsoluteIndex];
                        if (previousCharType.IsIsolateInitiator() || previousCharType == BidiClass.PDI) {
                            bidiClassValues[absoluteCharIndex] = BidiClass.ON;
                        } else {
                            bidiClassValues[absoluteCharIndex] = previousCharType;
                        }
                    }
                }
            }
        }

        // Rule W2.
        foreach (var isolatingRunSequence in isolatingRuns) {
            BidiClass lastStrongBidiClassValue = isolatingRunSequence.startOfSequene;
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                BidiClass currentBidiClassValue = bidiClassValues[absoluteCharIndex];

                if (currentBidiClassValue.IsStrongBidiClass()) {
                    lastStrongBidiClassValue = currentBidiClassValue;
                }
                else if (currentBidiClassValue == BidiClass.EN) {
                    if (lastStrongBidiClassValue == BidiClass.AL) {
                        bidiClassValues[absoluteCharIndex] = BidiClass.AN;
                    }
                }
            }
        }

        // Rule W3.
        foreach (var isolatingRunSequence in isolatingRuns) {
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                if (bidiClassValues[absoluteCharIndex] == BidiClass.AL) {
                    bidiClassValues[absoluteCharIndex] = BidiClass.R;
                }
            }
        }

        // Rule W4.
        foreach (var isolatingRunSequence in isolatingRuns) {
            for (int currentCharIndex = 1; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count - 1; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                BidiClass currentCharType = bidiClassValues[absoluteCharIndex];

                if (currentCharType != BidiClass.ES && currentCharType != BidiClass.CS) continue;

                int previousCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex - 1];
                int nextCharAbsoluteIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex + 1];

                BidiClass previousCharType = bidiClassValues[previousCharAbsoluteIndex];
                BidiClass nextCharType = bidiClassValues[nextCharAbsoluteIndex];

                if (previousCharType != nextCharType) continue;

                if (nextCharType == BidiClass.EN) {
                    bidiClassValues[absoluteCharIndex] = BidiClass.EN;
                } else if (currentCharType == BidiClass.CS && nextCharType == BidiClass.AN) {
                    bidiClassValues[absoluteCharIndex] = BidiClass.AN;
                }
            }
        }

        // Rule W5.
        foreach (var isolatingRunSequence in isolatingRuns) {
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                if (bidiClassValues[absoluteCharIndex] != BidiClass.EN) continue;

                for (int iteratedCharIndex = currentCharIndex; iteratedCharIndex >= 0; --iteratedCharIndex) {
                    int absoluteIteratedCharIndex = isolatingRunSequence.isolatingRunIndices[iteratedCharIndex];
                    if (bidiClassValues[absoluteIteratedCharIndex] == BidiClass.ET) {
                        bidiClassValues[absoluteIteratedCharIndex] = BidiClass.EN;
                    } else break;
                }
                for (int iteratedCharIndex = currentCharIndex; iteratedCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++iteratedCharIndex) {
                    int absoluteIteratedCharIndex = isolatingRunSequence.isolatingRunIndices[iteratedCharIndex];
                    if (bidiClassValues[absoluteIteratedCharIndex] == BidiClass.ET) {
                        bidiClassValues[absoluteIteratedCharIndex] = BidiClass.EN;
                    } else break;
                }
            }
        }

        // Rule W6.
        foreach (var isolatingRunSequence in isolatingRuns) {
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                if (bidiClassValues[absoluteCharIndex].IsSeparator() || bidiClassValues[absoluteCharIndex] == BidiClass.ET) {
                    bidiClassValues[absoluteCharIndex] = BidiClass.ON;
                }
            }
        }

        // Rule W7.
        foreach (var isolatingRunSequence in isolatingRuns) {
            BidiClass lastStrongBidiClassValue = isolatingRunSequence.startOfSequene;
            for (int currentCharIndex = 0; currentCharIndex < isolatingRunSequence.isolatingRunIndices.Count; ++currentCharIndex) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[currentCharIndex];
                BidiClass currentBidiClassValue = bidiClassValues[absoluteCharIndex];

                if (currentBidiClassValue.IsStrongBidiClass()) {
                    lastStrongBidiClassValue = currentBidiClassValue;
                }
                else if (currentBidiClassValue == BidiClass.EN && lastStrongBidiClassValue == BidiClass.L) {
                    bidiClassValues[absoluteCharIndex] = BidiClass.L;
                }
            }
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
    static uint[] ResolveExplicit(uint[] inString, uint paragraphEmbeddingLevel, out uint[] embeddingValues, out BidiClass[] bidiClassValues) {
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
        List<uint> embeddingValuesList = new(inString.Length);
        List<BidiClass> bidiClassValuesList = new(inString.Length);
        List<uint> filteredStringList = new(inString.Length);

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
                // TODO: Really inefficient, need to provide better enumerable support so that recreating the array will not be needed.
                // TODO: Also handle the case in which the FSI is invalid (GetMatchingPDIIndex is MaxValue).
                Console.WriteLine($"FSI Information: {currentIndex}, {GetMatchingPDIIndex(inString, currentIndex)}");
                ArraySegment<uint> isolatedString = new(inString, currentIndex, GetMatchingPDIIndex(inString, currentIndex) - currentIndex + 1);
                uint nextEmbeddingLevel = GetParagraphEmbeddingLevel(isolatedString.ToArray());
                if (nextEmbeddingLevel == RTL_DEFAULT_EMBEDDING_LEVEL) {
                    currentBidiClass = BidiClass.RLI;
                } else if (nextEmbeddingLevel == LTR_DEFAULT_EMBEDDING_LEVEL) {
                    currentBidiClass = BidiClass.LRI;
                } else {
                    throw new InvalidEnumArgumentException();
                }

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
            // According to X8. TODO: Paragraph support is not complete now.
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
                bidiClassValuesList.Add(currentBidiClass);
                embeddingValuesList.Add(newCurrentEmbeddedLevel);
                filteredStringList.Add(currentChar);
            }
        }

        embeddingValues = embeddingValuesList.ToArray();
        bidiClassValues = bidiClassValuesList.ToArray();

        return filteredStringList.ToArray();
    }

    public static uint GetLargerParityThan(uint minNum, bool isEven) {
        if (isEven) {
            return minNum % 2 == 0 ? minNum + 2 : minNum + 1; 
        }

        return minNum % 2 == 0 ? minNum + 1 : minNum + 2;
    }

    // According to PD2.
    public static uint GetParagraphEmbeddingLevel(uint[] inString) {
        int currentCharIndex = 0;
        while (currentCharIndex < inString.Length) {
            BidiClass currentBidiClass = BidiMap.GetBidiClass(inString[currentCharIndex]);
            if (currentBidiClass == BidiClass.L) {
                return LTR_DEFAULT_EMBEDDING_LEVEL;
            } else if (currentBidiClass == BidiClass.R || currentBidiClass == BidiClass.AL) {
                return RTL_DEFAULT_EMBEDDING_LEVEL;
            } else if (currentBidiClass.IsIsolateInitiator()) {
                currentCharIndex = GetMatchingPDIIndex(inString, currentCharIndex);
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

    public static string ConvertUInts(uint[] baseString) {
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
    public static int GetMatchingPDIIndex(uint[] inString, int startPosition) {
        BidiClass startBidiType = BidiMap.GetBidiClass(inString[startPosition]);
        if (!startBidiType.IsIsolateInitiator()) {
            return int.MaxValue;
        }

        int scopeCounter = 1;
        int currentIndex;

        for (currentIndex = startPosition + 1; currentIndex < inString.Length; ++currentIndex) {
            BidiClass currentBidiType = BidiMap.GetBidiClass(inString[currentIndex]);

            if (currentBidiType.IsIsolateInitiator()) scopeCounter += 1;
            if (currentBidiType == BidiClass.PDI) scopeCounter -= 1;

            if (scopeCounter == 0) {
                return currentIndex;
            }
        }

        return int.MaxValue;
    }

    // According to BD10 & BD11.
    public static int GetMatchingPDFIndex(uint[] inString, int startPosition) {
        BidiClass startBidiType = BidiMap.GetBidiClass(inString[startPosition]);
        if (!startBidiType.IsEmbeddingInitiator()) {
            return int.MaxValue;
        }

        int scopeCounter = 1;
        int currentIndex = startPosition;

        while (scopeCounter > 0 && currentIndex < inString.Length - 1) {
            BidiClass currentBidiType = BidiMap.GetBidiClass(inString[currentIndex]);

            // TODO: Handle PDI characters that close an isolate initiator before the embedding initiator, 
            // TODO: in accordance with BD11. Should stop the search.
            if (currentBidiType == BidiClass.PDF) scopeCounter -= 1;
            if (currentBidiType.IsEmbeddingInitiator()) scopeCounter += 1;
            if (currentBidiType.IsIsolateInitiator()) {
                currentIndex = GetMatchingPDFIndex(inString, currentIndex);
            }
            else {
                currentIndex += 1;
            }
        }

        return currentIndex;
    }
}
