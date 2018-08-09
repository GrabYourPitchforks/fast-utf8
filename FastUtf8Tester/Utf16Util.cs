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
        private static int GetUtf8ByteCount(ref char data, nuint length) => GetUtf8ByteCount_VectorizedAscii(ref data, length);

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
    }
}
