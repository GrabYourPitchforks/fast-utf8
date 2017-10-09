using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidUtf8Sequence(ReadOnlySpan<byte> buffer)
        {
            return (GetIndexOfFirstInvalidUtf8Char(buffer) < 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetRuneCount(ReadOnlySpan<byte> buffer, out int runeCount)
        {
            var retVal = GetRuneCount(ref buffer.DangerousGetPinnableReference(), buffer.Length);
            if (retVal < 0)
            {
                runeCount = 0;
                return false;
            }
            else
            {
                runeCount = retVal;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetUtf16CodeUnitCount(ReadOnlySpan<byte> buffer, out int utf16CodeUnitCount)
        {
            var retVal = GetUtf16CodeUnitCount(ref buffer.DangerousGetPinnableReference(), buffer.Length);
            if (retVal < 0)
            {
                utf16CodeUnitCount = 0;
                return false;
            }
            else
            {
                utf16CodeUnitCount = retVal;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetRuneCountPublic(ReadOnlySpan<byte> buffer)
        {
            if (GetIndexOfFirstInvalidUtf8CharEx(ref buffer.DangerousGetPinnableReference(), buffer.Length, out int runeCount, out _) < 0)
            {
                return runeCount; // success!
            }
            else
            {
                return -1; // failure
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetUtf16Count(ReadOnlySpan<byte> buffer)
        {
            if (GetIndexOfFirstInvalidUtf8CharEx(ref buffer.DangerousGetPinnableReference(), buffer.Length, out int runeCount, out int surrogateCount) < 0)
            {
                return runeCount + surrogateCount; // success!
            }
            else
            {
                return -1; // failure
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetIndexOfFirstInvalidUtf8Char(ReadOnlySpan<byte> buffer)
        {
            return GetIndexOfFirstInvalidUtf8CharEx(ref buffer.DangerousGetPinnableReference(), buffer.Length, out _, out _);
            //return GetIndexOfFirstInvalidUtf8Char(ref buffer.DangerousGetPinnableReference(), buffer.Length);
        }

        private static int GetRuneCount(ref byte buffer, int length)
        {
            // Assume that 'buffer' contains only ASCII characters, so one input byte is one Unicode scalar.
            // As we consume multi-byte characters we'll adjust the actual scalar count.
            int scalarCount = length;

            IntPtr offset = IntPtr.Zero;
            IntPtr offsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int remainingBytes = length - IntPtrToInt32NoOverflowCheck(offset);

            while (remainingBytes >= sizeof(uint))
            {
                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                BeforeDWordRead:

                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));

                AfterDWordRead:

                // First, check for the common case of all-ASCII bytes.

                if (Utf8DWordAllBytesAreAscii(thisDWord))
                {
                    offset += sizeof(uint);
                    remainingBytes -= sizeof(uint);

                    // If we saw a string of all ASCII, there's a good chance a significant amount of data afterward is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.
                    if (IntPtrIsLessThan(offset, offsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeDWordRead;
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (remainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset + sizeof(ulong)));

                                if ((thisQWord & 0x8080808080808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 2 * sizeof(ulong);
                                    break;
                                }

                                offset += 2 * sizeof(ulong);
                                remainingBytes -= 2 * sizeof(ulong);
                            }
                        }
                        else
                        {
                            while (remainingBytes >= 4 * sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 2 * sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 3 * sizeof(uint)));

                                if (!Utf8DWordAllBytesAreAscii(thisDWord))
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 4 * sizeof(uint);
                                    break;
                                }

                                offset += 4 * sizeof(uint);
                                remainingBytes -= 4 * sizeof(uint);
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.

                {
                    if (Utf8DWordFirstByteIsAscii(thisDWord))
                    {
                        offset += 1;
                        remainingBytes--;

                        uint mask = (BitConverter.IsLittleEndian) ? 0x00008000U : 0x00800000U;
                        if ((thisDWord & mask) == 0U)
                        {
                            offset += 1;
                            remainingBytes--;

                            mask = (BitConverter.IsLittleEndian) ? 0x00800000U : 0x00008000U;
                            if ((thisDWord & mask) == 0U)
                            {
                                offset += 1;
                                remainingBytes--;
                            }
                        }

                        if (remainingBytes < sizeof(uint))
                        {
                            break; // read remainder of data
                        }
                        else
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            // fall through to multi-byte consumption logic
                        }
                    }
                }

                // At this point, we know we're working with a multi-byte code unit,
                // but we haven't yet validated it.

                // The masks and comparands are given by the Unicode Standard, Table 3-6.
                // Additionally, we need to check for valid byte sequences per Table 3-7.

                // Check the 2-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x0000C0E0U : 0xE0C00000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x000080C0U : 0xC0800000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [ C2..DF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if (!IsWithinRangeInclusive(thisDWord & 0xFFU, 0xC2U, 0xDFU))
                            {
                                goto Error;
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xC2000000U, 0xDF000000U))
                            {
                                goto Error;
                            }
                        }

                        offset += 2;
                        remainingBytes -= 2;
                        scalarCount--; // two UTF-8 code units -> one scalar

                        if (remainingBytes >= 2)
                        {
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset)) << 16);
                            }
                            else
                            {
                                thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset));
                            }
                            goto AfterDWordRead;
                        }
                        else
                        {
                            break; // read remainder of data
                        }
                    }
                }

                // Check the 3-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00C0C0F0U : 0xF0C0C000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x008080E0U : 0xE0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   E0   ] [ A0..BF ] [ 80..BF ]
                        // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                        // [   ED   ] [ 80..9F ] [ 80..BF ]
                        // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0xA000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xEDU) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xE0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (thisDWord < 0xE0A00000U) { goto Error; }
                            if (IsWithinRangeInclusive(thisDWord, 0xEDA00000U, 0xEE790000U)) { goto Error; }
                        }

                        offset += 3;
                        remainingBytes -= 3;
                        scalarCount -= 2; // three UTF-8 code units -> one scalar
                        continue;
                    }
                }

                // Check the 4-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0xC0C0C0F8U : 0xF8C0C0C0U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x808080F0U : 0xF0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                        // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                        // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0x9000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xF4U) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xF0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xF0900000U, 0xF48FFFFFU)) { goto Error; }
                        }

                        offset += 4;
                        remainingBytes -= 4;
                        scalarCount -= 3; // four UTF-8 code units -> one scalar
                        continue;
                    }
                }

                // Error - no match.

                goto Error;
            }

            Debug.Assert(remainingBytes < 4);

            while (remainingBytes > 0)
            {
                uint firstByte = Unsafe.Add(ref buffer, offset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    offset += 1;
                    remainingBytes--;
                    continue;
                }
                else if (remainingBytes >= 2)
                {
                    uint secondByte = Unsafe.Add(ref buffer, offset + 1);
                    if (firstByte < 0xE0U)
                    {
                        // 2-byte case
                        if (firstByte >= 0xC2U && IsValidTrailingByte(secondByte))
                        {
                            offset += 2;
                            remainingBytes -= 2;
                            scalarCount--; // two UTF-8 code units -> one scalar
                            continue;
                        }
                    }
                    else if (remainingBytes >= 3)
                    {
                        uint thirdByte = Unsafe.Add(ref buffer, offset + 2);
                        if (firstByte <= 0xF0U)
                        {
                            if (firstByte == 0xE0U)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0xA0U, 0xBFU)) { goto Error; }
                            }
                            else if (firstByte == 0xEDU)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0x80U, 0x9FU)) { goto Error; }
                            }
                            else
                            {
                                if (!IsValidTrailingByte(secondByte)) { goto Error; }
                            }

                            if (IsValidTrailingByte(thirdByte))
                            {
                                offset += 3;
                                remainingBytes -= 3;
                                scalarCount -= 2; // three UTF-8 code units -> one scalar
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            Debug.Assert(scalarCount >= 0);
            return scalarCount;

            // Error handling logic.

            Error:
            return -1;
        }

        private static int GetUtf16CodeUnitCount(ref byte buffer, int length)
        {
            // Assume that 'buffer' contains only ASCII characters, so one input byte is one UTF-16 code unit.
            // As we consume multi-byte characters we'll adjust the actual code unit count.
            int utf16CodeUnitCount = length;

            IntPtr offset = IntPtr.Zero;
            IntPtr offsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int remainingBytes = length - IntPtrToInt32NoOverflowCheck(offset);

            while (remainingBytes >= sizeof(uint))
            {
                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                BeforeDWordRead:

                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));

                AfterDWordRead:

                // First, check for the common case of all-ASCII bytes.

                if ((thisDWord & 0x80808080U) == 0U)
                {
                    offset += sizeof(uint);
                    remainingBytes -= sizeof(uint);

                    // If we saw a string of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.
                    if (IntPtrIsLessThan(offset, offsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeDWordRead;
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (remainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset + sizeof(ulong)));

                                if ((thisQWord & 0x8080808080808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 2 * sizeof(ulong);
                                    break;
                                }

                                offset += 2 * sizeof(ulong);
                                remainingBytes -= 2 * sizeof(ulong);
                            }
                        }
                        else
                        {
                            while (remainingBytes >= 4 * sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 2 * sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 3 * sizeof(uint)));

                                if ((thisDWord & 0x80808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 4 * sizeof(uint);
                                    break;
                                }

                                offset += 4 * sizeof(uint);
                                remainingBytes -= 4 * sizeof(uint);
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00000080U : 0x80000000U;
                    if ((thisDWord & mask) == 0U)
                    {
                        offset += 1;
                        remainingBytes--;

                        mask = (BitConverter.IsLittleEndian) ? 0x00008000U : 0x00800000U;
                        if ((thisDWord & mask) == 0U)
                        {
                            offset += 1;
                            remainingBytes--;

                            mask = (BitConverter.IsLittleEndian) ? 0x00800000U : 0x00008000U;
                            if ((thisDWord & mask) == 0U)
                            {
                                offset += 1;
                                remainingBytes--;
                            }
                        }

                        if (remainingBytes < sizeof(uint))
                        {
                            break; // read remainder of data
                        }
                        else
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            // fall through to multi-byte consumption logic
                        }
                    }
                }

                // At this point, we know we're working with a multi-byte code unit,
                // but we haven't yet validated it.

                // The masks and comparands are given by the Unicode Standard, Table 3-6.
                // Additionally, we need to check for valid byte sequences per Table 3-7.

                // Check the 2-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x0000C0E0U : 0xE0C00000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x000080C0U : 0xC0800000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [ C2..DF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if (!IsWithinRangeInclusive(thisDWord & 0xFFU, 0xC2U, 0xDFU))
                            {
                                goto Error;
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xC2000000U, 0xDF000000U))
                            {
                                goto Error;
                            }
                        }

                        offset += 2;
                        remainingBytes -= 2;
                        utf16CodeUnitCount--; // two UTF-8 code units -> one UTF-16 code unit

                        if (remainingBytes >= 2)
                        {
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset)) << 16);
                            }
                            else
                            {
                                thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset));
                            }
                            goto AfterDWordRead;
                        }
                        else
                        {
                            break; // read remainder of data
                        }
                    }
                }

                // Check the 3-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00C0C0F0U : 0xF0C0C000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x008080E0U : 0xE0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   E0   ] [ A0..BF ] [ 80..BF ]
                        // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                        // [   ED   ] [ 80..9F ] [ 80..BF ]
                        // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0xA000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xEDU) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xE0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (thisDWord < 0xE0A00000U) { goto Error; }
                            if (IsWithinRangeInclusive(thisDWord, 0xEDA00000U, 0xEE790000U)) { goto Error; }
                        }

                        offset += 3;
                        remainingBytes -= 3;
                        utf16CodeUnitCount -= 2; // three UTF-8 code units -> one UTF-16 code unit
                        continue;
                    }
                }

                // Check the 4-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0xC0C0C0F8U : 0xF8C0C0C0U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x808080F0U : 0xF0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                        // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                        // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0x9000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xF4U) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xF0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xF0900000U, 0xF48FFFFFU)) { goto Error; }
                        }

                        offset += 4;
                        remainingBytes -= 4;
                        utf16CodeUnitCount -= 2; // four UTF-8 code units -> two UTF-16 code unit (surrogates)
                        continue;
                    }
                }

                // Error - no match.

                goto Error;
            }

            Debug.Assert(remainingBytes < 4);

            while (remainingBytes > 0)
            {
                uint firstByte = Unsafe.Add(ref buffer, offset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    offset += 1;
                    remainingBytes--;
                    continue;
                }
                else if (remainingBytes >= 2)
                {
                    uint secondByte = Unsafe.Add(ref buffer, offset + 1);
                    if (firstByte < 0xE0U)
                    {
                        // 2-byte case
                        if (firstByte >= 0xC2U && IsValidTrailingByte(secondByte))
                        {
                            offset += 2;
                            remainingBytes -= 2;
                            utf16CodeUnitCount--; // two UTF-8 code units -> one UTF-16 code unit
                            continue;
                        }
                    }
                    else if (remainingBytes >= 3)
                    {
                        uint thirdByte = Unsafe.Add(ref buffer, offset + 2);
                        if (firstByte <= 0xF0U)
                        {
                            if (firstByte == 0xE0U)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0xA0U, 0xBFU)) { goto Error; }
                            }
                            else if (firstByte == 0xEDU)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0x80U, 0x9FU)) { goto Error; }
                            }
                            else
                            {
                                if (!IsValidTrailingByte(secondByte)) { goto Error; }
                            }

                            if (IsValidTrailingByte(thirdByte))
                            {
                                offset += 3;
                                remainingBytes -= 3;
                                utf16CodeUnitCount -= 2; // three UTF-8 code units -> one UTF-16 code unit
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            Debug.Assert(utf16CodeUnitCount >= 0);
            return utf16CodeUnitCount;

            // Error handling logic.

            Error:
            return -1;
        }

        private static int GetIndexOfFirstInvalidUtf8CharEx(ref byte buffer, int length, out int runeCount, out int surrogateCount)
        {
            int tempRuneCount = length;
            int tempSurrogatecount = 0;

            IntPtr offset = IntPtr.Zero;

            // If the sequence is long enough, try running vectorized "is this sequence ASCII?"
            // logic. We perform a small test of the first 16 bytes to make sure they're all
            // ASCII before we incur the cost of invoking the vectorized code path.

            if (IntPtr.Size >= 8 && Vector.IsHardwareAccelerated && length >= 2 * sizeof(ulong) + 2 * Vector<byte>.Count)
            {
                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref buffer) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, sizeof(ulong)));
                if ((thisQWord & 0x8080808080808080UL) == 0UL)
                {
                    offset = (IntPtr)(2 * sizeof(ulong) + GetIndexOfFirstNonAsciiByteVectorized(ref Unsafe.Add(ref buffer, 2 * sizeof(ulong)), length - 2 * sizeof(ulong)));
                }
            }

            IntPtr offsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int remainingBytes = length - IntPtrToInt32NoOverflowCheck(offset);

            while (remainingBytes >= sizeof(uint))
            {
                BeforeInitialDWordRead:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(length - (int)offset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));

                AfterInitialDWordRead:

                // First, check for the common case of all-ASCII bytes.

                if ((thisDWord & 0x80808080U) == 0U)
                {
                    offset += sizeof(uint);
                    remainingBytes -= sizeof(uint);

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.

                    if (IntPtrIsLessThan(offset, offsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeInitialDWordRead; // we think there's non-ASCII data coming, so don't bother loop unrolling
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (remainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset + sizeof(ulong)));

                                if ((thisQWord & 0x8080808080808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 2 * sizeof(ulong); // non-ASCII data incoming
                                    goto BeforeInitialDWordRead;
                                }

                                offset += 2 * sizeof(ulong);
                                remainingBytes -= 2 * sizeof(ulong);
                            }
                        }
                        else
                        {
                            while (remainingBytes >= 4 * sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 2 * sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 3 * sizeof(uint)));

                                if ((thisDWord & 0x80808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 4 * sizeof(uint); // non-ASCII data incoming
                                    goto BeforeInitialDWordRead;
                                }

                                offset += 4 * sizeof(uint);
                                remainingBytes -= 4 * sizeof(uint);
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00000080U : 0x80000000U;
                    if ((thisDWord & mask) == 0U)
                    {
                        offset += 1;
                        remainingBytes--;

                        mask = (BitConverter.IsLittleEndian) ? 0x00008000U : 0x00800000U;
                        if ((thisDWord & mask) == 0U)
                        {
                            offset += 1;
                            remainingBytes--;

                            mask = (BitConverter.IsLittleEndian) ? 0x00800000U : 0x00008000U;
                            if ((thisDWord & mask) == 0U)
                            {
                                offset += 1;
                                remainingBytes--;
                            }
                        }

                        if (remainingBytes < sizeof(uint))
                        {
                            break; // read remainder of data
                        }
                        else
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            // fall through to multi-byte consumption logic
                        }
                    }
                }

                // At this point, we know we're working with a multi-byte code unit,
                // but we haven't yet validated it.

                // The masks and comparands are derived from the Unicode Standard, Table 3-6.
                // Additionally, we need to check for valid byte sequences per Table 3-7.

                // Check the 2-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x0000C0E0U : 0xE0C00000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x000080C0U : 0xC0800000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [ C2..DF ] [ 80..BF ]

                        ProcessTwoByteSequence:

                        if (BitConverter.IsLittleEndian)
                        {
                            if (!IsWithinRangeInclusive(thisDWord & 0xFFU, 0xC2U, 0xDFU)) { goto Error; }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xC2000000U, 0xDF000000U)) { goto Error; }
                        }

                        offset += 2;
                        remainingBytes -= 2;
                        tempRuneCount--; // 2 bytes -> 1 rune

                        // Optimization: Maybe we're processing a language like Hebrew
                        // or Russian, so we should expect to see another two-byte
                        // character immediately after this one.

                        uint innerMask = (BitConverter.IsLittleEndian) ? 0xC0E00000U : 0x0000E0C0U;
                        uint innerComparand = (BitConverter.IsLittleEndian) ? 0x80C00000U : 0x0000C080U;
                        if ((thisDWord & innerMask) == innerComparand)
                        {
                            if (BitConverter.IsLittleEndian)
                            {
                                if (!IsWithinRangeInclusive(thisDWord & 0xFF0000U, 0xC20000U, 0xDF0000U)) { goto Error; }
                            }
                            else
                            {
                                if (!IsWithinRangeInclusive(thisDWord, 0xC200U, 0xDF00U)) { goto Error; }
                            }

                            offset += 2;
                            remainingBytes -= 2;
                            tempRuneCount--; // 2 bytes -> 1 rune

                            if (remainingBytes >= sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                                if ((thisDWord & mask) == comparand)
                                {
                                    goto ProcessTwoByteSequence;
                                }
                                else
                                {
                                    goto AfterInitialDWordRead;
                                }
                            }
                            else
                            {
                                break; // Running out of data - go down slow path
                            }
                        }
                        else
                        {
                            if (remainingBytes >= 2)
                            {
                                if (BitConverter.IsLittleEndian)
                                {
                                    thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset + 2)) << 16);
                                }
                                else
                                {
                                    thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset + 2));
                                }
                                goto AfterInitialDWordRead;
                            }
                            else
                            {
                                break; // Running out of data - go down slow path
                            }
                        }
                    }
                }

                // Check the 3-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00C0C0F0U : 0xF0C0C000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x008080E0U : 0xE0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   E0   ] [ A0..BF ] [ 80..BF ]
                        // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                        // [   ED   ] [ 80..9F ] [ 80..BF ]
                        // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                        ProcessThreeByteSequence:
                        Debug.Assert((thisDWord & mask) == comparand);

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0xA000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xEDU) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xE0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (thisDWord < 0xE0A00000U) { goto Error; }
                            if (IsWithinRangeInclusive(thisDWord, 0xEDA00000U, 0xEE790000U)) { goto Error; }
                        }

                        offset += 3;
                        remainingBytes -= 3;
                        tempRuneCount -= 2; // 3 bytes -> 1 rune

                        // Optimization: If we read a character that consists of three UTF8 code units, we might be
                        // reading Cyrillic or CJK text. Let's optimistically assume that the next character also
                        // consists of three UTF8 code units and short-circuit some of the earlier logic. If this
                        // guess turns out to be incorrect we'll just jump back near the beginning of the loop.

                        // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                        // in to the text. If this happens strip it off now before seeing if the next character
                        // consists of three code units.
                        if ((BitConverter.IsLittleEndian && ((thisDWord >> 31) == 0U)) || (!BitConverter.IsLittleEndian && ((thisDWord & 0x80U) == 0U)))
                        {
                            offset += 1;
                            remainingBytes--;
                        }

                        if (remainingBytes >= sizeof(uint))
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            if ((thisDWord & mask) == comparand)
                            {
                                goto ProcessThreeByteSequence;
                            }
                            else
                            {
                                goto AfterInitialDWordRead;
                            }
                        }
                        else
                        {
                            break; // Running out of bytes; go down slow code path
                        }
                    }
                }

                // Check the 4-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0xC0C0C0F8U : 0xF8C0C0C0U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x808080F0U : 0xF0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                        // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                        // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0x9000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xF4U) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xF0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xF0900000U, 0xF48FFFFFU)) { goto Error; }
                        }

                        offset += 4;
                        remainingBytes -= 4;
                        tempRuneCount -= 3; // 4 bytes -> 1 rune
                        tempSurrogatecount++; // 4 bytes implies UTF16 surrogate
                        continue;
                    }
                }

                // Error - no match.

                goto Error;
            }

            Debug.Assert(remainingBytes < 4);
            while (remainingBytes > 0)
            {
                uint firstByte = Unsafe.Add(ref buffer, offset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    offset += 1;
                    remainingBytes--;
                    continue;
                }
                else if (remainingBytes >= 2)
                {
                    uint secondByte = Unsafe.Add(ref buffer, offset + 1);
                    if (firstByte < 0xE0U)
                    {
                        // 2-byte case
                        if (firstByte >= 0xC2U && IsValidTrailingByte(secondByte))
                        {
                            offset += 2;
                            remainingBytes -= 2;
                            tempRuneCount--; // 2 bytes -> 1 rune
                            continue;
                        }
                    }
                    else if (remainingBytes >= 3)
                    {
                        uint thirdByte = Unsafe.Add(ref buffer, offset + 2);
                        if (firstByte <= 0xF0U)
                        {
                            if (firstByte == 0xE0U)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0xA0U, 0xBFU)) { goto Error; }
                            }
                            else if (firstByte == 0xEDU)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0x80U, 0x9FU)) { goto Error; }
                            }
                            else
                            {
                                if (!IsValidTrailingByte(secondByte)) { goto Error; }
                            }

                            if (IsValidTrailingByte(thirdByte))
                            {
                                offset += 3;
                                remainingBytes -= 3;
                                tempRuneCount -= 2; // 3 bytes -> 1 rune
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            runeCount = tempRuneCount;
            surrogateCount = tempSurrogatecount;
            return -1;

            // Error handling logic.

            Error:
            runeCount = tempRuneCount - (length - IntPtrToInt32NoOverflowCheck(offset));
            surrogateCount = tempSurrogatecount;
            return IntPtrToInt32NoOverflowCheck(offset);
        }

        private static int GetIndexOfFirstInvalidUtf8Char(ref byte buffer, int length)
        {
            IntPtr offset = IntPtr.Zero;

            // If the sequence is long enough, try running vectorized "is this sequence ASCII?"
            // logic. We perform a small test of the first 16 bytes to make sure they're all
            // ASCII before we incur the cost of invoking the vectorized code path.

            if (IntPtr.Size >= 8 && Vector.IsHardwareAccelerated && length >= 2 * sizeof(ulong) + 2 * Vector<byte>.Count)
            {
                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref buffer) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, sizeof(ulong)));
                if ((thisQWord & 0x8080808080808080UL) == 0UL)
                {
                    offset = (IntPtr)(2 * sizeof(ulong) + GetIndexOfFirstNonAsciiByteVectorized(ref Unsafe.Add(ref buffer, 2 * sizeof(ulong)), length - 2 * sizeof(ulong)));
                }
            }

            IntPtr offsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int remainingBytes = length - IntPtrToInt32NoOverflowCheck(offset);

            while (remainingBytes >= sizeof(uint))
            {
                BeforeInitialDWordRead:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(length - (int)offset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));

                AfterInitialDWordRead:

                // First, check for the common case of all-ASCII bytes.

                if ((thisDWord & 0x80808080U) == 0U)
                {
                    offset += sizeof(uint);
                    remainingBytes -= sizeof(uint);

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.

                    if (IntPtrIsLessThan(offset, offsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeInitialDWordRead; // we think there's non-ASCII data coming, so don't bother loop unrolling
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (remainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, offset + sizeof(ulong)));

                                if ((thisQWord & 0x8080808080808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 2 * sizeof(ulong); // non-ASCII data incoming
                                    goto BeforeInitialDWordRead;
                                }

                                offset += 2 * sizeof(ulong);
                                remainingBytes -= 2 * sizeof(ulong);
                            }
                        }
                        else
                        {
                            while (remainingBytes >= 4 * sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 2 * sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset + 3 * sizeof(uint)));

                                if ((thisDWord & 0x80808080U) != 0U)
                                {
                                    offsetAtWhichToAllowUnrolling = offset + 4 * sizeof(uint); // non-ASCII data incoming
                                    goto BeforeInitialDWordRead;
                                }

                                offset += 4 * sizeof(uint);
                                remainingBytes -= 4 * sizeof(uint);
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00000080U : 0x80000000U;
                    if ((thisDWord & mask) == 0U)
                    {
                        offset += 1;
                        remainingBytes--;

                        mask = (BitConverter.IsLittleEndian) ? 0x00008000U : 0x00800000U;
                        if ((thisDWord & mask) == 0U)
                        {
                            offset += 1;
                            remainingBytes--;

                            mask = (BitConverter.IsLittleEndian) ? 0x00800000U : 0x00008000U;
                            if ((thisDWord & mask) == 0U)
                            {
                                offset += 1;
                                remainingBytes--;
                            }
                        }

                        if (remainingBytes < sizeof(uint))
                        {
                            break; // read remainder of data
                        }
                        else
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            // fall through to multi-byte consumption logic
                        }
                    }
                }

                // At this point, we know we're working with a multi-byte code unit,
                // but we haven't yet validated it.

                // The masks and comparands are derived from the Unicode Standard, Table 3-6.
                // Additionally, we need to check for valid byte sequences per Table 3-7.

                // Check the 2-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x0000C0E0U : 0xE0C00000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x000080C0U : 0xC0800000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [ C2..DF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if (!IsWithinRangeInclusive(thisDWord & 0xFFU, 0xC2U, 0xDFU)) { goto Error; }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xC2000000U, 0xDF000000U)) { goto Error; }
                        }

                        offset += 2;
                        remainingBytes -= 2;

                        if (remainingBytes >= 2)
                        {
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset)) << 16);
                            }
                            else
                            {
                                thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref buffer, offset));
                            }
                            goto AfterInitialDWordRead;
                        }
                        else
                        {
                            break; // Running out of bytes; go down slow code path
                        }
                    }
                }

                // Check the 3-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0x00C0C0F0U : 0xF0C0C000U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x008080E0U : 0xE0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   E0   ] [ A0..BF ] [ 80..BF ]
                        // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                        // [   ED   ] [ 80..9F ] [ 80..BF ]
                        // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                        ProcessThreeByteSequence:
                        Debug.Assert((thisDWord & mask) == comparand);

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0xA000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xEDU) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xE0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (thisDWord < 0xE0A00000U) { goto Error; }
                            if (IsWithinRangeInclusive(thisDWord, 0xEDA00000U, 0xEE790000U)) { goto Error; }
                        }

                        offset += 3;
                        remainingBytes -= 3;

                        // Optimization: If we read a character that consists of three UTF8 code units, we might be
                        // reading Cyrillic or CJK text. Let's optimistically assume that the next character also
                        // consists of three UTF8 code units and short-circuit some of the earlier logic. If this
                        // guess turns out to be incorrect we'll just jump back near the beginning of the loop.

                        // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                        // in to the text. If this happens strip it off now before seeing if the next character
                        // consists of three code units.
                        if ((BitConverter.IsLittleEndian && ((thisDWord >> 31) == 0U)) || (!BitConverter.IsLittleEndian && ((thisDWord & 0x80U) == 0U)))
                        {
                            offset += 1;
                            remainingBytes--;
                        }

                        if (remainingBytes >= sizeof(uint))
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, offset));
                            if ((thisDWord & mask) == comparand)
                            {
                                goto ProcessThreeByteSequence;
                            }
                            else
                            {
                                goto AfterInitialDWordRead;
                            }
                        }
                        else
                        {
                            break; // Running out of bytes; go down slow code path
                        }
                    }
                }

                // Check the 4-byte case.

                {
                    uint mask = (BitConverter.IsLittleEndian) ? 0xC0C0C0F8U : 0xF8C0C0C0U;
                    uint comparand = (BitConverter.IsLittleEndian) ? 0x808080F0U : 0xF0808000U;
                    if ((thisDWord & mask) == comparand)
                    {
                        // Per Table 3-7, valid sequences are:
                        // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                        // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                        // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                        if (BitConverter.IsLittleEndian)
                        {
                            if ((thisDWord & 0xFFFFU) >= 0x9000U)
                            {
                                if ((thisDWord & 0xFFU) == 0xF4U) { goto Error; }
                            }
                            else
                            {
                                if ((thisDWord & 0xFFU) == 0xF0U) { goto Error; }
                            }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xF0900000U, 0xF48FFFFFU)) { goto Error; }
                        }

                        offset += 4;
                        remainingBytes -= 4;
                        continue;
                    }
                }

                // Error - no match.

                goto Error;
            }

            Debug.Assert(remainingBytes < 4);
            while (remainingBytes > 0)
            {
                uint firstByte = Unsafe.Add(ref buffer, offset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    offset += 1;
                    remainingBytes--;
                    continue;
                }
                else if (remainingBytes >= 2)
                {
                    uint secondByte = Unsafe.Add(ref buffer, offset + 1);
                    if (firstByte < 0xE0U)
                    {
                        // 2-byte case
                        if (firstByte >= 0xC2U && IsValidTrailingByte(secondByte))
                        {
                            offset += 2;
                            remainingBytes -= 2;
                            continue;
                        }
                    }
                    else if (remainingBytes >= 3)
                    {
                        uint thirdByte = Unsafe.Add(ref buffer, offset + 2);
                        if (firstByte <= 0xF0U)
                        {
                            if (firstByte == 0xE0U)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0xA0U, 0xBFU)) { goto Error; }
                            }
                            else if (firstByte == 0xEDU)
                            {
                                if (!IsWithinRangeInclusive(secondByte, 0x80U, 0x9FU)) { goto Error; }
                            }
                            else
                            {
                                if (!IsValidTrailingByte(secondByte)) { goto Error; }
                            }

                            if (IsValidTrailingByte(thirdByte))
                            {
                                offset += 3;
                                remainingBytes -= 3;
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            return -1;

            // Error handling logic.

            Error:
            return IntPtrToInt32NoOverflowCheck(offset);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe static int GetIndexOfFirstNonAsciiByteVectorized(ref byte buffer, int length)
        {
            if (Vector.IsHardwareAccelerated && length >= 2 * Vector<byte>.Count)
            {
                IntPtr numBytesConsumed = IntPtr.Zero;
                int numBytesToConsumeBeforeAligned = (Vector<byte>.Count - ((int)Unsafe.AsPointer(ref buffer) % Vector<byte>.Count)) % Vector<byte>.Count;

                if (length - numBytesToConsumeBeforeAligned < 2 * Vector<byte>.Count)
                {
                    return 0; // after alignment, not enough data remaining to justify overhead of setting up vectorized search path
                }

                if (IntPtr.Size >= sizeof(ulong))
                {
                    while (numBytesToConsumeBeforeAligned >= sizeof(ulong))
                    {
                        if ((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, numBytesConsumed)) & 0x8080808080808080UL) != 0UL)
                        {
                            return IntPtrToInt32NoOverflowCheck(numBytesConsumed); // found a high bit set somewhere
                        }
                        numBytesConsumed += sizeof(ulong);
                        numBytesToConsumeBeforeAligned -= sizeof(ulong);
                    }

                    if (numBytesToConsumeBeforeAligned >= sizeof(uint))
                    {
                        if ((Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, numBytesConsumed)) & 0x80808080U) != 0U)
                        {
                            return IntPtrToInt32NoOverflowCheck(numBytesConsumed); // found a high bit set somewhere
                        }
                        numBytesConsumed += sizeof(uint);
                        numBytesToConsumeBeforeAligned -= sizeof(uint);
                    }
                }
                else
                {
                    while (numBytesToConsumeBeforeAligned >= sizeof(uint))
                    {
                        if ((Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, numBytesConsumed)) & 0x80808080U) != 0U)
                        {
                            return IntPtrToInt32NoOverflowCheck(numBytesConsumed); // found a high bit set somewhere
                        }
                        numBytesConsumed += sizeof(uint);
                        numBytesToConsumeBeforeAligned -= sizeof(uint);
                    }
                }

                Debug.Assert(numBytesToConsumeBeforeAligned < 4);

                while (numBytesToConsumeBeforeAligned-- != 0)
                {
                    if (((uint)Unsafe.Add(ref buffer, numBytesConsumed) & 0x80U) != 0U)
                    {
                        return IntPtrToInt32NoOverflowCheck(numBytesConsumed); // found a high bit set here
                    }
                    numBytesConsumed += 1;
                }

                // At this point, we're properly aligned to begin a fast vectorized search!

                IntPtr numBytesRemaining = (IntPtr)(length - IntPtrToInt32NoOverflowCheck(numBytesConsumed));
                Debug.Assert(length - (int)numBytesConsumed > 2 * Vector<byte>.Count); // this invariant was checked at beginning of method

                var highBitMask = new Vector<byte>(0x80);

                do
                {
                    // Read two vector lines at a time.
                    if (((Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref buffer, numBytesConsumed)) | Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref buffer, numBytesConsumed + Vector<byte>.Count))) & highBitMask) != Vector<byte>.Zero)
                    {
                        break; // found a non-ascii character somewhere in this vector
                    }

                    numBytesConsumed += 2 * Vector<byte>.Count;
                    numBytesRemaining -= 2 * Vector<byte>.Count;
                } while (IntPtrToInt32NoOverflowCheck(numBytesRemaining) > 2 * Vector<byte>.Count);

                return IntPtrToInt32NoOverflowCheck(numBytesConsumed);
            }
            else
            {
                return 0; // can't vectorize the search
            }
        }
    }
}
