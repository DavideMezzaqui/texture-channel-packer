using System;

namespace DAVFX.TextureChannelPacker
{
    /// <summary>Expected, user facing failure (bad input, unsupported file, size mismatch).</summary>
    public sealed class PackException : Exception
    {
        public PackException(string message) : base(message)
        {
        }

        public PackException(string message, Exception inner) : base(message, inner)
        {
        }

        /// <summary>
        /// True when the file looks valid but uses a variant a built-in decoder does not implement
        /// (as opposed to a damaged file). Callers can then try another reader.
        /// </summary>
        public bool IsUnsupported { get; private set; }

        public static PackException Unsupported(string message)
        {
            return new PackException(message) { IsUnsupported = true };
        }

        /// <summary>Same message and flag, with this exception as inner one (rethrowing from a worker thread).</summary>
        internal PackException Wrap()
        {
            return new PackException(Message, this) { IsUnsupported = IsUnsupported };
        }
    }
}
