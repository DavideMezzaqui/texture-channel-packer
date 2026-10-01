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
    }
}
