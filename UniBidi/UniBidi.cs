using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Text;

[assembly: InternalsVisibleTo("UniBidiTests")]

namespace UniBidi;

enum DirectionalOverrideStatus {
    LTR,
    RTL,
    NEUTRAL
}

// According to the 3.3.2 specification of applying the X rules.
class DirectionalStatus(uint embeddingLevel, DirectionalOverrideStatus directionalOverrideStatus, bool directionalIsolateStatus)
{
    public uint embeddingLevel = embeddingLevel;
    public DirectionalOverrideStatus directionalOverrideStatus = directionalOverrideStatus;
    public bool directionalIsolateStatus = directionalIsolateStatus;
}

public static class UniBidi
{
    // According to BD2.
    public const uint MAX_DPETH = 125;

    // Not part of spec pre se, but are related to P3.
    const uint LTR_DEFAULT_EMBEDDING_LEVEL = 0;
    const uint RTL_DEFAULT_EMBEDDING_LEVEL = 1;

    public static string ReorderString(string visualString) {
        // TODO: Fulfill P1 - Split by paragraph for the algorithm without discarding the paragraph breaks.
        // TODO: Also implement X8 according to how paragraphs are implemented.
        uint[] utf32String = ConvertString(visualString);

        ResolveExplicit(utf32String, GetParagraphEmbeddingLevel(utf32String), out uint[] embeddingValues,
                        out BidiClass[] bidiClassValues);

        // According to X9.
        LinkedList<uint> filterredUTF32StringList = new();
        foreach (uint currentChar in utf32String) {
            BidiClass bidiClass = BidiMap.GetBidiClass(currentChar);
            if (bidiClass == BidiClass.RLE || bidiClass == BidiClass.RLE || bidiClass == BidiClass.RLE || 
                bidiClass == BidiClass.RLE || bidiClass == BidiClass.RLE || bidiClass == BidiClass.RLE) {
                continue;
            }

            filterredUTF32StringList.AddLast(currentChar);
        }

        uint[] filterredUTF32String = filterredUTF32StringList.ToArray();

        throw new NotImplementedException();
    }

    static void HandleEmbedded(BidiClass embeddedChar, Stack<DirectionalStatus> directionalStack, ref uint overflowIsolateCount, ref uint overflowEmbeddingCount) {
        bool isEven;
        DirectionalOverrideStatus newDirectionalOverride;

        if (embeddedChar == BidiClass.RLE) {
            isEven = false;
            newDirectionalOverride = DirectionalOverrideStatus.NEUTRAL;
        } else if (embeddedChar == BidiClass.LRE) {
            isEven = true;
            newDirectionalOverride = DirectionalOverrideStatus.NEUTRAL;
        } else if (embeddedChar == BidiClass.RLO) {
            isEven = false;
            newDirectionalOverride = DirectionalOverrideStatus.RTL;

        } else if (embeddedChar == BidiClass.LRO) {
            isEven = true;
            newDirectionalOverride = DirectionalOverrideStatus.LTR;
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

        if (directionalStack.Peek().directionalOverrideStatus == DirectionalOverrideStatus.LTR) {
            isolateChar = BidiClass.L;
        } else if (directionalStack.Peek().directionalOverrideStatus == DirectionalOverrideStatus.RTL) {
            isolateChar = BidiClass.R;
        }


        uint newEmbeddingLevel = GetLargerParityThan(directionalStack.Peek().embeddingLevel, isEven);
        if (newEmbeddingLevel <= MAX_DPETH && overflowIsolateCount == 0 && overflowEmbeddingCount == 0) {
            validIsolateCount += 1;
            directionalStack.Push(new DirectionalStatus(newEmbeddingLevel, DirectionalOverrideStatus.NEUTRAL, true));
        } else {
            overflowIsolateCount += 1;
        }
    }

    // Trying to separate X1 - X9 rules to a different scope.
    static void ResolveExplicit(uint[] inString, uint paragraphEmbeddingLevel, out uint[] embeddingValues, out BidiClass[] bidiClassValues) {
        // TODO: Make max size be MAX_SIZE (125) + 2 according to 3.3.2.
        Stack<DirectionalStatus> directionalStack = new();

        // According to X1.
        directionalStack.Push(new DirectionalStatus(paragraphEmbeddingLevel, DirectionalOverrideStatus.NEUTRAL, false));

        // According to X1.
        uint overflowIsolateCount = 0;
        uint overflowEmbeddingCount = 0;
        uint validIsolateCount = 0;

        embeddingValues = new uint[inString.Length];
        bidiClassValues = new BidiClass[inString.Length];

        for (int currentIndex = 0; currentIndex < inString.Length; ++currentIndex) {
            uint currentChar = inString[currentIndex];
            BidiClass currentBidiClass  = BidiMap.GetBidiClass(currentChar);
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
            // TODO: What do I do with those? Interesting question.
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

                DirectionalOverrideStatus directionalOverrideStatus = directionalStack.Peek().directionalOverrideStatus;
                if (directionalOverrideStatus == DirectionalOverrideStatus.LTR) {
                    currentBidiClass = BidiClass.L;
                } else if (directionalOverrideStatus == DirectionalOverrideStatus.RTL) {
                    currentBidiClass = BidiClass.R;
                }
                break;
            };

            // TODO: I still don't have enough information about this, unfortunately...
            if (newCurrentEmbeddedLevel == uint.MaxValue) {
                throw new Exception("Invalid embedding level");
            }

            // TODO: I am not SURE I need to this in every occasion...
            bidiClassValues[currentIndex] = currentBidiClass;
            embeddingValues[currentIndex] = newCurrentEmbeddedLevel;
        }
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

    // According to BD13.
    public static void GetIsolatingRunSequences(uint[] inString) {
        List<(int, int)> isolatingRunSequences = new();
        throw new NotImplementedException();
    }
}
