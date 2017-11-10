using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace System.Buffers.Text
{
    internal static partial class Utf8Util
    {
        // This method will convert as many ASCII bytes as it can using fast vectorized processing, returning the number of
        // consumed (ASCII) bytes. It's possible that the method exits early, perhaps because there is some non-ASCII byte
        // later in the sequence or because we're running out of input to search. The intent is that the caller *skips over*
        // the number of bytes returned by this method, then it continues data processing from the next byte.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IntPtr ConvertAsciiBytesVectorized(ref byte inputBuffer, int inputBufferLength, ref char outputBuffer, int outputBufferLength)
        {
            // Only allow vectorization if vectors are hardware-accelerated and we have enough
            // data to allow a vectorized conversion.

            if (!Vector.IsHardwareAccelerated)
            {
                return IntPtr.Zero;
            }

            // We won't bother checking for alignment. The JITter will generate VMOVUPD instructions, which will work
            // regardless of whether the memory access is aligned. Vector memory accesses are faster when aligned,
            // but the execution time is dominated by the widening operation rather than the processor internally
            // fixing up misalignments.

            IntPtr currentOffset = IntPtr.Zero;
            IntPtr finalOffsetAtWhichCanConvert = (IntPtr)(Math.Min(inputBufferLength, outputBufferLength) - Vector<byte>.Count);

            Vector<byte> mask = new Vector<byte>((byte)0x80);
            for (; IntPtrIsLessThanOrEqualTo(currentOffset, finalOffsetAtWhichCanConvert); currentOffset += Vector<byte>.Count)
            {
                Vector<byte> inputVector = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref inputBuffer, currentOffset));
                if ((inputVector & mask) != Vector<byte>.Zero)
                {
                    break; // non-ASCII data incoming
                }

                // Input vector of all-ASCII bytes. Widen and write back to the output.
                // Widening operations are endianness-agnostic.

                // TODO: Vector.Widen is fast than non-vectorized code but still isn't implemented efficiently.
                // Ideally this would be implemented via VPMOVZXBW or similar intrinsic for best performance.

                Vector.Widen(inputVector, out var widenedVectorHigh, out var widenedVectorLow);
                Unsafe.WriteUnaligned<Vector<ushort>>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, currentOffset)), widenedVectorHigh);
                Unsafe.WriteUnaligned<Vector<ushort>>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, currentOffset + Vector<ushort>.Count)), widenedVectorLow);
            }

            return currentOffset;
        }

        /// <summary>
        /// Consumes an input span in its entirety, returning the number of characters written to the output.
        /// Throws if a conversion error occurs.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ConvertUtf8ToUtf16(ReadOnlySpan<byte> utf8, Span<char> utf16)
        {
            return (ConvertUtf8ToUtf16Core(ref utf8.DangerousGetPinnableReference(), utf8.Length, ref utf16.DangerousGetPinnableReference(), utf16.Length, out _, out int numCharsWritten))
                ? numCharsWritten
                : throw new ArgumentException(
                    message: "Invalid data.",
                    paramName: nameof(utf8));
        }

        // Returns true if conversion succeeded (even if not all bytes could be consumed due to buffer sizes),
        // false if an invalid character was encountered. If error then 'numBytesConsumed' will point to the
        // index of the first invalid byte.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ConvertUtf8ToUtf16Core(ref byte inputBuffer, int inputLength, ref char outputBuffer, int outputLength, out int numBytesConsumed, out int numCharsWritten)
        {
            // The fields below control where we read from / write to the buffer.

            IntPtr inputBufferCurrentOffset = IntPtr.Zero;

            // If the sequence is long enough, try running vectorized "is this sequence ASCII?"
            // logic. We perform a small test of the first few bytes to make sure they're all
            // ASCII before we incur the cost of invoking the vectorized code path.

            if (Vector.IsHardwareAccelerated)
            {
                if (IntPtr.Size >= 8)
                {
                    // Test first 16 bytes and check for all-ASCII.
                    if (2 * sizeof(ulong) <= Vector<byte>.Count)
                    {
                        if ((Math.Min(inputLength, outputLength) >= 2 * Vector<byte>.Count) && QWordAllBytesAreAscii(ReadAndFoldTwoQWordsUnaligned(ref inputBuffer)))
                        {
                            inputBufferCurrentOffset = ConvertAsciiBytesVectorized(ref inputBuffer, inputLength, ref outputBuffer, outputLength);
                        }
                    }
                }
                else
                {
                    // Test first 8 bytes and check for all-ASCII.
                    if (2 * sizeof(uint) <= Vector<byte>.Count)
                    {
                        if ((Math.Min(inputLength, outputLength) >= 2 * Vector<byte>.Count) && DWordAllBytesAreAscii(ReadAndFoldTwoDWordsUnaligned(ref inputBuffer)))
                        {
                            inputBufferCurrentOffset = ConvertAsciiBytesVectorized(ref inputBuffer, inputLength, ref outputBuffer, outputLength);
                        }
                    }
                }
            }

            int inputBufferRemainingBytes = inputLength - ConvertIntPtrToInt32WithoutOverflowCheck(inputBufferCurrentOffset);

            IntPtr outputBufferCurrentOffset = inputBufferCurrentOffset;
            int outputBufferRemainingChars = outputLength - ConvertIntPtrToInt32WithoutOverflowCheck(outputBufferCurrentOffset);

            // Begin the main loop.

#if DEBUG
            long lastOffsetProcessed = -1; // used for invariant checking in debug builds
#endif

            while (inputBufferRemainingBytes >= sizeof(uint))
            {
                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(inputLength - (int)inputBufferCurrentOffset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                AfterReadDWord:

#if DEBUG
                Debug.Assert(lastOffsetProcessed < (long)inputBufferCurrentOffset, "Algorithm should've made forward progress since last read.");
                lastOffsetProcessed = (long)inputBufferCurrentOffset;
#endif

                // First, check for the common case of all-ASCII bytes.

                if (Utf8DWordAllBytesAreAscii(thisDWord))
                {
                    // We read an all-ASCII sequence.

                    if (outputBufferRemainingChars < sizeof(uint)) { goto ProcessRemainingBytesSlow; } // running out of space, but may be able to write some data

                    WritePackedDWordAsChars(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset), thisDWord);
                    inputBufferCurrentOffset += 4;
                    inputBufferRemainingBytes -= 4;
                    outputBufferCurrentOffset += 4;
                    outputBufferRemainingChars -= 4;

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Below is basically unrolled loops with poor man's vectorization.

                    int maxIters = Math.Min(inputBufferRemainingBytes, outputBufferRemainingChars) / (2 * sizeof(uint));
                    for (int i = 0; i < maxIters; i++)
                    {
                        thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                        if (Utf8DWordAllBytesAreAscii(thisDWord))
                        {
                            WritePackedDWordAsChars(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset), thisDWord);
                            inputBufferCurrentOffset += 4;
                            outputBufferCurrentOffset += 4;
                        }
                        else
                        {
                            goto LoopTerminatedEarlyDueToNonAsciiData;
                        }

                        thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                        if (Utf8DWordAllBytesAreAscii(thisDWord))
                        {
                            WritePackedDWordAsChars(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset), thisDWord);
                            inputBufferCurrentOffset += 4;
                            outputBufferCurrentOffset += 4;
                        }
                        else
                        {
                            goto LoopTerminatedEarlyDueToNonAsciiData;
                        }
                    }

                    inputBufferRemainingBytes = inputLength - ConvertIntPtrToInt32WithoutOverflowCheck(inputBufferCurrentOffset);
                    outputBufferRemainingChars = outputLength - ConvertIntPtrToInt32WithoutOverflowCheck(outputBufferCurrentOffset);
                    continue; // need to perform a bounds check because we might be running out of data

                    LoopTerminatedEarlyDueToNonAsciiData:

                    // We know that there's *at least* one DWORD of data remaining in the buffer.
                    // We also know that it's not all-ASCII. We can skip the logic at the beginning of the main loop.

                    inputBufferRemainingBytes = inputLength - ConvertIntPtrToInt32WithoutOverflowCheck(inputBufferCurrentOffset);
                    outputBufferRemainingChars = outputLength - ConvertIntPtrToInt32WithoutOverflowCheck(outputBufferCurrentOffset);
                    goto AfterReadDWordSkipAllBytesAsciiCheck;
                }

                AfterReadDWordSkipAllBytesAsciiCheck:

                Debug.Assert(!DWordAllBytesAreAscii(thisDWord)); // this should have been handled earlier

                // Next, try stripping off ASCII bytes one at a time.
                // We only handle up to three ASCII bytes here since we handled the four ASCII byte case above.

                if (Utf8DWordFirstByteIsAscii(thisDWord))
                {
                    if (Utf8DWordSecondByteIsAscii(thisDWord))
                    {
                        // Optimization: compute the base offset now to avoid multiple 'LEA' instructions later.
                        ref char tempOutputBuffer = ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset);

                        if (Utf8DWordThirdByteIsAscii(thisDWord))
                        {
                            // Want to copy three characters to output buffer
                            if (outputBufferRemainingChars < 3) { goto ProcessRemainingBytesSlow; }

                            if (BitConverter.IsLittleEndian)
                            {
                                tempOutputBuffer = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 2) = (char)(byte)thisDWord;
                            }
                            else
                            {
                                thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 2) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Debug.Assert(thisDWord <= 0xFFU, "All bytes higher than the least signficant byte should've been shifted out.");
                                tempOutputBuffer = (char)thisDWord;
                            }

                            inputBufferCurrentOffset += 3;
                            inputBufferRemainingBytes -= 3;
                            outputBufferCurrentOffset += 3;
                            outputBufferRemainingChars -= 3;
                        }
                        else
                        {
                            if (outputBufferRemainingChars < 2) { goto ProcessRemainingBytesSlow; }

                            if (BitConverter.IsLittleEndian)
                            {
                                tempOutputBuffer = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord;
                            }
                            else
                            {
                                thisDWord >>= 16;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Debug.Assert(thisDWord <= 0xFFU, "All bytes higher than the least signficant byte should've been shifted out.");
                                tempOutputBuffer = (char)thisDWord;
                            }

                            inputBufferCurrentOffset += 2;
                            inputBufferRemainingBytes -= 2;
                            outputBufferCurrentOffset += 2;
                            outputBufferRemainingChars -= 2;
                        }
                    }
                    else
                    {
                        if (outputBufferRemainingChars == 0) { goto OutputBufferTooSmall; }

                        if (BitConverter.IsLittleEndian)
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(byte)thisDWord;
                        }
                        else
                        {
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)(thisDWord >> 24);
                        }

                        inputBufferCurrentOffset += 1;
                        inputBufferRemainingBytes -= 1;
                        outputBufferCurrentOffset += 1;
                        outputBufferRemainingChars -= 1;
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

                        if (outputBufferRemainingChars < 2) { goto ProcessRemainingBytesSlow; } // running out of output buffer

                        Unsafe.WriteUnaligned<uint>(
                            ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)),
                            ExtractTwoCharsPackedFromTwoAdjacentTwoByteSequences(thisDWord));

                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        outputBufferCurrentOffset += 2;
                        outputBufferRemainingChars -= 2;

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
                        // Calculate the reference ahead of time to prevent multiple 'LEA' instructions
                        ref char tempOutputBuffer = ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset);

                        if (Utf8DWordFourthByteIsAscii(thisDWord))
                        {
                            // 2-byte sequence + 2 ASCII bytes
                            if (outputBufferRemainingChars < 3) { goto ProcessRemainingBytesSlow; } // running out of room

                            tempOutputBuffer = ExtractCharFromFirstTwoByteSequence(thisDWord);
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord >>= 16;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 2) = (char)thisDWord;
                            }
                            else
                            {
                                Unsafe.Add(ref tempOutputBuffer, 2) = (char)(byte)thisDWord; thisDWord >>= 8;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord;
                            }

                            inputBufferCurrentOffset += 4;
                            inputBufferRemainingBytes -= 4;
                            outputBufferCurrentOffset += 3;
                            outputBufferRemainingChars -= 3;
                        }
                        else
                        {
                            // 2-byte sequence + 1 ASCII byte
                            if (outputBufferRemainingChars < 2) { goto ProcessRemainingBytesSlow; } // running out of room

                            tempOutputBuffer = ExtractCharFromFirstTwoByteSequence(thisDWord);
                            if (BitConverter.IsLittleEndian)
                            {
                                thisDWord >>= 16;
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)thisDWord;
                            }
                            else
                            {
                                Unsafe.Add(ref tempOutputBuffer, 1) = (char)(byte)(thisDWord >> 8);
                            }

                            inputBufferCurrentOffset += 3;
                            inputBufferRemainingBytes -= 3;
                            outputBufferCurrentOffset += 2;
                            outputBufferRemainingChars -= 2;

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
                        if (outputBufferRemainingChars == 0) { goto OutputBufferTooSmall; } // running out of room

                        Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = ExtractCharFromFirstTwoByteSequence(thisDWord);

                        inputBufferCurrentOffset += 2;
                        inputBufferRemainingBytes -= 2;
                        outputBufferCurrentOffset += 1;
                        outputBufferRemainingChars -= 1;
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

                        // Can't extract this check into its own helper method because JITter produces suboptimal
                        // assembly, even with aggressive inlining.

                        uint comparand = thisDWord & 0x0000200FU;
                        if ((comparand == 0U) || (comparand == 0x0000200DU)) { goto Error; }
                    }
                    else
                    {
                        uint comparand = thisDWord & 0x0F200000U;
                        if ((comparand == 0U) || (comparand == 0x0D200000U)) { goto Error; }
                    }

                    // Occasionally one-off ASCII characters like spaces, periods, or newlines will make their way
                    // in to the text. If this happens strip it off now before seeing if the next character
                    // consists of three code units.

                    if (Utf8DWordFourthByteIsAscii(thisDWord))
                    {
                        // 3-byte sequence + ASCII byte
                        if (outputBufferRemainingChars < 2) { goto ProcessRemainingBytesSlow; }

                        uint toWrite = ExtractTwoCharsPackedFromThreeByteSequenceFollowedByAsciiByte(thisDWord);
                        Unsafe.WriteUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), toWrite);

                        inputBufferCurrentOffset += 4;
                        inputBufferRemainingBytes -= 4;
                        outputBufferCurrentOffset += 2;
                        outputBufferRemainingChars -= 2;
                    }
                    else
                    {
                        // 3-byte sequence, no trailing ASCII byte
                        if (outputBufferRemainingChars == 0) { goto OutputBufferTooSmall; }

                        char toWrite = ExtractCharFromFirstThreeByteSequence(thisDWord);
                        Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)toWrite;
                        inputBufferCurrentOffset += 3;
                        inputBufferRemainingBytes -= 3;
                        outputBufferCurrentOffset += 1;
                        outputBufferRemainingChars -= 1;
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

                // Assume the 4-byte case, but we need to validate.

                {
                    // We need to check for overlong or invalid (over U+10FFFF) four-byte sequences.
                    //
                    // Per Table 3-7, valid sequences are:
                    // [   F0   ] [ 90..BF ] [ 80..BF ] [ 80..BF ]
                    // [ F1..F3 ] [ 80..BF ] [ 80..BF ] [ 80..BF ]
                    // [   F4   ] [ 80..8F ] [ 80..BF ] [ 80..BF ]

                    if (!Utf8DWordBeginsWithFourByteMask(thisDWord)) { goto Error; }

                    // Now check for overlong / out-of-range sequences.

                    if (BitConverter.IsLittleEndian)
                    {
                        // The DWORD we read is [ 10xxxxxx 10yyyyyy 10zzzzzz 11110www ].
                        // We want to get the 'w' byte in front of the 'z' byte so that we can perform
                        // a single range comparison. We'll take advantage of the fact that the JITter
                        // can detect a ROR / ROL operation, then we'll just zero out the bytes that
                        // aren't involved in the range check.

                        uint toCheck = (ushort)thisDWord;

                        // At this point, toCheck = [ 00000000 00000000 10zzzzzz 11110www ].

                        toCheck = (toCheck << 24) | (toCheck >> 8); // ROR 8 / ROL 24

                        // At this point, toCheck = [ 11110www 00000000 00000000 10zzzzzz ].

                        if (!IsWithinRangeInclusive(toCheck, 0xF0000090U, 0xF400008FU)) { goto Error; }
                    }
                    else
                    {
                        if (!IsWithinRangeInclusive(thisDWord, 0xF0900000U, 0xF48FFFFFU)) { goto Error; }
                    }

                    // Validation complete.

                    if (outputBufferRemainingChars < 2) { goto OutputBufferTooSmall; }

                    Unsafe.WriteUnaligned(
                        destination: ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)),
                        value: GenerateUtf16CodeUnitsFromFourUtf8CodeUnits(thisDWord));

                    inputBufferCurrentOffset += 4;
                    inputBufferRemainingBytes -= 4;
                    outputBufferCurrentOffset += 2;
                    outputBufferRemainingChars -= 2;

                    continue; // go back to beginning of loop for processing
                }
            }

            ProcessRemainingBytesSlow:

            while (inputBufferRemainingBytes > 0)
            {
                if (outputBufferRemainingChars == 0)
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
                    outputBufferRemainingChars -= 1;
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
                            outputBufferRemainingChars -= 1;
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
                                outputBufferRemainingChars -= 1;
                                continue;
                            }
                        }
                    }
                }

                // Error - no match.

                goto Error;
            }

            // If we reached this point, we're out of data, and we saw no bad UTF8 sequence.

            ReturnSuccess:
            numBytesConsumed = IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);
            numCharsWritten = IntPtrToInt32NoOverflowCheck(outputBufferCurrentOffset);
            return true;

            // Error handling logic.

            InputBufferTooSmall:
            goto ReturnSuccess; // input buffer too small = success case, caller will check

            OutputBufferTooSmall:
            goto ReturnSuccess; // output buffer too small = success case, caller will check

            Error:

            // We saw a bad sequence in the stream. However, this might be due to the input
            // buffer cutting out before we could read a multi-byte character. We'll check
            // this right now.

            {
                uint firstByte = Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset);
                Debug.Assert(firstByte >= 0x80U, "ASCII bytes are never invalid.");

                // These checks are from Table 3-6.
                // There are still some weird edge cases here that we don't handle. For example, consider an
                // input stream that ends with [ E0 80 ]. We won't report it as an error, but we also won't
                // consume it because we'll tell the caller that we're waiting for the final byte of the
                // sequence. But per Table 3-7 any 3-byte sequence that reads [ E0 80 ## ] is *always* invalid.
                // A properly-implemented caller will still be able to detect the error eventually.

                if (((firstByte & 0xE0U) == 0xC0U) && (inputBufferRemainingBytes < 2)) { goto InputBufferTooSmall; } // 2-byte marker but not enough input data
                if (((firstByte & 0xF0U) == 0xE0U) && (inputBufferRemainingBytes < 3)) { goto InputBufferTooSmall; } // 3-byte marker
                if (((firstByte & 0xF8U) == 0xF0U) && (inputBufferRemainingBytes < 4)) { goto InputBufferTooSmall; } // 4-byte marker

                // If we fell through to here, we really did see a bad sequence.

                numBytesConsumed = IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);
                numCharsWritten = IntPtrToInt32NoOverflowCheck(outputBufferCurrentOffset);
                return false;
            }
        }
    }
}
