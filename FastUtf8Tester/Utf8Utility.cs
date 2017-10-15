using System;

// TODO: Change all of the devdoc to use active tense instead of past tense.

namespace FastUtf8Tester
{
    /// <summary>
    /// Contains utility methods for inspecting UTF-8 sequences.
    /// </summary>
    /// <remarks>
    /// The UTF-8 specification can be found in the Unicode Standard, Sec. 3.9.
    /// http://www.unicode.org/versions/Unicode10.0.0/ch03.pdf
    /// </remarks>
    public static class Utf8Utility
    {
        private static readonly byte[] _replacementCharAsUtf8 = new byte[] { 0xEF, 0xBF, 0xBD };

        /// <summary>
        /// The Unicode Replacement Character (U+FFFD) as the three-byte UTF-8 sequence [ EF BF BD ].
        /// </summary>
        public static ReadOnlySpan<byte> ReplacementCharacterByteSequence => _replacementCharAsUtf8;

        /// <summary>
        /// If <paramref name="data"/> is a well-formed UTF-8 string and <paramref name="suppressStringCreationOnValidInput"/> is <see langword="false"/>, returns <paramref name="data"/> as a byte array.
        /// If <paramref name="data"/> is a well-formed UTF-8 string and <paramref name="suppressStringCreationOnValidInput"/> is <see langword="true"/>, returns <see langword="null"/>.
        /// If <paramref name="data"/> is not a well-formed UTF-8 string, returns a byte array which represents the well-formed UTF-8 string
        /// resulting from replacing all invalid sequences in the input data with the Unicode Replacement Character (U+FFFD).
        /// </summary>
        public static byte[] ConvertToWellFormedUtf8StringWithInvalidSequenceReplacement(ReadOnlySpan<byte> data, bool suppressStringCreationOnValidInput) => throw new NotImplementedException();

        /// <summary>
        /// If <paramref name="inputBuffer"/> is a well-formed UTF-8 string, copies <paramref name="inputBuffer"/> to <paramref name="outputBuffer"/> unmodified.
        /// If <paramref name="inputBuffer"/> is not a well-formed UTF-8 string, copies <paramref name="inputBuffer"/> to <paramref name="outputBuffer"/>
        /// and replaces all invalid UTF-8 sequences with the Unicode Replacement Character (U+FFFD) during copy.
        /// </summary>
        /// <returns>The number of bytes written to <paramref name="outputBuffer"/>.</returns>
        /// <remarks>
        /// The caller must allocate an output buffer large enough to hold the resulting UTF-8 string.
        /// This length can be determined by calling the <see cref="GetCountOfTotalBytesAfterInvalidSequenceReplacement(ReadOnlySpan{byte})"/> method.
        /// </remarks>
        public static int ConvertToWellFormedUtf8StringWithInvalidSequenceReplacement(ReadOnlySpan<byte> inputBuffer, Span<byte> outputBuffer) => throw new NotImplementedException();

        /// <summary>
        /// If <paramref name="data"/> is an ill-formed UTF-8 string, returns the number of bytes required to hold the resulting
        /// string where each invalid sequence in the input data has been replaced with the Unicode Replacement Character (U+FFFD).
        /// If <paramref name="data"/> is a well-formed UTF-8 string, returns the number of bytes in the input string.
        /// </summary>
        /// <remarks>
        /// The caller should not assume that the input data represents a well-formed UTF-8 string if the return value happens to
        /// match the number of bytes already present in the input data. The reason for this is that the well-formed UTF-8 string
        /// that results from replacement of invalid sequences with the Unicode Replacement Character (U+FFFD) may coincidentally
        /// have the same length as the input data, even if the contents are different. Use the <see cref="IsWellFormedUtf8String(ReadOnlySpan{byte})"/>
        /// method to determine if the input data is well-formed.
        /// </remarks>
        public static int GetCountOfTotalBytesAfterInvalidSequenceReplacement(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>
        /// Given the first byte of a sequence, returns the expected number of continuation bytes
        /// which should follow this byte.
        /// </summary>
        /// <remarks>
        /// Returns 0 if <paramref name="firstByte"/> does not have any expected continuation bytes
        /// or if <paramref name="firstByte"/> cannot begin a well-formed UTF-8 sequence.
        /// </remarks>
        public static int GetExpectedNumberOfContinuationBytes(byte firstByte)
        {
            if (firstByte < (byte)0xC2)
            {
                // ASII (one-byte sequence), or
                // continuation byte (invalid as first byte, hence no expected followers), or
                // [ C0..C1 ] (always invalid UTF-8 byte, hence no expected followers)
                return 0;
            }
            else if (firstByte < (byte)0xE0)
            {
                // [ C2..DF ] can start a two-byte sequence
                return 1;
            }
            else if (firstByte < (byte)0xF0)
            {
                // [ E0..EF ] can start a three-byte sequence
                return 2;
            }
            else if (firstByte <= (byte)0xF4)
            {
                // [ F0..F4 ] can start a four-byte sequence
                return 3;
            }
            else
            {
                // [ F5 .. FF ] (always invalid UTF-8 byte, hence no expected followers)
                return 0;
            }
        }

        /// <summary>
        /// Returns the index of the first byte of the first invalid UTF-8 sequence in <paramref name="data"/>,
        /// or -1 if <paramref name="data"/> is a well-formed UTF-8 string.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static int GetIndexOfFirstInvalidUtf8Sequence(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>
        /// Returns the length of the ill-formed UTF-8 sequence at the beginning of an input buffer.
        /// </summary>
        /// <remarks>
        /// Returns 0 if <paramref name="data"/> begins with a well-formed UTF-8 sequence or is empty.
        /// </remarks>
        public static int GetLengthOfInvalidSequence(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>
        /// Return <see langword="true"/> iff <paramref name="value"/> is an ASCII value (within the range 0-127, inclusive).
        /// </summary>
        public static bool IsAsciiValue(byte value) => (value < (byte)0x80);

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="value"/> is a UTF-8 continuation byte.
        /// A UTF-8 continuation byte is a byte whose value is in the range 0x80-0xBF, inclusive.
        /// </summary>
        public static bool IsUtf8ContinuationByte(byte value) => ((value & (byte)0xC0) == (byte)0x80);

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="data"/> represents a well-formed UTF-8 string.
        /// </summary>
        public static bool IsWellFormedUtf8String(ReadOnlySpan<byte> data) => (GetIndexOfFirstInvalidUtf8Sequence(data) < 0);

        /// <summary>
        /// Peeks at the first UTF-8 sequence in the input buffer and returns information about that
        /// sequence. If the sequence is well-formed, returns <see cref="SequenceValidity.WellFormed"/>
        /// and sets the <paramref name="scalarValue"/> output parameter to the scalar value encoded by
        /// the sequence. If the return value is anything other than <see cref="SequenceValidity.WellFormed"/>,
        /// sets the <paramref name="scalarValue"/> output parameter to <see cref="UnicodeScalar.ReplacementChar"/>.
        /// In all cases, the <paramref name="numBytesConsumed"/> output parameter will contain the
        /// number of UTF-8 code units read from the input buffer in order to make the determination.
        /// </summary>
        public static SequenceValidity PeekFirstSequence(ReadOnlySpan<byte> data, out int numBytesConsumed, out UnicodeScalar scalarValue) => throw new NotImplementedException();

        /// <summary>
        /// If <paramref name="data"/> ends with an incomplete multi-byte UTF-8 sequence, returns <see langword="true"/>
        /// and populates <paramref name="incompleteByteCount"/> and <paramref name="remainingByteCount"/> with the
        /// number of bytes present in the incomplete sequence and the number of bytes remaining to complete the
        /// sequence, respectively. If <paramref name="data"/> does not end with an incomplete multi-byte UTF-8
        /// sequence, returns <see langword="false"/> and sets both <paramref name="incompleteByteCount"/> and
        /// <paramref name="remainingByteCount"/> to 0.
        /// </summary>
        /// <remarks>
        /// Consider a string that ends in the byte sequence [ F0 9F 98 ]. These three bytes are not well-formed on
        /// their own; they're only well-formed as the first part of a four-byte sequence such as [ F0 9F 98 80 ].
        /// In this case the method will return <see langword="true"/> to indicate that the sequence is incomplete,
        /// <paramref name="incompleteByteCount"/> will be set to 3 to indicate that there are 3 bytes present in
        /// the incomplete sequence, and <paramref name="remainingByteCount"/> will be set to 1 to indicate that
        /// there is 1 byte remaining before the four-byte sequence is complete.
        /// </remarks>
        public static bool StringEndsWithIncompleteUtf8Sequence(ReadOnlySpan<byte> data, out int incompleteByteCount, out int remainingByteCount) => throw new NotImplementedException();

        /// <summary>
        /// Attempts to read the first rune (24-bit scalar value) from the provided UTF-8 sequence.
        /// </summary>
        /// <param name="rune">
        /// If this method returns <see langword="true"/>, contains the scalar value that appears at the beginning of the input buffer.
        /// If this method returns <see langword="false"/>, the value is undefined.
        /// </param>
        /// <param name="bytesConsumed">
        /// If this method returns <see langword="true"/>, contains the number of bytes of the input buffer which were consumed in order to generate this scalar value.
        /// If this method returns <see langword="false"/>, the value is undefined.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if a scalar value could be decoded from the beginning of the input buffer,
        /// <see langword="false"/> if the input buffer did not begin with a valid UTF-8 sequence.
        /// </returns>
        public static bool TryReadFirstRune(ReadOnlySpan<byte> inputBuffer, out int rune, out int bytesConsumed) => throw new NotImplementedException();

        /// <summary>
        /// Attempts to read the first rune (24-bit scalar value) from the provided UTF-8 sequence
        /// and convert that value to its UTF-16 representation.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if a scalar value could be decoded from the beginning of the input buffer and could be written to the output buffer,
        /// <see langword="false"/> if the input buffer did not begin with a valid UTF-8 sequence or the output buffer was too small to receive the value.
        /// </returns>
        public static bool TryReadFirstRuneAsUtf16(ReadOnlySpan<byte> inputBuffer, Span<char> outputBuffer, out int bytesConsumed, out int charsWritten) => throw new NotImplementedException();
    }
}
