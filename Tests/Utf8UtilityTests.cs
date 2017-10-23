using FastUtf8Tester;
using System;
using System.Text;
using System.Linq;
using Xunit;

namespace Tests
{
    public class Utf8UtilityTests
    {
        private static readonly UTF8Encoding _utf8EncodingWithoutReplacement = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
    }
}
