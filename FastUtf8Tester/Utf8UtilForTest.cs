using System;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    public static class Utf8UtilForTest
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetIndexOfFirstInvalidUtf8Sequence(ReadOnlySpan<byte> inputBuffer, out int runeCount, out int surrogatePairCount)
            => System.Buffers.Text.Utf8Util.GetIndexOfFirstInvalidUtf8Sequence(inputBuffer, out runeCount, out surrogatePairCount);

    }
}
