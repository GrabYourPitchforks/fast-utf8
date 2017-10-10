using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetIndexOfFirstInvalidByte(ReadOnlySpan<byte> utf8)
        {
            return GetIndexOfFirstInvalidUtf8CharCore(ref utf8.DangerousGetPinnableReference(), utf8.Length, out _, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetRuneCount(ReadOnlySpan<byte> utf8)
        {
            return (GetIndexOfFirstInvalidUtf8CharCore(ref utf8.DangerousGetPinnableReference(), utf8.Length, out int runeCount, out _) < 0)
                ? runeCount
                : throw new ArgumentException(
                    message: "Invalid data.",
                    paramName: nameof(utf8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetUtf16CharCount(ReadOnlySpan<byte> utf8)
        {
            return (GetIndexOfFirstInvalidUtf8CharCore(ref utf8.DangerousGetPinnableReference(), utf8.Length, out int runeCount, out int surrogateCount) < 0)
                ? runeCount + surrogateCount /* don't need checked addition since UTF16 char count can never exceed input byte count */
                : throw new ArgumentException(
                    message: "Invalid data.",
                    paramName: nameof(utf8));
        }

        public static int GetUtf16CharCountWithFallback(ReadOnlySpan<byte> utf8)
        {
            int totalUtf16CharCount = 0;
            int offset = 0;
            while (true)
            {
                int indexOfFirstInvalidByte = GetIndexOfFirstInvalidUtf8CharCore(
                    ref Unsafe.Add(ref utf8.DangerousGetPinnableReference(), offset),
                    inputLength: utf8.Length - offset,
                    runeCount: out int runeCount,
                    surrogateCount: out int surrogateCount);

                int thisIterUtf16CharCount = runeCount + surrogateCount; // guaranteed no overflow
                checked { totalUtf16CharCount += thisIterUtf16CharCount; } // but this might overflow due to error handling

                if (indexOfFirstInvalidByte < 0)
                {
                    return totalUtf16CharCount; // end of data
                }
                else
                {
                    offset += indexOfFirstInvalidByte;
                    int numInvalidBytesToReplace = GetInvalidByteCount(ref Unsafe.Add(ref utf8.DangerousGetPinnableReference(), offset), utf8.Length - offset);
                    checked { totalUtf16CharCount++; } // pretend we're writing out U+FFFD
                    offset += numInvalidBytesToReplace;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidUtf8Sequence(ReadOnlySpan<byte> buffer)
        {
            return GetIndexOfFirstInvalidByte(buffer) < 0;
        }

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
                    if (Utf8DWordSecondByteIsAscii(thisDWord))
                    {
                        if (Utf8DWordThirdByteIsAscii(thisDWord))
                        {
                            inputBufferCurrentOffset += 3;
                            inputBufferRemainingBytes -= 3;
                        }
                        else
                        {
                            inputBufferCurrentOffset += 2;
                            inputBufferRemainingBytes -= 2;
                        }
                    }
                    else
                    {
                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes--;
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

                    if (!IsFirstWordWellFormedTwoByteSequence(thisDWord)) { goto Error; }

                    // Optimization: If this is a two-byte-per-character language like Cyrillic or Hebrew,
                    // there's a good chance that if we see one two-byte run then there's another two-byte
                    // run immediately after. Let's check that now.

                    if (Utf8DWordEndsWithTwoByteMask(thisDWord) && IsSecondWordWellFormedTwoByteSequence(thisDWord))
                    {
                        ConsumeDualKnownGoodRunsOfTwoBytes:

                        // We have two runs of two bytes each.
                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        tempRuneCount -= 2; // 4 bytes -> 2 runes

                        if (inputBufferRemainingBytes >= sizeof(uint))
                        {
                            // Optimization: If we read a long run of two-byte sequences, the next sequence is probably
                            // also two bytes. Check for that first before going back to the beginning of the loop.

                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                            if (Utf8DWordBeginsAndEndsWithTwoByteMask(thisDWord))
                            {
                                if (IsFirstWordWellFormedTwoByteSequence(thisDWord) && IsSecondWordWellFormedTwoByteSequence(thisDWord))
                                {
                                    // Validated next bytes are 2x 2-byte sequences
                                    goto ConsumeDualKnownGoodRunsOfTwoBytes;
                                }
                                else
                                {
                                    // Mask said it was 2x 2-byte sequences but validation failed, go to beginning of loop for error handling
                                    goto AfterReadNextDWord;
                                }
                            }
                            else if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                            {
                                if (IsFirstWordWellFormedTwoByteSequence(thisDWord))
                                {
                                    // Validated next bytes are a single 2-byte sequence with no valid 2-byte sequence following
                                    goto ConsumeSingleKnownGoodRunOfTwoBytes;
                                }
                                else
                                {
                                    // Mask said it was a 2-byte sequence but validation failed, go to beginning of loop for error handling
                                    goto AfterReadNextDWord;
                                }
                            }
                            else
                            {
                                // Next bytes aren't a 2-byte sequence, go to beginning of loop for processing
                                goto AfterReadNextDWord;
                            }
                        }
                        else
                        {
                            break; // Running out of data - go down slow path
                        }
                    }

                    ConsumeSingleKnownGoodRunOfTwoBytes:

                    // We have only one run of two bytes. The next two bytes aren't a two-byte character,
                    // so we'll just jump straight back to the beginning of the loop.

                    inputBufferCurrentOffset += 2;
                    inputBufferRemainingBytes -= 2;
                    tempRuneCount--; // 2 bytes -> 1 rune
                    continue;
                }

                // Check the 3-byte case.

                BeforeProcessThreeByteSequence:

                if (Utf8DWordBeginsWithThreeByteMask(thisDWord))
                {
                    // Per Table 3-7, valid sequences are:
                    // [   E0   ] [ A0..BF ] [ 80..BF ]
                    // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                    // [   ED   ] [ 80..9F ] [ 80..BF ]
                    // [ EE..EF ] [ 80..BF ] [ 80..BF ]

                    Debug.Assert(Utf8DWordBeginsWithThreeByteMask(thisDWord));

                    // Big-endian examples of using the above validation table:
                    // E0A0 = 1110 0000 1010 0000 => invalid (overlong ) patterns are 1110 0000 100# ####
                    // ED9F = 1110 1101 1001 1111 => invalid (surrogate) patterns are 1110 1101 101# ####
                    // If using the bitmask ......................................... 0000 1111 0010 0000 (=0F20),
                    // Then invalid (overlong) patterns match the comparand ......... 0000 0000 0000 0000 (=0000),
                    // And invalid (surrogate) patterns match the comparand ......... 0000 1101 0010 0000 (=0D20).

                    if (BitConverter.IsLittleEndian)
                    {
                        uint toValidate = thisDWord & 0x0000200FU;
                        if ((toValidate == 0U) || (toValidate == 0x0000200DU)) { goto Error; }
                    }
                    else
                    {
                        if (thisDWord < 0xE0A00000U) { goto Error; }
                        if (IsWithinRangeInclusive(thisDWord, 0xEDA00000U, 0xEE790000U)) { goto Error; }
                    }

                    inputBufferCurrentOffset += 3;
                    inputBufferRemainingBytes -= 3;
                    tempRuneCount -= 2; // 3 bytes -> 1 rune

                    // Optimization: A three-byte character could indicate CJK text, which makes it likely
                    // that the character following this one is also CJK. If the leftover byte indicates
                    // that there's another three-byte sequence coming, try jumping directly to the 3-byte
                    // validation logic instead of the beginning of the loop.

                    if (Utf8DWordEndsWithThreeByteSequenceMarker(thisDWord) && inputBufferRemainingBytes >= sizeof(uint))
                    {
                        thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                        goto BeforeProcessThreeByteSequence;
                    }

                    // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                    // in to the text. If this happens strip it off now before seeing if the next character
                    // consists of three code units.
                    if (Utf8DWordFourthByteIsAscii(thisDWord))
                    {
                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes--;
                        goto BeforeReadNextDWord;
                    }

                    continue; // didn't see a three-byte marker or ASCII value at end of DWORD, go back to start of loop
                }

                // Check the 4-byte case.

                if (Utf8DWordBeginsWithFourByteMask(thisDWord))
                {
                    // Per Table 3-7, valid sequences are:
                    // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                    // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                    // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                    // Validation: use the second byte to determine what's an allowable first byte
                    // per the above table.

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

                    inputBufferCurrentOffset += 4;
                    inputBufferRemainingBytes -= 4;
                    tempRuneCount -= 3; // 4 bytes -> 1 rune
                    tempSurrogatecount++; // 4 bytes implies UTF16 surrogate pair

                    continue; // go back to beginning of loop for processing
                }

                // Error - no match.

                goto Error;
            }

            ProcessRemainingBytesSlow:

            Debug.Assert(inputBufferRemainingBytes < 4);
            while (inputBufferRemainingBytes > 0)
            {
                uint firstByte = Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset);

                if (firstByte < 0x80U)
                {
                    // 1-byte (ASCII) case
                    inputBufferCurrentOffset += 1;
                    inputBufferRemainingBytes -= 1;
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
                            tempRuneCount--; // 2 bytes -> 1 rune
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

            runeCount = tempRuneCount - inputBufferRemainingBytes; // we assumed earlier each byte corresponded to a single rune, perform fixup now to account for unread bytes
            surrogateCount = tempSurrogatecount;
            return IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);
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
