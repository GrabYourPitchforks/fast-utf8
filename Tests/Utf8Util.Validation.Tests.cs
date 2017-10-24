using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FastUtf8Tester;
using Xunit;

namespace Tests
{
    public class Utf8UtilTests
    {
        private const string X = "58"; // "X", 1 byte
        private const string Y = "59"; // "Y", 1 byte
        private const string Z = "5A"; // "Z", 1 byte
        private const string E_ACUTE = "C3A9"; // "é", 2 bytes
        private const string EURO_SYMBOL = "E282AC"; // "€", 3 bytes

        [Theory]
        [InlineData(X, 1, 0)]
        [InlineData(X + Y, 2, 0)]
        [InlineData(X + Y + Z, 3, 0)]
        [InlineData(E_ACUTE, 1, 0)]
        [InlineData(X + E_ACUTE, 2, 0)]
        [InlineData(E_ACUTE + X, 2, 0)]
        [InlineData(EURO_SYMBOL, 1, 0)]
        public void GetIndexOfFirstInvalidUtf8Sequence_WithSmallValidBuffers(string input, int expectedRuneCount, int expectedSurrogatePairCount)
            => GetIndexOfFirstInvalidUtf8Sequence_Test_Core(input, -1 /* expectedRetVal */, expectedRuneCount, expectedSurrogatePairCount);

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
