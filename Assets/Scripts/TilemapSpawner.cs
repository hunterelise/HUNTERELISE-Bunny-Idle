using UnityEngine;
using UnityEngine.Tilemaps;
using System;
using System.Collections.Generic;

public class TilemapSpawner : MonoBehaviour
{
    [Header("Tilemap (GROUND / LOGIC)")]
    // The tilemap that holds the real ground tiles used for gameplay
    public Tilemap tilemap;

    [Header("Tiles")]
    // Basic ground tiles
    public TileBase mud;
    public TileBase stone;

    [Header("Ore Tiles (common -> rare)")]
    // Ore tiles placed inside stone
    public TileBase oreGreen, oreBlue, orePurple, orePink, oreYellow;

    [Header("World Size")]
    // Width and height of the generated area
    public int width = 80;
    public int height = 60;

    // Y level that counts as the surface line
    public int surfaceY = 0;

    [Header("Layers")]
    // How deep the mud layer stays near the top
    public int groundTopDepth = 2;

    // Depth where stone generation begins
    public int stoneStartDepth = 12;

    [Header("Noise")]
    // Controls how stretched the noise is
    public float noiseScale = 0.08f;

    [Header("Target Ratios (sum should be 1.0)")]
    // Desired percentages for the overall underground mix
    [Range(0f, 1f)] public float mudPercent = 0.34f;
    [Range(0f, 1f)] public float stonePercent = 0.33f;
    [Range(0f, 1f)] public float orePercent = 0.33f;

    [Header("Ore Veins")]
    // Min and max number of tiles per vein
    public Vector2Int veinSizeRange = new Vector2Int(6, 16);

    // Chance that the vein grows into a neighboring stone tile
    [Range(0.3f, 0.9f)] public float veinClumpiness = 0.60f;

    [Header("Starter Burrow")]
    // Starting open area where the player begins
    public Vector3Int entranceCell = new Vector3Int(0, -1, 0);

    // How long the starting tunnel is
    public int starterTunnelLength = 18;

    [Header("Seed Control")]
    // If true, choose a new seed each play session
    public bool randomizeSeedOnPlay = true;

    // The seed used for repeatable generation
    public int seed;

    [Header("Systems")]
    // Dig system that reads the generated tilemap
    public DigManager digManager;

    // Pathfinding and walkability system
    public NodeGrid nodeGrid;

    // Optional separate visual tilemap for dug tiles
    public DugTileSpawner dugTileSpawner;

    // Stores noise values so we can compute a cutoff threshold
    private readonly List<float> _noiseSamples = new();

    // Stores stone cell positions so ores can be placed only in stone
    private readonly List<Vector3Int> _stoneCells = new();

    // Stores cells that were carved out so ores do not overwrite them
    private readonly HashSet<Vector3Int> _dugCells = new();

    void Start()
    {
        // If requested, generate a new seed at runtime
        if (randomizeSeedOnPlay)
            seed = Environment.TickCount ^ UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        Generate();
    }

    public void Generate()
    {
        // Make sure mud, stone, and ore ratios add up to 1
        NormalizeRatios();

        // Set random state so generation is repeatable for this seed
        UnityEngine.Random.InitState(seed);

        // Clear old tiles before generating new ones
        tilemap.ClearAllTiles();

        // Clear cached lists used during generation
        _noiseSamples.Clear();
        _stoneCells.Clear();
        _dugCells.Clear();

        // Noise offsets so different seeds produce different worlds
        float ox = seed * 0.0137f;
        float oy = seed * 0.0213f;

        int eligibleCount = 0;

        // Step 1
        // Collect noise samples only from deep underground cells where stone and ore can appear
        for (int x = -width / 2; x < width / 2; x++)
            for (int y = -height; y < surfaceY; y++)
            {
                int depth = -y;

                // Skip shallow layers so they stay mud
                if (depth <= groundTopDepth) continue;
                if (depth < stoneStartDepth) continue;

                eligibleCount++;

                // Perlin noise value used to decide stone vs mud
                float n = Mathf.PerlinNoise((x + 1000f) * noiseScale + ox,
                                            (y + 1000f) * noiseScale + oy);
                _noiseSamples.Add(n);
            }

        // We decide stone area by choosing a cutoff that matches the target ratio
        float stonePlusOre = stonePercent + orePercent;
        int targetStonePlusOre = Mathf.RoundToInt(eligibleCount * stonePlusOre);

        // Sort noise so we can pick a threshold value
        _noiseSamples.Sort();

        // Cells above this threshold become stone
        int cutoffIndex = Mathf.Clamp(eligibleCount - targetStonePlusOre, 0, Mathf.Max(0, eligibleCount - 1));
        float stoneThreshold = _noiseSamples.Count > 0 ? _noiseSamples[cutoffIndex] : 1f;

        // Step 2
        // Paint the base terrain tiles
        for (int x = -width / 2; x < width / 2; x++)
            for (int y = -height; y < surfaceY; y++)
            {
                int depth = -y;
                var pos = new Vector3Int(x, y, 0);

                // Shallow area stays mud
                if (depth <= groundTopDepth || depth < stoneStartDepth)
                {
                    tilemap.SetTile(pos, mud);
                    continue;
                }

                // Deep area uses noise to choose mud or stone
                float n = Mathf.PerlinNoise((x + 1000f) * noiseScale + ox,
                                            (y + 1000f) * noiseScale + oy);

                if (n >= stoneThreshold)
                {
                    tilemap.SetTile(pos, stone);

                    // Track stone cells for ore placement later
                    _stoneCells.Add(pos);
                }
                else
                {
                    tilemap.SetTile(pos, mud);
                }
            }

        // Step 3
        // Carve an opening and a short starter tunnel by clearing tiles to null
        CarveStarterBurrow();

        // Remove dug cells from the stone list so ores do not spawn inside empty tunnels
        _stoneCells.RemoveAll(c => _dugCells.Contains(c));

        // Step 4
        // Place ore veins inside stone until the target count is reached
        PlaceOresByTarget();

        // Step 5
        // Rebuild navigation nodes after terrain and carving are done
        if (nodeGrid != null)
        {
            // Make sure NodeGrid reads the same ground tilemap we generated
            nodeGrid.groundTilemap = tilemap;

            // Recompute walkable cells and pathing data
            nodeGrid.Rebuild();
        }

        // Step 6
        // Build dig data after the node grid exists
        if (digManager != null)
        {
            // Make sure DigManager reads the same ground tilemap we generated
            digManager.groundTilemap = tilemap;

            // Connect systems used by digging and movement logic
            digManager.nodeGrid = nodeGrid;
            digManager.dugTileSpawner = dugTileSpawner;

            // Provide DigManager the tile references it needs to recognize materials
            digManager.mud = mud;
            digManager.stone = stone;

            digManager.oreGreen = oreGreen;
            digManager.oreBlue = oreBlue;
            digManager.orePurple = orePurple;
            digManager.orePink = orePink;
            digManager.oreYellow = oreYellow;

            // Scan the tilemap and build internal data like hit points and solidity
            digManager.BuildFromTilemap();
        }
    }

    void NormalizeRatios()
    {
        // Ensure the three percentages add up to 1
        float sum = mudPercent + stonePercent + orePercent;

        // If values are broken or all zero, set a safe default
        if (sum <= 0.0001f)
        {
            mudPercent = 0.34f;
            stonePercent = 0.33f;
            orePercent = 0.33f;
            return;
        }

        // Scale values so their sum becomes exactly 1
        mudPercent /= sum;
        stonePercent /= sum;
        orePercent /= sum;
    }

    void CarveStarterBurrow()
    {
        // Make a wider opening at the entrance
        CarveCircle(entranceCell, 2);

        // Random walk to carve a tunnel going mostly downward
        var p = entranceCell;
        for (int i = 0; i < starterTunnelLength; i++)
        {
            CarveCircle(p, 1);

            // Randomly choose the next direction
            float r = UnityEngine.Random.value;
            if (r < 0.65f) p += Vector3Int.down;
            else if (r < 0.82f) p += Vector3Int.left;
            else p += Vector3Int.right;

            // Stop if we leave the generation bounds
            if (!InBounds(p)) break;
        }
    }

    void CarveCircle(Vector3Int center, int radius)
    {
        // Remove tiles in a circular area by setting them to null
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                // Check circle shape instead of a square
                if (dx * dx + dy * dy > radius * radius) continue;

                var c = new Vector3Int(center.x + dx, center.y + dy, 0);
                if (!InBounds(c)) continue;

                // Logic layer: empty cell becomes walkable because there is no ground tile
                tilemap.SetTile(c, null);

                // Visual layer: optionally paint a dug tile somewhere else
                if (dugTileSpawner != null)
                    dugTileSpawner.PlaceDugImmediate(c);

                // Track dug cells so ores do not overwrite them later
                _dugCells.Add(c);
            }
    }

    void PlaceOresByTarget()
    {
        // Ores are placed only in the stone region, so compute ore share inside that region
        float stonePlusOre = stonePercent + orePercent;
        if (stonePlusOre <= 0.0001f) return;

        float oreShareInsideStoneRegion = orePercent / stonePlusOre;

        // Target number of ore tiles based on how many stone cells exist
        int targetOreTiles = Mathf.RoundToInt(_stoneCells.Count * oreShareInsideStoneRegion);
        targetOreTiles = Mathf.Clamp(targetOreTiles, 0, _stoneCells.Count);

        // Use a different random stream for ore placement while still being deterministic
        UnityEngine.Random.InitState(seed + 999);

        int placed = 0;
        int safety = 0;

        // Keep placing veins until we hit the target or give up for safety
        while (placed < targetOreTiles && safety++ < 5000)
        {
            if (_stoneCells.Count == 0) break;

            // Pick a random stone cell as a starting point for the vein
            var start = _stoneCells[UnityEngine.Random.Range(0, _stoneCells.Count)];
            int depth = -start.y;

            // Choose vein size and ore type based on depth
            int veinSize = UnityEngine.Random.Range(veinSizeRange.x, veinSizeRange.y + 1);
            var oreTile = PickOreByDepth(depth);

            // Grow the vein and count how many ore tiles we actually placed
            placed += GrowOreVein(start, oreTile, veinSize, targetOreTiles - placed);
        }
    }

    int GrowOreVein(Vector3Int start, TileBase oreTile, int veinSize, int remainingNeeded)
    {
        // Frontier is the list of candidate cells we might expand into
        var frontier = new List<Vector3Int>(64) { start };

        int placed = 0;
        int safety = 0;

        // Continue until the vein is done, the target is met, or the frontier runs out
        while (veinSize > 0 && remainingNeeded > 0 && frontier.Count > 0 && safety++ < 5000)
        {
            // Pick a random point from the frontier
            var p = frontier[UnityEngine.Random.Range(0, frontier.Count)];
            var t = tilemap.GetTile(p);

            // Only replace stone tiles so veins stay inside rock
            if (t == stone)
            {
                tilemap.SetTile(p, oreTile);
                veinSize--;
                remainingNeeded--;
                placed++;
            }

            // Try to add neighboring stone cells to the frontier
            foreach (var n in Neighbors4(p))
            {
                if (!InBounds(n)) continue;
                if (_dugCells.Contains(n)) continue;

                // Clumpiness controls how likely the vein expands into neighbors
                if (tilemap.GetTile(n) == stone && UnityEngine.Random.value < veinClumpiness)
                    frontier.Add(n);
            }

            // Keep frontier from growing too large
            if (frontier.Count > 140)
                frontier.RemoveRange(0, 30);
        }

        return placed;
    }

    bool InBounds(Vector3Int p)
        => p.x >= -width / 2 && p.x < width / 2 && p.y >= -height && p.y < surfaceY;

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors used for vein growth and movement checks
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }

    TileBase PickOreByDepth(int depth)
    {
        // Weights decide how likely each ore is at this depth
        float wGreen = Mathf.Lerp(70, 20, Mathf.InverseLerp(stoneStartDepth, height, depth));
        float wBlue = 25;
        float wPurple = Mathf.Lerp(5, 25, Mathf.InverseLerp(stoneStartDepth + 8, height, depth));
        float wPink = Mathf.Lerp(0, 20, Mathf.InverseLerp(stoneStartDepth + 16, height, depth));
        float wYellow = Mathf.Lerp(0, 10, Mathf.InverseLerp(stoneStartDepth + 28, height + 40, depth));

        // Pick a random point in the total weight range
        float total = wGreen + wBlue + wPurple + wPink + wYellow;
        float r = UnityEngine.Random.value * total;

        // Subtract weights until we land in the chosen bucket
        if ((r -= wGreen) < 0) return oreGreen;
        if ((r -= wBlue) < 0) return oreBlue;
        if ((r -= wPurple) < 0) return orePurple;
        if ((r -= wPink) < 0) return orePink;
        return oreYellow;
    }
}
