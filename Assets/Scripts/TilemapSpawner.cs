using UnityEngine;
using UnityEngine.Tilemaps;
using System;
using System.Collections.Generic;

public class TilemapSpawner : MonoBehaviour
{
    [Header("Tilemap (GROUND / LOGIC)")]
    // Tilemap used for gameplay logic (mud, stone, ore, and empty cells)
    public Tilemap tilemap;

    [Header("Tiles")]
    // Base ground tiles
    public TileBase mud;
    public TileBase stone;

    [Header("Ore Tiles (common -> rare)")]
    // Ore tiles used for gem placement
    public TileBase oreGreen, oreBlue, orePurple, orePink, oreYellow;

    [Header("Chunk Size")]
    // Width of each generated chunk in tiles
    public int chunkWidth = 128;

    // Height of each generated chunk in tiles
    public int chunkHeight = 128;

    // World surface Y (top edge reference). Typically 0
    public int surfaceY = 0;

    [Header("Generation Seed")]
    // If true, a new seed is created on play
    public bool randomizeSeedOnPlay = true;

    // Seed used for deterministic generation
    public int seed;

    [Header("Noise")]
    // Perlin noise scale for mud versus stone variation
    public float noiseScale = 0.08f;

    [Header("Base Ratios (Tier 0)")]
    // Starting ratios for the first chunk
    [Range(0f, 1f)] public float baseMudPercent = 0.60f;
    [Range(0f, 1f)] public float baseStonePercent = 0.37f;
    [Range(0f, 1f)] public float baseOrePercent = 0.03f;

    [Header("Tier Scaling (per chunk)")]
    // How much mud decreases per chunk as the mine gets deeper
    [Range(0f, 0.50f)] public float mudDeltaPerTier = 0.08f;

    // How much stone increases per chunk as the mine gets deeper
    [Range(0f, 0.50f)] public float stoneDeltaPerTier = 0.06f;

    // How much ore increases per chunk as the mine gets deeper
    [Range(0f, 0.50f)] public float oreDeltaPerTier = 0.02f;

    [Header("Tier Caps")]
    // Lower bound for mud percent at deep tiers
    [Range(0f, 1f)] public float minMudPercent = 0.12f;

    // Upper bound for ore percent at deep tiers
    [Range(0f, 1f)] public float maxOrePercent = 0.25f;

    [Header("Filled 'Cave Feel' (Mud Pockets)")]
    // Local depth in the chunk before mud pockets start appearing
    public int cavesStartDepthLocal = 6;

    // Starting fill percent used by the cellular automata pass
    [Range(35, 70)] public int caveFillPercent = 52;

    // Number of smoothing iterations for the cellular automata pass
    [Range(1, 12)] public int caveSmoothIterations = 5;

    // Controls when empty pockets are born during smoothing
    [Range(3, 8)] public int caBirthLimit = 4;

    // Controls when pockets die and become solid again during smoothing
    [Range(1, 7)] public int caDeathLimit = 3;

    [Header("Mud Worms")]
    // Number of worm paths drawn per chunk
    public int wormCount = 30;

    // Length range for each worm path
    public Vector2Int wormLengthRange = new Vector2Int(60, 170);

    // Radius range for each worm path when softening to mud
    public Vector2Int wormRadiusRange = new Vector2Int(1, 2);

    // Chance per step that a worm turns
    [Range(0f, 1f)] public float wormTurnChance = 0.35f;

    // Chance per worm to create a branch
    [Range(0f, 1f)] public float wormSplitChance = 0.20f;

    [Header("Ore Veins")]
    // Min and max ore tiles in a vein
    public Vector2Int veinSizeRange = new Vector2Int(6, 16);

    // Chance that the vein grows into a neighboring stone tile
    [Range(0.3f, 0.9f)] public float veinClumpiness = 0.60f;

    [Header("Starter Burrow (only in chunk 0)")]
    // Entrance position for the starter tunnel
    public Vector3Int entranceCell = new Vector3Int(0, -1, 0);

    // Length of the starter tunnel
    public int starterTunnelLength = 18;

    [Header("Systems")]
    // Dig system that reads the generated world tilemap
    public DigManager digManager;

    // Node grid used for walkability and pathfinding
    public NodeGrid nodeGrid;

    // Optional separate tilemap for dug visuals
    public DugTileSpawner dugTileSpawner;

    [Header("Runtime (Debug)")]
    // Number of chunks that have been generated so far
    public int generatedChunks = 0;

    // Noise samples used to compute a stone threshold
    private readonly List<float> _noiseSamples = new();

    // Stone cells in the current chunk used for ore placement
    private readonly List<Vector3Int> _stoneCells = new();

    // Cells that have been carved to empty so ores do not spawn there
    private readonly HashSet<Vector3Int> _dugCells = new();

    void Start()
    {
        // Choose a seed on play if requested
        if (randomizeSeedOnPlay)
            seed = Environment.TickCount ^ UnityEngine.Random.Range(int.MinValue, int.MaxValue);

        // Generate the first chunk
        ResetAndGenerateFirstChunk();
    }

    // Clears the world and regenerates from chunk 0
    public void ResetAndGenerateFirstChunk()
    {
        generatedChunks = 0;

        // Clear the logic tilemap
        if (tilemap != null)
            tilemap.ClearAllTiles();

        // Clear dug visuals if present
        if (dugTileSpawner != null && dugTileSpawner.dugTilemap != null)
            dugTileSpawner.dugTilemap.ClearAllTiles();

        _dugCells.Clear();

        GenerateNextChunk();
    }

    // Generates one new chunk below the current mine
    public void GenerateNextChunk()
    {
        int chunkIndex = generatedChunks;

        GenerateChunk(chunkIndex);
        generatedChunks++;

        // Rebuild systems after adding new terrain
        RebuildSystemsAfterGeneration();
    }

    // Generates one chunk at the given chunk index
    public void GenerateChunk(int chunkIndex)
    {
        if (tilemap == null)
        {
            Debug.LogError("TilemapSpawner: tilemap not assigned.");
            return;
        }

        // Chunk x bounds are centered around x = 0
        int minX = -chunkWidth / 2;
        int maxX = (chunkWidth / 2) - 1;

        // Chunk y bounds stack downward from surfaceY
        int topY = surfaceY - (chunkIndex * chunkHeight);
        int bottomY = topY - chunkHeight;

        // Get the ratios for this depth tier
        GetRatiosForTier(chunkIndex, out float mudPercent, out float stonePercent, out float orePercent);

        // Seed Unity random so each chunk is deterministic but different
        UnityEngine.Random.InitState(seed ^ (chunkIndex * 7919));

        // Step 1
        // Sample noise to compute a threshold that matches stone plus ore ratio
        _noiseSamples.Clear();
        int eligibleCount = 0;

        float ox = (seed + chunkIndex * 97) * 0.0137f;
        float oy = (seed + chunkIndex * 97) * 0.0213f;

        for (int x = minX; x <= maxX; x++)
            for (int y = bottomY; y < topY; y++)
            {
                eligibleCount++;

                float n = Mathf.PerlinNoise((x + 1000f) * noiseScale + ox,
                                            (y + 1000f) * noiseScale + oy);
                _noiseSamples.Add(n);
            }

        float stonePlusOre = stonePercent + orePercent;
        int targetStonePlusOre = Mathf.RoundToInt(eligibleCount * stonePlusOre);

        _noiseSamples.Sort();
        int cutoffIndex = Mathf.Clamp(eligibleCount - targetStonePlusOre, 0, Mathf.Max(0, eligibleCount - 1));
        float stoneThreshold = _noiseSamples.Count > 0 ? _noiseSamples[cutoffIndex] : 1f;

        // Step 2
        // Paint base mud and stone tiles for this chunk
        for (int x = minX; x <= maxX; x++)
            for (int y = bottomY; y < topY; y++)
            {
                var pos = new Vector3Int(x, y, 0);

                float n = Mathf.PerlinNoise((x + 1000f) * noiseScale + ox,
                                            (y + 1000f) * noiseScale + oy);

                tilemap.SetTile(pos, (n >= stoneThreshold) ? stone : mud);
            }

        // Step 3
        // Add filled mud pockets for variety without carving empty space
        MakeMudPockets_CellAuto(minX, maxX, bottomY, topY, chunkIndex);
        MakeMudPockets_Worms(minX, maxX, bottomY, topY, chunkIndex);

        // Step 4
        // Collect stone cells in this chunk for ore placement
        BuildStoneCellsForChunk(minX, maxX, bottomY, topY);

        // Step 5
        // Carve the starter tunnel only in chunk 0
        if (chunkIndex == 0)
            CarveStarterBurrow();

        // Prevent ore from spawning inside carved empty cells
        _stoneCells.RemoveAll(c => _dugCells.Contains(c));

        // Step 6
        // Place ore veins inside stone tiles in this chunk
        PlaceOresByTargetForChunk(chunkIndex);
    }

    void GetRatiosForTier(int tier, out float mudPercent, out float stonePercent, out float orePercent)
    {
        // Apply tier deltas and caps
        mudPercent = Mathf.Max(minMudPercent, baseMudPercent - (tier * mudDeltaPerTier));
        stonePercent = baseStonePercent + (tier * stoneDeltaPerTier);
        orePercent = Mathf.Min(maxOrePercent, baseOrePercent + (tier * oreDeltaPerTier));

        // Normalize so the three values sum to 1
        float sum = mudPercent + stonePercent + orePercent;
        if (sum <= 0.0001f)
        {
            mudPercent = 0.60f;
            stonePercent = 0.37f;
            orePercent = 0.03f;
            return;
        }

        mudPercent /= sum;
        stonePercent /= sum;
        orePercent /= sum;
    }

    void MakeMudPockets_CellAuto(int minX, int maxX, int bottomY, int topY, int chunkIndex)
    {
        // Build a local boolean map for this chunk where true means keep solid
        int w = (maxX - minX + 1);
        int h = (topY - bottomY);

        if (w <= 0 || h <= 0) return;

        bool[,] map = new bool[w, h];

        // Use a deterministic seed for the cellular automata pass
        UnityEngine.Random.InitState(seed + chunkIndex * 100000 + 12345);

        // Initialize the map with random values after a starting depth
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int worldY = bottomY + y;

                // Depth within the chunk, 0 at the top of the chunk
                int localDepth = (topY - 1) - worldY;

                // Keep the top part stable so it does not become too patchy
                if (localDepth < cavesStartDepthLocal)
                {
                    map[x, y] = true;
                    continue;
                }

                // Force borders to stay solid
                if (x == 0 || x == w - 1 || y == 0 || y == h - 1)
                {
                    map[x, y] = true;
                    continue;
                }

                // Randomly decide solid or pocket seed
                map[x, y] = UnityEngine.Random.Range(0, 100) < caveFillPercent;
            }

        // Smooth the map using cellular automata rules
        for (int i = 0; i < caveSmoothIterations; i++)
        {
            bool[,] next = new bool[w, h];

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int worldY = bottomY + y;
                    int localDepth = (topY - 1) - worldY;

                    if (localDepth < cavesStartDepthLocal)
                    {
                        next[x, y] = true;
                        continue;
                    }

                    if (x == 0 || x == w - 1 || y == 0 || y == h - 1)
                    {
                        next[x, y] = true;
                        continue;
                    }

                    int solidNeighbors = CountSolidNeighbors8(map, x, y, w, h);

                    // If a cell is solid, it stays solid when enough neighbors are solid
                    // If a cell is pocket, it becomes solid unless it has many solid neighbors
                    if (map[x, y])
                        next[x, y] = solidNeighbors >= caDeathLimit;
                    else
                        next[x, y] = solidNeighbors > caBirthLimit;
                }

            map = next;
        }

        // Apply pockets by converting those cells to mud
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                // False means we decided this cell becomes a mud pocket
                if (map[x, y]) continue;

                var c = new Vector3Int(minX + x, bottomY + y, 0);
                SoftenToMud(c);
            }
    }

    static int CountSolidNeighbors8(bool[,] map, int x, int y, int w, int h)
    {
        // Counts solid neighbors in 8 directions around a cell
        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int nx = x + dx;
                int ny = y + dy;

                // Out of bounds is treated as solid to keep edges closed
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                {
                    count++;
                    continue;
                }

                if (map[nx, ny]) count++;
            }

        return count;
    }

    void MakeMudPockets_Worms(int minX, int maxX, int bottomY, int topY, int chunkIndex)
    {
        // Uses random walk worms to soften areas into mud paths
        UnityEngine.Random.InitState(seed + chunkIndex * 100000 + 54321);

        for (int i = 0; i < wormCount; i++)
        {
            int len = UnityEngine.Random.Range(wormLengthRange.x, wormLengthRange.y + 1);
            int rad = UnityEngine.Random.Range(wormRadiusRange.x, wormRadiusRange.y + 1);

            int startX = UnityEngine.Random.Range(minX + 2, maxX - 1);
            int startY = UnityEngine.Random.Range(bottomY + 2, topY - 2);

            var p = new Vector3Int(startX, startY, 0);

            // Bias direction toward going down, with some sideways movement
            Vector3Int dir = UnityEngine.Random.value < 0.55f ? Vector3Int.down :
                             UnityEngine.Random.value < 0.80f ? Vector3Int.left :
                                                                Vector3Int.right;

            bool didSplit = false;

            for (int step = 0; step < len; step++)
            {
                // Stop if the worm leaves the chunk bounds
                if (p.x < minX || p.x > maxX || p.y < bottomY || p.y >= topY) break;

                // Occasionally enlarge radius to vary thickness
                int r = rad + (UnityEngine.Random.value < 0.15f ? 1 : 0);
                SoftenCircleToMud(p, r);

                // Sometimes change direction
                if (UnityEngine.Random.value < wormTurnChance)
                {
                    float t = UnityEngine.Random.value;
                    if (t < 0.33f) dir = TurnLeft(dir);
                    else if (t < 0.66f) dir = TurnRight(dir);
                    else dir = UnityEngine.Random.value < 0.5f ? Vector3Int.left : Vector3Int.right;
                }

                // Extra bias to drift downward
                if (UnityEngine.Random.value < 0.25f)
                    dir = Vector3Int.down;

                p += dir;

                // One optional split per worm after it has progressed a bit
                if (!didSplit && UnityEngine.Random.value < wormSplitChance && step > len / 4)
                {
                    didSplit = true;
                    MudWormBranch(p, dir, len / 2, rad, minX, maxX, bottomY, topY);
                }
            }
        }
    }

    void MudWormBranch(Vector3Int start, Vector3Int baseDir, int len, int rad,
                       int minX, int maxX, int bottomY, int topY)
    {
        // Creates a smaller branch worm from a parent worm path
        var p = start;
        Vector3Int dir = UnityEngine.Random.value < 0.5f ? TurnLeft(baseDir) : TurnRight(baseDir);

        for (int step = 0; step < len; step++)
        {
            if (p.x < minX || p.x > maxX || p.y < bottomY || p.y >= topY) break;

            SoftenCircleToMud(p, rad);

            if (UnityEngine.Random.value < wormTurnChance)
                dir = UnityEngine.Random.value < 0.5f ? TurnLeft(dir) : TurnRight(dir);

            if (UnityEngine.Random.value < 0.20f)
                dir = Vector3Int.down;

            p += dir;
        }
    }

    static Vector3Int TurnLeft(Vector3Int dir)
    {
        // Rotates a cardinal direction left
        if (dir == Vector3Int.up) return Vector3Int.left;
        if (dir == Vector3Int.left) return Vector3Int.down;
        if (dir == Vector3Int.down) return Vector3Int.right;
        return Vector3Int.up;
    }

    static Vector3Int TurnRight(Vector3Int dir)
    {
        // Rotates a cardinal direction right
        if (dir == Vector3Int.up) return Vector3Int.right;
        if (dir == Vector3Int.right) return Vector3Int.down;
        if (dir == Vector3Int.down) return Vector3Int.left;
        return Vector3Int.up;
    }

    void SoftenToMud(Vector3Int c)
    {
        // Converts a solid tile to mud unless it is ore or empty
        var t = tilemap.GetTile(c);
        if (t == null) return;
        if (IsOreTile(t)) return;

        tilemap.SetTile(c, mud);
    }

    void SoftenCircleToMud(Vector3Int center, int radius)
    {
        // Softens a circular area around a point into mud
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                var c = new Vector3Int(center.x + dx, center.y + dy, 0);
                SoftenToMud(c);
            }
    }

    void CarveStarterBurrow()
    {
        // Carve a wider opening, then dig a small tunnel downward with some turns
        CarveEmptyCircle(entranceCell, 2);

        var p = entranceCell;
        for (int i = 0; i < starterTunnelLength; i++)
        {
            CarveEmptyCircle(p, 1);

            float r = UnityEngine.Random.value;
            if (r < 0.65f) p += Vector3Int.down;
            else if (r < 0.82f) p += Vector3Int.left;
            else p += Vector3Int.right;
        }
    }

    void CarveEmptyCell(Vector3Int c)
    {
        // Remove the logic tile so this cell becomes empty and walkable
        tilemap.SetTile(c, null);

        // Place a dug visual tile if that system exists
        if (dugTileSpawner != null)
            dugTileSpawner.PlaceDugImmediate(c);

        // Track carved cells so we can avoid spawning ore inside them
        _dugCells.Add(c);
    }

    void CarveEmptyCircle(Vector3Int center, int radius)
    {
        // Carves a circular area of empty cells
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                var c = new Vector3Int(center.x + dx, center.y + dy, 0);
                CarveEmptyCell(c);
            }
    }

    void BuildStoneCellsForChunk(int minX, int maxX, int bottomY, int topY)
    {
        // Builds a list of stone cells for ore placement in this chunk
        _stoneCells.Clear();

        for (int x = minX; x <= maxX; x++)
            for (int y = bottomY; y < topY; y++)
            {
                var c = new Vector3Int(x, y, 0);
                if (tilemap.GetTile(c) == stone)
                    _stoneCells.Add(c);
            }
    }

    void PlaceOresByTargetForChunk(int chunkIndex)
    {
        // Compute ore share inside the stone region for this tier
        GetRatiosForTier(chunkIndex, out float mudPercent, out float stonePercent, out float orePercent);

        float stonePlusOre = stonePercent + orePercent;
        if (stonePlusOre <= 0.0001f) return;

        float oreShareInsideStone = orePercent / stonePlusOre;

        int targetOreTiles = Mathf.RoundToInt(_stoneCells.Count * oreShareInsideStone);
        targetOreTiles = Mathf.Clamp(targetOreTiles, 0, _stoneCells.Count);

        // Increase vein size slightly for deeper tiers
        int tierVeinBonus = Mathf.Clamp(chunkIndex, 0, 8);
        int veinMin = Mathf.Clamp(veinSizeRange.x + tierVeinBonus / 2, 2, 999);
        int veinMax = Mathf.Clamp(veinSizeRange.y + tierVeinBonus, veinMin, 999);

        // Use deterministic random values for ore placement
        UnityEngine.Random.InitState(seed + 999 + chunkIndex * 13337);

        int placed = 0;
        int safety = 0;

        while (placed < targetOreTiles && safety++ < 8000)
        {
            if (_stoneCells.Count == 0) break;

            // Pick a random stone cell to start a vein
            var start = _stoneCells[UnityEngine.Random.Range(0, _stoneCells.Count)];

            int depth = -start.y;
            int veinSize = UnityEngine.Random.Range(veinMin, veinMax + 1);
            var oreTile = PickOreByDepth(depth);

            placed += GrowOreVein(start, oreTile, veinSize, targetOreTiles - placed);
        }
    }

    int GrowOreVein(Vector3Int start, TileBase oreTile, int veinSize, int remainingNeeded)
    {
        // Frontier holds candidate cells to expand the vein into
        var frontier = new List<Vector3Int>(64) { start };

        int placed = 0;
        int safety = 0;

        while (veinSize > 0 && remainingNeeded > 0 && frontier.Count > 0 && safety++ < 8000)
        {
            var p = frontier[UnityEngine.Random.Range(0, frontier.Count)];
            var t = tilemap.GetTile(p);

            // Only replace stone so ore stays inside rock
            if (t == stone)
            {
                tilemap.SetTile(p, oreTile);
                veinSize--;
                remainingNeeded--;
                placed++;
            }

            // Expand into neighboring stone cells based on clumpiness
            foreach (var n in Neighbors4(p))
            {
                if (_dugCells.Contains(n)) continue;

                if (tilemap.GetTile(n) == stone && UnityEngine.Random.value < veinClumpiness)
                    frontier.Add(n);
            }

            // Keep frontier size under control
            if (frontier.Count > 160)
                frontier.RemoveRange(0, 40);
        }

        return placed;
    }

    TileBase PickOreByDepth(int depth)
    {
        // Deeper layers increase the chance of rarer ores
        float wGreen = Mathf.Lerp(70, 20, Mathf.InverseLerp(12, 600, depth));
        float wBlue = 25;
        float wPurple = Mathf.Lerp(5, 25, Mathf.InverseLerp(20, 600, depth));
        float wPink = Mathf.Lerp(0, 20, Mathf.InverseLerp(40, 650, depth));
        float wYellow = Mathf.Lerp(0, 10, Mathf.InverseLerp(70, 700, depth));

        float total = wGreen + wBlue + wPurple + wPink + wYellow;
        float r = UnityEngine.Random.value * total;

        if ((r -= wGreen) < 0) return oreGreen;
        if ((r -= wBlue) < 0) return oreBlue;
        if ((r -= wPurple) < 0) return orePurple;
        if ((r -= wPink) < 0) return orePink;
        return oreYellow;
    }

    bool IsOreTile(TileBase t)
        => t == oreGreen || t == oreBlue || t == orePurple || t == orePink || t == oreYellow;

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }

    void RebuildSystemsAfterGeneration()
    {
        // NodeGrid must read from the same tilemap that was generated
        if (nodeGrid != null)
        {
            nodeGrid.groundTilemap = tilemap;
            nodeGrid.Rebuild();
        }

        // DigManager rebuilds HP tracking and frontier data from the tilemap
        if (digManager != null)
        {
            digManager.groundTilemap = tilemap;
            digManager.nodeGrid = nodeGrid;
            digManager.dugTileSpawner = dugTileSpawner;

            digManager.mud = mud;
            digManager.stone = stone;

            digManager.oreGreen = oreGreen;
            digManager.oreBlue = oreBlue;
            digManager.orePurple = orePurple;
            digManager.orePink = orePink;
            digManager.oreYellow = oreYellow;

            digManager.BuildFromTilemap();
        }
    }
}
