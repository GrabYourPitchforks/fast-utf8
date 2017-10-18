using System;

namespace FastUtf8Tester
{
    /// <summary>
    /// Contains factory methods to create <see cref="INativeMemory"/> instances.
    /// </summary>
    public static partial class NativeMemory
    {
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
        public static INativeMemory Allocate(int cb, PoisonPagePlacement placement)
        {
            var retVal = AllocateWithoutDataPopulation(cb, placement);
            new Random().NextBytes(retVal.Span); // doesn't need to be cryptographically strong
            return retVal;
        }

        /// <summary>
        /// Similar to <see cref="Allocate(int, PoisonPagePlacement)"/>, but populates the allocated
        /// native memory block from existing data rather than using random data.
        /// </summary>
        public static INativeMemory AllocateFromExistingData(ReadOnlySpan<byte> data, PoisonPagePlacement placement)
        {
            var retVal = AllocateWithoutDataPopulation(data.Length, placement);
            data.CopyTo(retVal.Span);
            return retVal;
        }

        private static INativeMemory AllocateWithoutDataPopulation(int cb, PoisonPagePlacement placement)
        {
            //
            // PRECONDITION CHECKS
            //

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

            return (Environment.OSVersion.Platform == PlatformID.Win32NT)
                ? AllocateWithoutDataPopulationWindows(cb, placement) /* Windows-specific code */
                : AllocateWithoutDataPopulationDefault(cb) /* non-Windows-specific code */;
        }
    }
}
