﻿using System;
using System.Collections.Generic;
using System.Text;

namespace FastUtf8Tester
{
    /// <summary>
    /// Represents a region of native memory. The <see cref="Span"/> property can be used
    /// to get a span backed by this memory region.
    /// </summary>
    public interface INativeMemory : IDisposable
    {
        /// <summary>
        /// Returns a value stating whether this native memory block is readonly.
        /// </summary>
        bool IsReadonly { get; }

        /// <summary>
        /// Gets the <see cref="Span{byte}"/> which represents this native memory.
        /// This <see cref="INativeMemory"/> instance must be kept alive while working with the span.
        /// </summary>
        Span<byte> Span { get; }

        /// <summary>
        /// Sets this native memory block to be readonly. Writes to this block will cause an AV.
        /// This method has no effect if the memory block is zero length or if the underlying
        /// OS does not support marking the memory block as readonly.
        /// </summary>
        void MakeReadonly();

        /// <summary>
        /// Sets this native memory block to be read+write.
        /// This method has no effect if the memory block is zero length or if the underlying
        /// OS does not support marking the memory block as read+write.
        /// </summary>
        void MakeWriteable();
    }
}
