using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Buffers.Text
{
    internal static partial class Utf16Util
    {
        /// <summary>
        /// Returns <see langword="true"/> iff all chars in <paramref name="value"/> are ASCII.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool DWordAllCharsAreAscii(uint value)
        {
            return ((value & 0xFF80FF80U) == 0U);
        }

        /// <summary>
        /// Given a UTF-16 buffer which has been read into a DWORD in machine endianness,
        /// returns <see langword="true"/> iff the first UTF-16 code unit of the buffer
        /// represents an ASCII character.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool DWordBeginsWithUtf16AsciiChar(uint value)
        {
            return (BitConverter.IsLittleEndian && ((value & 0xFF80U) == 0))
                || (!BitConverter.IsLittleEndian && (value <= 0x007FFFFFU));
        }

        /// <summary>
        /// Given a UTF-16 buffer which has been read into a DWORD in machine endianness,
        /// returns <see langword="true"/> iff the first UTF-16 code unit of the buffer
        /// represents U+0800 or higher.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool DWordBeginsWithUtf16U0800OrHigherChar(uint value)
        {
            return (BitConverter.IsLittleEndian && ((value & 0xF800U) != 0))
                || (!BitConverter.IsLittleEndian && (value >= 0x08000000U));
        }
    }
}
