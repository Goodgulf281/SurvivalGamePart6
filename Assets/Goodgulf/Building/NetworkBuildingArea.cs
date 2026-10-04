// ============================================================
// Script:      NetworkBuildingArea.cs
// Episode:     EP06 — FishNet multiplayer, Phase 3
// Description: The networked side of one BuildingArea. The server
//              spawns one per area (Builder.GetOrCreateBuildingArea)
//              and keeps a synced record of every piece in it; the
//              pieces themselves are plain local GameObjects.
//
//              • Server: Builder creates the real piece (with its
//                ConstructedItem, grounding and neighbours) and adds a
//                PieceRecord here; removing the ConstructedItem removes
//                the record.
//              • Clients: each record becomes a local copy of the build
//                prefab under this object, and a removed record destroys
//                it. Late joiners and areas that come into view build
//                every existing record. The host skips this, because
//                the server's own pieces are already in its scene.
//              • Every client creates the area outline locally.
//
//              Put this on a prefab with a NetworkObject and assign it
//              to Builder's "Network Building Area Prefab".
// Author:      Goodgulf
// ============================================================

using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Goodgulf.Logging;
using Goodgulf.Pathfinding;
using UnityEngine;

namespace Goodgulf.Building
{
    /// <summary>
    /// What a client needs to rebuild one piece locally.
    /// Plain public fields so FishNet generates the serializer.
    /// </summary>
    public struct PieceRecord
    {
        /// <summary>The ConstructedItem's global ID (assigned by the server).</summary>
        public int Id;
        public int TemplateId;
        public Vector3 Position;
        public Quaternion Rotation;
        public int SnapIndex;
    }

    /// <summary>
    /// Networked building area: syncs the area's pieces to every client that observes it.
    /// </summary>
    public class NetworkBuildingArea : NetworkBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = true;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        // ==================================================================
        // Synced state
        // ==================================================================

        private readonly SyncVar<string> _areaId = new();
        private readonly SyncDictionary<int, PieceRecord> _pieces = new();

        /// <summary>The BuildingArea's ID (the same on server and clients).</summary>
        public string AreaId => _areaId.Value;

        // ==================================================================
        // Private state
        // ==================================================================

        // Server: the area data this object represents, set just before Spawn
        private BuildingArea _area;

        // Client: local copies of the pieces, by piece ID
        private readonly Dictionary<int, GameObject> _localPieces = new Dictionary<int, GameObject>();
        private GameObject _outline;

        // ==================================================================
        // Unity / network lifecycle
        // ==================================================================

        private void Awake()
        {
            _pieces.OnChange += Pieces_OnChange;
        }

        /// <summary>
        /// Server: link this object to its area data. Call before spawning.
        /// </summary>
        public void SetArea(BuildingArea area)
        {
            _area = area;
        }

        public override void OnStartServer()
        {
            if (_area == null)
            {
                this.LogError("Spawned without a BuildingArea; call SetArea before Spawn");
                return;
            }

            // Set here so the value goes out in the spawn message
            _areaId.Value = _area.Id;
        }

        public override void OnStartClient()
        {
            // Every client (the host too) draws the outline locally
            if (Builder.Instance != null)
                _outline = Builder.Instance.CreateAreaOutline(transform.position);

            // Build whatever already exists (late join, or the area just came into view)
            if (!IsServerInitialized)
            {
                foreach (KeyValuePair<int, PieceRecord> kvp in _pieces)
                    SpawnLocalPiece(kvp.Value);
            }
        }

        public override void OnStopClient()
        {
            // The area left this client's view or the client disconnected
            foreach (GameObject piece in _localPieces.Values)
            {
                if (piece != null)
                    Destroy(piece);
            }
            _localPieces.Clear();

            if (_outline != null)
                Destroy(_outline);
        }

        // ==================================================================
        // Server API
        // ==================================================================

        /// <summary>Server: record a new piece so clients build it.</summary>
        public void AddPiece(PieceRecord record)
        {
            if (!IsServerInitialized) return;
            _pieces[record.Id] = record;
        }

        /// <summary>Server: remove a piece's record so clients destroy their copy.</summary>
        public void RemovePiece(int pieceId)
        {
            if (!IsServerInitialized) return;
            _pieces.Remove(pieceId);
        }

        /// <summary>
        /// Server → clients: these pieces collapse. Sent before their records are removed
        /// (an RPC arrives before the SyncType change, fishnet.md §13), so each client
        /// hands its copy to the falling effect and the later Remove finds nothing to destroy.
        /// The host is excluded: the server already runs the effect on the real pieces.
        /// </summary>
        [ObserversRpc(ExcludeServer = true)]
        public void ObserversCollapse(int[] pieceIds)
        {
            if (pieceIds == null)
                return;

            foreach (int id in pieceIds)
            {
                if (!_localPieces.TryGetValue(id, out GameObject piece))
                    continue;

                // No longer ours to destroy: the effect destroys it after a delay
                _localPieces.Remove(id);

                if (piece == null)
                    continue;

                Vector3 position = piece.transform.position;

                if (piece.TryGetComponent(out ConstructedItemBehaviour cib))
                {
                    // Same effect as the server: blue flash, unfreeze, push, destroy after 10 s
                    cib.SetMeshColor(new Color(0, 0, 1.0f));
                    cib.RemoveConstructedItem(true);
                }
                else
                {
                    Destroy(piece);
                }

                if (TerrainGraphIntegration.Instance != null)
                    TerrainGraphIntegration.Instance.InvalidateObstacleCache(position, Builder.ObstacleInvalidationRadius);
            }

            if (_debugEnabled)
                this.LogInfo($"Collapse of {pieceIds.Length} piece(s) in area {AreaId}");
        }

        // ==================================================================
        // Client: mirroring the records
        // ==================================================================

        private void Pieces_OnChange(SyncDictionaryOperation op, int key, PieceRecord value, bool asServer)
        {
            // The server (and so the host) owns the real pieces
            if (asServer || IsServerInitialized)
                return;

            switch (op)
            {
                case SyncDictionaryOperation.Add:
                case SyncDictionaryOperation.Set:
                    SpawnLocalPiece(value);
                    break;

                case SyncDictionaryOperation.Remove:
                    DestroyLocalPiece(key);
                    break;

                case SyncDictionaryOperation.Clear:
                    foreach (int id in new List<int>(_localPieces.Keys))
                        DestroyLocalPiece(id);
                    break;
            }
        }

        private void SpawnLocalPiece(PieceRecord record)
        {
            // Idempotent: OnStartClient and the initial sync callbacks may both deliver a record
            if (_localPieces.TryGetValue(record.Id, out GameObject existing))
            {
                if (existing != null)
                    return;
                _localPieces.Remove(record.Id);
            }

            if (Builder.Instance == null)
                return;

            GameObject piece = Builder.Instance.InstantiatePieceCopy(record, transform);
            if (piece == null)
                return;

            _localPieces[record.Id] = piece;

            // Pathfinding still runs on every client until Phase 5
            if (TerrainGraphIntegration.Instance != null)
                TerrainGraphIntegration.Instance.InvalidateObstacleCache(record.Position, Builder.ObstacleInvalidationRadius);
        }

        private void DestroyLocalPiece(int pieceId)
        {
            if (!_localPieces.TryGetValue(pieceId, out GameObject piece))
                return;

            _localPieces.Remove(pieceId);

            if (piece == null)
                return;

            Vector3 position = piece.transform.position;
            Destroy(piece);

            // Pathfinding still runs on every client until Phase 5
            if (TerrainGraphIntegration.Instance != null)
                TerrainGraphIntegration.Instance.InvalidateObstacleCache(position, Builder.ObstacleInvalidationRadius);
        }
    }
}
