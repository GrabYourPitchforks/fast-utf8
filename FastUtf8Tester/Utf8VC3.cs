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
        private static uint ROL32(uint value, int shift)
        {
            // shift &= 0x1F;
            return (value << shift) | (value >> (32 - shift));
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
