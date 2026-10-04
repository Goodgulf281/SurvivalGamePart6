// ============================================================
// Script:      TerrainEdit.cs
// Episode:     EP06 — FishNet multiplayer, Phase 2
// Description: One terrain height edit (raise or depress), stored by
//              WorldState in a synced list so every machine applies the
//              same edits in the same order: live to loaded chunks, and
//              replayed when a chunk loads later.
//
//              Plain public fields so FishNet generates the serializer.
// Author:      Goodgulf
// ============================================================

using UnityEngine;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// A single terrain height edit. Positive <see cref="Depth"/> pushes the terrain down,
    /// negative raises it (see <see cref="TerrainUtils.PushDownTerrain"/>).
    /// </summary>
    public struct TerrainEdit
    {
        /// <summary>Chunk the edit belongs to (the chunk containing <see cref="Position"/>).</summary>
        public int ChunkX;
        public int ChunkZ;

        /// <summary>World-space centre of the edit.</summary>
        public Vector3 Position;

        /// <summary>Radius in metres.</summary>
        public float Radius;

        /// <summary>Metres to push down at the centre; negative raises.</summary>
        public float Depth;

        public TerrainEdit(Vector2Int chunk, Vector3 position, float radius, float depth)
        {
            ChunkX   = chunk.x;
            ChunkZ   = chunk.y;
            Position = position;
            Radius   = radius;
            Depth    = depth;
        }

        public Vector2Int GetChunk() => new Vector2Int(ChunkX, ChunkZ);

        /// <summary>
        /// Applies the edit to <paramref name="terrain"/>, which must be the terrain of the
        /// edit's chunk. Deterministic: the same edits applied in the same order to the same
        /// generated heights give the same result on every machine.
        /// </summary>
        public void ApplyTo(Terrain terrain)
        {
            TerrainUtils.PushDownTerrain(terrain, Position, Radius, Depth);
        }
    }
}
