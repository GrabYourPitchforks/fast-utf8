// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers.Text
{
    internal static partial class Utf16Util
    {
        private const int SIZE_OF_VECTOR256_IN_CHARS = 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetUtf8ByteCount(ReadOnlySpan<char> utf16Data) => GetUtf8ByteCount(ref MemoryMarshal.GetReference(utf16Data), utf16Data.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetUtf8ByteCount(ref char data, nuint length) => GetUtf8ByteCount_Bmi(ref data, length);

        private static int GetUtf8ByteCount_VectorizedAscii(ref char data, nuint length)
        {
            // Can we attempt vectorization?

            if (length >= SIZE_OF_VECTOR256_IN_CHARS)
            {
                Vector256<ushort> notAsciiCheck = Avx2.SetAllVector256<ushort>(0xFF80);
                ref char lastPositionWhereCanReadVector = ref Unsafe.Add(ref Unsafe.Add(ref data, length), -SIZE_OF_VECTOR256_IN_CHARS);
                do
                {
                    Vector256<ushort> dataAsVector = Unsafe.ReadUnaligned<Vector256<ushort>>(ref Unsafe.As<char, byte>(ref data));
                    if (!Avx2.TestZ(notAsciiCheck, dataAsVector))
                    {
                        return -1;
                    }
                    data = ref Unsafe.Add(ref data, SIZE_OF_VECTOR256_IN_CHARS);
                } while (!Unsafe.IsAddressGreaterThan(ref data, ref lastPositionWhereCanReadVector));
            }

            return data;
        }

        private static int GetUtf8ByteCount_VectorizedNonAscii(ref char data, nuint length)
        {
            Debug.Assert(length >= SIZE_OF_VECTOR256_IN_CHARS);

            ref char lastPositionWhereCanReadVector = ref Unsafe.Add(ref Unsafe.Add(ref data, length), -SIZE_OF_VECTOR256_IN_CHARS);

            // Check for 2-byte and 3-byte sequences (no surrogates yet)

            Vector256<short> normalizationVector = Avx2.SetAllVector256<short>(short.MinValue);
            Vector256<short> twoUtf8ByteCheckVector = Avx2.SetAllVector256<short>(0x7F + short.MinValue);
            Vector256<short> threeUtf8ByteCheckVector = Avx2.SetAllVector256<short>(0x7FF + short.MinValue);

            Vector256<short> surrogateNormalizationVector = Avx2.SetAllVector256<short>(0x2000);
            Vector256<short> isSurrogateCheckVector = Avx2.SetAllVector256<short>(0x77FF);
            Vector256<short> isLowSurrogateCheckVector = Avx2.SetAllVector256<short>(0x7BFF);

            int accum = 0;

            do
            {
                Vector256<short> dataAsVector = Unsafe.ReadUnaligned<Vector256<short>>(ref Unsafe.As<char, byte>(ref data));
                dataAsVector = Avx2.Xor(dataAsVector, normalizationVector);

                var maskedVector = Avx2.Or(
                    Avx2.CompareGreaterThan(dataAsVector, threeUtf8ByteCheckVector),
                    Avx2.ShiftRightLogical(Avx2.CompareGreaterThan(dataAsVector, twoUtf8ByteCheckVector), 8));

                // Each element (word) of maskedVector is:
                // 0xFFFF if the original element was [ U+0800..U+FFFF ]
                // 0x00FF if the original element was [ U+0080..U+07FF ]
                // 0x0000 if the original element was [ U+0000..U+007F ]
                // We haven't yet checked for surrogate pairs.
                //
                // This means that when we turn this into a byte vector and popcnt the high bits of
                // each individual byte element, we'll see each adjacent pair of bits as:
                // 00 -> ASCII (popcnt = 0)
                // 01 -> 2-byte UTF-8 sequence (popcnt = 1)
                // 11 -> 3-byte UTF-8 sequence (popcnt = 2)
                // Then we add 16 to the result of the popcnt to get the real UTF-8 code unit count.

                var bytesRequired = Popcnt.PopCount((uint)Avx2.MoveMask(Avx2.StaticCast<short, byte>(maskedVector)));

                accum = accum + bytesRequired + SIZE_OF_VECTOR256_IN_CHARS;

                // Check for surrogates!

                IntPtr adjustmentForEndsWithSurrogate = IntPtr.Zero;
                dataAsVector = Avx2.Xor(dataAsVector, surrogateNormalizationVector);
                var isSurrogateMask = (uint)Avx2.MoveMask(Avx2.StaticCast<short, byte>(Avx2.CompareGreaterThan(dataAsVector, isSurrogateCheckVector)));
                if (isSurrogateMask != 0)
                {
                    var isLowSurrogateMask = (uint)Avx2.MoveMask(Avx2.StaticCast<short, byte>(Avx2.CompareGreaterThan(dataAsVector, isLowSurrogateCheckVector)));
                    var isHighSurrogateMask = (isSurrogateMask ^ isLowSurrogateMask) & unchecked((uint)~3);
                    if (isHighSurrogateMask != (isLowSurrogateMask << 2))
                    {
                        // invalid surrogate pair found (high not followed by low, or low not preceded by high)
                        return -1;
                    }

                    accum -= Popcnt.PopCount(isHighSurrogateMask);
                    adjustmentForEndsWithSurrogate = (IntPtr)(-((int)isSurrogateMask & 2));
                }
                data = ref Unsafe.Add(ref Unsafe.AddByteOffset(ref data, adjustmentForEndsWithSurrogate), SIZE_OF_VECTOR256_IN_CHARS);
            } while (!Unsafe.IsAddressGreaterThan(ref data, ref lastPositionWhereCanReadVector));

            return accum;
        }

        private static int GetUtf8ByteCount_BitMasks(ref char data, nuint length)
        {
            // Can we attempt vectorization?

            if (length >= 8)
            {
                ulong COMPARISON_CONST = 0xFF80FF80FF80FF80ul;
                ref char lastPositionWhereCanRead8Chars = ref Unsafe.Add(ref Unsafe.Add(ref data, length), -8);
                do
                {
                    if (((Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref data)) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref data, 4)))) & COMPARISON_CONST) != 0)
                    {
                        return -1;
                    }
                    data = ref Unsafe.Add(ref data, 8);
                } while (!Unsafe.IsAddressGreaterThan(ref data, ref lastPositionWhereCanRead8Chars));
            }

            return data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OperationStatus ConvertUtf16ToUtf8(ReadOnlySpan<char> source, Span<byte> destination, out int charsConsumed, out int bytesWritten)
        {
            return ConvertUtf16ToUtf8(ref MemoryMarshal.GetReference(source), source.Length, ref MemoryMarshal.GetReference(destination), destination.Length, out charsConsumed, out bytesWritten);
        }

        private static OperationStatus ConvertUtf16ToUtf8(ref char source, int sourceLength, ref byte destination, int destinationLength, out int charsConsumed, out int bytesWritten)
        {


            if (sourceLength < 2)
            {
                goto SmallInput;
            }

            ref char finalPosWhereCanReadTwoCharsFromInput = ref Unsafe.Add(ref Unsafe.Add(ref source, sourceLength), -2);

            // TODO: Vectorize me if possible

            // Begin the main loop.

#if DEBUG
            ref char lastBufferPosProcessed = ref Unsafe.AsRef<char>(null); // used for invariant checking in debug builds
#endif

            while (!Unsafe.IsAddressLessThan(ref finalPosWhereCanReadTwoCharsFromInput, ref source))
            {
                // Read 32 bits at a time. This is enough to hold any possible UTF-16 encoded scalar.

                uint thisDWord = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref source));

                AfterReadDWord:

#if DEBUG
                Debug.Assert(Unsafe.IsAddressLessThan(ref lastBufferPosProcessed, ref source), "Algorithm should've made forward progress since last read.");
                lastBufferPosProcessed = ref source;
#endif

                // First, check for the common case of all-ASCII input.

                if (DWordAllCharsAreAscii(thisDWord))
                {
                    // We read an all-ASCII sequence.
#error not implemented
                }

                // Next, try stripping off ASCII characters one at a time.
                // We only handle a single ASCII character here since we handled the two-ASCII char case above.

                if (DWordBeginsWithUtf16AsciiChar(thisDWord))
                {
                    if (destinationLength == 0)
                    {
                        goto DestinationTooSmall;
                    }

                    if (BitConverter.IsLittleEndian)
                    {
                        destination = (byte)thisDWord;
                        thisDWord >>= 16;
                    }
                    else
                    {
                        destination = (byte)(thisDWord >> 16);
                        thisDWord <<= 16;
                    }

                    source = ref Unsafe.Add(ref source, 1);

                    destination = ref Unsafe.Add(ref destination, 1);
                    destinationLength--;
                }

                // Check the [ U+0080..U+07FF ] case: 2 UTF-8 bytes.

                if (!DWordBeginsWithUtf16U0800OrHigherChar(thisDWord))
                {
                    if (BitConverter.IsLittleEndian)
                    {
                        if (destinationLength < 2)
                        {
                            goto DestinationTooSmall;
                        }

                        // We know the low WORD of thisDWord is [ 00000yyy yyxxxxxx ], so there's room
                        // in the high bits of the low WORD to store the "110" UTF-8 2-byte sequence marker.
                        //
                        // UTF-8 sequence is [ 110yyyyy 10xxxxxx ].

                        destination = (byte)((thisDWord + (0b110 << 11)) >> 6);
                        thisDWord = (thisDWord & 0xFFFF003FU) | 0x80; // continuation byte marker
                        Unsafe.Add(ref destination, 1) = (byte)thisDWord;
                        thisDWord >>= 16;

                        // Is this character followed by an ASCII character? We can't perform this
                        // optimization if we see a null character in the second position, as that
                        // character may have been introduced by the ASCII stripping step earlier
                        // in the processing loop. If we see a null character (which should be very
                        // uncommon), just go back to the start of the loop.

                        if (Utf8Util.IsInRangeInclusive(thisDWord, 0x00010000U, 0x007FFFFFU))
                        {
                            // ASCII character found!

                            if (destinationLength >= 3)
                            {
                                Unsafe.Add(ref destination, 2) = (byte)(thisDWord >> 16);

                                source = ref Unsafe.Add(ref source, 2); // consumed 2 source characters

                                destination = ref Unsafe.Add(ref destination, 3); // wrote 3 destination bytes
                                destinationLength -= 3;

                                continue; // continue with main loop
                            }
                            else
                            {
                                source = ref Unsafe.Add(ref source, 1); // consumed 1 source character

                                destination = ref Unsafe.Add(ref destination, 2); // wrote 2 destination bytes
                                destinationLength -= 2;

                                goto DestinationTooSmall;
                            }
                        }
                    }
                    else
                    {
#error not implemented
                    }
                }

            }

            SmallInput:

            DestinationTooSmall:

                #error not implemented

        }
    }
}
