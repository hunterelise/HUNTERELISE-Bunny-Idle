using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class DigManager : MonoBehaviour
{
    [Header("References")]
    // Tilemap that stores the real ground tiles used for gameplay logic
    public Tilemap groundTilemap;

    // Grid that stores which cells are empty or solid for movement and pathing
    public NodeGrid nodeGrid;

    // Optional system that places visual dug tiles on a separate tilemap
    public DugTileSpawner dugTileSpawner;

    [Header("Base Tiles")]
    // Main ground tile types
    public TileBase mud;
    public TileBase stone;

    [Header("Ore Tiles")]
    // Ore tiles used for gem value and ore detection
    public TileBase oreGreen, oreBlue, orePurple, orePink, oreYellow;

    [Header("HP")]
    // Hit points for mud tiles
    public int mudHp = 4;

    // Hit points for stone and ore tiles
    public int stoneHp = 12;

    // Current hit points for each solid cell
    private readonly Dictionary<Vector3Int, int> hp = new();

    // Max hit points for each solid cell, used for damage visuals
    private readonly Dictionary<Vector3Int, int> maxHp = new();

    // Dig frontier is the set of solid cells next to empty space
    private readonly HashSet<Vector3Int> frontier = new();

    public void BuildFromTilemap()
    {
        // Clear old state before rebuilding
        hp.Clear();
        maxHp.Clear();
        frontier.Clear();

        // Validate required references
        if (groundTilemap == null || nodeGrid == null)
        {
            Debug.LogError("DigManager: assign groundTilemap and nodeGrid.");
            return;
        }

        // Scan all positions inside tilemap bounds
        var bounds = groundTilemap.cellBounds;

        foreach (var pos in bounds.allPositionsWithin)
        {
            // Any non null tile is considered solid and diggable
            var t = groundTilemap.GetTile(pos);
            if (t == null) continue;

            // Decide max HP based on tile type
            int m = GetMaxHpForTile(t);
            if (m <= 0) continue;

            // Store both current HP and max HP
            hp[pos] = m;
            maxHp[pos] = m;

            // Ensure tile starts fully visible
            var c = groundTilemap.GetColor(pos);
            c.a = 1f;
            groundTilemap.SetColor(pos, c);
        }

        // After HP is built, compute which tiles are on the frontier
        RebuildFrontier();
    }

    int GetMaxHpForTile(TileBase t)
    {
        // Mud and stone have different durability
        if (t == mud) return mudHp;
        if (t == stone) return stoneHp;

        // Ores are treated as stone durability
        if (IsOreTile(t)) return stoneHp;

        // Unknown tiles are ignored
        return 0;
    }

    bool IsOreTile(TileBase t)
        => t == oreGreen || t == oreBlue || t == orePurple || t == orePink || t == oreYellow;

    // True if this cell is currently tracked as solid and diggable
    public bool IsSolid(Vector3Int cell) => hp.ContainsKey(cell);

    // Returns the set of diggable frontier cells
    public IEnumerable<Vector3Int> GetFrontier() => frontier;

    // Returns max HP for a cell, or zero if not tracked
    public int GetMaxHp(Vector3Int cell) => maxHp.TryGetValue(cell, out var m) ? m : 0;

    // Tile type checks used by BunnyAgent for mode decisions
    public bool IsOreCell(Vector3Int cell) => IsOreTile(groundTilemap.GetTile(cell));
    public bool IsMudCell(Vector3Int cell) => groundTilemap.GetTile(cell) == mud;
    public bool IsStoneCell(Vector3Int cell) => groundTilemap.GetTile(cell) == stone;

    public int GetMaterialsYield(Vector3Int cell)
    {
        // Returns how many basic materials a tile gives when dug
        var t = groundTilemap.GetTile(cell);

        if (t == mud) return 1;
        if (t == stone) return 3;

        // Ores are not counted as materials so materials mode does not chase them
        if (IsOreTile(t)) return 0;

        return 0;
    }

    public int GetGemsValue(Vector3Int cell)
    {
        // Returns gem value for ore tiles, used by ores mode
        var t = groundTilemap.GetTile(cell);

        if (t == oreYellow) return 100;
        if (t == orePink) return 70;
        if (t == orePurple) return 50;
        if (t == oreBlue) return 35;
        if (t == oreGreen) return 25;

        return 0;
    }

    // Scans the tilemap to find all ore cells
    // This is simple but can be expensive, so caching is a future optimization
    public IEnumerable<Vector3Int> GetAllOreCells()
    {
        if (groundTilemap == null) yield break;

        var bounds = groundTilemap.cellBounds;
        foreach (var pos in bounds.allPositionsWithin)
        {
            var t = groundTilemap.GetTile(pos);
            if (t != null && IsOreTile(t))
                yield return pos;
        }
    }

    // Scans the tilemap to find all stone cells
    public IEnumerable<Vector3Int> GetAllStoneCells()
    {
        if (groundTilemap == null) yield break;

        var bounds = groundTilemap.cellBounds;
        foreach (var pos in bounds.allPositionsWithin)
        {
            var t = groundTilemap.GetTile(pos);
            if (t == stone)
                yield return pos;
        }
    }

    public void RebuildFrontier()
    {
        // Frontier is any solid tile adjacent to an empty walkable cell
        frontier.Clear();

        foreach (var cell in hp.Keys)
            if (IsAdjacentToEmpty(cell))
                frontier.Add(cell);
    }

    bool IsAdjacentToEmpty(Vector3Int cell)
    {
        // If any neighbor is walkable, this solid tile can be dug next
        foreach (var n in Neighbors4(cell))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    bool HasAnyStandCell(Vector3Int solid)
    {
        // Bunny must have a walkable neighbor to stand on to dig this solid
        foreach (var n in Neighbors4(solid))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    // Returns a list of nearby frontier tiles sorted by distance
    // BunnyAgent applies mode logic to choose among these candidates
    public List<Vector3Int> GetFrontierCandidates(Vector3Int bunnyCell, int radius, int maxCount)
    {
        // Store both cell and distance so we can sort
        var list = new List<(Vector3Int cell, int dist)>();

        foreach (var p in frontier)
        {
            // Use Manhattan distance for grid based range checks
            int dist = Mathf.Abs(p.x - bunnyCell.x) + Mathf.Abs(p.y - bunnyCell.y);
            if (dist > radius) continue;

            // Skip targets the bunny cannot stand next to
            if (!HasAnyStandCell(p)) continue;

            list.Add((p, dist));
        }

        // Sort nearest first so we evaluate closer options first
        list.Sort((a, b) => a.dist.CompareTo(b.dist));

        // Return only up to maxCount entries
        var result = new List<Vector3Int>(Mathf.Min(maxCount, list.Count));
        for (int i = 0; i < list.Count && result.Count < maxCount; i++)
            result.Add(list[i].cell);

        return result;
    }

    public bool ApplyHit(Vector3Int cell, int hitDamage)
    {
        // Ignore hits on cells that are not tracked as solid
        if (!hp.TryGetValue(cell, out var curHp))
            return false;

        // Reduce HP by at least 1
        curHp -= Mathf.Max(1, hitDamage);

        // Break the tile when HP reaches zero
        if (curHp <= 0)
        {
            BreakTile(cell);
            return true;
        }

        // Store updated HP and adjust tile opacity for feedback
        hp[cell] = curHp;
        UpdateOpacityStages(cell);
        return false;
    }

    void UpdateOpacityStages(Vector3Int cell)
    {
        // Adjust opacity in simple steps based on remaining HP
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
        // Remove the logic tile so the cell becomes empty
        groundTilemap.SetTile(cell, null);

        // Reset color so future tiles are not tinted
        groundTilemap.SetColor(cell, Color.white);

        // Update the node grid so pathfinding sees the cell as walkable
        nodeGrid.SetEmpty(cell);

        // Place a dug visual tile if the visual system exists
        if (dugTileSpawner != null)
            dugTileSpawner.PlaceDug(cell);

        // Stop tracking HP for this cell
        hp.Remove(cell);
        maxHp.Remove(cell);

        // Frontier may change when a tile breaks, so rebuild it
        RebuildFrontier();
    }

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors on the grid
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }
}
