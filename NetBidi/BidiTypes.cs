namespace NetBidi;

public enum BracketType : byte {
    CLOSE,
    OPEN,
    NOT_BRACKET
}

public enum BidiClass : byte {
    L = 0,
    R = 1,
    AL = 2,
    EN = 3,
    ES = 4,
    ET = 5,
    AN = 6,
    CS = 7,
    NSM = 8,
    BN = 9,
    B = 10,
    S = 11,
    WS = 12,
    ON = 13,
    LRE = 14,
    LRO = 15,
    RLE = 16,
    RLO = 17,
    PDF = 18,
    LRI = 19,
    RLI = 20,
    FSI = 21,
    PDI = 22,
}

// Extension methods to the BidiClass enum for more readable code.
static class BidiClassMethods {
    public static bool IsIsolateInitiator(this BidiClass bidiClass) {
        return bidiClass == BidiClass.FSI || bidiClass == BidiClass.RLI || bidiClass == BidiClass.LRI;
    }

    public static bool IsEmbeddingInitiator(this BidiClass bidiClass) {
        return bidiClass == BidiClass.RLE || bidiClass == BidiClass.LRE ||
               bidiClass == BidiClass.RLO || bidiClass == BidiClass.LRO;
    }

    public static bool IsStrongBidiClass(this BidiClass bidiClass) {
        return bidiClass == BidiClass.R || bidiClass == BidiClass.L || bidiClass == BidiClass.AL;
    }

    public static bool IsSeparator(this BidiClass bidiClass) {
        return bidiClass == BidiClass.ES || bidiClass == BidiClass.CS || bidiClass == BidiClass.B || bidiClass == BidiClass.S;
    }

    // 3.1.4 NI symbol.
    public static bool IsNeutralOrIsolate(this BidiClass bidiClass) {
        return bidiClass == BidiClass.B || bidiClass == BidiClass.S || bidiClass == BidiClass.WS || bidiClass == BidiClass.ON ||
               bidiClass == BidiClass.FSI || bidiClass == BidiClass.LRI || bidiClass == BidiClass.RLI || bidiClass == BidiClass.PDI;
    }
}