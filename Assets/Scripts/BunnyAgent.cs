using System.Collections.Generic;
using UnityEngine;

public class BunnyAgent : MonoBehaviour
{
    // Reference to the digging system
    public DigManager digManager;

    // Reference to the grid used for movement and pathfinding
    public NodeGrid nodeGrid;

    [Header("Movement")]
    // How fast the bunny moves
    public float moveSpeed = 4f;

    // How close the bunny must be to a cell to count as arrived
    public float arriveDistance = 0.02f;

    [Header("Targeting")]
    // How far the bunny can search for dig targets
    public int searchRadius = 35;

    // Time between path recalculations
    public float repathCooldown = 0.5f;

    [Header("Digging")]
    // How many dig hits per second the bunny performs
    public float digHitsPerSecond = 1f;

    // Damage dealt per dig hit
    public int hitDamage = 3;

    // The solid tile the bunny is trying to dig
    Vector3Int targetSolid;

    // The cell the bunny stands on to dig
    Vector3Int standCell;

    // Current path the bunny is following
    List<Vector3Int> path;

    // Index of the current step in the path
    int pathIndex;

    // Timer for digging hits
    float hitTimer;

    // Timer to limit how often paths are recalculated
    float repathTimer;

    void Update()
    {
        // Stop if required systems are missing
        if (digManager == null || nodeGrid == null || nodeGrid.groundTilemap == null) return;

        // Count down repath cooldown
        repathTimer -= Time.deltaTime;

        // Get the bunny current grid cell
        var bunnyCell = nodeGrid.groundTilemap.WorldToCell(transform.position);

        // If the bunny is inside solid ground, move it to a nearby empty cell
        if (!nodeGrid.IsWalkable(bunnyCell))
        {
            if (TryFindNearbyEmpty(bunnyCell, 2, out var empty))
            {
                transform.position = nodeGrid.CellCenterWorld(empty);
                bunnyCell = empty;
            }
        }

        // Planning phase
        if (!HasPlan())
        {
            // Wait until repath cooldown finishes
            if (repathTimer > 0f) return;
            repathTimer = repathCooldown;

            // Find the best solid tile to dig
            if (!digManager.TryGetBestFrontier(bunnyCell, searchRadius, out targetSolid))
                return;

            // Choose a standing cell next to the solid and calculate a path to it
            if (!TryPickStandAndPath(bunnyCell, targetSolid, out standCell, out path))
                return;

            // Reset path and dig timers
            pathIndex = 0;
            hitTimer = 0f;
        }

        // Movement phase
        if (!AtCell(standCell))
        {
            MoveAlongPath();
            return;
        }

        // If the target is no longer solid, stop digging
        if (!digManager.IsSolid(targetSolid))
        {
            ClearPlan();
            return;
        }

        // If the bunny is no longer next to the target, stop and replan
        if (!IsAdjacent(nodeGrid.groundTilemap.WorldToCell(transform.position), targetSolid))
        {
            ClearPlan();
            return;
        }

        // Handle digging speed and timing
        hitTimer += Time.deltaTime;
        float secondsPerHit = 1f / Mathf.Max(0.01f, digHitsPerSecond);

        // Apply dig hits as long as enough time has passed
        while (hitTimer >= secondsPerHit)
        {
            hitTimer -= secondsPerHit;

            bool broke = digManager.ApplyHit(targetSolid, hitDamage);
            if (broke)
            {
                ClearPlan();
                break;
            }
        }
    }

    // Checks if the bunny currently has a valid plan
    bool HasPlan() => path != null && path.Count > 0;

    // Clears all current planning and digging data
    void ClearPlan()
    {
        path = null;
        pathIndex = 0;
        targetSolid = default;
        standCell = default;
        hitTimer = 0f;
    }

    // Checks if the bunny is standing in a specific cell
    bool AtCell(Vector3Int cell)
        => nodeGrid.groundTilemap.WorldToCell(transform.position) == cell;

    // Moves the bunny step by step along the current path
    void MoveAlongPath()
    {
        // If the path is finished, snap to the final stand cell
        if (pathIndex >= path.Count)
        {
            transform.position = nodeGrid.CellCenterWorld(standCell);
            return;
        }

        var next = path[pathIndex];

        // Stop if the next cell becomes unwalkable
        if (!nodeGrid.IsWalkable(next))
        {
            ClearPlan();
            return;
        }

        // Move toward the next cell
        Vector3 targetPos = nodeGrid.CellCenterWorld(next);
        transform.position = Vector3.MoveTowards(transform.position, targetPos, moveSpeed * Time.deltaTime);

        // Advance to the next path step when close enough
        if (Vector3.Distance(transform.position, targetPos) <= arriveDistance)
            pathIndex++;
    }

    // Picks the best adjacent stand cell and shortest path to it
    bool TryPickStandAndPath(Vector3Int bunnyCell, Vector3Int solid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        bestStand = default;
        bestPath = null;

        int bestScore = int.MinValue;

        // Check all four neighbors around the solid tile
        foreach (var n in Neighbors4(solid))
        {
            if (!nodeGrid.IsWalkable(n)) continue;

            var p = nodeGrid.FindPathAStar(bunnyCell, n);
            if (p == null) continue;

            // Prefer shorter paths
            int score = -p.Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestStand = n;
                bestPath = p;
            }
        }

        return bestPath != null;
    }

    // Finds the closest walkable cell near a starting point
    bool TryFindNearbyEmpty(Vector3Int start, int radius, out Vector3Int found)
    {
        found = default;
        int best = int.MaxValue;
        bool ok = false;

        // Search in a square area around the start cell
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                var c = new Vector3Int(start.x + dx, start.y + dy, start.z);
                if (!nodeGrid.IsWalkable(c)) continue;

                int d = Mathf.Abs(dx) + Mathf.Abs(dy);
                if (d < best)
                {
                    best = d;
                    found = c;
                    ok = true;
                }
            }

        return ok;
    }

    // Checks if two cells are directly next to each other
    static bool IsAdjacent(Vector3Int a, Vector3Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) == 1;

    // Returns the four cardinal neighbors of a cell
    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }
}
