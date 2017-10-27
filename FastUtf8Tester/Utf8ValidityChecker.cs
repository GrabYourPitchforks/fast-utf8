using System;
using System.Diagnostics;

namespace FastUtf8Tester
{
    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    public struct Utf8ValidityChecker
    {
        // Packed data that contains both the partial sequence seen and the expected number of bytes remaining.
        // PS1B: partial sequence first byte, etc.
        // LEN: BYTE stating how many bytes (0 .. 3) have been read so far into the partial sequence.
        // If the high bit of LEN is set, an invalid sequence was seen earlier.
        // Big-endian machine: [ PS1B, PS2B, PS3B, LEN ]
        // Little-endian machine: [ PS3B, PS2B, PS1B, LEN ]
        private uint _partialSequence;

        private string DebuggerDisplay
        {
            get
            {
                if (_partialSequence == 0)
                {
                    return "Data VALID so far; no partial sequence consumed.";
                }
                else if (IsInvalid)
                {
                    return "Data INVALID.";
                }
                else
                {
                    switch ((byte)_partialSequence)
                    {
                        case 1:
                            return (BitConverter.IsLittleEndian)
                                ? FormattableString.Invariant($"Data VALID so far; partial sequence [ {(byte)(_partialSequence >> 8):X2} ] consumed.")
                                : FormattableString.Invariant($"Data VALID so far; partial sequence [ {(_partialSequence >> 24):X2} ] consumed.");
                        case 2:
                            return (BitConverter.IsLittleEndian)
                                ? FormattableString.Invariant($"Data VALID so far; partial sequence [ {(byte)(_partialSequence >> 8):X2} {(byte)(_partialSequence >> 16):X2} ] consumed.")
                                : FormattableString.Invariant($"Data VALID so far; partial sequence [ {(_partialSequence >> 24):X2} {(_partialSequence >> 16):X2} ] consumed.");

                        case 3:
                            return (BitConverter.IsLittleEndian)
                                ? FormattableString.Invariant($"Data VALID so far; partial sequence [ {(byte)(_partialSequence >> 8):X2} {(byte)(_partialSequence >> 16):X2} {(_partialSequence >> 24):X2} ] consumed.")
                                : FormattableString.Invariant($"Data VALID so far; partial sequence [ {(_partialSequence >> 24):X2} {(_partialSequence >> 16):X2} {(byte)(_partialSequence >> 8):X2} ] consumed.");

                        default:
                            return "** INTERNAL ERROR **";
                    }
                }
            }
        }

        private bool IsInvalid => ((byte)_partialSequence & 0x80) != 0;

        private void MarkInvalid()
        {
            _partialSequence |= 0x80;
        }

        private void Reset()
        {
            _partialSequence = 0;
        }

        public unsafe bool TryConsume(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                // Quick escape: no incoming data => we already know if we're valid

                return !IsInvalid;
            }
            if (_partialSequence == 0)
            {
                // Common case: no partial sequence remains, we can just consume data as-is.

                var indexOfFirstInvalidSequence = Utf8Utility.GetIndexOfFirstInvalidUtf8Sequence(bytes);
                if (indexOfFirstInvalidSequence < 0)
                {
                    // Successfully consumed entire buffer without error
                    return true;
                }
                else
                {
                    // Couldn't consume entire buffer; is this due to a partial buffer or truly invalid data?
                    bytes = bytes.Slice(indexOfFirstInvalidSequence);
                    var validity = Utf8Utility.PeekFirstSequence(bytes, out int numBytesConsumed, out _);
                    if (validity == SequenceValidity.Incomplete)
                    {
                        // Saw a partial (not invalid) sequence, remember it for next time
                        Debug.Assert(1 <= numBytesConsumed && numBytesConsumed <= 3);
                        uint* pNewPartialSequence = stackalloc uint[1];
                        bytes.Slice(0, numBytesConsumed).CopyTo(new Span<byte>(pNewPartialSequence, 3));
                        if (BitConverter.IsLittleEndian)
                        {
                            _partialSequence = ((*pNewPartialSequence) << 8) | (uint)numBytesConsumed;
                        }
                        else
                        {
                            _partialSequence = *pNewPartialSequence | (uint)numBytesConsumed;
                        }

                        return true;
                    }
                    else
                    {
                        // Truly invalid data
                        Debug.Assert(validity == SequenceValidity.Invalid); // shouldn't have gotten 'Empty' or 'WellFormed'
                        MarkInvalid();
                        return false;
                    }
                }
            }
            else if (!IsInvalid)
            {
                // Less common case: there's a partial sequence and we need to stitch it to the incoming data.

                var originalBytesSpan = bytes;
                int originalPartialSequenceByteCount = (byte)_partialSequence;
                int newPartialSequenceByteCount = originalPartialSequenceByteCount;

                uint* pNewPartialSequence = stackalloc uint[1];
                if (BitConverter.IsLittleEndian)
                {
                    *pNewPartialSequence = _partialSequence >> 8;
                }
                else
                {
                    *pNewPartialSequence = _partialSequence;
                }

                Span<byte> partialSequenceAsBytes = new Span<byte>(pNewPartialSequence, 4);
                while (newPartialSequenceByteCount < 4 && bytes.Length > 1)
                {
                    partialSequenceAsBytes[newPartialSequenceByteCount] = bytes[0];
                    newPartialSequenceByteCount++;
                    bytes = bytes.Slice(1);
                }

                // We've either completely populated our partial sequence buffer or we've run out
                // of incoming data. Either way let's try checking the validity of the partial
                // sequence buffer once more.

                var validity = Utf8Utility.PeekFirstSequence(partialSequenceAsBytes.Slice(0, newPartialSequenceByteCount), out int numBytesConsumed, out _);
                Debug.Assert(1 <= numBytesConsumed && numBytesConsumed <= 4);

                if (validity == SequenceValidity.WellFormed)
                {
                    // This is the happy path; we've consumed some set of bytes from the input
                    // buffer and it has caused the partial sequence to validate. Let's calculate
                    // how many bytes from the input buffer were required to complete the sequence,
                    // then strip them off the incoming data and recurse. Note: recursion of this method
                    // is safe since the next iteration will begin with 'no partial sequence', so
                    // the max recursion depth will never be more than 2, hence no stack overflow risk.

                    Reset();
                    return TryConsume(originalBytesSpan.Slice(numBytesConsumed - originalPartialSequenceByteCount));
                }
                else if (validity == SequenceValidity.Incomplete)
                {
                    // We've consumed all data available to us and we still have an incomplete sequence.
                    // It's still valid (until we see invalid bytes), so squirrel away what we've seen
                    // and report success to our caller.

                    Debug.Assert(numBytesConsumed < 4);
                    Debug.Assert(bytes.IsEmpty);

                    if (BitConverter.IsLittleEndian)
                    {
                        _partialSequence = *pNewPartialSequence << 8;
                    }
                    else
                    {
                        _partialSequence = *pNewPartialSequence & ~(uint)0xFF;
                    }
                    _partialSequence |= (uint)numBytesConsumed;

                    return true;
                }
                else
                {
                    // Truly invalid data.

                    Debug.Assert(validity == SequenceValidity.Invalid);

                    MarkInvalid();
                    return false;
                }
            }
            else
            {
                // Rare case: A previous call contained invalid data, so this instance is forever tainted.
                return false;
            }
        }
    }
}
