﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Buffers.Text
{
    /// <summary>
    /// Represents a 24-bit Unicode scalar value.
    /// A scalar value is any value in the range [U+0000..U+D7FF] or [U+E000..U+10FFFF].
    /// </summary>
    public struct UnicodeScalar : IComparable<UnicodeScalar>, IEquatable<UnicodeScalar>
    {
        /// <summary>
        /// The Unicode Replacement Character U+FFFD.
        /// </summary>
        public static readonly UnicodeScalar ReplacementChar = new UnicodeScalar(0xFFFD);

        /// <summary>
        /// The integer value of this scalar.
        /// </summary>
        public readonly uint Value; // = U+0000 if using default init

        /// <summary>
        /// Constructs a Unicode scalar from the given UTF-16 code point.
        /// The code point must not be a surrogate.
        /// </summary>
        /// <param name="char"></param>
        public UnicodeScalar(char @char)
            : this((uint)@char, null)
        {
            // None of the APIs on this type are guaranteed to produce correct results
            // if we don't validate the input during construction.

            if (Utf8Util.IsLowWordSurrogate(Value))
            {
                throw new ArgumentOutOfRangeException(
                   message: "Value must be between U+0000 and U+D7FF, inclusive; or value must be between U+E000 and U+FFFF, inclusive.",
                   paramName: nameof(@char));
            }
        }

        /// <summary>
        /// Constructs a Unicode scalar from the given value.
        /// The value must represent a valid scalar.
        /// </summary>
        public UnicodeScalar(int value)
            : this((uint)value)
        {
        }

        /// <summary>
        /// Constructs a Unicode scalar from the given value.
        /// The value must represent a valid scalar.
        /// </summary>
        public UnicodeScalar(uint value)
            : this(value, null)
        {
            // None of the APIs on this type are guaranteed to produce correct results
            // if we don't validate the input during construction.

            if (!IsValidScalar(Value))
            {
                throw new ArgumentOutOfRangeException(
                    message: "Value must be between U+0000 and U+D7FF, inclusive; or value must be between U+E000 and U+10FFFF, inclusive.",
                    paramName: nameof(value));
            }
        }

        // Non-validating ctor for internal use
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private UnicodeScalar(uint value, object unused)
        {
            Value = value;
        }

        /// <summary>
        /// Converts a <see cref="char"/> to a <see cref="UnicodeScalar"/>.
        /// Identical to calling <see cref="UnicodeScalar.UnicodeScalar(char)"/>.
        /// </summary>
        public static explicit operator UnicodeScalar(char @char) => new UnicodeScalar(@char);

        /// <summary>
        /// Converts an <see cref="int"/> to a <see cref="UnicodeScalar"/>.
        /// Identical to calling <see cref="UnicodeScalar.UnicodeScalar(int)"/>.
        /// </summary>
        public static explicit operator UnicodeScalar(int value) => new UnicodeScalar(value);

        /// <summary>
        /// Converts a <see cref="uint"/> to a <see cref="UnicodeScalar"/>.
        /// Identical to calling <see cref="UnicodeScalar.UnicodeScalar(uint)"/>.
        /// </summary>
        public static explicit operator UnicodeScalar(uint value) => new UnicodeScalar(value);

        /// <summary>
        /// If this scalar is representable as a single UTF-16 code unit (see <see cref="Utf16CodeUnitCount"/>),
        /// returns the scalar value as the UTF-16 code unit. Otherwise throws <see cref="OverflowException"/>.
        /// </summary>
        public static explicit operator char(UnicodeScalar value) => checked((char)value.Value);

        /// <summary>
        /// Returns the scalar value as a 24-bit signed integer. Equivalent to reading the <see cref="Value"/> field and casting to <see cref="int"/>.
        /// </summary>
        public static explicit operator int(UnicodeScalar value) => (int)value.Value;

        /// <summary>
        /// Returns the scalar value as a 24-bit unsigned integer. Equivalent to reading the <see cref="Value"/> field.
        /// </summary>
        public static implicit operator uint(UnicodeScalar value) => value.Value;

        public static bool operator ==(UnicodeScalar a, UnicodeScalar b) => (a.Value == b.Value);

        public static bool operator !=(UnicodeScalar a, UnicodeScalar b) => (a.Value != b.Value);

        public static bool operator <(UnicodeScalar a, UnicodeScalar b) => (a.Value < b.Value);

        public static bool operator <=(UnicodeScalar a, UnicodeScalar b) => (a.Value <= b.Value);

        public static bool operator >(UnicodeScalar a, UnicodeScalar b) => (a.Value > b.Value);

        public static bool operator >=(UnicodeScalar a, UnicodeScalar b) => (a.Value >= b.Value);

        private bool IsValid => IsValidScalar(Value);

        /// <summary>
        /// Returns the number of UTF-16 code units (<see cref="char"/>s) required to represent this scalar.
        /// Scalars in the range [U+0000..U+FFFF] require one UTF-16 code unit.
        /// Scalars in the range [U+10000..U+10FFFF] require two UTF-16 code units.
        /// </summary>
        public int Utf16CodeUnitCount
        {
            get
            {
                Debug.Assert(IsValid);
                return (Value < 0x10000U) ? 1 : 2;
            }
        }

        /// <summary>
        /// Returns the number of UTF-8 code units (<see cref="byte"/>s) required to represent this scalar.
        /// Scalars in the range [U+0000..U+007F] require one UTF-8 code unit.
        /// Scalars in the range [U+0080..U+07FF] require two UTF-8 code units.
        /// Scalars in the range [U+0800..U+FFFF] require three UTF-8 code unit.
        /// Scalars in the range [U+10000..U+10FFFF] require four UTF-8 code units.
        /// </summary>
        public int Utf8CodeUnitCount
        {
            get
            {
                Debug.Assert(IsValid);

                if (Value < 0x80U)
                {
                    return 1;
                }
                else if (Value < 0x800U)
                {
                    return 2;
                }
                else if (Value < 0x10000U)
                {
                    return 3;
                }
                else
                {
                    return 4;
                }
            }
        }

        public int CompareTo(UnicodeScalar other)
        {
            return this.Value.CompareTo(other.Value);
        }

        /// <summary>
        /// Copies the UTF-16 code unit representation of this scalar to an output buffer.
        /// The buffer must be large enough to hold the required number of <see cref="char"/>s.
        /// The <see cref="Utf16CodeUnitCount"/> property gives the required output buffer length.
        /// </summary>
        public void CopyUtf16CodeUnitsTo(Span<char> utf16)
        {
            // See the Unicode Standard, Table 3-5.
            Debug.Assert(IsValid);

            if (Value < 0x10000U && utf16.Length >= 1)
            {
                // Scalar is single UTF-16 code unit
                utf16[0] = (char)Value;
            }
            else if (utf16.Length >= 2)
            {
                // Scalar is surrogate pair
                utf16[0] = (char)(0xD7C0U + (Value >> 10));
                utf16[1] = (char)(0xDC00U | (Value & 0x3FFU));
            }
            else
            {
                throw new ArgumentException(
                    message: "Argument is not long enough to hold output value.",
                    paramName: nameof(utf16));
            }
        }

        /// <summary>
        /// Copies the UTF-8 code unit representation of this scalar to an output buffer.
        /// The buffer must be large enough to hold the required number of <see cref="byte"/>s.
        /// The <see cref="Utf8CodeUnitCount"/> property gives the required output buffer length.
        /// </summary>
        public void CopyUtf8CodeUnitsTo(Span<byte> utf8)
        {
            // See the Unicode Standard, Table 3-6.
            Debug.Assert(IsValid);

            if (Value < 0x80U && utf8.Length >= 1)
            {
                // Single UTF-8 code unit
                utf8[0] = (byte)Value;
            }
            else if (Value < 0x800U && utf8.Length >= 2)
            {
                // Two UTF-8 code units
                utf8[0] = (byte)(0xC0U | (Value >> 6));
                utf8[1] = (byte)(0x80U | (Value & 0x3FU));
            }
            else if (Value < 0x10000U && utf8.Length >= 3)
            {
                // Three UTF-8 code units
                utf8[0] = (byte)(0xE0U | (Value >> 12));
                utf8[1] = (byte)(0x80U | ((Value >> 6) & 0x3FU));
                utf8[2] = (byte)(0x80U | (Value & 0x3FU));
            }
            else if (utf8.Length >= 4)
            {
                // Four UTF-8 code units
                utf8[0] = (byte)(0xF0U | (Value >> 18));
                utf8[1] = (byte)(0x80U | ((Value >> 12) & 0x3FU));
                utf8[2] = (byte)(0x80U | ((Value >> 6) & 0x3FU));
                utf8[3] = (byte)(0x80U | (Value & 0x3FU));
            }
            else
            {
                throw new ArgumentException(
                  message: "Argument is not long enough to hold output value.",
                  paramName: nameof(utf8));
            }
        }

        internal static UnicodeScalar CreateWithoutValidation(uint value) => new UnicodeScalar(value, null);

        public override bool Equals(object other) => ((other is UnicodeScalar) && this.Equals((UnicodeScalar)other));

        public bool Equals(UnicodeScalar other) => (this.Value == other.Value);

        public override int GetHashCode() => (int)Value;

        // Returns this scalar as a printable character; returns the replacement char if the scalar is invalid.
        private string GetPrintableCharacter() => ((IsValid) ? this : ReplacementChar).GetPrintableCharacterCore();

        // Returns this scalar as a printable character; no validity checking is performed.
        private unsafe string GetPrintableCharacterCore()
        {
            int codeUnitCount = Utf16CodeUnitCount;
            char* pChars = stackalloc char[codeUnitCount];
            Span<char> chars = new Span<char>(pChars, codeUnitCount);
            CopyUtf16CodeUnitsTo(chars);
            return new String(chars);
        }

        private static bool IsValidScalar(uint value) =>
            (value < 0xD800U) || Utf8Util.IsInRangeInclusive(value, 0xE000U, 0x10FFFFU);

        public override string ToString()
        {
            // This can be made much more efficient.
            return FormattableString.Invariant($"U+{Value:X4} ({GetPrintableCharacter()})");
        }
    }
}
