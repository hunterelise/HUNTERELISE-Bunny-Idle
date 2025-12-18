using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

// Each cell is classified as empty or solid for movement and pathfinding
public enum NodeType { Empty, Solid }

public class NodeGrid : MonoBehaviour
{
    [Header("References")]
    // Tilemap used as the source of truth for what is solid and what is empty
    public Tilemap groundTilemap;

    [Header("Build Range")]
    // If true, build nodes from the tilemap bounds
    public bool useTilemapBounds = true;

    // If not using tilemap bounds, use this custom area instead
    public BoundsInt customBounds = new BoundsInt(-40, -60, 0, 80, 60, 1);

    [Header("Gizmos (Scene View)")]
    // Toggles drawing debug nodes in the Scene view
    public bool drawGizmos = true;

    // Draw empty nodes if enabled
    public bool drawEmptyNodes = true;

    // Draw solid nodes if enabled
    public bool drawSolidNodes = true;

    // Size of the gizmo cube drawn per node
    [Range(0.05f, 1f)] public float nodeSize = 0.20f;

    // Transparency for gizmo colors
    [Range(0f, 1f)] public float gizmoAlpha = 0.35f;

    // Stores the node type for each cell that was built
    private readonly Dictionary<Vector3Int, NodeType> nodes = new();

    // Exposes nodes as read only to other scripts
    public IReadOnlyDictionary<Vector3Int, NodeType> Nodes => nodes;

    public void Rebuild()
    {
        // NodeGrid cannot build without a tilemap
        if (groundTilemap == null)
        {
            Debug.LogError("NodeGrid: groundTilemap not assigned.");
            return;
        }

        // Remove old node data
        nodes.Clear();

        // Decide which area to scan
        BoundsInt b = useTilemapBounds ? groundTilemap.cellBounds : customBounds;

        // Read every cell and mark it solid if it has a tile
        foreach (var cell in b.allPositionsWithin)
        {
            bool solid = groundTilemap.GetTile(cell) != null;
            nodes[cell] = solid ? NodeType.Solid : NodeType.Empty;
        }
    }

    // Walkable means the cell exists in our dictionary and is empty
    public bool IsWalkable(Vector3Int cell)
        => nodes.TryGetValue(cell, out var t) && t == NodeType.Empty;

    // Solid means the cell exists in our dictionary and is solid
    public bool IsSolid(Vector3Int cell)
        => nodes.TryGetValue(cell, out var t) && t == NodeType.Solid;

    // Updates a cell when something is dug out
    public void SetEmpty(Vector3Int cell) => nodes[cell] = NodeType.Empty;

    // Updates a cell when something becomes solid again
    public void SetSolid(Vector3Int cell) => nodes[cell] = NodeType.Solid;

    // Converts a cell to a world position at the center of the cell
    public Vector3 CellCenterWorld(Vector3Int cell)
    {
        return groundTilemap.GetCellCenterWorld(cell);
    }

    // Finds a path using A* while moving only through empty cells
    public List<Vector3Int> FindPathAStar(Vector3Int start, Vector3Int goal, int maxNodes = 6000)
    {
        // Pathing is only valid if both start and goal are walkable
        if (!IsWalkable(start) || !IsWalkable(goal)) return null;

        // If already at the goal, return a simple path
        if (start == goal) return new List<Vector3Int> { start };

        // Open set stores nodes to explore, ordered by lowest estimated total cost
        var open = new SimpleMinHeap();

        // Stores the best previous cell for each visited cell
        var cameFrom = new Dictionary<Vector3Int, Vector3Int>(1024);

        // Stores cost from start to each cell
        var gScore = new Dictionary<Vector3Int, int>(1024);

        // Start at cost zero
        gScore[start] = 0;

        // Priority is gScore plus heuristic distance to goal
        open.Push(start, Heuristic(start, goal));

        int visited = 0;

        // Loop until we find the goal or run out of nodes to explore
        while (open.Count > 0 && visited++ < maxNodes)
        {
            // Get the node with the lowest priority value
            var current = open.Pop();

            // Stop when we reach the goal
            if (current == goal) return ReconstructPath(cameFrom, current);

            int currentG = gScore[current];

            // Explore the four neighbors
            foreach (var n in Neighbors4(current))
            {
                // Only move through walkable cells
                if (!IsWalkable(n)) continue;

                // Each move costs 1
                int tentative = currentG + 1;

                // If this is a better route to n, store it
                if (!gScore.TryGetValue(n, out int old) || tentative < old)
                {
                    cameFrom[n] = current;
                    gScore[n] = tentative;

                    // f is estimated total cost through this node
                    int f = tentative + Heuristic(n, goal);
                    open.Push(n, f);
                }
            }
        }

        // No path found
        return null;
    }

    // Manhattan distance works well for four direction grid movement
    static int Heuristic(Vector3Int a, Vector3Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    static List<Vector3Int> ReconstructPath(Dictionary<Vector3Int, Vector3Int> cameFrom, Vector3Int current)
    {
        // Walk backwards from goal to start using cameFrom
        var path = new List<Vector3Int> { current };

        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            path.Add(current);
        }

        // Reverse so path goes start to goal
        path.Reverse();
        return path;
    }

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }

    void OnDrawGizmos()
    {
        // Gizmos only appear in the Scene view and are meant for debugging
        if (!drawGizmos || groundTilemap == null) return;

        foreach (var kv in nodes)
        {
            var cell = kv.Key;
            var type = kv.Value;

            // Allow filtering of which node types to draw
            if (type == NodeType.Empty && !drawEmptyNodes) continue;
            if (type == NodeType.Solid && !drawSolidNodes) continue;

            // Choose a different color for empty vs solid
            Gizmos.color = (type == NodeType.Empty)
                ? new Color(1f, 1f, 1f, gizmoAlpha)
                : new Color(0f, 1f, 1f, gizmoAlpha);

            // Draw a small wire cube at the center of the cell
            Vector3 p = groundTilemap.GetCellCenterWorld(cell);
            Gizmos.DrawWireCube(p, new Vector3(nodeSize, nodeSize, 0.01f));
        }
    }

    // Small priority queue used by A* to always pop the lowest priority node first
    private class SimpleMinHeap
    {
        // Each entry stores a node and its priority value
        private readonly List<(Vector3Int node, int pri)> data = new();

        // Number of items currently stored
        public int Count => data.Count;

        public void Push(Vector3Int node, int pri)
        {
            // Add and then move it upward until heap order is correct
            data.Add((node, pri));
            SiftUp(data.Count - 1);
        }

        public Vector3Int Pop()
        {
            // Remove the root item, then move the last item to the root and fix heap order
            var root = data[0].node;
            var last = data[data.Count - 1];
            data.RemoveAt(data.Count - 1);

            if (data.Count > 0)
            {
                data[0] = last;
                SiftDown(0);
            }

            return root;
        }

        void SiftUp(int i)
        {
            // Move item up while it has a lower priority than its parent
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (data[i].pri >= data[p].pri) break;

                (data[i], data[p]) = (data[p], data[i]);
                i = p;
            }
        }

        void SiftDown(int i)
        {
            // Move item down while it has a higher priority than a child
            int n = data.Count;
            while (true)
            {
                int l = i * 2 + 1;
                int r = l + 1;
                int s = i;

                if (l < n && data[l].pri < data[s].pri) s = l;
                if (r < n && data[r].pri < data[s].pri) s = r;

                if (s == i) break;

                (data[i], data[s]) = (data[s], data[i]);
                i = s;
            }
        }
    }
}
