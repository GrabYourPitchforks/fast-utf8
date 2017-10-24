using FastUtf8Tester;
using System;
using System.Text;
using Xunit;

namespace Tests
{
    public class Utf8UtilityTests
    {
        private static readonly UTF8Encoding _utf8EncodingWithoutReplacement = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        [Fact]
        public void GetExpectedNumberOfContinuationBytes_ForAllInputs()
        {
            // For [ 00..7F ], ASCII characters
            for (uint i = 0x00; i <= 0x7F; i++)
            {
                Assert.Equal(0, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ 80..BF ], continuation code units (never valid start bytes)
            for (uint i = 0x80; i <= 0xBF; i++)
            {
                Assert.Equal(0, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ C0..C1 ], overlong 2-byte starting code units (never valid bytes)
            for (uint i = 0x80; i <= 0xBF; i++)
            {
                Assert.Equal(0, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ C2..DF ], 2-byte sequence starting markers
            for (uint i = 0xC2; i <= 0xDF; i++)
            {
                Assert.Equal(1, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ E0..EF ], 3-byte sequence starting markers
            for (uint i = 0xE0; i <= 0xEF; i++)
            {
                Assert.Equal(2, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ F0..F4 ], 4-byte sequence starting markers
            for (uint i = 0xF0; i <= 0xF4; i++)
            {
                Assert.Equal(3, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }

            // For [ F5..FF ], never valid UTF-8 code units
            for (uint i = 0xF5; i <= 0xFF; i++)
            {
                Assert.Equal(0, Utf8Utility.GetExpectedNumberOfContinuationBytes((byte)i));
            }
        }

        [Fact]
        public void IsWellFormedUtf8String_WithStringOfAllPossibleScalarValues_ReturnsTrue()
        {
            // Arrange

            byte[] allScalarsAsUtf8 = _utf8EncodingWithoutReplacement.GetBytes(_stringWithAllScalars.Value);

            // Act & assert

            Assert.True(Utf8Utility.IsWellFormedUtf8String(allScalarsAsUtf8));
        }

        [Fact]
        public void IsWellFormedUtf8String_WithCorruptedStringOfAllPossibleScalarValues_ReturnsFalse()
        {
            // Arrange

            byte[] allScalarsAsUtf8 = _utf8EncodingWithoutReplacement.GetBytes(_stringWithAllScalars.Value);
            allScalarsAsUtf8[0x1000] ^= 0x80; // modify the high bit of one of the characters, which will corrupt the header

            // Act & assert

            Assert.False(Utf8Utility.IsWellFormedUtf8String(allScalarsAsUtf8));
        }

        [Theory]
        [InlineData(new byte[] { 0x80 }, 1)] // [ 80 ] can never appear at start of sequence, 1 invalid byte
        [InlineData(new byte[] { 0x80, 0x80 }, 1)] // [ 80 ] can never appear at start of sequence, 1 invalid byte
        [InlineData(new byte[] { 0xC0, 0x80 }, 1)] // [ C0 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xC1, 0x80 }, 1)] // [ C1 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xC2, 0xC2 }, 1)] // [ C2 ] is improperly terminated sequence, 1 invalid byte
        [InlineData(new byte[] { 0xE0, 0x00, 0x80 }, 1)] // [ E0 ] is invalid if not followed by continuation byte, 1 invalid byte
        [InlineData(new byte[] { 0xE0, 0x80 }, 2)] // [ E0 80 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xE0, 0x80, 0x80 }, 2)] // [ E0 80 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xE0, 0x9F }, 2)] // [ E0 9F ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xE0, 0x9F, 0x80 }, 2)] // [ E0 9F ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xE0, 0xA0, 0xC2 }, 2)] // [ E0 A0 ] is improperly terminated sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xED, 0xA0 }, 2)] // [ ED A0 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, 2)] // [ ED A0 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF0, 0x80 }, 2)] // [ F0 80 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF0, 0x80, 0x80 }, 2)] // [ F0 80 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF0, 0x80, 0x80, 0x80 }, 2)] // [ F0 80 ] is overlong sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF0, 0x90, 0x80, 0xC2 }, 3)] // [ F0 90 80 ] is improperly terminated sequence, 3 invalid bytes
        [InlineData(new byte[] { 0xF4, 0x90 }, 2)] // [ F4 90 ] is out-of-range sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF4, 0x90, 0x80 }, 2)] // [ F4 90 ] is out-of-range sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, 2)] // [ F4 90 ] is out-of-range sequence, 2 invalid bytes
        [InlineData(new byte[] { 0xF5 }, 1)] // [ F5 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xF5, 0x80 }, 1)] // [ F5 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xF5, 0x80, 0x80 }, 1)] // [ F5 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 }, 1)] // [ F5 ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xFF }, 1)] // [ FF ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xFF, 0x80 }, 1)] // [ FF ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xFF, 0x80, 0x80 }, 1)] // [ FF ] is always invalid, 1 invalid byte
        [InlineData(new byte[] { 0xFF, 0x80, 0x80, 0x80 }, 1)] // [ FF ] is always invalid, 1 invalid byte
        public void PeekFirstSequence_WithInvalidSequence_ReturnsInvalid(byte[] sequence, int invalidSequenceLength)
        {
            // Act

            var validity = Utf8Utility.PeekFirstSequence(sequence, out var numBytesConsumed, out var scalarValue);

            // Assert

            Assert.Equal(SequenceValidity.Invalid, validity);
            Assert.Equal(invalidSequenceLength, numBytesConsumed);
            Assert.Equal(UnicodeScalar.ReplacementChar, scalarValue);
        }

        [Theory]
        [InlineData(new byte[] { 0xC2 })]
        [InlineData(new byte[] { 0xDF })]
        [InlineData(new byte[] { 0xE0 })]
        [InlineData(new byte[] { 0xE0, 0xA0 })]
        [InlineData(new byte[] { 0xE0, 0xBF })]
        [InlineData(new byte[] { 0xED })]
        [InlineData(new byte[] { 0xED, 0x80 })]
        [InlineData(new byte[] { 0xED, 0x9F })]
        [InlineData(new byte[] { 0xF0 })]
        [InlineData(new byte[] { 0xF0, 0x90 })]
        [InlineData(new byte[] { 0xF0, 0x90, 0x80 })]
        [InlineData(new byte[] { 0xF4 })]
        [InlineData(new byte[] { 0xF4, 0x8F })]
        [InlineData(new byte[] { 0xF4, 0x8F, 0x80 })]
        public void PeekFirstSequence_WithIncompleteSequence_ReturnsIncomplete(byte[] sequence)
        {
            // Act

            var validity = Utf8Utility.PeekFirstSequence(sequence, out var numBytesConsumed, out var scalarValue);

            // Assert

            Assert.Equal(SequenceValidity.Incomplete, validity);
            Assert.Equal(sequence.Length, numBytesConsumed);
            Assert.Equal(UnicodeScalar.ReplacementChar, scalarValue);
        }

        [Fact]
        public void PeekFirstSequence_WithEmptyInput_ReturnsEmptyValidity()
        {
            // Act

            var validity = Utf8Utility.PeekFirstSequence(ReadOnlySpan<byte>.Empty, out var numBytesConsumed, out var scalarValue);

            // Assert

            Assert.Equal(SequenceValidity.Empty, validity);
            Assert.Equal(0, numBytesConsumed);
            Assert.Equal(UnicodeScalar.ReplacementChar, scalarValue);
        }

        [Fact]
        public void PeekFirstSequence_WithValidNonZeroInput_ReturnsProperRepresentation()
        {
            // Loop over all inputs before the surrogate range
            for (uint i = 0; i < 0xD800U; i++)
            {
                PeekFirstSequence_WithValidNonZeroInput_ReturnsProperRepresentation_Core(i);
            }

            // Loop over all inputs after the surrogate range
            for (uint i = 0xE000U; i <= 0x10FFFFU; i++)
            {
                PeekFirstSequence_WithValidNonZeroInput_ReturnsProperRepresentation_Core(i);
            }
        }

        private void PeekFirstSequence_WithValidNonZeroInput_ReturnsProperRepresentation_Core(uint scalar)
        {
            // Arrange

            string asUtf16String = Char.ConvertFromUtf32((int)scalar);
            byte[] asUtf8Bytes = _utf8EncodingWithoutReplacement.GetBytes(asUtf16String);

            // Act & assert 1 - with no trailing data

            var validity = Utf8Utility.PeekFirstSequence(asUtf8Bytes, out var numBytesConsumed, out var scalarValue);

            Assert.Equal(SequenceValidity.WellFormed, validity);
            Assert.Equal(asUtf8Bytes.Length, numBytesConsumed);
            Assert.Equal(scalar, (uint)scalarValue.Value);

            // Act & assert 2 - with trailing data

            byte[] asUtf8BytesWithExtra = new byte[asUtf8Bytes.Length + 1];
            asUtf8Bytes.CopyTo(asUtf8BytesWithExtra);
            asUtf8BytesWithExtra[asUtf8BytesWithExtra.Length - 1] = 0xFF; // end with an always-invalid byte

            validity = Utf8Utility.PeekFirstSequence(asUtf8BytesWithExtra, out numBytesConsumed, out scalarValue);

            Assert.Equal(SequenceValidity.WellFormed, validity);
            Assert.Equal(asUtf8Bytes.Length, numBytesConsumed);
            Assert.Equal(scalar, (uint)scalarValue.Value);
        }

        private static readonly Lazy<string> _stringWithAllScalars = new Lazy<string>(CreateStringWithAllScalars);

        private static string CreateStringWithAllScalars()
        {
            StringBuilder sb = new StringBuilder();

            // Everything before the surrogate range
            for (int i = 0; i < 0xD800; i++)
            {
                sb.Append(Char.ConvertFromUtf32(i));
            }

            // Everything after the surrogate range
            for (int i = 0xE000; i <= 0x10FFFF; i++)
            {
                sb.Append(Char.ConvertFromUtf32(i));
            }

            return sb.ToString();
        }
    }
}
