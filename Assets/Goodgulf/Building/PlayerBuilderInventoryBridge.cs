// ============================================================
// Script:      PlayerBuilderInventoryBridge.cs
// Episode:     EP06 — FishNet multiplayer, Phase 3
// Description: Bridge between the player and the builder system.
//              Lives on the player prefab.
//
//              • Inventory: a SyncDictionary written only by the server
//                and readable only by the owning client, so the UI can
//                show it but only the server can spend it.
//              • Build and destroy requests: ServerRpcs. The caller's
//                player is this object, so the server knows who asks
//                and checks reach, rate, template, inventory, the
//                non-buildable areas and duplicates before Builder
//                does the work.
//
//              Inventory is stored as:
//                  Key   = buildItemId
//                  Value = amount available
// Author:      Goodgulf
// ============================================================

using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Goodgulf.Building;
using UnityEngine;

/// <summary>
/// Acts as a bridge between the player and the builder system,
/// exposing a simple inventory API for buildable items and the networked build requests.
/// </summary>
public class PlayerBuilderInventoryBridge : NetworkBehaviour
{
    // ==================================================================
    // Inspector fields
    // ==================================================================

    /// <summary>
    /// Enables verbose debug logging for inventory operations.
    /// </summary>
    [SerializeField]
    private bool _debugEnabled = true;

    // ==================================================================
    // Synced state
    // ==================================================================

    /// <summary>
    /// Holds all buildable items and their remaining amounts.
    /// Written by the server only; only the owner receives it.
    /// </summary>
    private readonly SyncDictionary<int, int> _inventory =
        new(new SyncTypeSettings(WritePermission.ServerOnly, ReadPermission.OwnerOnly));

    // Server: time of the last accepted build or destroy request, for rate limiting
    private float _lastRequestTime = float.NegativeInfinity;

    // ==================================================================
    // Network lifecycle
    // ==================================================================

    /// <summary>
    /// The server gives every new player its starting inventory.
    /// Typically replaced by real inventory data later.
    /// </summary>
    public override void OnStartServer()
    {
        AddItemToInventory(0, 20);
        AddItemToInventory(1, 100);
        AddItemToInventory(2, 10);
    }

    // ==================================================================
    // Inventory (read anywhere it is synced; written on the server)
    // ==================================================================

    /// <summary>
    /// Checks whether at least one item of the given buildItemId
    /// is available in the inventory.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <returns>True if at least one item is available, otherwise false</returns>
    public bool InventoryLeft(int buildItemId)
    {
        if (_inventory.TryGetValue(buildItemId, out int amount))
        {
            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeft({buildItemId}): {amount} left");

            // Return true only if there is at least one item left
            if (amount > 0)
                return true;
        }

        if (_debugEnabled)
            Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeft({buildItemId}): no such item in inventory");

        return false;
    }

    /// <summary>
    /// Returns the remaining amount of a given buildable item.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <returns>The remaining amount, or 0 if not found</returns>
    public int InventoryLeftAmount(int buildItemId)
    {
        if (_inventory.TryGetValue(buildItemId, out int amount))
        {
            return amount;
        }

        if (_debugEnabled)
            Debug.Log($"PlayerBuilderInventoryBridge.InventoryLeftAmount({buildItemId}): no such item in inventory");

        return 0;
    }

    /// <summary>
    /// Server: attempts to consume a specific amount of an item from the inventory.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <param name="amount">The amount to consume</param>
    /// <returns>
    /// True if the inventory contained enough items and the amount was consumed,
    /// otherwise false.
    /// </returns>
    public bool ConsumeItemAmount(int buildItemId, int amount)
    {
        if (!IsServerInitialized)
        {
            Debug.LogWarning("PlayerBuilderInventoryBridge.ConsumeItemAmount(): only the server can change the inventory");
            return false;
        }

        if (_inventory.TryGetValue(buildItemId, out int amountInInventory))
        {
            if (_debugEnabled)
                Debug.Log($"PlayerBuilderInventoryBridge.ConsumeItemAmount({buildItemId},{amount}): amount in inventory={amountInInventory}");

            // Only consume if enough items are available
            if (amount <= amountInInventory)
            {
                _inventory[buildItemId] = amountInInventory - amount;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Server: adds an amount of an item to the inventory.
    /// Creates a new entry if the item does not yet exist.
    /// </summary>
    /// <param name="buildItemId">The ID of the buildable item</param>
    /// <param name="amount">The amount to add</param>
    /// <returns>The new total amount of this item in the inventory</returns>
    public int AddItemToInventory(int buildItemId, int amount)
    {
        if (!IsServerInitialized)
        {
            Debug.LogWarning("PlayerBuilderInventoryBridge.AddItemToInventory(): only the server can change the inventory");
            return InventoryLeftAmount(buildItemId);
        }

        _inventory.TryGetValue(buildItemId, out int current);
        int total = current + amount;
        _inventory[buildItemId] = total;

        if (_debugEnabled)
            Debug.Log($"PlayerBuilderInventoryBridge.AddItemToInventory({buildItemId},{amount}): new amount in inventory={total}");

        return total;
    }

    // ==================================================================
    // Build and destroy requests (owner → server)
    // ==================================================================

    /// <summary>
    /// Ask the server to build a piece. The client keeps the preview, raycast and
    /// snapping local; the server validates and builds.
    /// </summary>
    public void RequestBuild(int templateId, Vector3 position, Quaternion rotation, int snapIndex)
    {
        ServerRequestBuild(templateId, position, rotation, snapIndex);
    }

    /// <summary>
    /// Ask the server to destroy the given pieces (IDs from the local copies).
    /// </summary>
    public void RequestDestruct(int[] pieceIds)
    {
        ServerRequestDestruct(pieceIds);
    }

    [ServerRpc]
    private void ServerRequestBuild(int templateId, Vector3 position, Quaternion rotation, int snapIndex)
    {
        Builder builder = Builder.Instance;
        if (builder == null)
            return;

        // Untrusted client input: validate everything
        BuildTemplate template = builder.GetBuildItemTemplate(templateId);
        if (template == null)
        {
            Reject($"unknown template {templateId}");
            return;
        }

        if (!PassesRateLimit(builder) || !InReach(builder, position))
            return;

        if (snapIndex < 0 || snapIndex > template.snapPoints.Count)
        {
            Reject($"snap index {snapIndex} out of range");
            return;
        }

        if (!InventoryLeft(templateId))
        {
            Reject($"no inventory left for template {templateId}");
            return;
        }

        if (builder.IsPositionInNonBuildableArea(position))
        {
            Reject($"{position} is in a non-buildable area");
            return;
        }

        if (builder.IsDuplicatePiece(templateId, position, rotation))
        {
            Reject($"a {template.name} already exists at {position}");
            return;
        }

        if (!builder.TryGetTerrainAt(position, out _))
        {
            Reject($"no terrain loaded at {position}");
            return;
        }

        builder.InstantiateBuildingPrefab(templateId, position, rotation, snapIndex);
        ConsumeItemAmount(templateId, 1);
    }

    [ServerRpc]
    private void ServerRequestDestruct(int[] pieceIds)
    {
        Builder builder = Builder.Instance;
        if (builder == null || pieceIds == null || pieceIds.Length == 0)
            return;

        if (!PassesRateLimit(builder))
            return;

        // Keep only pieces that exist and are within reach
        List<int> allowed = new List<int>();
        foreach (int id in pieceIds)
        {
            ConstructedItem item = builder.GetConstructedItem(id);
            if (item != null && Vector3.Distance(transform.position, item.Position) <= builder.MaxBuildReach)
                allowed.Add(id);
        }

        if (allowed.Count < pieceIds.Length)
            Reject($"{pieceIds.Length - allowed.Count} of {pieceIds.Length} piece(s) missing or out of reach");

        builder.DestructConstructedItems(allowed);
    }

    // ==================================================================
    // Validation helpers (server)
    // ==================================================================

    private bool PassesRateLimit(Builder builder)
    {
        if (Time.time - _lastRequestTime < builder.MinBuildInterval)
            return false;

        _lastRequestTime = Time.time;
        return true;
    }

    private bool InReach(Builder builder, Vector3 position)
    {
        float distance = Vector3.Distance(transform.position, position);
        if (distance <= builder.MaxBuildReach)
            return true;

        Reject($"{distance:F1} m away (max {builder.MaxBuildReach} m)");
        return false;
    }

    private void Reject(string reason)
    {
        Debug.LogWarning($"PlayerBuilderInventoryBridge: build request from client {OwnerId} rejected: {reason}");
    }
}
