// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Buffers.Text
{
    public struct Utf8VC2
    {
        private uint _data;

        private const int CONT_BYTE_SHIFT = 6;
        private const int STATE_SHIFT = 29;

        private const uint STATE_NO_DATA = 0;
        private const uint STATE_CONSUME_TWO_CONT_BYTES = 1;
        private const uint STATE_CONSUME_A0BF_THEN_ONE_CONT_BYTE = 2;
        private const uint STATE_CONSUME_809F_THEN_ONE_CONT_BYTE = 3;
        private const uint STATE_CONSUME_ONE_CONT_BYTE = 4;
        private const uint STATE_CONSUME_THREE_CONT_BYTES = 5;
        private const uint STATE_CONSUME_90BF_THEN_TWO_CONT_BYTES = 6;
        private const uint STATE_CONSUME_808F_THEN_TWO_CONT_BYTES = 7;

        private static Status ConsumeCodeUnitCore(uint codeUnit, uint existingData, out uint newData, out UnicodeScalar scalar)
        {
            // The state machine here is given by the Unicode Standard, Table 3-7.
            // http://www.unicode.org/versions/Unicode10.0.0/ch03.pdf

            uint tempScalar;
            uint tempNewData;
            Status tempReturnCode;

            switch (existingData >> STATE_SHIFT)
            {
                case STATE_NO_DATA:
                    // No data consumed so far; consume the first byte.
                    {
                        if ((codeUnit & 0x80) == 0)
                        {
                            // ASCII byte - return as-is
                            tempScalar = codeUnit;
                            goto Success;
                        }

                        codeUnit -= 0xC2;
                        if (codeUnit <= (0xDF - 0xC2))
                        {
                            // Code unit is between [ C2..DF ]; this begins a 2-byte sequence.
                            tempNewData = codeUnit | (STATE_CONSUME_ONE_CONT_BYTE << STATE_SHIFT);
                            goto Incomplete;
                        }

                        codeUnit -= (0xE0 - 0xC2);
                        if (Environment.Is64BitProcess)
                        {
                            // Optimistically assume code unit is between [ E0..F4 ]; this begins a 3- or 4-byte sequence.

                            const ulong NEW_STATE_LOOKUP = 0b111_101_101_101_110_001_001_011_001_001_001_001_001_001_001_001_001_001_001_001_010UL;
                            //                                F4  F3  F2  F1  F0  EF  EE  ED  EC  EB  EA  E9  E8  E7  E6  E5  E4  E3  E2  E1  E0

                            tempNewData = codeUnit & 0xF;
                            uint newState = (uint)(NEW_STATE_LOOKUP >> (3 * (int)codeUnit)) & 0x7;
                            if (newState == 0) { goto Error; } // not within [ E0..F4 ], so within [ 80..C1 ] or [ F5..FF ], which are invalid leading bytes.

                            tempNewData |= newState << STATE_SHIFT;
                            goto Incomplete;
                        }
                        else
                        {
                            if (codeUnit <= (0xEF - 0xE0))
                            {
                                // Code unit is between [ E0..EF ]; this begins a 3-byte sequence.

                                const uint NEW_STATE_LOOKUP = 0b01_01_11_01_01_01_01_01_01_01_01_01_01_01_01_10U;
                                //                              EF EE ED EC EB EA E9 E8 E7 E6 E5 E4 E3 E2 E1 E0

                                tempNewData = codeUnit;
                                uint newState = (NEW_STATE_LOOKUP >> (2 * (int)codeUnit)) & 0x3;

                                tempNewData |= newState << STATE_SHIFT;
                                goto Incomplete;
                            }

                            codeUnit -= (0xF0 - 0xE0);
                            {
                                // Optimisitcally assume code unit is between [ F0..F4 ]; this begins a 4-byte sequence.

                                const uint NEW_STATE_LOOKUP = 0b111_101_101_101_110U;
                                //                               F4  F3  F2  F1  F0

                                tempNewData = codeUnit;
                                uint newState = (NEW_STATE_LOOKUP >> (3 * (int)codeUnit)) & 0x7;
                                if (newState == 0) { goto Error; } // not within [ F0..F4 ], so within [ 80..C1 ] or [ F5..FF ], which are invalid leading bytes.

                                tempNewData |= newState << STATE_SHIFT;
                                goto Incomplete;
                            }
                        }
                    }

                case STATE_CONSUME_ONE_CONT_BYTE:
                    {
                        // Expecting one more continuation byte.

                        // If a continuation byte has the form 10xxxxxx, then can flip the first bit and
                        // compare value <= 00111111. Bonus: after first bit is flipped, can OR this same
                        // comparand without further bit twiddling into the existing data buffer.

                        codeUnit ^= 0x80;
                        if (codeUnit > 0x3F) { goto Error; }

                        tempScalar = (existingData << CONT_BYTE_SHIFT) | codeUnit;
                        goto Success;
                    }

                case STATE_CONSUME_TWO_CONT_BYTES:
                    {
                        // Expecting two more continuation bytes.

                        codeUnit ^= 0x80;
                        if (codeUnit > 0x3F) { goto Error; }

                        tempNewData = (existingData << CONT_BYTE_SHIFT) | codeUnit | (STATE_CONSUME_ONE_CONT_BYTE << STATE_SHIFT);
                        goto Incomplete;
                    }

                case STATE_CONSUME_THREE_CONT_BYTES:
                    {
                        // Expecting three more continuation bytes.

                        codeUnit ^= 0x80;
                        if (codeUnit > 0x3F) { goto Error; }

                        tempNewData = (existingData << CONT_BYTE_SHIFT) | codeUnit | (STATE_CONSUME_TWO_CONT_BYTES << STATE_SHIFT);
                        goto Incomplete;
                    }

                case STATE_CONSUME_A0BF_THEN_ONE_CONT_BYTE:
                    {
                        // Expecting [ A0..BF ], then one more continuation byte.

                        codeUnit ^= 0xA0;
                        if (codeUnit > (0xBF - 0xA0)) { goto Error; }

                        tempNewData = (existingData << CONT_BYTE_SHIFT) | codeUnit | (0x20 | (STATE_CONSUME_ONE_CONT_BYTE << STATE_SHIFT));
                        goto Incomplete;
                    }

                case STATE_CONSUME_809F_THEN_ONE_CONT_BYTE:
                    {
                        // Expecting [ 80..9F ], then one more continuation byte.

                        codeUnit ^= 0x80;
                        if (codeUnit > (0x9F - 0x80)) { goto Error; }

                        tempNewData = (existingData << CONT_BYTE_SHIFT) | codeUnit | (STATE_CONSUME_ONE_CONT_BYTE << STATE_SHIFT);
                        goto Incomplete;
                    }

                case STATE_CONSUME_90BF_THEN_TWO_CONT_BYTES:
                    {
                        // Expecting [ 90..BF ], then two more continuation bytes.

                        codeUnit -= 0x90; // SUB less optimal than XOR but is required in this case
                        if (codeUnit > (0xBF - 0x90)) { goto Error; }

                        tempNewData = ((existingData << CONT_BYTE_SHIFT) | codeUnit) + (0x10 | (STATE_CONSUME_TWO_CONT_BYTES << STATE_SHIFT));
                        goto Incomplete;
                    }

                case STATE_CONSUME_808F_THEN_TWO_CONT_BYTES:
                    {
                        // Expecting [ 80..8F ], then two more continuation bytes.

                        codeUnit ^= 0x80;
                        if (codeUnit > (0x8F - 0x80)) { goto Error; }

                        tempNewData = (existingData << CONT_BYTE_SHIFT) | codeUnit | (STATE_CONSUME_TWO_CONT_BYTES << STATE_SHIFT);
                        goto Incomplete;
                    }
            }

            Error:
            {
                // Invalid.

                tempNewData = STATE_NO_DATA; // reset
                tempScalar = 0xFFFD;
                tempReturnCode = Status.Invalid;
                goto Return;
            }

            Incomplete:
            {
                // Incomplete but haven't yet seen invalid byte.

                tempScalar = 0xFFFD;
                tempReturnCode = Status.Incomplete;
                goto Return;
            }

            Success:
            {
                // Finished!

                tempNewData = STATE_NO_DATA; // reset;
                tempReturnCode = Status.Done;
                goto Return;
            }

            Return:
            {
                newData = tempNewData;
                scalar = UnicodeScalar.CreateWithoutValidation(tempScalar);
                return tempReturnCode;
            }
        }

        public Status ConsumeCodeUnit(byte codeUnit, out UnicodeScalar scalar)
        {
            return ConsumeCodeUnitCore(codeUnit, _data, out _data, out scalar);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ConsumeContinuationByte(byte codeUnit)
        {
            Debug.Assert(Utf8Utility.IsUtf8ContinuationByte(codeUnit));
            _data = (_data << 6) | (uint)(codeUnit & 0x3F);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Status InvalidAndReset(out UnicodeScalar scalar)
        {
            this = default;
            scalar = UnicodeScalar.ReplacementChar;
            return Status.Invalid;
        }

        public enum Status
        {
            Done = 0,
            Incomplete = 1,
            Invalid = 2
        }
    }
}
