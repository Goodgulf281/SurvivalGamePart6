// ============================================================
// Script:      WorldSave.cs
// Episode:     EP06 — FishNet multiplayer, Phase 6
// Description: The server's world save: the seed, every built piece
//              in build order, and the terrain edit list. One JSON
//              file per process (Multiplayer Play Mode players each
//              get their own folder):
//                persistentDataPath/Saves[_<mppm id>]/world.json
//
//              Pieces are stored as what is needed to rebuild them
//              (template, position, rotation, snap index); grounding,
//              neighbours and structural distance are recomputed by
//              Builder when the server rebuilds them.
// Author:      Goodgulf
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using Goodgulf.Logging;
using Goodgulf.TerrainUtils;
using UnityEngine;

namespace Goodgulf.Networking
{
    /// <summary>One saved building piece.</summary>
    [Serializable]
    public struct SavedPiece
    {
        public int TemplateId;
        public Vector3 Position;
        public Quaternion Rotation;
        public int SnapIndex;
    }

    /// <summary>Everything the server needs to restore a world.</summary>
    [Serializable]
    public class WorldSaveData
    {
        /// <summary>Bump when the format changes; older saves are rejected.</summary>
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public int Seed;

        // In build order, so neighbours and grounding come out the same when rebuilt
        public List<SavedPiece> Pieces = new List<SavedPiece>();

        // In the order the server applied them; replayed on chunk load like live edits
        public List<TerrainEdit> TerrainEdits = new List<TerrainEdit>();
    }

    /// <summary>Reads and writes the world save file.</summary>
    public static class WorldSaveFile
    {
        private const string FolderName = "Saves";
        private const string FileName = "world.json";

        /// <summary>Full path of this process's save file.</summary>
        public static string FilePath => Path.Combine(PlayModeInstance.GetInstanceDataPath(FolderName), FileName);

        public static bool Exists => File.Exists(FilePath);

        public static bool Save(WorldSaveData data)
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // Write to a temporary file first so a crash can't leave a half-written save
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonUtility.ToJson(data, true));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tempPath, path);

                GameLogger.Info($"World saved to {path}: {data.Pieces.Count} piece(s), {data.TerrainEdits.Count} terrain edit(s), seed {data.Seed}");
                return true;
            }
            catch (Exception e)
            {
                GameLogger.Error($"Failed to save the world: {e.Message}");
                return false;
            }
        }

        public static bool TryLoad(out WorldSaveData data)
        {
            data = null;
            string path = FilePath;

            if (!File.Exists(path))
                return false;

            try
            {
                data = JsonUtility.FromJson<WorldSaveData>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                GameLogger.Error($"Failed to read the world save {path}: {e.Message}");
                return false;
            }

            if (data == null || data.Version != WorldSaveData.CurrentVersion)
            {
                GameLogger.Warning($"Ignoring world save {path}: version {data?.Version}, expected {WorldSaveData.CurrentVersion}");
                data = null;
                return false;
            }

            data.Pieces ??= new List<SavedPiece>();
            data.TerrainEdits ??= new List<TerrainEdit>();
            return true;
        }
    }
}
