using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        private static readonly DecoderFallback _defaultFallback = new DecoderReplacementFallback("\uFFFD");

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
        public static int GetUtf16CharCount(ReadOnlySpan<byte> utf8, DecoderFallback fallback = null)
        {
            int offsetOfInvalidData = GetIndexOfFirstInvalidUtf8CharCore(ref utf8.DangerousGetPinnableReference(), utf8.Length, out int runeCount, out int surrogateCount);
            int utf16CharsCount = runeCount + surrogateCount;
            if (offsetOfInvalidData >= 0)
            {
                checked { utf16CharsCount += GetUtf16CharCountWithFallback(utf8.Slice(offsetOfInvalidData), fallback); }
            }
            return utf16CharsCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int GetUtf16CharCountWithFallback(ReadOnlySpan<byte> sequenceThatBeginsWithInvalidData, DecoderFallback fallback)
        {
            DecoderFallbackBuffer buffer = (fallback ?? _defaultFallback).CreateFallbackBuffer();
            int utf16CharCount = 0;

            while (true)
            {
                int numInvalidBytes = GetInvalidByteCount(ref sequenceThatBeginsWithInvalidData.DangerousGetPinnableReference(), sequenceThatBeginsWithInvalidData.Length);
                var invalidByteArray = sequenceThatBeginsWithInvalidData.Slice(0, numInvalidBytes).ToArray();
                buffer.Reset();
                if (buffer.Fallback(invalidByteArray, 0))
                {
                    checked { utf16CharCount += buffer.Remaining; }
                }

                sequenceThatBeginsWithInvalidData = sequenceThatBeginsWithInvalidData.Slice(numInvalidBytes);
                int newOffset = GetIndexOfFirstInvalidUtf8CharCore(ref sequenceThatBeginsWithInvalidData.DangerousGetPinnableReference(), sequenceThatBeginsWithInvalidData.Length, out int runeCount, out int surrogateCount);
                int numUtf16CharsSeenThisLoop = runeCount + surrogateCount; // will never overflow
                checked { utf16CharCount += numUtf16CharsSeenThisLoop; }

                if (newOffset < 0)
                {
                    return utf16CharCount;
                }
                else
                {
                    sequenceThatBeginsWithInvalidData = sequenceThatBeginsWithInvalidData.Slice(newOffset);
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
            // logic. We perform a small test of the first few bytes to make sure they're all
            // ASCII before we incur the cost of invoking the vectorized code path.

            if (Vector.IsHardwareAccelerated)
            {
                if (IntPtr.Size >= 8)
                {
                    // Test first 16 bytes and check for all-ASCII.
                    if ((inputLength >= 2 * sizeof(ulong) + 2 * Vector<byte>.Count) && Utf8QWordAllBytesAreAscii(ReadAndFoldTwoQWords(ref inputBuffer)))
                    {
                        inputBufferCurrentOffset = (IntPtr)(2 * sizeof(ulong) + ConsumeAsciiBytesVectorized(ref Unsafe.Add(ref inputBuffer, 2 * sizeof(ulong)), inputLength - 2 * sizeof(ulong)));
                    }
                }
                else
                {
                    // Test first 8 bytes and check for all-ASCII.
                    if ((inputLength >= 2 * sizeof(uint) + 2 * Vector<byte>.Count) && Utf8DWordAllBytesAreAscii(ReadAndFoldTwoDWords(ref inputBuffer)))
                    {
                        inputBufferCurrentOffset = (IntPtr)(2 * sizeof(uint) + ConsumeAsciiBytesVectorized(ref Unsafe.Add(ref inputBuffer, 2 * sizeof(uint)), inputLength - 2 * sizeof(uint)));
                    }
                }
            }

            IntPtr inputBufferOffsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int inputBufferRemainingBytes = inputLength - IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);

            // Begin the main loop.

            while (inputBufferRemainingBytes >= sizeof(uint))
            {
                BeforeReadDWord:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(inputLength - (int)inputBufferCurrentOffset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                AfterReadDWord:

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
                        // We saw non-ASCII data last time we tried loop unrolling, so don't bother going
                        // down the unrolling path again until we've bypassed that data. No need to perform
                        // a bounds check here since we already checked the bounds as part of the loop unrolling path.
                        goto BeforeReadDWord;
                    }
                    else
                    {
                        if (IntPtr.Size >= 8)
                        {
                            while (inputBufferRemainingBytes >= 2 * sizeof(ulong))
                            {
                                ulong thisQWord = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset))
                                    | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + sizeof(ulong)));

                                if (!Utf8QWordAllBytesAreAscii(thisQWord))
                                {
                                    inputBufferOffsetAtWhichToAllowUnrolling = inputBufferCurrentOffset + 2 * sizeof(ulong); // non-ASCII data incoming
                                    goto BeforeReadDWord;
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

                                if (!Utf8DWordAllBytesAreAscii(thisDWord))
                                {
                                    inputBufferOffsetAtWhichToAllowUnrolling = inputBufferCurrentOffset + 4 * sizeof(uint); // non-ASCII data incoming
                                    goto BeforeReadDWord;
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

                BeforeProcessTwoByteSequence:

                if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                {
                    // Per Table 3-7, valid sequences are:
                    // [ C2..DF ] [ 80..BF ]

                    if (!IsFirstWordWellFormedTwoByteSequence(thisDWord)) { goto Error; }

                    ProcessTwoByteSequenceSkipOverlongFormCheck:

                    // Optimization: If this is a two-byte-per-character language like Cyrillic or Hebrew,
                    // there's a good chance that if we see one two-byte run then there's another two-byte
                    // run immediately after. Let's check that now.

                    // On little-endian platforms, we can check for the two-byte UTF8 mask *and* validate that
                    // the value isn't overlong using a single comparison. On big-endian platforms, we'll need
                    // to validate the mask and validate that the sequence isn't overlong as two separate comparisons.

                    if ((BitConverter.IsLittleEndian && Utf8DWordEndsWithValidTwoByteSequenceLittleEndian(thisDWord))
                        || (!BitConverter.IsLittleEndian && (Utf8DWordEndsWithTwoByteMask(thisDWord) && IsSecondWordWellFormedTwoByteSequence(thisDWord))))
                    {
                        ConsumeTwoAdjacentKnownGoodTwoByteSequences:

                        // We have two runs of two bytes each.
                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        tempRuneCount -= 2; // 4 bytes -> 2 runes

                        if (inputBufferRemainingBytes >= sizeof(uint))
                        {
                            // Optimization: If we read a long run of two-byte sequences, the next sequence is probably
                            // also two bytes. Check for that first before going back to the beginning of the loop.

                            thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                            if (BitConverter.IsLittleEndian)
                            {
                                if (Utf8DWordBeginsWithValidTwoByteSequenceLittleEndian(thisDWord))
                                {
                                    // The next sequence is a valid two-byte sequence.
                                    goto ProcessTwoByteSequenceSkipOverlongFormCheck;
                                }
                            }
                            else
                            {
                                if (Utf8DWordBeginsAndEndsWithTwoByteMask(thisDWord))
                                {
                                    if (IsFirstWordWellFormedTwoByteSequence(thisDWord) && IsSecondWordWellFormedTwoByteSequence(thisDWord))
                                    {
                                        // Validated next bytes are 2x 2-byte sequences
                                        goto ConsumeTwoAdjacentKnownGoodTwoByteSequences;
                                    }
                                    else
                                    {
                                        // Mask said it was 2x 2-byte sequences but validation failed, go to beginning of loop for error handling
                                        goto AfterReadDWord;
                                    }
                                }
                                else if (Utf8DWordBeginsWithTwoByteMask(thisDWord))
                                {
                                    if (IsFirstWordWellFormedTwoByteSequence(thisDWord))
                                    {
                                        // Validated next bytes are a single 2-byte sequence with no valid 2-byte sequence following
                                        goto ConsumeSingleKnownGoodTwoByteSequence;
                                    }
                                    else
                                    {
                                        // Mask said it was a 2-byte sequence but validation failed, go to beginning of loop for error handling
                                        goto AfterReadDWord;
                                    }
                                }
                            }

                            // If we reached this point, the next sequence is something other than a valid
                            // two-byte sequence, so go back to the beginning of the loop.
                            goto AfterReadDWord;
                        }
                        else
                        {
                            goto ProcessRemainingBytesSlow; // Running out of data - go down slow path
                        }
                    }

                    ConsumeSingleKnownGoodTwoByteSequence:

                    // The buffer contains a 2-byte sequence followed by 2 bytes that aren't a 2-byte sequence.
                    // Unlikely that a 3-byte sequence would follow a 2-byte sequence, so perhaps remaining
                    // bytes are ASCII?

                    if (Utf8DWordThirdByteIsAscii(thisDWord))
                    {
                        if (Utf8DWordFourthByteIsAscii(thisDWord))
                        {
                            inputBufferCurrentOffset += 4; // a 2-byte sequence + 2 ASCII bytes
                            inputBufferRemainingBytes -= 4; // a 2-byte sequence + 2 ASCII bytes
                            tempRuneCount--; // 2-byte sequence + 2 ASCII bytes -> 3 runes
                        }
                        else
                        {
                            inputBufferCurrentOffset += 3; // a 2-byte sequence + 1 ASCII byte
                            inputBufferRemainingBytes -= 3; // a 2-byte sequence + 1 ASCII byte
                            tempRuneCount--; // 2-byte sequence + 1 ASCII bytes -> 2 runes

                            // A two-byte sequence followed by an ASCII byte followed by a non-ASCII byte.
                            // Read in the next DWORD and jump directly to the start of the multi-byte processing block.

                            if (inputBufferRemainingBytes >= sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                                goto BeforeProcessTwoByteSequence;
                            }
                        }
                    }
                    else
                    {
                        inputBufferCurrentOffset += 2;
                        inputBufferRemainingBytes -= 2;
                        tempRuneCount--; // 2-byte sequence -> 1 rune1
                    }

                    continue;
                }

                // Check the 3-byte case.

                if (Utf8DWordBeginsWithThreeByteMask(thisDWord))
                {
                    ProcessThreeByteSequenceWithCheck:

                    // We need to check for overlong or surrogate three-byte sequences.
                    //
                    // Per Table 3-7, valid sequences are:
                    // [   E0   ] [ A0..BF ] [ 80..BF ]
                    // [ E1..EC ] [ 80..BF ] [ 80..BF ]
                    // [   ED   ] [ 80..9F ] [ 80..BF ]
                    // [ EE..EF ] [ 80..BF ] [ 80..BF ]
                    //
                    // Big-endian examples of using the above validation table:
                    // E0A0 = 1110 0000 1010 0000 => invalid (overlong ) patterns are 1110 0000 100# ####
                    // ED9F = 1110 1101 1001 1111 => invalid (surrogate) patterns are 1110 1101 101# ####
                    // If using the bitmask ......................................... 0000 1111 0010 0000 (=0F20),
                    // Then invalid (overlong) patterns match the comparand ......... 0000 0000 0000 0000 (=0000),
                    // And invalid (surrogate) patterns match the comparand ......... 0000 1101 0010 0000 (=0D20).

                    if (BitConverter.IsLittleEndian)
                    {
                        // The "overlong or surrogate" check can be implemented using a single jump, but there's
                        // some overhead to moving the bits into the correct locations in order to perform the
                        // correct comparison, and in practice the processor's branch prediction capability is
                        // good enough that we shouldn't bother. So we'll use two jumps instead.

                        uint comparand = thisDWord & 0x0000200FU;
                        if ((comparand == 0U) || (comparand == 0x0000200DU)) { goto Error; }
                    }
                    else
                    {
                        uint comparand = thisDWord & 0x0F200000U;
                        if ((comparand == 0U) || (comparand == 0x0D200000U)) { goto Error; }
                    }

                    inputBufferCurrentOffset += 3;
                    inputBufferRemainingBytes -= 3;
                    tempRuneCount -= 2; // 3 bytes -> 1 rune

                    // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                    // in to the text. If this happens strip it off now before seeing if the next character
                    // consists of three code units.

                    if (Utf8DWordFourthByteIsAscii(thisDWord))
                    {
                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes--;
                    }

                    if (inputBufferRemainingBytes >= sizeof(uint))
                    {
                        thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                        // Optimization: A three-byte character could indicate CJK text, which makes it likely
                        // that the character following this one is also CJK. We'll check for a three-byte sequence
                        // marker now and jump directly to three-byte sequence processing if we see one, skipping
                        // all of the logic at the beginning of the loop.

                        if (Utf8DWordBeginsWithThreeByteMask(thisDWord))
                        {
                            goto ProcessThreeByteSequenceWithCheck; // Found another [not yet validated] three-byte sequence; process
                        }
                        else
                        {
                            goto AfterReadDWord; // Probably ASCI punctuation or whitespace; go back to start of loop
                        }
                    }
                    else
                    {
                        goto ProcessRemainingBytesSlow; // Running out of data
                    }
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

        // This method will consume as many ASCII bytes as it can using fast vectorized processing, returning the number of
        // consumed (ASCII) bytes. It's possible that the method exits early, perhaps because there is some non-ASCII byte
        // later in the sequence or because we're running out of input to search. The intent is that the caller *skips over*
        // the number of bytes returned by this method, then it continues data processing from the next byte.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int ConsumeAsciiBytesVectorized(ref byte buffer, int length)
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
                        if (!Utf8QWordAllBytesAreAscii(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buffer, numBytesConsumed))))
                        {
                            return IntPtrToInt32NoOverflowCheck(numBytesConsumed); // found a high bit set somewhere
                        }
                        numBytesConsumed += sizeof(ulong);
                        numBytesToConsumeBeforeAligned -= sizeof(ulong);
                    }

                    if (numBytesToConsumeBeforeAligned >= sizeof(uint))
                    {
                        if (!Utf8DWordAllBytesAreAscii(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, numBytesConsumed))))
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
                        if (!Utf8DWordAllBytesAreAscii(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref buffer, numBytesConsumed))))
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
                    if ((Unsafe.Add(ref buffer, numBytesConsumed) & (byte)0x80U) != (byte)0)
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
