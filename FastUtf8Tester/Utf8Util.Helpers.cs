using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        // Assuming 'buffer' points to the start of an invalid sequence, returns the length (in bytes)
        // of the invalid sequence.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int GetInvalidByteCount(ref byte buffer, int bufferLength)
        {
            // We don't try to optimize this code path because it should only ever be hit in exceptional (error) cases.

            uint firstByte = (bufferLength >= 1) ? (uint)buffer : 0;

            if (firstByte < 0x80U)
            {
                return 0; // ASCII byte (or empty buffer) => not invalid sequence
            }

            if (!IsWithinRangeInclusive(firstByte, 0xC2U, 0xF4U))
            {
                // Per Table 3-7, if the first byte is not ASCII, it must be [ C2 .. F4 ].
                return 1;
            }

            // Begin multi-byte checking

            uint secondByte = (bufferLength >= 2) ? (uint)Unsafe.Add(ref buffer, 1) : 0;

            if (!IsValidTrailingByte(secondByte))
            {
                // The first byte said it was the start of a multi-byte sequence, but the
                // following byte is not a trailing byte. Hence the first byte is incorrect.
                return 1;
            }

            if (firstByte < 0xE0U)
            {
                // Valid two-byte sequence: [ C2 .. DF ] [ 80 .. BF ]
                return 0;
            }

            // These sequences come from Table 3-7.
            // The (first byte, second byte) tuples below are invalid.
            if ((firstByte == 0xE0U && secondByte < 0xA0U)
                || (firstByte == 0xEDU && secondByte > 0x9FU)
                || (firstByte == 0xF0U && secondByte < 0x90U)
                || (firstByte == 0xF4U && secondByte > 0x8FU))
            {
                return 2;
            }

            uint thirdByte = (bufferLength >= 3) ? (uint)Unsafe.Add(ref buffer, 2) : 0;

            if (!IsValidTrailingByte(thirdByte))
            {
                // The first two bytes represent an incomplete three-byte (or four-byte) sequence.
                return 2;
            }

            if (firstByte < 0xF0U)
            {
                // Valid three-byte sequence
                return 0;
            }

            uint fourthByte = (bufferLength >= 4) ? (uint)Unsafe.Add(ref buffer, 3) : 0;

            if (!IsValidTrailingByte(fourthByte))
            {
                // The first three bytes represent an incomplete four-byte sequence.
                return 3;
            }

            // Valid four-byte sequence
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static bool IntPtrIsLessThan(IntPtr a, IntPtr b) => (a.ToPointer() < b.ToPointer());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IntPtrToInt32NoOverflowCheck(IntPtr value)
        {
            if (IntPtr.Size == 4)
            {
                return (int)value;
            }
            else
            {
                return (int)(long)value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidTrailingByte(uint value)
        {
            return ((value & 0xC0U) == 0x80U);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsWithinRangeInclusive(uint value, uint lowerBound, uint upperBound) => ((value - lowerBound) <= (upperBound - lowerBound));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsWithinRangeInclusive(byte value, byte lowerBound, byte upperBound) => ((byte)(value - lowerBound) <= (byte)(upperBound - lowerBound));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GenerateUtf16CodeUnitFromUtf8CodeUnits(uint firstByte, uint secondByte)
        {
            return ((firstByte & 0x1FU) << 6)
                | (secondByte & 0x3FU);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GenerateUtf16CodeUnitFromUtf8CodeUnits(uint firstByte, uint secondByte, uint thirdByte)
        {
            return ((firstByte & 0x0FU) << 12)
                   | ((secondByte & 0x3FU) << 6)
                   | (secondByte & 0x3FU);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GenerateUtf16CodeUnitsFromUtf8CodeUnits(uint firstByte, uint secondByte, uint thirdByte, uint fourthByte)
        {
            // This method needs to generate a surrogate pair.
            // RETURN VALUE IS BIG ENDIAN

            uint retVal = ((firstByte & 0x3U) << 24)
                  | ((secondByte & 0x3FU) << 18)
                  | ((thirdByte & 0x30U) << 12)
                  | ((thirdByte & 0x0FU) << 6)
                  | (fourthByte & 0x3FU);
            retVal -= 0x400000U; // convert uuuuu to wwww per Table 3-5
            retVal += 0xD800DC00U; // add surrogate markers back in
            return retVal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GenerateUtf16CodeUnitsFromFourUtf8CodeUnits(uint utf8)
        {
            // input and output are in machine order
            if (BitConverter.IsLittleEndian)
            {
                // UTF8 [ 10xxxxxx 10yyyyyy 10uuzzzz 11110uuu ] = scalar 000uuuuu zzzzyyyy yyxxxxxx
                // UTF16 scalar 000uuuuuzzzzyyyyyyxxxxxx = [ 110111yy yyxxxxxx 110110ww wwzzzzyy ]
                // where wwww = uuuuu - 1
                uint retVal = (utf8 & 0x0F0000U) << 6; // retVal = [ 000000yy yy000000 00000000 00000000 ]
                retVal |= (utf8 & 0x3F000000U) >> 8; // retVal = [ 000000yy yyxxxxxx 00000000 00000000 ]
                retVal |= (utf8 & 0xFFU) << 8; // retVal = [ 000000yy yyxxxxxx 11110uuu 00000000 ]
                retVal |= (utf8 & 0x3F00U) >> 6; // retVal = [ 000000yy yyxxxxxx 11110uuu uuzzzz00 ]
                retVal |= (utf8 & 0x030000U) >> 16; // retVal = [ 000000yy yyxxxxxx 11110uuu uuzzzzyy ]
                retVal -= 0x40U;// retVal = [ 000000yy yyxxxxxx 111100ww wwzzzzyy ]
                retVal -= 0x2000U; // retVal = [ 000000yy yyxxxxxx 110100ww wwzzzzyy ]
                retVal += 0x0800U; // retVal = [ 000000yy yyxxxxxx 110110ww wwzzzzyy ]
                retVal += 0xDC000000U; // retVal = [ 110111yy yyxxxxxx 110110ww wwzzzzyy ]
                return retVal;
            }
            else
            {
                // UTF8 [ 11110uuu 10uuzzzz 10yyyyyy 10xxxxxx ] = scalar 000uuuuu zzzzyyyy yyxxxxxx
                // UTF16 scalar 000uuuuuxxxxxxxxxxxxxxxx = [ 110110wwwwxxxxxx 110111xxxxxxxxx ]
                // where wwww = uuuuu - 1
                //if (Bmi2.IsSupported)
                //{
                //    uint retVal = Bmi2.ParallelBitDeposit(Bmi2.ParallelBitExtract(utf8, 0x0F3F3F00U), 0x03FF03FFU); // retVal = [ 00000uuuuuzzzzyy 000000yyyyxxxxxx ]
                //    retVal -= 0x4000U; // retVal = [ 000000wwwwzzzzyy 000000yyyyxxxxxx ]
                //    retVal += 0xD800DC00U; // retVal = [ 110110wwwwzzzzyy 110111yyyyxxxxxx ]
                //    return retVal;
                //}
                //else
                {
                    uint retVal = utf8 & 0xFF000000U; // retVal = [ 11110uuu 00000000 00000000 00000000 ]
                    retVal |= (utf8 & 0x3F0000U) << 2; // retVal = [ 11110uuu uuzzzz00 00000000 00000000 ]
                    retVal |= (utf8 & 0x3000U) << 4; // retVal = [ 11110uuu uuzzzzyy 00000000 00000000 ]
                    retVal |= (utf8 & 0x0F00U) >> 2; // retVal = [ 11110uuu uuzzzzyy 000000yy yy000000 ]
                    retVal |= (utf8 & 0x3FU); // retVal = [ 11110uuu uuzzzzyy 000000yy yyxxxxxx ]
                    retVal -= 0x20000000U; // retVal = [ 11010uuu uuzzzzyy 000000yy yyxxxxxx ]
                    retVal -= 0x400000U; // retVal = [ 110100ww wwzzzzyy 000000yy yyxxxxxx ]
                    retVal += 0xDC00U; // retVal = [ 110100ww wwzzzzyy 110111yy yyxxxxxx ]
                    retVal += 0x08000000U; // retVal = [ 110110ww wwzzzzyy 110111yy yyxxxxxx ]
                    return retVal;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsWithTwoByteMask(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     const uint mask = 0x0000C0E0U;
            //     const uint comparand = 0x000080C0U;
            //     return ((value & mask) == comparand);
            // }
            // else
            // {
            //     const uint mask = 0xE0C00000U;
            //     const uint comparand = 0xC0800000U;
            //     return ((value & mask) == comparand);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0x0000C0E0U) == 0x000080C0U))
                || (!BitConverter.IsLittleEndian && ((value & 0xE0C00000U) == 0xC0800000U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsWithFourByteMaskAndHasValidFirstByteLittleEndian(uint value)
        {
            // Per Table 3-7, valid 4-byte sequences are:
            // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
            // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
            // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]
            // In little-endian, that would be represented as:
            // [ 10xxxxxx 10yyyyyy 10zzzzzz 11110www ].
            // Due to the little-endian representation we can perform a trick by ANDing the value
            // with the bitmask [ 11000000 11000000 11000000 11111111 ] and checking that the value is within
            // the range [ 11000000_11000000_11000000_11110000, 11000000_11000000_11000000_11110100 ].
            // This performs both the 4-byte-sequence bitmask check and validates that the first byte
            // is within the range [ F0..F4 ], but it doesn't validate the second byte.

            Debug.Assert(BitConverter.IsLittleEndian);

            return (BitConverter.IsLittleEndian && IsWithinRangeInclusive(value & 0xC0C0C0FFU, 0x808080F0U, 0x808080F4U))
                || (!BitConverter.IsLittleEndian && false); // this line - while weird - helps JITter produce optimal code
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsWithValidTwoByteSequenceLittleEndian(uint value)
        {
            // Per Table 3-7, valid 2-byte sequences are [ C2..DF ] [ 80..BF ].
            // In little-endian, that would be represented as:
            // [ ######## ######## 10xxxxxx 110yyyyy ].
            // Due to the little-endian representation we can perform a trick by ANDing the low
            // WORD with the bitmask [ 11000000 11111111 ] and checking that the value is within
            // the range [ 11000000_11000010, 11000000_11011111 ]. This performs both the
            // 2-byte-sequence bitmask check and overlong form validation with one comparison.

            Debug.Assert(BitConverter.IsLittleEndian);

            return (BitConverter.IsLittleEndian && IsWithinRangeInclusive(value & 0xC0FFU, 0x80C2U, 0x80DFU))
                || (!BitConverter.IsLittleEndian && false); // this line - while weird - helps JITter produce optimal code
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordEndsWithValidTwoByteSequenceLittleEndian(uint value)
        {
            // See comment in Utf8DWordBeginsWithValidTwoByteSequenceLittleEndian for how this works.
            // The only difference is that we use the high WORD instead of the low WORD.

            Debug.Assert(BitConverter.IsLittleEndian);

            return (BitConverter.IsLittleEndian && IsWithinRangeInclusive(value & 0xC0FF0000U, 0x80C20000U, 0x80DF0000U))
                || (!BitConverter.IsLittleEndian && false); // this line - while weird - helps JITter produce optimal code
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordEndsWithTwoByteMask(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     const uint mask = 0xC0E00000U;
            //     const uint comparand = 0x80C00000U;
            //     return ((value & mask) == comparand);
            // }
            // else
            // {
            //     const uint mask = 0x0000E0C0U;
            //     const uint comparand = 0x0000C080U;
            //     return ((value & mask) == comparand);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0xC0E00000U) == 0x80C00000U))
                  || (!BitConverter.IsLittleEndian && ((value & 0x0000E0C0U) == 0x0000C080U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsAndEndsWithTwoByteMask(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     const uint mask = 0xC0E0C0E0U;
            //     const uint comparand = 0x80C080C0U;
            //     return ((value & mask) == comparand);
            // }
            // else
            // {
            //     const uint mask = 0xE0C0E0C0U;
            //     const uint comparand = 0xC080C080U;
            //     return ((value & mask) == comparand);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0xC0E0C0E0U) == 0x80C080C0U))
                || (!BitConverter.IsLittleEndian && ((value & 0xE0C0E0C0U) == 0xC080C080U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsWithThreeByteMask(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     const uint mask = 0x00C0C0F0U;
            //     const uint comparand = 0x008080E0U;
            //     return ((value & mask) == comparand);
            // }
            // else
            // {
            //     const uint mask = 0xF0C0C000U;
            //     const uint comparand = 0xE0808000U;
            //     return ((value & mask) == comparand);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0x00C0C0F0U) == 0x008080E0U))
                   || (!BitConverter.IsLittleEndian && ((value & 0xF0C0C000U) == 0xE0808000U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsWithFourByteMask(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     const uint mask = 0xC0C0C0F8U;
            //     const uint comparand = 0x808080F0U;
            //     return ((value & mask) == comparand);
            // }
            // else
            // {
            //     const uint mask = 0xF8C0C0C0U;
            //     const uint comparand = 0xF0808000U;
            //     return ((value & mask) == comparand);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0xC0C0C0F8U) == 0x808080F0U))
                   || (!BitConverter.IsLittleEndian && ((value & 0xF8C0C0C0U) == 0xF0808000U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordEndsWithThreeByteSequenceMarker(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     // search input word for [ B1 A3 A2 A1 ]
            //     return ((value & 0xF0000000U) == 0xE0000000U);
            // }
            // else
            // {
            //     // search input word for [ A1 A2 A3 B1 ]
            //     return ((value & 0xF0U) == 0xE0U);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0xF0000000U) == 0xE0000000U))
                   || (!BitConverter.IsLittleEndian && ((value & 0xF0U) == 0xE0U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordAllBytesAreAscii(uint value)
        {
            return ((value & 0x80808080U) == 0U);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8QWordAllBytesAreAscii(ulong value)
        {
            return ((value & 0x8080808080808080UL) == 0UL);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadAndFoldTwoQWords(ref byte buffer)
        {
            return Unsafe.ReadUnaligned<ulong>(ref buffer)
                | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, sizeof(ulong)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadAndFoldTwoDWords(ref byte buffer)
        {
            return Unsafe.ReadUnaligned<uint>(ref buffer)
                | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, sizeof(uint)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordFirstByteIsAscii(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     return ((value & 0x80U) == 0U);
            // }
            // else
            // {
            //     return ((int)value >= 0);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0x80U) == 0U))
                || (!BitConverter.IsLittleEndian && ((int)value >= 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordSecondByteIsAscii(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     return ((value & 0x8000U) == 0U);
            // }
            // else
            // {
            //     return ((value & 0x800000U) == 0U);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0x8000U) == 0U))
                || (!BitConverter.IsLittleEndian && ((value & 0x800000U) == 0U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordThirdByteIsAscii(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     return ((value & 0x800000U) == 0U);
            // }
            // else
            // {
            //     return ((value & 0x8000U) == 0U);
            // }

            return (BitConverter.IsLittleEndian && ((value & 0x800000U) == 0U))
                || (!BitConverter.IsLittleEndian && ((value & 0x8000U) == 0U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordFourthByteIsAscii(uint value)
        {
            // The code in this method is equivalent to the code
            // below, but the JITter is able to inline + optimize it
            // better in release builds.
            //
            // if (BitConverter.IsLittleEndian)
            // {
            //     return ((int)value >= 0);
            // }
            // else
            // {
            //     return ((value & 0x80U) == 0U);
            // }

            return (BitConverter.IsLittleEndian && ((int)value >= 0))
                || (!BitConverter.IsLittleEndian && ((value & 0x80U) == 0U));
        }

        // Widens a 32-bit DWORD to a 64-bit QWORD by placing bytes into alternating slots.
        // [ AA BB CC DD ] -> [ 00 AA 00 BB 00 CC 00 DD ]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static ulong Widen(uint value)
        {
            //if (Bmi2.IsSupported)
            //{
            //    return Bmi2.ParallelBitDeposit((ulong)value, 0x00FF00FF00FF00FFUL);
            //}
            //else
            {
                ulong qWord = value;
                return ((qWord & 0xFF000000UL) << 24)
                    | ((qWord & 0xFF0000UL) << 16)
                    | (((value & 0xFF00U) << 8) | (byte)value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWellFormedCharPackFromDoubleTwoByteSequences(uint value)
        {
            // Given a value [ AAAA BBBB ], ensures that both AAAA and BBBB are
            // at least U+0080. It's assumed that both AAAA and BBBB are < U+0800
            // since such a scalar can't be formed from a two-byte UTF8 sequence.

            // This method uses only arithmetic operations and bit manipulation
            // in order to avoid storing + loading flags between calls.

            uint a = value - 0x00800000U; // high bit will be set (underflow) if AAAA < 0x0080
            uint b = (value & 0xFFFFU) - 0x0080U; // high bit will be set (underflow) if BBBB < 0x0080
            return ((int)(a | b) >= 0); // if any high bit is set, underflow occurred
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWellFormedCharPackFromQuadTwoByteSequences(ulong value)
        {
            // Like IsWellFormedCharPackFromDoubleTwoByteSequences, but works with
            // 64-bit values of the form [ AAAA BBBB CCCC DDDD ].

            ulong a = value - 0x0080000000000000UL; // high bit will be set (underflow) if AAAA < 0x0080
            ulong b = (value & 0x0000FFFF00000000UL) - 0x0000008000000000U; // high bit will be set (underflow) if BBBB < 0x0080
            ulong c = (value & 0x00000000FFFF0000UL) - 0x0000000000800000U; // high bit will be set (underflow) if CCCC < 0x0080
            ulong d = (value & 0x000000000000FFFFUL) - 0x0000000000000080U; // high bit will be set (underflow) if DDDD < 0x0080
            return ((long)(a | b | c | d) >= 0L); // if any high bit is set, underflow occurred
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsWellFormedCharPackFromDualThreeByteSequences(uint packedChars, ulong secondDWord)
        {
            if (!BitConverter.IsLittleEndian)
            {
                // TODO: SUPPORT BIG ENDIAN
                throw new NotImplementedException();
            }

            return (packedChars >= 0x08000000U) /* char 'B' is >= U+0800 */
                && ((packedChars & 0xF8000000U) != 0xD8000000U) /* char 'B' isn't a surrogate */
                && ((packedChars & 0x0000F800U) != 0U) /* char 'A' is >= U+0800 */
                && ((packedChars & 0x0000F800U) != 0x0000D800U) /* char 'A' isn't a surrogate */
                && ((secondDWord & 0x0000C0C0U) == 0x00008080U); /* secondDWord has correct masking */
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFirstWordWellFormedTwoByteSequence(uint value)
        {
            // ASSUMPTION: Caller has already checked the '110y yyyy 10xx xxxx' mask of the input.
            Debug.Assert(Utf8DWordBeginsWithTwoByteMask(value));

            // Per Table 3-7, first byte of two-byte sequence must be within range C2 .. DF.
            // Since we already validated it's 80 <= ?? <= DF (per mask check earlier), now only need
            // to check that it's >= C2.

            return (BitConverter.IsLittleEndian && ((byte)value >= (byte)0xC2))
                || (!BitConverter.IsLittleEndian && (value >= 0xC2000000U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSecondWordWellFormedTwoByteSequence(uint value)
        {
            // ASSUMPTION: Caller has already checked the '110y yyyy 10xx xxxx' mask of the input.
            Debug.Assert(Utf8DWordEndsWithTwoByteMask(value));

            // Per Table 3-7, first byte of two-byte sequence must be within range C2 .. DF.
            // We already validated that it's 80 .. DF (per mask check earlier).
            // C2 = 1100 0010
            // DF = 1101 1111
            // This means that we can use the mask 0001 1110 (1E) and a non-zero comparand.

            return (BitConverter.IsLittleEndian && ((value & 0x001E0000U) != 0U))
                || (!BitConverter.IsLittleEndian && ((value & 0x1E00U) != 0U));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSurrogateFast(uint @char) => ((@char & 0xF800U) == 0xD800U);
    }
}
