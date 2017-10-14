﻿using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;

namespace FastUtf8Tester
{
    /// <summary>
    /// Represents a region of native memory. The <see cref="Span"/> property can be used
    /// to get a span backed by this memory region.
    /// </summary>
    public sealed class NativeMemory : IDisposable
    {
        private static readonly int SystemPageSize = Environment.SystemPageSize;

        private readonly VirtualAllocHandle _handle;
        private readonly int _length;
        private readonly int _offset;

        private NativeMemory(VirtualAllocHandle handle, int offset, int length)
        {
            _handle = handle;
            _offset = offset;
            _length = length;
        }
        
        /// <summary>
        /// Returns a value stating whether this native memory block is readonly.
        /// </summary>
        public bool IsReadonly => (Protection != VirtualAllocProtection.PAGE_READWRITE);

        private unsafe VirtualAllocProtection Protection
        {
            get
            {
                bool refAdded = false;
                try
                {
                    _handle.DangerousAddRef(ref refAdded);
                    if (UnsafeNativeMethods.VirtualQuery(
                        lpAddress: _handle.DangerousGetHandle() + _offset,
                        lpBuffer: out var memoryInfo,
                        dwLength: (IntPtr)sizeof(MEMORY_BASIC_INFORMATION)) == IntPtr.Zero)
                    {
                        Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                        throw new InvalidOperationException("VirtualQuery failed unexpectedly.");
                    }
                    return memoryInfo.Protect;
                }
                finally
                {
                    if (refAdded)
                    {
                        _handle.DangerousRelease();
                    }
                }
            }
            set
            {
                if (_length > 0)
                {
                    bool refAdded = false;
                    try
                    {
                        _handle.DangerousAddRef(ref refAdded);
                        if (!UnsafeNativeMethods.VirtualProtect(
                            lpAddress: _handle.DangerousGetHandle() + _offset,
                            dwSize: (IntPtr)_length,
                            flNewProtect: value,
                            lpflOldProtect: out _))
                        {
                            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                            throw new InvalidOperationException("VirtualProtect failed unexpectedly.");
                        }
                    }
                    finally
                    {
                        if (refAdded)
                        {
                            _handle.DangerousRelease();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the <see cref="Span{byte}"/> which represents this native memory.
        /// This <see cref="NativeMemory"/> instance must be kept alive while working with the span.
        /// </summary>
        public unsafe Span<byte> Span
        {
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
            get
            {
                bool refAdded = false;
                try
                {
                    _handle.DangerousAddRef(ref refAdded);
                    return new Span<byte>((void*)(_handle.DangerousGetHandle() + _offset), _length);
                }
                finally
                {
                    if (refAdded)
                    {
                        _handle.DangerousRelease();
                    }
                }
            }
        }

        /// <summary>
        /// Allocates a new <see cref="NativeMemory"/> region which is immediately preceded by
        /// or immediately followed by a poison (MEM_NOACCESS) page. If <paramref name="placement"/>
        /// is <see cref="PoisonPagePlacement.BeforeSpan"/>, then attempting to read the memory
        /// immediately before the returned <see cref="NativeMemory"/> will result in an AV.
        /// If <paramref name="placement"/> is <see cref="PoisonPagePlacement.AfterSpan"/>, then
        /// attempting to read the memory immediately after the returned <see cref="NativeMemory"/>
        /// will result in AV.
        /// </summary>
        /// <remarks>
        /// The newly-allocated memory will be populated with random data.
        /// </remarks>
        public static NativeMemory Allocate(int cb, PoisonPagePlacement placement)
        {
            var retVal = AllocateWithoutDataPopulation(cb, placement);
            new Random().NextBytes(retVal.Span); // doesn't need to be cryptographically strong
            return retVal;
        }

        /// <summary>
        /// Similar to <see cref="Allocate(int, PoisonPagePlacement)"/>, but populates the allocated
        /// native memory block from existing data rather than using random data.
        /// </summary>
        public static NativeMemory AllocateFromExistingData(ReadOnlySpan<byte> data, PoisonPagePlacement placement)
        {
            var retVal = AllocateWithoutDataPopulation(data.Length, placement);
            data.CopyTo(retVal.Span);
            return retVal;
        }

        private static NativeMemory AllocateWithoutDataPopulation(int cb, PoisonPagePlacement placement)
        {
            //
            // PRECONDITION CHECKS
            //

            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException("This type currently only works on Windows.");
            }

            if (cb < 0)
            {
                throw new ArgumentOutOfRangeException(
                    message: "Number of bytes to allocate must be non-negative.",
                    paramName: nameof(cb));
            }

            if (placement != PoisonPagePlacement.BeforeSpan && placement != PoisonPagePlacement.AfterSpan)
            {
                throw new ArgumentOutOfRangeException(
                    message: "Invalid enum value.",
                    paramName: nameof(placement));
            }

            //
            // PROCESSING
            //

            long totalBytesToAllocate = cb;
            checked
            {
                // We only need to round cb up if it's not an exact multiple
                // of the system page size.

                var leftoverBytes = cb % SystemPageSize;
                if (leftoverBytes != 0)
                {
                    totalBytesToAllocate += SystemPageSize - leftoverBytes;
                }

                // Finally, account for the poison pages at the front and back.

                totalBytesToAllocate += 2 * SystemPageSize;
            }

            // Reserve and commit the entire range as NOACCESS.

            var handle = UnsafeNativeMethods.VirtualAlloc(
                lpAddress: IntPtr.Zero,
                dwSize: (IntPtr)totalBytesToAllocate /* cast throws OverflowException if out of range */,
                flAllocationType: VirtualAllocAllocationType.MEM_RESERVE | VirtualAllocAllocationType.MEM_COMMIT,
                flProtect: VirtualAllocProtection.PAGE_NOACCESS);

            if (handle == null || handle.IsInvalid)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                throw new InvalidOperationException("VirtualAlloc failed unexpectedly.");
            }

            // Done allocating! Now carve out a READWRITE section bookended by the NOACCESS
            // pages and return that carved-out section to the caller. Since memory protection
            // flags only apply at page-level granularity, we need to "left-align" or "right-
            // align" the section we carve out so that it's guaranteed adjacent to one of
            // the NOACCESS bookend pages.

            return new NativeMemory(
                handle: handle,
                offset: (placement == PoisonPagePlacement.BeforeSpan)
                    ? SystemPageSize /* just after leading poison page */
                    : checked((int)(totalBytesToAllocate - SystemPageSize - cb)) /* just beforr trailing poison page */,
                length: cb)
            {
                Protection = VirtualAllocProtection.PAGE_READWRITE
            };
        }

        /// <summary>
        /// Releases this native memory. Use of any previous <see cref="Span{byte}"/> derived
        /// from this instance will result in undefined behavior.
        /// </summary>
        public void Dispose()
        {
            _handle.Dispose();
        }

        /// <summary>
        /// Sets this native memory block to be readonly. Writes to this block will cause an AV.
        /// This method has no effect if the memory block is zero length.
        /// </summary>
        public void MakeReadonly()
        {
            Protection = VirtualAllocProtection.PAGE_READONLY;
        }

        /// <summary>
        /// Sets this native memory block to be read+write.
        /// This method has no effect if the memory block is zero length.
        /// </summary>
        public void MakeWriteable()
        {
            Protection = VirtualAllocProtection.PAGE_READWRITE;
        }

        private sealed class VirtualAllocHandle : SafeHandle
        {
            // Called by P/Invoke when returning SafeHandles
            private VirtualAllocHandle()
                : base(IntPtr.Zero, ownsHandle: true)
            {
            }

            // Do not provide a finalizer - SafeHandle's critical finalizer will
            // call ReleaseHandle for you.

            public override bool IsInvalid => (handle == IntPtr.Zero);

            protected override bool ReleaseHandle() =>
                UnsafeNativeMethods.VirtualFree(handle, IntPtr.Zero, VirtualAllocAllocationType.MEM_RELEASE);
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

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public VirtualAllocProtection AllocationProtect;
            public IntPtr RegionSize;
            public VirtualAllocAllocationType State;
            public VirtualAllocProtection Protect;
            public VirtualAllocAllocationType Type;
        };

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
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
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

            // https://msdn.microsoft.com/en-us/library/windows/desktop/aa366902(v=vs.85).aspx
            [DllImport(KERNEL32_LIB, CallingConvention = CallingConvention.Winapi, SetLastError = true)]
            public static extern IntPtr VirtualQuery(
                [In] IntPtr lpAddress,
                [Out] out MEMORY_BASIC_INFORMATION lpBuffer,
                [In] IntPtr dwLength);
        }
    }
}
