using CCoop.Protocol.Messages;

namespace CCoop.Core
{
    /// <summary>
    /// A whole layer, verified and ready to apply.
    ///
    /// A type rather than a widening list of parameters: the three payload sections are
    /// all <c>byte[]</c> or close to it, and passing them positionally is exactly the
    /// mistake that would surface as a scrambled world rather than a compile error.
    /// </summary>
    public sealed class LayerSnapshot
    {
        /// <summary>Dimensions, spawn point, layer modifier and section lengths.</summary>
        public LayerPrepareMessage Prepare;

        /// <summary>Block grid, 2 bytes per cell, row-major.</summary>
        public byte[] Terrain;

        /// <summary>
        /// Fluid grid, 1 byte per cell. Empty when the host had no fluid layer to send.
        /// </summary>
        public byte[] Fluid;

        /// <summary>Every object world generation created. Null if the host sent none.</summary>
        public EntityManifest Entities;
    }
}
