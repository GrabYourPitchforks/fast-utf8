using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace ConsoleApp3
{
    static class Utf8Util
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
        public static void ConvertUtf8ToUtf16(ReadOnlySpan<byte> utf8, Span<char> utf16)
        {
            ConvertUtf8ToUtf16Ex(ref utf8.DangerousGetPinnableReference(), utf8.Length, ref utf16.DangerousGetPinnableReference(), utf16.Length, out _);
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

                if ((thisDWord & 0x80808080U) == 0U)
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

        private static int ConvertUtf8ToUtf16Ex(ref byte inputBuffer, int inputLength, ref char outputBuffer, int outputLength, out int numCharsWritten)
        {
            IntPtr inputBufferCurrentOffset = IntPtr.Zero;
            IntPtr inputBufferOffsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int inputBufferRemainingBytes = inputLength - IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);

            IntPtr outputBufferCurrentOffset = IntPtr.Zero;
            int remainingOutputBufferSize = outputLength;

            while (inputBufferRemainingBytes >= sizeof(uint))
            {
                BeforeInitialDWordRead:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(inputLength - (int)inputBufferCurrentOffset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                AfterInitialDWordRead:

                // First, check for the common case of all-ASCII bytes.

                if (remainingOutputBufferSize >= 4 && (thisDWord & 0x80808080U) == 0U)
                {
                    inputBufferCurrentOffset += sizeof(uint);
                    inputBufferRemainingBytes -= sizeof(uint);

                    ulong thisQWord = thisDWord;
                    if (Bmi2.IsSupported)
                    {
                        thisQWord = Bmi2.ParallelBitDeposit(thisQWord, 0x00FF00FF00FF00FFUL);
                    }
                    else
                    {
                        thisQWord = ((thisQWord & 0xFF000000UL) << 24)
                            | ((thisQWord & 0xFF0000UL) << 16)
                            | ((thisQWord & 0xFF00UL) << 8)
                            | (thisQWord & 0xFFUL);
                    }

                    Unsafe.WriteUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), thisQWord);
                    outputBufferCurrentOffset += 4;
                    remainingOutputBufferSize -= 4;

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Let's try performing a vectorized widening operation.

                    if (IntPtrIsLessThan(inputBufferCurrentOffset, inputBufferOffsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeInitialDWordRead; // we think there's non-ASCII data coming, so don't bother loop unrolling
                    }
                    else if (Vector.IsHardwareAccelerated)
                    {
                        while (inputBufferRemainingBytes >= Vector<byte>.Count && remainingOutputBufferSize >= Vector<byte>.Count)
                        {
                            var inputVector = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                            if ((inputVector & new Vector<byte>(0x80)) == Vector<byte>.Zero)
                            {
                                Vector.Widen(inputVector,
                                    dest1: out Unsafe.As<char, Vector<ushort>>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)),
                                    dest2: out Unsafe.As<char, Vector<ushort>>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset + Vector<ushort>.Count)));

                                inputBufferCurrentOffset += Vector<byte>.Count;
                                inputBufferRemainingBytes -= Vector<byte>.Count;
                                outputBufferCurrentOffset += Vector<byte>.Count;
                                remainingOutputBufferSize -= Vector<byte>.Count;
                            }
                            else
                            {
                                inputBufferOffsetAtWhichToAllowUnrolling = inputBufferCurrentOffset + Vector<byte>.Count; // saw non-ASCII data later in the stream
                                goto BeforeInitialDWordRead;
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.

                {
                    if (Utf8DWordFirstByteIsAscii(thisDWord))
                    {
                        if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes--;
                        if (BitConverter.IsLittleEndian)
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(thisDWord & 0xFFU);
                        }
                        else
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(thisDWord >> 24);
                        }
                        outputBufferCurrentOffset += 1;
                        remainingOutputBufferSize -= 1;

                        if (Utf8DWordSecondByteIsAscii(thisDWord))
                        {
                            if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                            inputBufferCurrentOffset += 1;
                            inputBufferRemainingBytes--;
                            if (BitConverter.IsLittleEndian)
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)((thisDWord >> 8) & 0xFFU);
                            }
                            else
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)((thisDWord >> 16) & 0xFFU);
                            }
                            outputBufferCurrentOffset += 1;
                            remainingOutputBufferSize -= 1;

                            if (Utf8DWordThirdByteIsAscii(thisDWord))
                            {
                                if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                                inputBufferCurrentOffset += 1;
                                inputBufferRemainingBytes--;
                                if (BitConverter.IsLittleEndian)
                                {
                                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)((thisDWord >> 16) & 0xFFU);
                                }
                                else
                                {
                                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)((thisDWord >> 8) & 0xFFU);
                                }
                                outputBufferCurrentOffset += 1;
                                remainingOutputBufferSize -= 1;
                            }
                        }

                        if (inputBufferRemainingBytes < sizeof(uint))
                        {
                            break; // read remainder of data
                        }
                        else
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
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
                    if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                    {
                        // Per Table 3-7, valid sequences are:
                        // [ C2..DF ] [ 80..BF ]

                        ProcessTwoByteDWordTryConsumeMany:

                        // Optimization: If this is a two-byte-per-character language like Cyrillic or Hebrew,
                        // there's a good chance that if we see one two-byte run then there are more two-byte
                        // runs that follow it. Let's check that now.

                        if (Utf8DWordEndsWithTwoByteMask(thisDWord))
                        {
                            // At this point, we know we have two runs of two bytes each.
                            // Can we extend this to four runs of two bytes each?

                            if (inputBufferRemainingBytes >= sizeof(uint))
                            {
                                uint nextDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 4));
                                if (Utf8DWordBeginsAndEndsWithTwoByteMask(nextDWord))
                                {
                                    // We have four runs of two bytes each. Shift the two DWORDs
                                    // together to make a run of 4 wide characters.

                                    ulong thisQWord = thisDWord;
                                    if (BitConverter.IsLittleEndian)
                                    {
                                        thisQWord |= ((ulong)nextDWord) << 32;
                                    }
                                    else
                                    {
                                        thisQWord = ((thisQWord) << 32) | (ulong)nextDWord;
                                    }

                                    // At this point, thisQWord = [ 110aaaaa10bbbbbb 110ccccc10dddddd ... ]
                                    // We want to remove the "110" and "10" headers from each byte.

                                    if (BitConverter.IsLittleEndian)
                                    {
                                        // At this point, thisDWord = [ 10dddddd110ccccc 10bbbbbb110aaaaa ]
                                        // We want thisDWord = [ 00000cccccdddddd 00000aaaaabbbbbb ]

                                        // TODO: BMI2 SUPPORT
                                        thisQWord = ((thisQWord & 0x001F001F001F001FUL) << 6) | ((thisQWord & 0x3F003F003F003F00UL) >> 8);
                                    }
                                    else
                                    {
                                        throw new NotImplementedException();
                                    }

                                    // Now that we have a run of 4 wide characters, make sure each character has a scalar value of >= U+0080.
                                    // Note: non-short-circuiting AND below (optimize for valid data, not worried about performance for invalid data)

                                    bool firstCharIsValid = (thisQWord >= 0x0080000000000000UL);
                                    bool secondCharIsValid = ((thisQWord & 0x0000FF8000000000UL) != 0UL);
                                    bool thirdCharIsValid = ((thisQWord & 0x00000000FF800000UL) != 0UL);
                                    bool fourthCharIsValid = ((thisQWord & 0x000000000000FF80UL) != 0UL);
                                    bool allCharsAreValid = firstCharIsValid & secondCharIsValid & thirdCharIsValid & fourthCharIsValid;

                                    if (allCharsAreValid)
                                    {
                                        inputBufferCurrentOffset += 8;
                                        inputBufferRemainingBytes -= 8;
                                        Unsafe.WriteUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), thisQWord);
                                        outputBufferCurrentOffset += 4;
                                        remainingOutputBufferSize -= 4;

                                        if (inputBufferRemainingBytes >= sizeof(uint))
                                        {
                                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                                            // Optimization: If we read a long run of two-byte sequences, the next sequence is probably
                                            // also two bytes. Check for that first before going back to the beginning of the loop.
                                            if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                                            {
                                                goto ProcessTwoByteDWordTryConsumeMany;
                                            }
                                            else
                                            {
                                                goto AfterInitialDWordRead;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // Invalid sequence incoming! Try consuming only the first two bytes instead of pipelining.
                                        // We'll eventually report the error.
                                        goto ProcessTwoByteDWordDoNotTryConsumeMany;
                                    }
                                }
                                else
                                {
                                    // We have two runs of two bytes each. Shift the two WORDs
                                    // together to make a run of 2 wide characters.

                                    if (BitConverter.IsLittleEndian)
                                    {
                                        // At this point, thisDWord = [ 10dddddd110ccccc 10bbbbbb110aaaaa ]
                                        // We want thisDWord = [ 00000cccccdddddd 00000aaaaabbbbbb ]

                                        // TODO: BMI2 SUPPORT
                                        thisDWord = ((thisDWord & 0x001F001FU) << 6) | ((thisDWord & 0x3F003F00U) >> 8);
                                    }
                                    else
                                    {
                                        throw new NotImplementedException();
                                    }

                                    // Now that we have a run of 2 wide characters, make sure each character has a scalar value of >= U+0080.
                                    // Note: non-short-circuiting AND below (optimize for valid data, not worried about performance for invalid data)

                                    bool firstCharIsValid = (thisDWord >= 0x00800000U);
                                    bool secondCharIsValid = ((thisDWord & 0x0000FF80U) != 0U);
                                    bool allCharsAreValid = firstCharIsValid & secondCharIsValid;

                                    if (allCharsAreValid)
                                    {
                                        inputBufferCurrentOffset += 4;
                                        inputBufferRemainingBytes -= 4;
                                        Unsafe.WriteUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), thisDWord);
                                        outputBufferCurrentOffset += 2;
                                        remainingOutputBufferSize -= 2;

                                        // We've already read the next DWORD, and we know it's not a two-byte sequence,
                                        // so go to the beginning of the loop.

                                        thisDWord = nextDWord;
                                        goto AfterInitialDWordRead;
                                    }
                                    else
                                    {
                                        // Invalid sequence incoming! Try consuming only the first two bytes instead of pipelining.
                                        // We'll eventually report the error.
                                        goto ProcessTwoByteDWordDoNotTryConsumeMany;
                                    }
                                }
                            }
                        }

                        ProcessTwoByteSequence:
                        ProcessTwoByteDWordDoNotTryConsumeMany:

                        if (BitConverter.IsLittleEndian)
                        {
                            if (!IsWithinRangeInclusive(thisDWord & 0xFFU, 0xC2U, 0xDFU)) { goto Error; }
                        }
                        else
                        {
                            if (!IsWithinRangeInclusive(thisDWord, 0xC2000000U, 0xDF000000U)) { goto Error; }
                        }

                        if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                        inputBufferCurrentOffset += 2;
                        inputBufferRemainingBytes -= 2;
                        if (BitConverter.IsLittleEndian)
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x1FU) << 6) | ((thisDWord & 0x3F00U) >> 8));
                        }
                        else
                        {
                            if (Bmi2.IsSupported)
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)Bmi2.ParallelBitExtract(thisDWord, 0x1F3F0000U);
                            }
                            else
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x1F000000U) >> 18) | ((thisDWord & 0x3F0000U) >> 16));
                            }
                        }
                        outputBufferCurrentOffset += 1;
                        remainingOutputBufferSize -= 1;

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

                            if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                            inputBufferCurrentOffset += 2;
                            inputBufferRemainingBytes -= 2;
                            if (BitConverter.IsLittleEndian)
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x1F0000U) >> 10) | ((thisDWord & 0x3F000000U) >> 24));
                            }
                            else
                            {
                                if (Bmi2.IsSupported)
                                {
                                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)Bmi2.ParallelBitExtract(thisDWord, 0x1F3FU);
                                }
                                else
                                {
                                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x1F00U) >> 2) | (thisDWord & 0x3FU));
                                }
                            }
                            outputBufferCurrentOffset += 1;
                            remainingOutputBufferSize -= 1;

                            if (inputBufferRemainingBytes >= sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                                if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
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
                            if (inputBufferRemainingBytes >= 2)
                            {
                                if (BitConverter.IsLittleEndian)
                                {
                                    thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2)) << 16);
                                }
                                else
                                {
                                    thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2));
                                }
                                goto AfterInitialDWordRead;
                            }
                            else
                            {
                                break; // Running out of data - go down slow path
                            }
                        }

                        if (inputBufferRemainingBytes >= 2)
                        {
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord = (thisDWord >> 16) | ((uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2)) << 16);
                            }
                            else
                            {
                                thisDWord = (thisDWord << 16) | (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2));
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

                        if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                        inputBufferCurrentOffset += 3;
                        inputBufferRemainingBytes -= 3;
                        if (BitConverter.IsLittleEndian)
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x0FU) << 12) | ((thisDWord & 0x3F00U) >> 2) | ((thisDWord & 0x3F0000U) >> 16));
                        }
                        else
                        {
                            if (Bmi2.IsSupported)
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)Bmi2.ParallelBitExtract(thisDWord, 0x0F3F3FU);
                            }
                            else
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(((thisDWord & 0x0F0000U) >> 4) | ((thisDWord & 0x3F00U) >> 2) | (thisDWord & 0x3FU));
                            }
                        }
                        outputBufferCurrentOffset += 1;
                        remainingOutputBufferSize -= 1;

                        // Optimization: If we read a character that consists of three UTF8 code units, we might be
                        // reading Cyrillic or CJK text. Let's optimistically assume that the next character also
                        // consists of three UTF8 code units and short-circuit some of the earlier logic. If this
                        // guess turns out to be incorrect we'll just jump back near the beginning of the loop.

                        // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                        // in to the text. If this happens strip it off now before seeing if the next character
                        // consists of three code units.
                        if ((BitConverter.IsLittleEndian && ((thisDWord >> 31) == 0U)) || (!BitConverter.IsLittleEndian && ((thisDWord & 0x80U) == 0U)))
                        {
                            if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                            inputBufferCurrentOffset += 1;
                            inputBufferRemainingBytes--;
                            if (BitConverter.IsLittleEndian)
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(thisDWord >> 24);
                            }
                            else
                            {
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(thisDWord & 0xFFU);
                            }
                            outputBufferCurrentOffset += 1;
                            remainingOutputBufferSize--;
                        }

                        if (inputBufferRemainingBytes >= sizeof(uint))
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
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

                        if (remainingOutputBufferSize < 2) { goto OutputBufferTooSmall; }
                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        Unsafe.WriteUnaligned(
                            destination: ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)),
                            value: GenerateUtf16CodeUnitsFromFourUtf8CodeUnits(thisDWord));
                        outputBufferCurrentOffset += 2;
                        remainingOutputBufferSize -= 2;
                        continue;
                    }
                }

                // Error - no match.

                goto Error;
            }

            Debug.Assert(inputBufferRemainingBytes < 4);
            while (inputBufferRemainingBytes > 0)
            {
                if (remainingOutputBufferSize == 0)
                {
                    goto OutputBufferTooSmall;
                }

                uint firstByte = Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    inputBufferCurrentOffset += 1;
                    inputBufferRemainingBytes--;
                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)firstByte;
                    outputBufferCurrentOffset += 1;
                    remainingOutputBufferSize -= 1;
                    continue;
                }
                else if (inputBufferRemainingBytes >= 2)
                {
                    uint secondByte = Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 1);
                    if (firstByte < 0xE0U)
                    {
                        // 2-byte case
                        if (firstByte >= 0xC2U && IsValidTrailingByte(secondByte))
                        {
                            inputBufferCurrentOffset += 2;
                            inputBufferRemainingBytes -= 2;
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)GenerateUtf16CodeUnitFromUtf8CodeUnits(firstByte, secondByte);
                            outputBufferCurrentOffset += 1;
                            remainingOutputBufferSize -= 1;
                            continue;
                        }
                    }
                    else if (inputBufferRemainingBytes >= 3)
                    {
                        uint thirdByte = Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2);
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
                                inputBufferCurrentOffset += 3;
                                inputBufferRemainingBytes -= 3;
                                Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)GenerateUtf16CodeUnitFromUtf8CodeUnits(firstByte, secondByte, thirdByte);
                                outputBufferCurrentOffset += 1;
                                remainingOutputBufferSize -= 1;
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            numCharsWritten = IntPtrToInt32NoOverflowCheck(outputBufferCurrentOffset);
            return IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);

            // Error handling logic.

            OutputBufferTooSmall:
            throw new NotImplementedException();

            Error:
            throw new NotImplementedException();
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
        private static bool IsWithinRangeInclusive(uint value, uint lowerBound, uint upperBound) => ((value - lowerBound) <= (value - upperBound));

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
                if (Bmi2.IsSupported)
                {
                    uint retVal = Bmi2.ParallelBitDeposit(Bmi2.ParallelBitExtract(utf8, 0x0F3F3F00U), 0x03FF03FFU); // retVal = [ 00000uuuuuzzzzyy 000000yyyyxxxxxx ]
                    retVal -= 0x4000U; // retVal = [ 000000wwwwzzzzyy 000000yyyyxxxxxx ]
                    retVal += 0xD800DC00U; // retVal = [ 110110wwwwzzzzyy 110111yyyyxxxxxx ]
                    return retVal;
                }
                else
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
            if (BitConverter.IsLittleEndian)
            {
                const uint mask = 0x0000C0E0U;
                const uint comparand = 0x000080C0U;
                return ((value & mask) == comparand);
            }
            else
            {
                const uint mask = 0xE0C00000U;
                const uint comparand = 0xC0800000U;
                return ((value & mask) == comparand);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordEndsWithTwoByteMask(uint value)
        {
            if (BitConverter.IsLittleEndian)
            {
                const uint mask = 0xC0E00000U;
                const uint comparand = 0x80C00000U;
                return ((value & mask) == comparand);
            }
            else
            {
                const uint mask = 0x0000E0C0U;
                const uint comparand = 0x0000C080U;
                return ((value & mask) == comparand);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordBeginsAndEndsWithTwoByteMask(uint value)
        {
            if (BitConverter.IsLittleEndian)
            {
                const uint mask = 0xC0E0C0E0U;
                const uint comparand = 0x80C080C0U;
                return ((value & mask) == comparand);
            }
            else
            {
                const uint mask = 0xE0C0E0C0U;
                const uint comparand = 0xC080C080U;
                return ((value & mask) == comparand);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordFirstByteIsAscii(uint value)
        {
            if (BitConverter.IsLittleEndian)
            {
                return ((value & 0x80U) == 0U);
            }
            else
            {
                return ((int)value >= 0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordSecondByteIsAscii(uint value)
        {
            if (BitConverter.IsLittleEndian)
            {
                return ((value & 0x8000U) == 0U);
            }
            else
            {
                return ((value & 0x800000U) == 0U);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Utf8DWordThirdByteIsAscii(uint value)
        {
            if (BitConverter.IsLittleEndian)
            {
                return ((value & 0x800000U) == 0U);
            }
            else
            {
                return ((value & 0x8000U) == 0U);
            }
        }
    }
}
