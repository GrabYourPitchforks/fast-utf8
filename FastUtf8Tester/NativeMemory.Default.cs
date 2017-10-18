using System;

namespace FastUtf8Tester
{
    public static partial class NativeMemory
    {
        // for non-Windows systems
        private static INativeMemory AllocateWithoutDataPopulationDefault(int cb)
        {
            return new ArrayWrapper(cb);
        }

        private sealed class ArrayWrapper : INativeMemory
        {
            private readonly byte[] _array;

            public ArrayWrapper(int cb)
            {
                _array = new byte[cb];
            }

            public bool IsReadonly => false;

            public Span<byte> Span => _array;

            public void Dispose()
            {
                // no-op
            }

            public void MakeReadonly()
            {
                // no-op
            }

            public void MakeWriteable()
            {
                // no-op
            }
        }
    }
}
