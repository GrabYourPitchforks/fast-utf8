using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    internal static partial class Utf8Util
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetIndexOfFirstInvalidByte(ReadOnlySpan<byte> input)
        {
            return Utf8Utility.GetIndexOfFirstInvalidUtf8Sequence(input);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] CreateValidUtf8StringFromPossiblyInvalidUtf8Input(ReadOnlySpan<byte> input, bool suppressCreationIfInputIsValid = false)
        {
            int offsetOfFirstInvalidByte = GetIndexOfFirstInvalidByte(input);
            if (offsetOfFirstInvalidByte < 0)
            {
                // No invalid data; return original array as-is
                return (suppressCreationIfInputIsValid) ? null : input.ToArray();
            }
            else
            {
                return CreateValidUtf8StringFromKnownInvalidUtf8Input(input, offsetOfFirstInvalidByte);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] CreateValidUtf8StringFromKnownInvalidUtf8Input(ReadOnlySpan<byte> input, int offsetOfFirstInvalidByte)
        {
            Debug.Assert(offsetOfFirstInvalidByte >= 0);

            //
            // First, need to calculate how many bytes will be in the final valid UTF8 string.
            //

            int totalValidByteCount = input.Length; // for now assume every byte is invalid; we'll adjust this down as we iterate
            int totalInvalidSequenceCount = 0;

            ReadOnlySpan<byte> sequenceWithKnownInvalidStart = input.Slice(offsetOfFirstInvalidByte);
            while (true)
            {
                totalInvalidSequenceCount++;
                int numInvalidBytesAtStartOfSequence = GetInvalidByteCount(ref sequenceWithKnownInvalidStart.DangerousGetPinnableReference(), sequenceWithKnownInvalidStart.Length);
                totalValidByteCount -= numInvalidBytesAtStartOfSequence;
                Debug.Assert(numInvalidBytesAtStartOfSequence > 0); // We should always point to a span which is known to begin with an invalid sequence

                var remainder = sequenceWithKnownInvalidStart.Slice(numInvalidBytesAtStartOfSequence); // skip over all invalid bytes at the beginning of the sequence
                int offsetOfNextInvalidByte = GetIndexOfFirstInvalidByte(remainder);
                if (offsetOfNextInvalidByte < 0)
                {
                    break; // total valid byte count and total invalid sequence count are now properly counted
                }
                else
                {
                    sequenceWithKnownInvalidStart = remainder.Slice(offsetOfNextInvalidByte); // and go back to beginning of loop
                }
            }

            byte[] retVal = new byte[checked(totalValidByteCount + totalInvalidSequenceCount * 3 /* U+FFFD is 3 UTF8 code units */)];
            Span<byte> retValRemainder = retVal;

            //
            // Now, process the input sequence again, copying runs of valid bytes to the output buffer
            // and replacing invalid sequences with U+FFFD.
            //

            input.Slice(0, offsetOfFirstInvalidByte).CopyTo(retValRemainder);
            input = input.Slice(offsetOfFirstInvalidByte);
            retValRemainder = retValRemainder.Slice(offsetOfFirstInvalidByte);

            while (true)
            {
                // Write U+FFFD as UTF8 ([ EF BF BD ]).
                retValRemainder[0] = 0xEF;
                retValRemainder[1] = 0xBF;
                retValRemainder[2] = 0xBD;
                retValRemainder = retValRemainder.Slice(3);

                // Skip over invalid bytes at beginning of input sequence.
                input = input.Slice(GetInvalidByteCount(ref input.DangerousGetPinnableReference(), input.Length));

                // Find the start of the next invalid byte, copying over all valid bytes from
                // the input buffer to the output buffer.
                offsetOfFirstInvalidByte = GetIndexOfFirstInvalidByte(input);
                if (offsetOfFirstInvalidByte < 0)
                {
                    input.CopyTo(retValRemainder); // the remainder of the input is valid
                    retValRemainder = retValRemainder.Slice(input.Length);
                    break;
                }
                else
                {
                    input.Slice(0, offsetOfFirstInvalidByte).CopyTo(retValRemainder);
                    retValRemainder = retValRemainder.Slice(offsetOfFirstInvalidByte);
                }
            }

            //
            // All data should have been copied from input to output.
            // We're done.
            //

            Debug.Assert(retValRemainder.IsEmpty);
            return retVal;
        }
    }
}
