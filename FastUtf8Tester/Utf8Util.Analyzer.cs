using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        private static int GetIndexOfFirstInvalidUtf8CharCore(ref byte inputBuffer, int inputLength, out int runeCount, out int surrogateCount)
        {
            // The fields below control where we read from the buffer.

            IntPtr inputBufferCurrentOffset = IntPtr.Zero;
            int tempRuneCount = inputLength;
            int tempSurrogatecount = 0;

            // If the sequence is long enough, try running vectorized "is this sequence ASCII?"
            // logic. We perform a small test of the first 16 bytes to make sure they're all
            // ASCII before we incur the cost of invoking the vectorized code path.

            if (IntPtr.Size >= 8 && Vector.IsHardwareAccelerated && inputLength >= 2 * sizeof(ulong) + 2 * Vector<byte>.Count)
            {
                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref inputBuffer) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputBuffer, sizeof(ulong)));
                if ((thisQWord & 0x8080808080808080UL) == 0UL)
                {
                    inputBufferCurrentOffset = (IntPtr)(2 * sizeof(ulong) + GetIndexOfFirstNonAsciiByteVectorized(ref Unsafe.Add(ref inputBuffer, 2 * sizeof(ulong)), inputLength - 2 * sizeof(ulong)));
                }
            }

            IntPtr inputBufferOffsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int inputBufferRemainingBytes = inputLength - IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);

            // Begin the main loop.

            while (inputBufferRemainingBytes >= sizeof(uint))
            {
                BeforeReadNextDWord:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(inputLength - (int)inputBufferCurrentOffset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                AfterReadNextDWord:

                // First, check for the common case of all-ASCII bytes.

                if (Utf8DWordAllBytesAreAscii(thisDWord))
                {
                    // We read an all-ASCII sequence.

                    inputBufferCurrentOffset += 4;
                    inputBufferRemainingBytes -= 4;

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.

                    if (IntPtrIsLessThan(inputBufferCurrentOffset, inputBufferOffsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeReadNextDWord; // we think there's non-ASCII data coming, so don't bother loop unrolling
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (inputBufferRemainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + sizeof(ulong)));

                                if ((thisQWord & 0x8080808080808080U) != 0U)
                                {
                                    inputBufferOffsetAtWhichToAllowUnrolling = inputBufferCurrentOffset + 2 * sizeof(ulong); // non-ASCII data incoming
                                    goto BeforeReadNextDWord;
                                }

                                inputBufferCurrentOffset += 2 * sizeof(ulong);
                                inputBufferRemainingBytes -= 2 * sizeof(ulong);
                            }
                        }
                        else
                        {
                            while (inputBufferRemainingBytes >= 4 * sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 2 * sizeof(uint)))
                                    | Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + 3 * sizeof(uint)));

                                if ((thisDWord & 0x80808080U) != 0U)
                                {
                                    inputBufferOffsetAtWhichToAllowUnrolling = inputBufferCurrentOffset + 4 * sizeof(uint); // non-ASCII data incoming
                                    goto BeforeReadNextDWord;
                                }

                                inputBufferCurrentOffset += 4 * sizeof(uint);
                                inputBufferRemainingBytes -= 4 * sizeof(uint);
                            }
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.
                // We only handle up to three ASCII bytes here since we handled the four ASCII byte case above.

                if (Utf8DWordFirstByteIsAscii(thisDWord))
                {
                    inputBufferCurrentOffset += 1;
                    inputBufferRemainingBytes--;
                    if (Utf8DWordSecondByteIsAscii(thisDWord))
                    {
                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes--;
                        if (Utf8DWordThirdByteIsAscii(thisDWord))
                        {
                            inputBufferCurrentOffset += 1;
                            inputBufferRemainingBytes--;
                        }
                    }

                    if (inputBufferRemainingBytes < sizeof(uint))
                    {
                        goto ProcessRemainingBytesSlow; // Input buffer doesn't contain enough data to read a DWORD
                    }
                    else
                    {
                        // The input buffer at the current offset contains a non-ASCII byte.
                        // Read an entire DWORD and fall through to multi-byte consumption logic.
                        thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                    }
                }

                // At this point, we know we're working with a multi-byte code unit,
                // but we haven't yet validated it.

                // The masks and comparands are derived from the Unicode Standard, Table 3-6.
                // Additionally, we need to check for valid byte sequences per Table 3-7.

                // Check the 2-byte case.

                if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                {
                    // Per Table 3-7, valid sequences are:
                    // [ C2..DF ] [ 80..BF ]

                    ProcessTwoByteSequence:

                    if (!IsFirstWordWellFormedTwoByteSequence(thisDWord)) { goto Error; }

                    // Optimization: If this is a two-byte-per-character language like Cyrillic or Hebrew,
                    // there's a good chance that if we see one two-byte run then there's another two-byte
                    // run immediately after. Let's check that now.

                    if (Utf8DWordEndsWithTwoByteMask(thisDWord) && IsSecondWordWellFormedTwoByteSequence(thisDWord))
                    {
                        // We have two runs of two bytes each.
                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        tempRuneCount -= 2; // 4 bytes -> 2 runes

                        if (inputBufferRemainingBytes >= sizeof(uint))
                        {
                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                            // Optimization: If we read a long run of two-byte sequences, the next sequence is probably
                            // also two bytes. Check for that first before going back to the beginning of the loop.
                            if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                            {
                                goto ProcessTwoByteSequence;
                            }
                            else
                            {
                                goto AfterReadNextDWord;
                            }
                        }
                        else
                        {
                            break; // Running out of data - go down slow path
                        }
                    }
                    else
                    {
                        // We have only one run of two bytes. The next two bytes aren't a two-byte character,
                        // so we'll just jump straight back to the beginning of the loop.

                        inputBufferCurrentOffset += 2;
                        inputBufferRemainingBytes -= 2;
                        tempRuneCount--; // 2 bytes -> 1 rune
                        continue;
                    }
                }

                // Check the 3-byte case.

                if (Utf8DWordBeginsWithThreeByteMask(thisDWord))
                {
                    // Per Table 3-7, valid sequences are:
                    // [   E0   ] [ A0..BF ] [ 80..BF ]
                    // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                    // [   ED   ] [ 80..9F ] [ 80..BF ]
                    // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                    ProcessThreeByteSequence:
                    Debug.Assert(Utf8DWordBeginsWithThreeByteMask(thisDWord));

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

    }
}
