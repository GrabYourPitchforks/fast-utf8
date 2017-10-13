using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace FastUtf8Tester
{
    /// <summary>
    /// Represents a region of memory that is bookended by poison pages. Attempts to access the
    /// page immediately before or immediately after this region of memory are guaranteed to
    /// AV. This is useful for testing method that perform unsafe memory access.
    /// </summary>
    internal sealed class PoisonBookendedMemory : IDisposable
    {
        private static readonly int SystemPageSize = Environment.SystemPageSize;

        private static readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        private readonly long _bytesRequested;
        private readonly VirtualAllocHandle _handle;
        private readonly long _totalBytesAllocated;

        /// <summary>
        /// Allocates a region of memory bookended by poison pages.
        /// The usable (non-poisoned) region of memory is guaranteed to be at least
        /// <paramref name="cb"/> bytes in length.
        /// </summary>
        public PoisonBookendedMemory(IntPtr cb)
        {
            // Platform check

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException("Memory protection requires Windows.");
            }

            // Check for invalid values

            _bytesRequested = (long)cb;
            if (_bytesRequested < 0)
            {
                throw new ArgumentOutOfRangeException(
                    message: "Value must represent a non-negative byte count.",
                    paramName: nameof(cb));
            }

            _totalBytesAllocated = _bytesRequested;

            checked
            {
                // We only need to round CB up if it's not an exact multiple
                // of the system page size.

                var leftoverBytes = _bytesRequested % SystemPageSize;
                if (leftoverBytes != 0)
                {
                    _totalBytesAllocated += SystemPageSize - leftoverBytes;
                }

                // Finally, account for the poison pages at the front and back.

                _totalBytesAllocated += 2 * SystemPageSize;
            }

            // Reserve and commit the entire range, marking NOACCESS.

            _handle = UnsafeNativeMethods.VirtualAlloc(
                lpAddress: IntPtr.Zero,
                dwSize: (IntPtr)_totalBytesAllocated,
                flAllocationType: VirtualAllocAllocationType.MEM_RESERVE | VirtualAllocAllocationType.MEM_COMMIT,
                flProtect: VirtualAllocProtection.PAGE_NOACCESS);

            if (_handle == null || _handle.IsInvalid)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                throw new InvalidOperationException("VirtualAlloc failed unexpectedly.");
            }

            // If there's a non-zero range to mark as READ+WRITE,
            // do it now.

            if (_bytesRequested > 0)
            {
                bool acquiredHandle = false;
                try
                {
                    _handle.DangerousAddRef(ref acquiredHandle);
                    if (!UnsafeNativeMethods.VirtualProtect(
                        lpAddress: _handle.DangerousGetHandle() + SystemPageSize /* bypass poison page */,
                        dwSize: (IntPtr)_bytesRequested,
                        flNewProtect: VirtualAllocProtection.PAGE_READWRITE,
                        lpflOldProtect: out _))
                    {
                        Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                        throw new InvalidOperationException("VirtualProtect failed unexpectedly.");
                    }
                }
                finally
                {
                    if (acquiredHandle)
                    {
                        _handle.DangerousRelease();
                    }
                }
            }
        }

        /// <summary>
        /// Allocates a region of memory bookended by poison pages.
        /// The usable (non-poisoned) region of memory is guaranteed to be at least
        /// <paramref name="cb"/> bytes in length.
        /// </summary>
        public PoisonBookendedMemory(int cb)
            : this((IntPtr)cb)
        {
        }

        /// <summary>
        /// Allocates a region of memory bookended by poison pages.
        /// The usable (non-poisoned) region of memory is guaranteed to be at least
        /// <paramref name="cb"/> bytes in length.
        /// </summary>
        public PoisonBookendedMemory(long cb)
            : this((IntPtr)cb)
        {
        }

        /// <summary>
        /// Disposes of this instance. All <see cref="Span{Byte}"/> ranges returned by this instance
        /// will be no longer valid.
        /// </summary>
        public void Dispose()
        {
            _handle.Dispose();
        }

        /// <summary>
        /// Returns a <see cref="Span{byte}"/> of length <paramref name="cb"/> bytes
        /// which is guaranteed to be immediately followed by a poison page. No guarantee
        /// is made that the returned span is immediately preceded by a poison page.
        /// </summary>
        /// <returns>
        /// A <see cref="Span{byte}"/> of length <paramref name="cb"/>.
        /// The span is populated with random data.
        /// </returns>
        /// <remarks>
        /// Multiple calls to the <see cref="GetSpanAtEnd(int)"/> and <see cref="GetSpanAtFront(int)"/>
        /// methods may return overlapping spans. The caller should only use one span at a time
        /// and should create another <see cref="PoisonBookendedMemory"/> instance if a guaranteed
        /// non-overlapping span is needed.
        /// </remarks>
        public unsafe Span<byte> GetSpanAtEnd(int cb)
        {
            if (cb < 0 || cb > _bytesRequested)
            {
                throw new ArgumentOutOfRangeException(paramName: nameof(cb));
            }

            bool acquiredHandle = false;
            try
            {
                _handle.DangerousAddRef(ref acquiredHandle);
                var retVal = new Span<byte>((void*)((long)_handle.DangerousGetHandle() + _totalBytesAllocated - SystemPageSize - cb), cb);
                _rng.GetBytes(retVal);
                return retVal;
            }
            finally
            {
                if (acquiredHandle)
                {
                    _handle.DangerousRelease();
                }
            }
        }

        /// <summary>
        /// Returns a <see cref="Span{byte}"/> of length <paramref name="cb"/> bytes
        /// which is guaranteed to be immediately preceded by a poison page. No guarantee
        /// is made that the returned span is immediately followed by a poison page.
        /// </summary>
        /// <returns>
        /// A <see cref="Span{byte}"/> of length <paramref name="cb"/>.
        /// The span is populated with random data.
        /// </returns>
        /// <remarks>
        /// Multiple calls to the <see cref="GetSpanAtEnd(int)"/> and <see cref="GetSpanAtFront(int)"/>
        /// methods may return overlapping spans. The caller should only use one span at a time
        /// and should create another <see cref="PoisonBookendedMemory"/> instance if a guaranteed
        /// non-overlapping span is needed.
        /// </remarks>
        public unsafe Span<byte> GetSpanAtFront(int cb)
        {
            if (cb < 0 || cb > _bytesRequested)
            {
                throw new ArgumentOutOfRangeException(paramName: nameof(cb));
            }

            bool acquiredHandle = false;
            try
            {
                _handle.DangerousAddRef(ref acquiredHandle);
                var retVal = new Span<byte>((void*)(_handle.DangerousGetHandle() + SystemPageSize), cb);
                _rng.GetBytes(retVal);
                return retVal;
            }
            finally
            {
                if (acquiredHandle)
                {
                    _handle.DangerousRelease();
                }
            }
        }

        internal sealed class VirtualAllocHandle : SafeHandle
        {
            // Called by P/Invoke when returning SafeHandles
            private VirtualAllocHandle()
                : base(IntPtr.Zero, ownsHandle: true)
            {
            }

            // Do not provide a finalizer - SafeHandle's critical finalizer will
            // call ReleaseHandle for you.

            public override bool IsInvalid => (handle == IntPtr.Zero);

            protected override bool ReleaseHandle()
            {
                return UnsafeNativeMethods.VirtualFree(handle, IntPtr.Zero, VirtualAllocAllocationType.MEM_RELEASE);
            }
        }

        // from winnt.h
        [Flags]
        private enum VirtualAllocAllocationType : uint
        {
            MEM_COMMIT = 0x1000,
            MEM_RESERVE = 0x2000,
            MEM_DECOMMIT = 0x4000,
            MEM_RELEASE = 0x8000,
            MEM_FREE = 0x10000,
            MEM_PRIVATE = 0x20000,
            MEM_MAPPED = 0x40000,
            MEM_RESET = 0x80000,
            MEM_TOP_DOWN = 0x100000,
            MEM_WRITE_WATCH = 0x200000,
            MEM_PHYSICAL = 0x400000,
            MEM_ROTATE = 0x800000,
            MEM_LARGE_PAGES = 0x20000000,
            MEM_4MB_PAGES = 0x80000000,
        }

        // from winnt.h
        [Flags]
        private enum VirtualAllocProtection : uint
        {
            PAGE_NOACCESS = 0x01,
            PAGE_READONLY = 0x02,
            PAGE_READWRITE = 0x04,
            PAGE_WRITECOPY = 0x08,
            PAGE_EXECUTE = 0x10,
            PAGE_EXECUTE_READ = 0x20,
            PAGE_EXECUTE_READWRITE = 0x40,
            PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_GUARD = 0x100,
            PAGE_NOCACHE = 0x200,
            PAGE_WRITECOMBINE = 0x400,
        }

        [SuppressUnmanagedCodeSecurity]
        private static class UnsafeNativeMethods
        {
            private const string KERNEL32_LIB = "kernel32.dll";

            // https://msdn.microsoft.com/en-us/library/windows/desktop/aa366887(v=vs.85).aspx
            [DllImport(KERNEL32_LIB, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            public static extern VirtualAllocHandle VirtualAlloc(
                [In] IntPtr lpAddress,
                [In] IntPtr dwSize,
                [In] VirtualAllocAllocationType flAllocationType,
                [In] VirtualAllocProtection flProtect);

            // https://msdn.microsoft.com/en-us/library/windows/desktop/aa366892(v=vs.85).aspx
            [DllImport(KERNEL32_LIB, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool VirtualFree(
                [In] IntPtr lpAddress,
                [In] IntPtr dwSize,
                [In] VirtualAllocAllocationType dwFreeType);

            // https://msdn.microsoft.com/en-us/library/windows/desktop/aa366898(v=vs.85).aspx
            [DllImport(KERNEL32_LIB, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool VirtualProtect(
                [In] IntPtr lpAddress,
                [In] IntPtr dwSize,
                [In] VirtualAllocProtection flNewProtect,
                [Out] out VirtualAllocProtection lpflOldProtect);
        }
    }
}
