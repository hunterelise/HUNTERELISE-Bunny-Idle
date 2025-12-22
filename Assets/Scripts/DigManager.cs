using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class DigManager : MonoBehaviour
{
    [Header("References")]
    // Tilemap that holds the real ground tiles for gameplay logic
    public Tilemap groundTilemap;

    // Node grid that tracks which cells are empty or solid for movement and pathfinding
    public NodeGrid nodeGrid;

    // Optional system that places a dug visual tile on a separate tilemap
    public DugTileSpawner dugTileSpawner;

    [Header("Base Tiles")]
    // Main tile types used in the ground tilemap
    public TileBase mud;
    public TileBase stone;

    [Header("Ore Tiles")]
    // Ore tile types used for gem value and ore detection
    public TileBase oreGreen, oreBlue, orePurple, orePink, oreYellow;

    [Header("HP")]
    // Hit points for mud tiles
    public int mudHp = 4;

    // Hit points for stone and ore tiles
    public int stoneHp = 12;

    // Current hit points for each solid cell
    private readonly Dictionary<Vector3Int, int> hp = new();

    // Max hit points for each solid cell used for damage visuals
    private readonly Dictionary<Vector3Int, int> maxHp = new();

    // Solid cells that touch an empty cell and can be dug next
    private readonly HashSet<Vector3Int> frontier = new();

    public void BuildFromTilemap()
    {
        // Reset stored state before scanning the tilemap
        hp.Clear();
        maxHp.Clear();
        frontier.Clear();

        // Validate required references
        if (groundTilemap == null || nodeGrid == null)
        {
            Debug.LogError("DigManager: assign groundTilemap and nodeGrid.");
            return;
        }

        // Scan every cell in the tilemap bounds
        var bounds = groundTilemap.cellBounds;

        foreach (var pos in bounds.allPositionsWithin)
        {
            // If there is no tile, the cell is empty and not diggable
            var t = groundTilemap.GetTile(pos);
            if (t == null) continue;

            // Decide max HP for this tile type
            int m = GetMaxHpForTile(t);
            if (m <= 0) continue;

            // Store HP values for this solid cell
            hp[pos] = m;
            maxHp[pos] = m;

            // Reset opacity to fully visible on rebuild
            var c = groundTilemap.GetColor(pos);
            c.a = 1f;
            groundTilemap.SetColor(pos, c);
        }

        // Compute which solid cells are exposed to empty space
        RebuildFrontier();
    }

    int GetMaxHpForTile(TileBase t)
    {
        // Mud and stone have different durability
        if (t == mud) return mudHp;
        if (t == stone) return stoneHp;

        // Ores use the same HP as stone
        if (IsOreTile(t)) return stoneHp;

        // Unknown tiles are ignored
        return 0;
    }

    bool IsOreTile(TileBase t)
        => t == oreGreen || t == oreBlue || t == orePurple || t == orePink || t == oreYellow;

    // True if this cell has HP tracking, meaning it is solid
    public bool IsSolid(Vector3Int cell) => hp.ContainsKey(cell);

    // Exposes the current frontier set
    public IEnumerable<Vector3Int> GetFrontier() => frontier;

    // Returns max HP for a cell, or 0 if not tracked
    public int GetMaxHp(Vector3Int cell) => maxHp.TryGetValue(cell, out var m) ? m : 0;

    // Tile type checks used by other scripts
    public bool IsOreCell(Vector3Int cell) => IsOreTile(groundTilemap.GetTile(cell));
    public bool IsMudCell(Vector3Int cell) => groundTilemap.GetTile(cell) == mud;
    public bool IsStoneCell(Vector3Int cell) => groundTilemap.GetTile(cell) == stone;

    public int GetMaterialsYield(Vector3Int cell)
    {
        // Returns how many basic materials a dug tile should give
        var t = groundTilemap.GetTile(cell);

        if (t == mud) return 1;
        if (t == stone) return 3;

        // Ores are not counted as materials
        if (IsOreTile(t)) return 0;

        return 0;
    }

    public int GetGemsValue(Vector3Int cell)
    {
        // Returns gem value for ore tiles, or 0 for non ore tiles
        var t = groundTilemap.GetTile(cell);

        if (t == oreYellow) return 100;
        if (t == orePink) return 70;
        if (t == orePurple) return 50;
        if (t == oreBlue) return 35;
        if (t == oreGreen) return 25;

        return 0;
    }

    public IEnumerable<Vector3Int> GetAllOreCells()
    {
        // Scans the full tilemap and yields positions that contain ore tiles
        if (groundTilemap == null) yield break;

        var bounds = groundTilemap.cellBounds;
        foreach (var pos in bounds.allPositionsWithin)
        {
            var t = groundTilemap.GetTile(pos);
            if (t != null && IsOreTile(t))
                yield return pos;
        }
    }

    public IEnumerable<Vector3Int> GetAllStoneCells()
    {
        // Scans the full tilemap and yields positions that contain stone tiles
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
        // Frontier is any solid cell that has an empty neighbor
        frontier.Clear();

        foreach (var cell in hp.Keys)
            if (IsAdjacentToEmpty(cell))
                frontier.Add(cell);
    }

    bool IsAdjacentToEmpty(Vector3Int cell)
    {
        // If any neighbor is walkable, this solid tile is a frontier tile
        foreach (var n in Neighbors4(cell))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    bool HasAnyStandCell(Vector3Int solid)
    {
        // Bunny can only dig a tile if it can stand next to it
        foreach (var n in Neighbors4(solid))
            if (nodeGrid.IsWalkable(n))
                return true;

        return false;
    }

    public List<Vector3Int> GetFrontierCandidates(Vector3Int bunnyCell, int radius, int maxCount)
    {
        // Builds a list of frontier tiles near the bunny, sorted by distance
        var list = new List<(Vector3Int cell, int dist)>();

        foreach (var p in frontier)
        {
            // Manhattan distance on the grid
            int dist = Mathf.Abs(p.x - bunnyCell.x) + Mathf.Abs(p.y - bunnyCell.y);
            if (dist > radius) continue;

            // Skip targets that have no place to stand
            if (!HasAnyStandCell(p)) continue;

            list.Add((p, dist));
        }

        // Nearest first
        list.Sort((a, b) => a.dist.CompareTo(b.dist));

        // Limit the amount returned to keep planning fast
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

        // Reduce HP by at least 1 each hit
        curHp -= Mathf.Max(1, hitDamage);

        // Break the tile when HP reaches zero
        if (curHp <= 0)
        {
            BreakTile(cell);
            return true;
        }

        // Store new HP and update opacity feedback
        hp[cell] = curHp;
        UpdateOpacityStages(cell);
        return false;
    }

    void UpdateOpacityStages(Vector3Int cell)
    {
        // Uses simple opacity steps to show tile damage
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
        // Remove tile from logic tilemap so the cell becomes empty
        groundTilemap.SetTile(cell, null);

        // Reset color to avoid leftover opacity on future tiles
        groundTilemap.SetColor(cell, Color.white);

        // Update the node grid so pathfinding sees this as walkable
        nodeGrid.SetEmpty(cell);

        // Place a dug visual tile if the visual system exists
        if (dugTileSpawner != null)
            dugTileSpawner.PlaceDug(cell);

        // Stop tracking HP for this cell
        hp.Remove(cell);
        maxHp.Remove(cell);

        // Frontier changes when tiles break, so rebuild it
        RebuildFrontier();
    }

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors used for adjacency checks
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }
}
