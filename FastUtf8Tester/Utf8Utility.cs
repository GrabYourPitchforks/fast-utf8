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
        /// <summary>
        /// The Unicode Replacement Character (U+FFFD) as a UTF-8 sequence ([ EF BF BD ]).
        /// </summary>
        public static ReadOnlySpan<byte> ReplacementCharacterByteSequence { get => throw new NotImplementedException(); }

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
        /// Returns <see langword="true"/> iff the input data terminates in the middle of a multi-byte UTF-8 sequence.
        /// For example, this method returns <see langword="true"/> if the input data ends with the two bytes [ E0 BF ],
        /// as the UTF-8 decoding procedure expects there to be a continuation byte immediately thereafter.
        /// Returns <see langword="false"/> for an empty input string.
        /// </summary>
        public static bool DoesStringEndWithTruncatedSequence(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>
        /// If the input data ends with a truncated multi-byte UTF-8 sequence (see <see cref="DoesStringEndWithTruncatedSequence(ReadOnlySpan{byte})"/>),
        /// returns the number of remaining continuation bytes that the decoder expects to see in order to properly terminate
        /// the sequence. Returns 0 if the string does not end with a truncated multi-byte UTF-8 sequence.
        /// </summary>
        public static int GetCountOfRemainingContinuationBytes(ReadOnlySpan<byte> data) => throw new NotImplementedException();

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
        public static int GetExpectedNumberOfContinuationBytes(byte firstByte) => throw new NotImplementedException();

        /// <summary>
        /// Given the first byte of a sequence, returns the expected number of continuation bytes
        /// which should follow this byte.
        /// </summary>
        /// <remarks>
        /// Only the low-order byte of <paramref name="firstByte"/> is checked.
        /// </remarks>
        public static int GetExpectedNumberOfContinuationBytes(uint firstByte) => throw new NotImplementedException();

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
        /// If the input data ends with a truncated multi-byte UTF-8 sequence (see <see cref="DoesStringEndWithTruncatedSequence(ReadOnlySpan{byte})"/>),
        /// returns the number of bytes at the end of the string that were part of the truncated sequence.
        /// Returns 0 if the string does not end with a truncated multi-byte UTF-8 sequence.
        /// </summary>
        public static int GetLengthOfTruncatedSequenceAtEndOfString(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>
        /// Return <see langword="true"/> iff <paramref name="value"/> is an ASCII value (within the range 0-127, inclusive).
        /// </summary>
        public static bool IsAsciiValue(byte value) => throw new NotImplementedException();

        /// <summary>
        /// Return <see langword="true"/> iff <paramref name="value"/> is an ASCII value (within the range 0-127, inclusive).
        /// </summary>
        public static bool IsAsciiValue(uint value) => throw new NotImplementedException();

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="value"/> is a UTF-8 continuation byte.
        /// A UTF-8 continuation byte is a byte whose value is in the range 0x80-0xBF, inclusive.
        /// </summary>
        public static bool IsUtf8ContinuationByte(byte value) => throw new NotImplementedException();

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="value"/> is a UTF-8 continuation byte.
        /// A UTF-8 continuation byte is a byte whose value is in the range 0x80-0xBF, inclusive.
        /// </summary>
        /// <remarks>
        /// Only the low-order byte of <paramref name="value"/> is checked.
        /// </remarks>
        public static bool IsUtf8ContinuationByte(uint value) => throw new NotImplementedException();

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="buffer"/> represents a well-formed UTF-8 string.
        /// </summary>
        public static bool IsWellFormedUtf8String(ReadOnlySpan<byte> buffer) => throw new NotImplementedException();

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
