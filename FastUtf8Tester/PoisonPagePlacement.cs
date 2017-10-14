using System;

namespace FastUtf8Tester
{
    /// <summary>
    /// Dictates where the poison page should be placed.
    /// </summary>
    public enum PoisonPagePlacement
    {
        /// <summary>
        /// The poison page should be placed before the span.
        /// Attempting to access the memory page immediately before the
        /// span will result in an AV.
        /// </summary>
        BeforeSpan,

        /// <summary>
        /// The poison page should be placed after the span.
        /// Attempting to access the memory page immediately following the
        /// span will result in an AV.
        /// </summary>
        AfterSpan
    }
}
