using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class DigManager : MonoBehaviour
{
    [Header("References")]
    // Tilemap used for gameplay logic. Solid tiles exist here, empty cells are null.
    public Tilemap groundTilemap;

    // Grid system that knows which cells are walkable and supports pathfinding
    public NodeGrid nodeGrid;

    // Optional system that places dug visuals on a separate tilemap
    public DugTileSpawner dugTileSpawner;

    [Header("Base Tiles")]
    // Main ground tiles
    public TileBase mud;
    public TileBase stone;

    [Header("Ore Tiles")]
    // Ore tiles used to assign value and treat as solid
    public TileBase oreGreen, oreBlue, orePurple, orePink, oreYellow;

    [Header("HP")]
    // Hit points for mud tiles
    public int mudHp = 4;

    // Hit points for stone and ore tiles
    public int stoneHp = 12;

    // Current hit points for each solid cell
    private readonly Dictionary<Vector3Int, int> hp = new();

    // Max hit points for each solid cell, used to calculate damage visuals
    private readonly Dictionary<Vector3Int, int> maxHp = new();

    // Solid cells that are next to an empty walkable cell and can be dug next
    private readonly HashSet<Vector3Int> frontier = new();

    public void BuildFromTilemap()
    {
        // Reset stored data
        hp.Clear();
        maxHp.Clear();
        frontier.Clear();

        // Validate required references
        if (groundTilemap == null || nodeGrid == null)
        {
            Debug.LogError("DigManager: assign groundTilemap and nodeGrid.");
            return;
        }

        // Scan all cells in the tilemap bounds and build HP entries
        var bounds = groundTilemap.cellBounds;

        foreach (var pos in bounds.allPositionsWithin)
        {
            var t = groundTilemap.GetTile(pos);
            if (t == null) continue;

            // Decide max HP based on tile type
            int m = GetMaxHpForTile(t);
            if (m <= 0) continue;

            // Store current and max HP for this solid cell
            hp[pos] = m;
            maxHp[pos] = m;

            // Ensure tiles start fully visible
            var c = groundTilemap.GetColor(pos);
            c.a = 1f;
            groundTilemap.SetColor(pos, c);
        }

        // Build the initial list of diggable cells
        RebuildFrontier();
    }

    int GetMaxHpForTile(TileBase t)
    {
        // Mud and stone have different durability
        if (t == mud) return mudHp;
        if (t == stone) return stoneHp;

        // Any ore tile is treated like stone durability
        return stoneHp;
    }

    // Returns true if this cell currently has HP, meaning it is solid
    public bool IsSolid(Vector3Int cell) => hp.ContainsKey(cell);

    // Returns the set of currently diggable solid cells
    public IEnumerable<Vector3Int> GetFrontier() => frontier;

    public int GetValue(Vector3Int cell)
    {
        // Value is used for target choice. Higher means more desirable to dig.
        var t = groundTilemap.GetTile(cell);

        if (t == oreYellow) return 100;
        if (t == orePink) return 70;
        if (t == orePurple) return 50;
        if (t == oreBlue) return 35;
        if (t == oreGreen) return 25;

        // Mud is common and worth more than plain stone
        if (t == mud) return 10;
        if (t == stone) return 2;

        // Empty or unknown tiles have no value
        return 0;
    }

    public void RebuildFrontier()
    {
        // Frontier is any solid cell that has at least one empty neighbor
        frontier.Clear();

        foreach (var cell in hp.Keys)
            if (IsAdjacentToEmpty(cell))
                frontier.Add(cell);
    }

    bool IsAdjacentToEmpty(Vector3Int cell)
    {
        // Check the four neighbors and see if any is walkable
        foreach (var n in Neighbors4(cell))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    public bool TryGetBestFrontier(Vector3Int bunnyCell, int radius, out Vector3Int best)
    {
        // Find the best dig target near the bunny based on value and distance
        best = default;
        int bestScore = int.MinValue;

        foreach (var p in frontier)
        {
            // Use Manhattan distance on the grid
            int dist = Mathf.Abs(p.x - bunnyCell.x) + Mathf.Abs(p.y - bunnyCell.y);
            if (dist > radius) continue;

            // Skip targets that have no place to stand next to them
            if (!HasAnyStandCell(p)) continue;

            // Prefer valuable tiles, but reduce score for long travel distance
            int score = GetValue(p) * 10 - dist;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return bestScore != int.MinValue;
    }

    bool HasAnyStandCell(Vector3Int solid)
    {
        // A solid tile can be dug only if at least one neighbor is walkable
        foreach (var n in Neighbors4(solid))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    public bool ApplyHit(Vector3Int cell, int hitDamage)
    {
        // If the tile has no HP entry, it is not diggable
        if (!hp.TryGetValue(cell, out var curHp))
            return false;

        // Reduce HP by at least 1 each hit
        curHp -= Mathf.Max(1, hitDamage);

        // If HP reaches zero, break the tile and open the cell
        if (curHp <= 0)
        {
            BreakTile(cell);
            return true;
        }

        // Store the new HP and update tile visual opacity
        hp[cell] = curHp;
        UpdateOpacityStages(cell);
        return false;
    }

    void UpdateOpacityStages(Vector3Int cell)
    {
        // Opacity shows damage level in simple steps
        int cur = hp[cell];
        int max = maxHp[cell];

        float frac = cur / (float)max;

        float a =
            (frac >= 0.75f) ? 1.00f :
            (frac >= 0.50f) ? 0.75f :
            (frac >= 0.25f) ? 0.50f :
                              0.25f;

        var c = groundTilemap.GetColor(cell);
        c.a = a;
        groundTilemap.SetColor(cell, c);
    }

    void BreakTile(Vector3Int cell)
    {
        // Remove the tile from the logic tilemap so the space becomes empty
        groundTilemap.SetTile(cell, null);

        // Reset color so if a tile returns later it is not tinted
        groundTilemap.SetColor(cell, Color.white);

        // Tell the node grid that this cell is now empty and walkable
        nodeGrid.SetEmpty(cell);

        // Place a dug visual tile on the separate visual tilemap, if used
        if (dugTileSpawner != null)
            dugTileSpawner.PlaceDug(cell);

        // Remove HP tracking for this cell since it is no longer solid
        hp.Remove(cell);
        maxHp.Remove(cell);

        // Update frontier so new edges become diggable
        RebuildFrontier();
    }

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors on a grid
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }
}
