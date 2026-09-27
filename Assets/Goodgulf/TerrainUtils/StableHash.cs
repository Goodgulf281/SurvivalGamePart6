// ============================================================
// Script:      StableHash.cs
// Description: Deterministic hashing (FNV-1a, 32-bit) of strings and float grids.
//              Unlike string.GetHashCode() or Object.GetInstanceID(),
//              the result is identical across processes, platforms,
//              editor and builds. Required so that every machine in a
//              networked session places the same prefabs from a seed.
// Author:      Goodgulf
// ============================================================

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Deterministic hashing helpers for seed and cache-key generation.
    /// </summary>
    public static class StableHash
    {
        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime       = 16777619;

        /// <summary>
        /// Returns the 32-bit FNV-1a hash of <paramref name="text"/> over its UTF-16 code units.
        /// A null string hashes to the offset basis.
        /// </summary>
        public static int Fnv1a(string text)
        {
            uint hash = FnvOffsetBasis;

            if (text != null)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    hash = (hash ^ (byte)c) * FnvPrime;
                    hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
                }
            }

            return unchecked((int)hash);
        }

        /// <summary>
        /// Returns the 32-bit FNV-1a hash of the raw bits of every value in <paramref name="values"/>.
        /// Bit-exact: used to compare terrain heightmaps between machines.
        /// </summary>
        public static int Fnv1a(float[,] values)
        {
            uint hash = FnvOffsetBasis;

            if (values != null)
            {
                foreach (float value in values)
                {
                    uint bits = unchecked((uint)System.BitConverter.SingleToInt32Bits(value));
                    hash = (hash ^ (bits & 0xFF)) * FnvPrime;
                    hash = (hash ^ ((bits >> 8) & 0xFF)) * FnvPrime;
                    hash = (hash ^ ((bits >> 16) & 0xFF)) * FnvPrime;
                    hash = (hash ^ (bits >> 24)) * FnvPrime;
                }
            }

            return unchecked((int)hash);
        }
    }
}
