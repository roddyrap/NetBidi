﻿using System.Runtime.CompilerServices;
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

        uint[] filterredLogicalString = ResolveExplicit(logicalString, paragraphEmbeddingLevel,
                                                      out uint[] embeddingValues, out BidiClass[] bidiClassValues);

        List<IsolatingRunSequence> isolatingRuns = GetIsolatingRunLevels(filterredLogicalString, embeddingValues, bidiClassValues, paragraphEmbeddingLevel);
        ResolveX10(isolatingRuns, bidiClassValues);
        ResolveNI(isolatingRuns, filterredLogicalString, embeddingValues, bidiClassValues);

        uint[] outputLogicalString = ReorderString(filterredLogicalString, embeddingValues, bidiClassValues, paragraphEmbeddingLevel, mirrorCharacters);
        return ConvertUInts(outputLogicalString);
    }

    public static string BidiResolveString(string logicalString) {
        return BidiResolveString(ConvertString(logicalString));
    }

    // The L rules implementation. TODO: Linebreaking size support?
    static uint[] ReorderString(uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues, uint paragraphEmbeddingLevel, bool mirrorCharacters = true) {
        for (int absoluteCharIndex = 0; absoluteCharIndex < inString.Length; ++absoluteCharIndex) {
            BidiClass bidiClassValue = bidiClassValues[absoluteCharIndex];
            if (bidiClassValue == BidiClass.S || bidiClassValue == BidiClass.B) {
                embeddingValues[absoluteCharIndex] = paragraphEmbeddingLevel;

                for (int iteratedCharIndex = 0; iteratedCharIndex >= 0; --iteratedCharIndex) {
                    BidiClass iteratedBidiClassValue = bidiClassValues[iteratedCharIndex];
                    if (iteratedBidiClassValue == BidiClass.WS || iteratedBidiClassValue.IsIsolateInitiator() || iteratedBidiClassValue == BidiClass.PDI) {
                        embeddingValues[absoluteCharIndex] = paragraphEmbeddingLevel;
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
        for (uint minReversedLevel = highestEmbeddingLevel; minReversedLevel > 0; --minReversedLevel) {
            int reverseStartIndex = int.MaxValue;
            for (int currentIndex = 0; currentIndex < newString.Length; ++currentIndex) {
                if (embeddingValues[currentIndex] >= minReversedLevel && reverseStartIndex == int.MaxValue) {
                    reverseStartIndex = currentIndex;
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
            if (BidiMap.GetBracketType(currentChar) == BracketType.OPEN) {
                // BD16 specifies that if the bracket stack is too small (need a 64th entry) then processing is immediatetly stopped.
                if (bracketStack.Count < MAX_BRACKET_PAIRS)
                    bracketStack.Add((currentAbsoluteIndex, BidiMap.GetPairedBracket(currentChar)));
                else return bracketPairs;
            } else if (BidiMap.GetBracketType(currentChar) == BracketType.CLOSE) {
                for (int bracketStackIndex = bracketStack.Count -1; bracketStackIndex >= 0; --bracketStackIndex) {
                    // TODO: Unicode weird U+3009 and U+232A equivalence.
                    if (currentChar == bracketStack[bracketStackIndex].Item2) {
                        bracketPairs.Add((bracketStack[bracketStackIndex].Item1, currentAbsoluteIndex));
                        bracketStack.RemoveRange(bracketStackIndex, bracketStack.Count - bracketStackIndex);

                        break;
                    }
                }
            }
        }

        return bracketPairs;
    }

    static BidiClass NIResolveStrongBidiClass(BidiClass bidiClass) {
        if (bidiClass == BidiClass.L ) {
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

    // NI rules.
    static void ResolveNI(List<IsolatingRunSequence> isolatingRuns, uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues) {
        foreach (IsolatingRunSequence isolatingRunSequence in isolatingRuns) {
            // N0.
            List<(int, int)> bracketPairs = GetBracketPairs(isolatingRunSequence, inString, bidiClassValues);
            foreach ((int, int) bracketIndices in bracketPairs) {
                BidiClass foundStrongBidiClass = BidiClass.ON;
                for (int currentAbsoluteIndex = bracketIndices.Item1; currentAbsoluteIndex <= bracketIndices.Item2; ++currentAbsoluteIndex) {
                    foundStrongBidiClass = NIResolveStrongBidiClass(bidiClassValues[currentAbsoluteIndex]);
                    if (foundStrongBidiClass != BidiClass.ON) break;
                }

                if ((foundStrongBidiClass == BidiClass.L && isolatingRunSequence.embdeddingLevel % 2 == LTR_DEFAULT_EMBEDDING_LEVEL) ||
                    (foundStrongBidiClass == BidiClass.R && isolatingRunSequence.embdeddingLevel % 2 == RTL_DEFAULT_EMBEDDING_LEVEL)) {
                    bidiClassValues[bracketIndices.Item1] = foundStrongBidiClass;
                    bidiClassValues[bracketIndices.Item2] = foundStrongBidiClass;
                } else if (foundStrongBidiClass != BidiClass.ON) {
                    BidiClass foundStrongBidiClassValue = isolatingRunSequence.startOfSequene;
                    for (int beforeBracketIndex = bracketIndices.Item1; beforeBracketIndex >= 0; --beforeBracketIndex) {
                        BidiClass resolvedBidiClassValue = NIResolveStrongBidiClass(bidiClassValues[beforeBracketIndex]);
                        if (resolvedBidiClassValue != BidiClass.ON) {
                            foundStrongBidiClassValue = resolvedBidiClassValue;
                            break;
                        }
                    }

                    bidiClassValues[bracketIndices.Item1] = foundStrongBidiClassValue;
                    bidiClassValues[bracketIndices.Item2] = foundStrongBidiClassValue;
                }
            }

            // Rule N1.
            int n1CurrentCharIndex = 0;
            BidiClass oldResolvedStrongBidiClassValue = isolatingRunSequence.startOfSequene;
            while (n1CurrentCharIndex < isolatingRunSequence.isolatingRunIndices.Count) {
                int absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[n1CurrentCharIndex];

                int neutralIsolatesStartIndex = n1CurrentCharIndex;
                while (bidiClassValues[absoluteCharIndex].IsNeutralOrIsolate() && n1CurrentCharIndex < isolatingRunSequence.isolatingRunIndices.Count - 1) {
                    n1CurrentCharIndex += 1;
                    absoluteCharIndex = isolatingRunSequence.isolatingRunIndices[n1CurrentCharIndex];
                }

                // We can afford to stop the while loop on the last character even if it's NI because the resolved strong type will
                // be ON, which means that even then nothing will happen, which is desired.
                BidiClass resolvedStrongBidiClassValue = NIResolveStrongBidiClass(bidiClassValues[absoluteCharIndex]);
                if (oldResolvedStrongBidiClassValue == resolvedStrongBidiClassValue && oldResolvedStrongBidiClassValue != BidiClass.ON) {
                    for (int changedNIIndex = neutralIsolatesStartIndex; changedNIIndex < n1CurrentCharIndex; ++changedNIIndex) {
                        int absoluteChangedNIIndex = isolatingRunSequence.isolatingRunIndices[changedNIIndex];
                        bidiClassValues[absoluteChangedNIIndex] = oldResolvedStrongBidiClassValue;
                    }
                }

                n1CurrentCharIndex += 1;
            }

            // Rule N2.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                if (bidiClassValues[absoluteCharIndex].IsNeutralOrIsolate()) {
                    bidiClassValues[absoluteCharIndex] = isolatingRunSequence.embdeddingLevel % 2 == LTR_DEFAULT_EMBEDDING_LEVEL ? BidiClass.L : BidiClass.R;
                }
            }

            // Rules I1 & I2.
            // Important: From now on we can't use the isolating run sequence's embedding value, because it may not represent all characters inside of it.
            foreach (int absoluteCharIndex in isolatingRunSequence.isolatingRunIndices) {
                BidiClass currentBidiClassValue = bidiClassValues[absoluteCharIndex];

                // I1.
                if (isolatingRunSequence.embdeddingLevel % 2 == LTR_DEFAULT_EMBEDDING_LEVEL) {
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
        }
    }

    // Really simple (and wasteful) algorithm, according to BD7.
    static List<ArraySegment<uint>> GetLevelRuns(uint[] inString, uint[] embeddingValues) {
        List<ArraySegment<uint>> levelRuns = new();
        if (inString.Length != embeddingValues.Length) return levelRuns;

        int currentLevelRunStartIndex = int.MaxValue;
        for (int currentIndex = 0; currentIndex < inString.Length; ++currentIndex) {
            // If starting a level run.
            if (currentIndex == 0 || embeddingValues[currentIndex] != embeddingValues[currentIndex - 1]) {
                if (currentLevelRunStartIndex != int.MaxValue) throw new Exception("Can't start a level run where one already exists");
                currentLevelRunStartIndex = currentIndex;
            }
            // If closing a level run.
            else if (currentIndex == inString.Length - 1 || embeddingValues[currentIndex] != embeddingValues[currentIndex + 1]) {
                if (currentLevelRunStartIndex == int.MaxValue) throw new Exception("Can't end a non-existant level run");

                levelRuns.Add(new ArraySegment<uint>(inString, currentLevelRunStartIndex, currentIndex - currentLevelRunStartIndex));
                currentLevelRunStartIndex = int.MaxValue;
            }
        }

        return levelRuns;
    }

    // According to BD13, using values calculated from X1-X9. TODO: Probably got a bug here lol.
    static List<IsolatingRunSequence> GetIsolatingRunLevels(uint[] inString, uint[] embeddingValues, BidiClass[] bidiClassValues, uint paragraphEmbeddingLevel) {
        List<ArraySegment<uint>> levelRuns = GetLevelRuns(inString, embeddingValues);

        // TODO: Finding PDI vailidity is dumb and really wasteful because I am doing it in the explicit resolve already.
        // TODO: I should REALLY use it instead of doing it AGAIN here.

        // Store the level run index of each valid PDI.
        Dictionary<int, int> pdiLevelRuns = new();

        // Store the index of the matching PDI to the index of every VALId isolate initiator.
        Dictionary<int, int> isolateInitiatorToPDI = new();

        for (int currentLevelRunIndex = 0; currentLevelRunIndex < levelRuns.Count; ++currentLevelRunIndex) {
            for (int currentCharIndex = 0; currentCharIndex < levelRuns[currentLevelRunIndex].Count; ++currentCharIndex) {
                int absoluteCharIndex = levelRuns[currentLevelRunIndex].Offset + currentCharIndex;

                // Dumb way to add valid PDIs. The current bidi char check happens inside the method, so I don't need to do it here.
                // TODO: Replace PDI recognition because it's dumb.
                int matchingPdiIndex = GetMatchingPDIIndex(inString, absoluteCharIndex);
                if (matchingPdiIndex != int.MaxValue) {
                    isolateInitiatorToPDI.Add(absoluteCharIndex, matchingPdiIndex);
                }
                else if (bidiClassValues[absoluteCharIndex] == BidiClass.PDI && isolateInitiatorToPDI.ContainsValue(absoluteCharIndex)) {
                    pdiLevelRuns.Add(absoluteCharIndex, currentLevelRunIndex);
                }
            }
        }

        List<IsolatingRunSequence> isolationRunSequences = new();

        foreach (ArraySegment<uint> levelRun in levelRuns) {
            int startIndex = levelRun.Offset;

            if (bidiClassValues[startIndex] != BidiClass.PDI || !isolateInitiatorToPDI.ContainsValue(startIndex)) {
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
            isEven = true;
        } else if (isolateChar == BidiClass.LRI) {
            isEven = false;
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
                ArraySegment<uint> isolatedString = new(inString, currentIndex, GetMatchingPDIIndex(inString, currentIndex));
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

                // TODO: The actual fuck???? But yeah I am pretty sure X6a wants that.
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

        for (currentIndex = startPosition; scopeCounter > 0 && currentIndex < inString.Length - 1; ++currentIndex) {
            BidiClass currentBidiType = BidiMap.GetBidiClass(inString[currentIndex]);
            if (currentBidiType.IsIsolateInitiator()) scopeCounter += 1;
            if (currentBidiType == BidiClass.PDI) scopeCounter -= 1;
        }

        return currentIndex;
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
