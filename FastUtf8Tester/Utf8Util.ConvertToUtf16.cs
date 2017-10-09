using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
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
        private static bool ConvertUtf8ToUtf16Core(ref byte inputBuffer, int inputLength, ref char outputBuffer, int outputLength, out int numBytesConsumed, out int numCharsWritten)
        {
            // The fields below control where we read from / write to the buffer.

            IntPtr inputBufferCurrentOffset = IntPtr.Zero;
            IntPtr inputBufferOffsetAtWhichToAllowUnrolling = IntPtr.Zero;
            int inputBufferRemainingBytes = inputLength - IntPtrToInt32NoOverflowCheck(inputBufferCurrentOffset);

            IntPtr outputBufferCurrentOffset = IntPtr.Zero;
            int remainingOutputBufferSize = outputLength;

            // Begin the main loop.

            while (inputBufferRemainingBytes >= sizeof(uint))
            {
                BeforeReadNextDWord:

                // Read 32 bits at a time. This is enough to hold any possible UTF8-encoded scalar.

                Debug.Assert(inputLength - (int)inputBufferCurrentOffset >= sizeof(uint));
                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));

                AfterReadNextDWord:

                // First, check for the common case of all-ASCII bytes.

                if (Utf8DWordAllBytesAreAscii(thisDWord) && remainingOutputBufferSize >= 4)
                {
                    // We read an all-ASCII sequence, and there's enough space in the output buffer to hold it.
                    // Simply widen the DWORD into a QWORD and write it to the output buffer. Endianness-independent.

                    inputBufferCurrentOffset += sizeof(uint);
                    inputBufferRemainingBytes -= sizeof(uint);
                    Unsafe.WriteUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), Widen(thisDWord));
                    outputBufferCurrentOffset += 4;
                    remainingOutputBufferSize -= 4;

                    // If we saw a sequence of all ASCII, there's a good chance a significant amount of following data is also ASCII.
                    // Let's try performing a vectorized widening operation.

                    if (IntPtrIsLessThan(inputBufferCurrentOffset, inputBufferOffsetAtWhichToAllowUnrolling))
                    {
                        goto BeforeReadNextDWord; // we think there's non-ASCII data coming, so don't bother loop unrolling
                    }
                    else if (Vector.IsHardwareAccelerated)
                    {
                        if (inputBufferRemainingBytes >= Vector<byte>.Count && remainingOutputBufferSize >= Vector<byte>.Count)
                        {
                            var highBitMaskVector = new Vector<byte>(0x80);
                            do
                            {
                                var inputVector = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                                if ((inputVector & highBitMaskVector) == Vector<byte>.Zero)
                                {
                                    // TODO: Is it ok for this to be unaligned without explicit use of 'unaligned' keyword?
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
                                    goto BeforeReadNextDWord;
                                }
                            } while (inputBufferRemainingBytes >= Vector<byte>.Count && remainingOutputBufferSize >= Vector<byte>.Count);
                        }
                    }

                    continue;
                }

                // Next, try stripping off ASCII bytes one at a time.
                // We only handle up to three ASCII bytes here since we handled the four ASCII byte case above.

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

                    ProcessTwoByteSequenceWithLookahead:

                    // Optimization: If this is a two-byte-per-character language like Cyrillic or Hebrew,
                    // there's a good chance that if we see one two-byte run then there are more two-byte
                    // runs that follow it. Let's check that now.

                    if (Utf8DWordEndsWithTwoByteMask(thisDWord))
                    {
                        // At this point, we know we have two runs of two bytes each.
                        // Can we extend this to four runs of two bytes each?

                        if (inputBufferRemainingBytes >= 8 && remainingOutputBufferSize >= 4)
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
                                    // TODO: BIG ENDIAN SUPPORT
                                    throw new NotImplementedException();
                                }

                                // Only write data to output buffer if passed validation.
                                if (IsWellFormedCharPackFromQuadTwoByteSequences(thisQWord))
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
                                            goto ProcessTwoByteSequenceWithLookahead;
                                        }
                                        else
                                        {
                                            goto AfterReadNextDWord;
                                        }
                                    }
                                }
                                else
                                {
                                    // Invalid sequence incoming! Try consuming only the first two bytes instead of pipelining.
                                    // We'll eventually report the error.
                                    goto ProcessTwoByteSequenceWithoutLookahead;
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
                                    // TODO: BIG ENDIAN SUPPORT
                                    throw new NotImplementedException();
                                }

                                // Only write data to output buffer if passed validation.
                                if (IsWellFormedCharPackFromDoubleTwoByteSequences(thisDWord))
                                {
                                    inputBufferCurrentOffset += 4;
                                    inputBufferRemainingBytes -= 4;
                                    Unsafe.WriteUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset)), thisDWord);
                                    outputBufferCurrentOffset += 2;
                                    remainingOutputBufferSize -= 2;

                                    // We've already read the next DWORD, and we know it's not a two-byte sequence,
                                    // so go to the beginning of the loop.

                                    thisDWord = nextDWord;
                                    goto AfterReadNextDWord;
                                }
                                else
                                {
                                    // Invalid sequence incoming! Try consuming only the first two bytes instead of pipelining.
                                    // We'll eventually report the error.
                                    goto ProcessTwoByteSequenceWithoutLookahead;
                                }
                            }
                        }
                    }

                    ProcessTwoByteSequenceWithoutLookahead:

                    uint thisChar;
                    if (BitConverter.IsLittleEndian)
                    {
                        // At this point, thisDWord = [ 10dddddd110ccccc 10bbbbbb110aaaaa ]
                        // We want thisDWord = [ ################ 00000aaaaabbbbbb ], where # is ignored

                        thisChar = ((thisDWord & 0x1FU) << 6) | ((thisDWord & 0x3F00U) >> 8);
                    }
                    else
                    {
                        // TODO: SUPPORT BIG ENDIAN
                        throw new NotImplementedException();
                    }

                    // Validation checking, scalar must be >= U+0080.
                    if (thisChar < 0x80U) { goto Error; }

                    if (remainingOutputBufferSize == 0) { goto OutputBufferTooSmall; }
                    inputBufferCurrentOffset += 2;
                    inputBufferRemainingBytes -= 2;
                    Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)thisChar;
                    outputBufferCurrentOffset += 1;
                    remainingOutputBufferSize -= 1;

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

                        // We know from checking earlier that the next WORD doesn't represent a two-byte sequence, or it does
                        // represent a sequence and there's fewer than 4 bytes remaining in the stream. Either way jump back
                        // to the beginning so that it can be handled properly.

                        goto AfterReadNextDWord;
                    }
                    else
                    {
                        break; // Running out of data - go down slow path
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

                    ProcessThreeByteSequenceWithLookahead:

                    Debug.Assert(Utf8DWordBeginsWithThreeByteMask(thisDWord));

                    //goto ProcessThreeByteSequenceNoLookahead;

                    // Optimization: A three-byte character could indicate CJK text, which makes it likely
                    // that the character following this one is also CJK. If the leftover byte indicates
                    // that there's another three-byte sequence coming, try consuming multiple sequences
                    // at once.

                    // If a second sequence is coming, the original input stream will contain [ A1 A2 A3 B1 | B2 B3 ... ]

                    if (Utf8DWordEndsWithThreeByteSequenceMarker(thisDWord) && inputBufferRemainingBytes >= 6 && remainingOutputBufferSize >= 2)
                    {
                        uint secondDWord = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset + sizeof(uint)));

                        // Incoming sequence is believed to be [ A1 A2 A3 B1 B2 B3 ]

                        uint firstChar, secondChar;

                        if (BitConverter.IsLittleEndian)
                        {
                            // thisDWord = [ B1 A3 A2 A1 ], validated
                            // secondDWord = [ 00 00 B3 B2 ], unvalidated
                            // want to produce two wide chars value = [ By Bx ] [ Ay Ax ]

                            firstChar = ((thisDWord & 0x0000000FU) << 12)
                                | ((thisDWord & 0x00003F00U) >> 2)
                                | ((thisDWord & 0x003F0000U) >> 16);

                            secondChar = ((thisDWord & 0x0F000000U) >> 12)
                                | ((secondDWord & 0x0000003FU) << 6)
                                | ((secondDWord & 0x00003F00U) >> 8);
                        }
                        else
                        {
                            // TODO: SUPPORT BIG ENDIAN
                            throw new NotImplementedException();
                        }

                        // Validation

                        if ((firstChar < 0x0800U) || IsSurrogateFast(firstChar)
                            || (secondChar < 0x0800U) || IsSurrogateFast(secondChar)
                            || ((secondDWord & 0xC0C0U) != 0x8080U))
                        {
                            goto ProcessThreeByteSequenceNoLookahead; // validation failed; error will be handled later
                        }
                        else
                        {
                            inputBufferCurrentOffset += 6;
                            inputBufferRemainingBytes -= 6;
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset) = (char)firstChar;
                            Unsafe.Add(ref outputBuffer, outputBufferCurrentOffset + 1) = (char)secondChar;
                            outputBufferCurrentOffset += 2;
                            remainingOutputBufferSize -= 2;

                            // We just read a three-byte character from the buffer,
                            // so chances are the next character is also a three-byte
                            // character. Perform this check eagerly to bypass all the
                            // logic at the beginning of the loop.

                            if (inputBufferRemainingBytes >= sizeof(uint))
                            {
                                thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inputBuffer, inputBufferCurrentOffset));
                                if (Utf8DWordBeginsWithThreeByteMask(thisDWord))
                                {
                                    goto ProcessThreeByteSequenceWithLookahead;
                                }
                                else
                                {
                                    goto AfterReadNextDWord;
                                }
                            }
                            else
                            {
                                break; // running out of data - go down slow path
                            }
                        }
                    }

                    ProcessThreeByteSequenceNoLookahead:

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
                    if (Utf8DWordFourthByteIsAscii(thisDWord))
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
                        goto AfterReadNextDWord;
                    }
                    else
                    {
                        goto ProcessRemainingBytesSlow; // running out of data
                    }
                }

                // Check the 4-byte case.

                if (Utf8DWordBeginsWithFourByteMask(thisDWord))
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

                // Error - no match.

                goto Error;
            }

            ProcessRemainingBytesSlow:

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
                // sequence. But per Table 3-7 any 3-byte sequence that reads [ E0 80 ##] is *always* invalid.
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
