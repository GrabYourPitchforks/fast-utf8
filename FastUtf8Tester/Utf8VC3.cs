// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Buffers.Text
{
    public struct Utf8VC3
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ROL8(byte value)
        {
            return (uint)((value >> 4) | (value << 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ROL32(uint value, int shift)
        {
            // shift &= 0x1F;
            return (value << shift) | (value >> (32 - shift));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ROR32(uint value, int shift)
        {
            return (value >> shift) | (value << (32 - shift));
        }

        public Status Foo(byte codePoint)
        {
            return Foo2(codePoint, _data, out _data);
        }
        public enum Status
        {
            Done = 0,
            Incomplete = 1,
            Invalid = 2
        }

        public static uint Foo3AndUpdateState(uint codePoint, uint oldState)
        {
            // The state value is [ w0000000 zzzzzzzz yyyyyyyy xxxxxxxx ],
            // where zzzzzzzz = number of continuation bytes left to read,
            //       yyyyyyyy = the lower bound (inclusive) of the next valid continuation byte,
            //       xxxxxxxx = the range [upper bound - lower bound] (inclusive) of the next valid continuation byte,
            // and   w is set if there was an error.

            // We only care about zzzzzzzz for the purpose of determining whether we're
            // expecting to read the start of a sequence or a continuation code unit.

            if ((oldState & 0xFF0000U) == 0)
            {
                // If this code unit is [ 00..7F ], then it's an ASCII character,
                // which is a valid single-unit sequence, so we're done.

                if ((codePoint & 0x80) == 0) { return 0; }

                // If this code unit is [ C2..DF ], then it's the start of a 2-byte sequence.
                // We expect one more continuation byte, and its valid range is [ 80..BF ].

                codePoint -= 0xC2U;
                if (codePoint <= (0xDFU - 0xC2U)) { return 0x1803F; }

                // If this code unit is [ E0..EF ], then it's the start of a 3-byte sequence.
                // We expect two more continuation bytes, but the valid range of the first
                // continuation byte depends on the value of the first byte of the sequence.

                codePoint -= (0xE0U - 0xC2U);
                if (codePoint <= (0xEFU - 0xE0U))
                {
                    return ((1U >> (int)codePoint) << 13) // 0x2000 iff code point is E0
                        + ((0x1BFFC0U >> (int)codePoint) & 0x20U) // 0x20 iff code point is *not* E0 or ED, 
                        + 0x20000U // 2 continuation bytes expected
                        + 0x8000U // lower bound (inclusive) is 80 (or A0 iff code point is E0)
                        + 0x1FU; // range (inclusive) is 1F iff code point is E0 or ED, otherwise 3F
                }

                // If this code unit is [ F0..F4 ], then it's the start of a 4-byte sequence.
                // We expect three more continuation bytes, but the valid range of the first
                // continuation byte depends on the value of the first byte in the sequence.

                codePoint -= (0xF0U - 0xE0U);
                if (codePoint <= (0xF4U - 0xF0U))
                {
                    return ((1U >> (int)codePoint) << 13) // 0x2000 iff code point is F0
                        + ((0x33320U >> (4 * (int)codePoint)) & 0xF0U) // 0x20 iff code point is F0, 0x30 iff code point is F1..F3, 0x00 iff code point is F4
                        + 0x30000U // 3 continuation bytes expected
                        + 0x8000U // lower bound (inclusive) is 80 (or 90 iff code point is F0)
                        + 0xFU; // range (inclusive) is 0F, 2F, or 3F, depending on first byte
                }
            }
            else
            {
                // We're expecting a continuation byte.

                if ((codePoint - ((uint)(ushort)oldState >> 8)) <= (byte)oldState)
                {
                    // Incoming continuation byte was in the valid range.
                    // Decrement zzzzzzzz by 1 and reset yyyyyyyyxxxxxxxx to 803F.
                    // The code below is written such that this becomes a single AND and LEA.

                    return (oldState & 0x30000U) + 0x803FU - 0x10000U;
                }
            }

            // If we reached this point, an invalid code unit was seen.
            // Report an error (w bit set) to the caller.

            return (uint)1 << 31;
        }

        private static Status Foo2(uint codePoint, uint oldData, out uint newData)
        {
            uint tempNewData;
            Status retVal;

            if (oldData == 0)
            {
                // first byte

                // ASCII?
                if ((codePoint & 0x80) == 0) { goto Done; }

                codePoint -= 0xC2U;
                if (codePoint <= (0xDFU - 0xC2U))
                {
                    tempNewData = 0x180BFU;
                    goto Incomplete;
                }

                codePoint -= (0xE0U - 0xC2U);
                if (codePoint <= (0xEFU - 0xE0U))
                {
                    //if (codePoint == 0) { tempNewData = 0x2A0BFU; }
                    //else if (codePoint == 0xDU) { tempNewData = 0x2809FU; }
                    //else { tempNewData = 0x280BFU; }
                    //goto Incomplete;

                    tempNewData = (ROL32(0x802000U, (int)codePoint) & 0x2020U) ^ 0x280BFU;
                    goto Incomplete;
                }

                codePoint -= (0xF0U - 0xE0U);
                if (codePoint <= (0xF4U - 0xF0U))
                {
                    tempNewData = (ROL32(0x30001000U, 2 * (int)codePoint) & 0x1030U) ^ 0x380BFU;
                    goto Incomplete;
                }

                goto Error;
            }
            else
            {
                // continuation byte
                if (!Utf8Util.IsInRangeInclusive(codePoint, (uint)(ushort)oldData >> 8, (byte)oldData)) { goto Error; }

                if (oldData < 0x20000U) { goto Done; }

                tempNewData = (oldData & 0x30000U) + 0x80BFU - 0x10000U;
                goto Incomplete;
            }

            Done:
            retVal = Status.Done;
            goto ReturnWithEmptyNewData;

            Error:
            retVal = Status.Invalid;
            goto ReturnWithEmptyNewData;

            Incomplete:
            retVal = Status.Incomplete;
            goto Return;

            ReturnWithEmptyNewData:
            tempNewData = 0;
            goto Return;

            Return:
            newData = tempNewData;
            return retVal;
        }

        private uint _data;
    }
}
