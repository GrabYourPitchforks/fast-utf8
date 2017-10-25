using System;
using FastUtf8Tester;
using Xunit;

namespace Tests
{
    public class Utf8UtilTests
    {
        private const string X = "58"; // U+0058 LATIN CAPITAL LETTER X, 1 byte
        private const string Y = "59"; // U+0058 LATIN CAPITAL LETTER Y, 1 byte
        private const string Z = "5A"; // U+0058 LATIN CAPITAL LETTER Z, 1 byte
        private const string E_ACUTE = "C3A9"; // U+00E9 LATIN SMALL LETTER E WITH ACUTE, 2 bytes
        private const string EURO_SYMBOL = "E282AC"; // U+20AC EURO SIGN, 3 bytes
        private const string GRINNING_FACE = "F09F9880"; // U+1F600 GRINNING FACE, 4 bytes

        [Theory]
        [InlineData("", 0, 0)] // empty string is OK
        [InlineData(X, 1, 0)]
        [InlineData(X + Y, 2, 0)]
        [InlineData(X + Y + Z, 3, 0)]
        [InlineData(E_ACUTE, 1, 0)]
        [InlineData(X + E_ACUTE, 2, 0)]
        [InlineData(E_ACUTE + X, 2, 0)]
        [InlineData(EURO_SYMBOL, 1, 0)]
        public void GetIndexOfFirstInvalidUtf8Sequence_WithSmallValidBuffers(string input, int expectedRuneCount, int expectedSurrogatePairCount)
        {
            // These test cases are for the "slow processing" code path at the end of GetIndexOfFirstInvalidUtf8Sequence,
            // so inputs should be less than 4 bytes.

            Assert.InRange(input.Length, 0, 6);

            GetIndexOfFirstInvalidUtf8Sequence_Test_Core(input, -1 /* expectedRetVal */, expectedRuneCount, expectedSurrogatePairCount);
        }

        [Theory]
        [InlineData("80", 0, 0, 0)] // sequence cannot begin with continuation character
        [InlineData("8182", 0, 0, 0)] // sequence cannot begin with continuation character
        [InlineData("838485", 0, 0, 0)] // sequence cannot begin with continuation character
        [InlineData(X + "80", 1, 1, 0)] // sequence cannot begin with continuation character
        [InlineData(X + "8182", 1, 1, 0)] // sequence cannot begin with continuation character
        [InlineData("C0", 0, 0, 0)] // [ C0 ] is always invalid
        [InlineData("C080", 0, 0, 0)] // [ C0 ] is always invalid
        [InlineData("C08081", 0, 0, 0)] // [ C0 ] is always invalid
        [InlineData(X + "C1", 1, 1, 0)] // [ C1 ] is always invalid
        [InlineData(X + "C180", 1, 1, 0)] // [ C1 ] is always invalid
        [InlineData("C2", 0, 0, 0)] // [ C2 ] is improperly terminated
        [InlineData(X + "C27F", 1, 1, 0)] // [ C2 ] is improperly terminated
        [InlineData(X + "E282", 1, 1, 0)] // [ E2 82 ] is improperly terminated
        [InlineData("E2827F", 0, 0, 0)] // [ E2 82 ] is improperly terminated
        [InlineData("E09F80", 0, 0, 0)] // [ E0 9F ... ] is overlong
        [InlineData("E0C080", 0, 0, 0)] // [ E0 ] is improperly terminated
        [InlineData("ED7F80", 0, 0, 0)] // [ ED ] is improperly terminated
        [InlineData("EDA080", 0, 0, 0)] // [ ED A0 ... ] is surrogate
        public void GetIndexOfFirstInvalidUtf8Sequence_WithSmallInvalidBuffers(string input, int expectedRetVal, int expectedRuneCount, int expectedSurrogatePairCount)
        {
            // These test cases are for the "slow processing" code path at the end of GetIndexOfFirstInvalidUtf8Sequence,
            // so inputs should be less than 4 bytes.

            Assert.InRange(input.Length, 0, 6);

            GetIndexOfFirstInvalidUtf8Sequence_Test_Core(input, expectedRetVal, expectedRuneCount, expectedSurrogatePairCount);
        }

        [Theory]
        [InlineData(E_ACUTE + "21222324" + "303132333435363738393A3B3C3D3E3F", 21, 0)] // Loop unrolling at end of buffer
        [InlineData(E_ACUTE + "21222324" + "303132333435363738393A3B3C3D3E3F" + "3031323334353637" + E_ACUTE + "38393A3B3C3D3E3F", 38, 0)] // Loop unrolling interrupted by non-ASCII
        [InlineData("212223" + E_ACUTE + "30313233", 8, 0)] // 3 ASCII bytes followed by non-ASCII
        [InlineData("2122" + E_ACUTE + "30313233", 7, 0)] // 2 ASCII bytes followed by non-ASCII
        [InlineData("21" + E_ACUTE + "30313233", 6, 0)] // 1 ASCII byte followed by non-ASCII
        [InlineData(E_ACUTE + E_ACUTE + E_ACUTE + E_ACUTE, 4, 0)] // 4x 2-byte sequences, exercises optimization code path in 2-byte sequence processing
        [InlineData(E_ACUTE + E_ACUTE + E_ACUTE + "5051", 5, 0)] // 3x 2-byte sequences + 2 ASCII bytes, exercises optimization code path in 2-byte sequence processing
        [InlineData(E_ACUTE + "5051", 3, 0)] // single 2-byte sequence + 2 trailing ASCII bytes, exercises draining logic in 2-byte sequence processing
        [InlineData(E_ACUTE + "50" + E_ACUTE + "304050", 6, 0)] // single 2-byte sequences + 1 trailing ASCII byte + 2-byte sequence, exercises draining logic in 2-byte sequence processing
        [InlineData(EURO_SYMBOL + "20", 2, 0)] // single 3-byte sequence + 1 trailing ASCII byte, exercises draining logic in 3-byte sequence processing
        [InlineData(EURO_SYMBOL + "203040", 5, 0)] // single 3-byte sequence + 3 trailing ASCII byte, exercises draining logic and "running out of data" logic in 3-byte sequence processing
        [InlineData(EURO_SYMBOL + EURO_SYMBOL + EURO_SYMBOL, 3, 0)] // 3x 3-byte sequences, exercises "stay within 3-byte loop" logic in 3-byte sequence processing
        [InlineData(EURO_SYMBOL + EURO_SYMBOL + EURO_SYMBOL + EURO_SYMBOL, 4, 0)] // 4x 3-byte sequences, exercises "consume multiple bytes at a time" logic in 3-byte sequence processing
        [InlineData(EURO_SYMBOL + EURO_SYMBOL + EURO_SYMBOL + E_ACUTE, 4, 0)] // 3x 3-byte sequences + single 2-byte sequence, exercises "consume multiple bytes at a time" logic in 3-byte sequence processing
        [InlineData(EURO_SYMBOL + EURO_SYMBOL + E_ACUTE + E_ACUTE + E_ACUTE + E_ACUTE, 6, 0)] // 2x 3-byte sequences + 4x 2-byte sequences, exercises "consume multiple bytes at a time" logic in 3-byte sequence processing
        [InlineData(GRINNING_FACE + GRINNING_FACE, 2, 2)] // 2x 4-byte sequences, exercises 4-byte sequence processing
        [InlineData(GRINNING_FACE + "303132", 4, 2)] // single 4-byte sequence + 3 ASCII bytes, exercises 4-byte sequence processing and draining logic
        public void GetIndexOfFirstInvalidUtf8Sequence_WithLargeValidBuffers(string input, int expectedRuneCount, int expectedSurrogatePairCount)
        {
            // These test cases are for the "fast processing" code which is the main loop of GetIndexOfFirstInvalidUtf8Sequence,
            // so inputs should be less >= 4 bytes.

            Assert.True(input.Length >= 8);

            GetIndexOfFirstInvalidUtf8Sequence_Test_Core(input, -1 /* expectedRetVal */, expectedRuneCount, expectedSurrogatePairCount);
        }

        private static void GetIndexOfFirstInvalidUtf8Sequence_Test_Core(string inputHex, int expectedRetVal, int expectedRuneCount, int expectedSurrogatePairCount)
        {
            // Arrange

            var inputBytes = NativeMemory.GetProtectedReadonlyBuffer(DecodeHex(inputHex));

            // Act

            var indexOfFirstInvalidChar = Utf8UtilForTest.GetIndexOfFirstInvalidUtf8Sequence(inputBytes, out int actualRuneCount, out int actualSurrogatePairCount);

            // Assert

            Assert.Equal(expectedRetVal, indexOfFirstInvalidChar);
            Assert.Equal(expectedRuneCount, actualRuneCount);
            Assert.Equal(expectedSurrogatePairCount, actualSurrogatePairCount);
        }

        private static byte[] DecodeHex(string input)
        {
            int ParseNibble(char ch)
            {
                ch -= (char)'0';
                if (ch < 10) { return ch; }

                ch -= (char)('A' - '0');
                if (ch < 6) { return (ch + 10); }

                ch -= (char)('a' - 'A');
                if (ch < 6) { return (ch + 10); }

                throw new Exception("Invalid hex character.");
            }

            if (input.Length % 2 != 0) { throw new Exception("Invalid hex data."); }

            byte[] retVal = new byte[input.Length / 2];
            for (int i = 0; i < retVal.Length; i++)
            {
                retVal[i] = (byte)((ParseNibble(input[2 * i]) << 4) | ParseNibble(input[2 * i + 1]));
            }

            return retVal;
        }
    }
}
