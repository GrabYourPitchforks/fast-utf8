using System;
using System.Runtime.CompilerServices;

namespace FastUtf8Tester
{
    public static class Utf8UtilForTest
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetIndexOfFirstInvalidUtf8Sequence(ReadOnlySpan<byte> inputBuffer, out int runeCount, out int surrogatePairCount)
            => Utf8Util.GetIndexOfFirstInvalidUtf8Sequence(ref inputBuffer.DangerousGetPinnableReference(), inputBuffer.Length, out runeCount, out surrogatePairCount);

    }
}
