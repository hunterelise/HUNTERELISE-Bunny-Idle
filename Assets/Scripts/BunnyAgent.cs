// BunnyAgent.cs
using System.Collections.Generic;
using UnityEngine;

public class BunnyAgent : MonoBehaviour
{
    // References to the digging system and pathfinding grid
    public DigManager digManager;
    public NodeGrid nodeGrid;

    [Header("Movement")]
    // Movement speed in world units per second
    public float moveSpeed = 2f;

    // How close the bunny must be to consider it arrived
    public float arriveDistance = 0.03f;

    [Header("Targeting")]
    // Max distance (in tiles) to search for dig targets
    public int searchRadius = 35;

    // Minimum time between choosing a new target
    public float repathCooldown = 0.7f;

    // Limits the number of frontier tiles we evaluate each repath
    public int maxCandidates = 30;

    [Header("Digging")]
    // Hits applied per second while digging
    public float digHitsPerSecond = 1f;

    // Damage per hit
    public int hitDamage = 3;

    // Dig modes influence what the bunny prefers to dig
    public enum DigMode { Balanced, Materials, Ores }

    [Header("Goal Mode")]
    // Current mode for target selection
    public DigMode digMode = DigMode.Balanced;

    [Header("Mode Bias")]
    // Materials mode uses this when digging toward a stone pocket goal
    public float stoneGoalBiasWeight = 6f;

    // Ores mode uses this when digging toward an ore pocket goal
   public float oreGoalBiasWeight = 8f;

    [Header("Awareness / Sensing")]
    // If true, the bunny scans nearby tiles and remembers ores and stone pockets
    public bool useSensing = true;

    // Radius in tiles used for scanning
    public int senseRadius = 16;

    // Time between scans
    public float senseInterval = 0.6f;

    // If true, ores mode prefers higher value ore within sensed range
    public bool orePreferHighestValue = true;

    // If true, materials mode tries to tunnel toward stone pockets even if mud is closer
    public bool materialsPreferStonePocket = true;

    // Timer until the next sensing scan
    float senseTimer;

    // Memory of nearby ore and stone locations from the last scan
    readonly List<Vector3Int> knownOres = new();
    readonly List<Vector3Int> knownStone = new();

    [Header("Scoring / Limits")]
    // Allows limited extra travel when choosing a goal-biased plan
    public int extraTravelAllowanceSteps = 10;

    [Header("Goal Tunneling (makes mode feel intentional)")]
    // Weight for candidates that reduce distance to the goal pocket
    public float goalImproveWeight = 8f;

    // Weight for path length cost
    public float pathCostWeight = 1.0f;

    // Time after breaking a tile where the bunny tries to keep digging in the same direction
    public float digDirectionStreakSeconds = 1.5f;

    // Bonus score if the next target continues the same digging direction
    public float digDirectionBonus = 3.0f;

    // Countdown for the direction streak
    float digDirStreakTimer;

    // Last digging direction as a 4 direction vector
    Vector3Int lastDigDir;

    [Header("Debug Hotkeys")]
    // Allows changing dig mode with keys during play
    public bool enableModeHotkeys = true;
    public KeyCode balancedKey = KeyCode.Alpha1;
    public KeyCode materialsKey = KeyCode.Alpha2;
    public KeyCode oresKey = KeyCode.Alpha3;
    public bool logModeChanges = true;

    [Header("Debug Selection")]
    // Logs sensing and selection info
    public bool logSelections = false;

    [Header("Animation")]
    // Animator to drive movement and digging states
    public Animator animator;

    // Smoothing for the Speed parameter
    public float speedDampTime = 0.08f;

    [Header("Visual Facing + Dig Nudge")]
    // Object that gets flipped left and right
    public Transform visualsRoot;

    // Small position nudge toward the target while digging
    public float digNudge = 0.08f;

    [Header("Recovery (anti-jank)")]
    // Speed multiplier used when escaping from being inside a solid tile
    public float recoverSpeedMultiplier = 1.25f;

    // Arrival distance for recovery movement
    public float recoverArriveDistance = 0.02f;

    [Header("Plan Stability")]
    // Minimum time to keep a plan before clearing it due to changes
    public float minPlanDuration = 1.0f;

    // Animator parameter ids
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int IsDiggingHash = Animator.StringToHash("IsDigging");

    // Current dig target and where the bunny should stand to dig it
    Vector3Int targetSolid;
    Vector3Int standCell;

    // Current path to the stand cell
    List<Vector3Int> path;
    int pathIndex;

    // Timers for digging hits and replanning
    float hitTimer;
    float repathTimer;
    float planLockTimer;

    // Used to calculate speed for animation
    Vector3 lastPos;

    // Recovery state for when the bunny ends up inside a solid tile
    bool recovering;
    Vector3 recoverWorldTarget;

    void Awake()
    {
        // Auto assign optional references if not set
        if (animator == null) animator = GetComponent<Animator>();
        if (visualsRoot == null) visualsRoot = transform;

        // Store position for speed calculations
        lastPos = transform.position;
    }

    void Update()
    {
        // If key systems are missing, stop digging and do nothing
        if (digManager == null || nodeGrid == null || nodeGrid.groundTilemap == null)
        {
            SetDigging(false);
            return;
        }

        // Optional debug mode switching
        HandleModeHotkeys();

        // Update timers
        repathTimer -= Time.deltaTime;
        planLockTimer -= Time.deltaTime;
        digDirStreakTimer -= Time.deltaTime;

        // Determine bunny position in grid space
        var bunnyCell = nodeGrid.groundTilemap.WorldToCell(transform.position);

        // Periodically scan nearby tiles to update local memory
        senseTimer -= Time.deltaTime;
        if (useSensing && senseTimer <= 0f)
        {
            senseTimer = Mathf.Max(0.05f, senseInterval);
            SenseTerrain(bunnyCell);
        }

        // If inside a solid, move to the closest empty cell to recover
        if (!nodeGrid.IsWalkable(bunnyCell))
        {
            if (!recovering && TryFindNearbyEmpty(bunnyCell, 2, out var empty))
            {
                recovering = true;
                recoverWorldTarget = nodeGrid.CellCenterWorld(empty);

                // Clear plan because grid position and path can be invalid now
                ClearPlan();
                SetDigging(false);
            }
        }

        // Recovery movement overrides normal behavior
        if (recovering)
        {
            float spd = moveSpeed * recoverSpeedMultiplier;
            transform.position = Vector3.MoveTowards(transform.position, recoverWorldTarget, spd * Time.deltaTime);
            FaceWorld(recoverWorldTarget);

            if (Vector3.Distance(transform.position, recoverWorldTarget) <= recoverArriveDistance)
                recovering = false;

            return;
        }

        // Planning: choose a new target and compute a path
        if (!HasPlan())
        {
            // Respect cooldown between plans
            if (repathTimer > 0f)
            {
                SetDigging(false);
                return;
            }

            repathTimer = repathCooldown;

            // Pick a solid target, a stand cell, and a path to reach it
            if (!TryPickBestTarget(bunnyCell, out targetSolid, out standCell, out path))
            {
                SetDigging(false);
                return;
            }

            pathIndex = 0;
            hitTimer = 0f;
            planLockTimer = minPlanDuration;
        }

        // Movement: follow the path until we reach the dig stance position
        if (!AtDigStandPosition())
        {
            MoveAlongPath();
            SetDigging(false);
            FaceMovement();
            return;
        }

        // If the target is no longer solid, abandon the plan
        if (!digManager.IsSolid(targetSolid))
        {
            ClearPlan();
            SetDigging(false);
            return;
        }

        // Must stay adjacent to the target to dig
        var currentCell = nodeGrid.groundTilemap.WorldToCell(transform.position);

        if (!IsAdjacent(currentCell, targetSolid))
        {
            // If the plan lock expired, allow clearing and replanning
            if (planLockTimer <= 0f)
                ClearPlan();

            SetDigging(false);
            return;
        }

        // Digging: face target and apply hits on a fixed interval
        SetDigging(true);
        FaceCell(currentCell, targetSolid);

        // Slightly step toward the solid while digging for nicer visuals
        Vector3 digPos = GetDigPosition(standCell, targetSolid);
        transform.position = Vector3.MoveTowards(transform.position, digPos, moveSpeed * Time.deltaTime);

        hitTimer += Time.deltaTime;
        float secondsPerHit = 1f / Mathf.Max(0.01f, digHitsPerSecond);

        while (hitTimer >= secondsPerHit)
        {
            hitTimer -= secondsPerHit;

            bool broke = digManager.ApplyHit(targetSolid, hitDamage);
            if (broke)
            {
                // Record dig direction and start a short streak after breaking a tile
                lastDigDir = ClampToDir4(targetSolid - standCell);
                digDirStreakTimer = digDirectionStreakSeconds;

                ClearPlan();
                SetDigging(false);
                break;
            }
        }
    }

    void LateUpdate()
    {
        // Update the animator speed based on movement this frame
        if (animator == null) return;

        float speed = 0f;
        if (Time.deltaTime > 0f)
            speed = (transform.position - lastPos).magnitude / Time.deltaTime;

        animator.SetFloat(SpeedHash, speed, speedDampTime, Time.deltaTime);
        lastPos = transform.position;
    }

    void SenseTerrain(Vector3Int bunnyCell)
    {
        // Refresh the local memory lists
        knownOres.Clear();
        knownStone.Clear();

        int r = Mathf.Clamp(senseRadius, 1, 64);
        int r2 = r * r;

        // Scan a circular area around the bunny
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r2) continue;

                var c = new Vector3Int(bunnyCell.x + dx, bunnyCell.y + dy, bunnyCell.z);

                // Only track solid cells that can be dug later
                if (!digManager.IsSolid(c)) continue;

                if (digManager.IsOreCell(c)) knownOres.Add(c);
                else if (digManager.IsStoneCell(c)) knownStone.Add(c);
            }
        }

        if (logSelections)
            Debug.Log($"[{name}] Sense: ores={knownOres.Count}, stone={knownStone.Count} (r={r})");
    }

    bool TryGetSensedOreGoal(Vector3Int bunnyCell, out Vector3Int oreGoal)
    {
        // Picks an ore goal from sensed ore cells
        oreGoal = default;
        if (!useSensing || knownOres.Count == 0) return false;

        int bestDist = int.MaxValue;
        int bestValue = 0;

        foreach (var c in knownOres)
        {
            int v = digManager.GetGemsValue(c);
            int d = Mathf.Abs(c.x - bunnyCell.x) + Mathf.Abs(c.y - bunnyCell.y);

            if (orePreferHighestValue)
            {
                // Prefer higher value, break ties by distance
                if (v > bestValue || (v == bestValue && d < bestDist))
                {
                    bestValue = v;
                    bestDist = d;
                    oreGoal = c;
                }
            }
            else
            {
                // Prefer closer distance, break ties by value
                if (d < bestDist || (d == bestDist && v > bestValue))
                {
                    bestDist = d;
                    bestValue = v;
                    oreGoal = c;
                }
            }
        }

        return oreGoal != default;
    }

    bool TryGetSensedStoneGoal(Vector3Int bunnyCell, out Vector3Int stoneGoal)
    {
        // Picks the closest sensed stone cell
        stoneGoal = default;
        if (!useSensing || knownStone.Count == 0) return false;

        int bestDist = int.MaxValue;

        foreach (var c in knownStone)
        {
            int d = Mathf.Abs(c.x - bunnyCell.x) + Mathf.Abs(c.y - bunnyCell.y);
            if (d < bestDist)
            {
                bestDist = d;
                stoneGoal = c;
            }
        }

        return stoneGoal != default;
    }

    void HandleModeHotkeys()
    {
        // Debug hotkeys for switching modes
        if (!enableModeHotkeys) return;

        if (Input.GetKeyDown(balancedKey))
        {
            digMode = DigMode.Balanced;
            if (logModeChanges) Debug.Log($"[{name}] DigMode = {digMode}");
            ClearPlan();
        }
        else if (Input.GetKeyDown(materialsKey))
        {
            digMode = DigMode.Materials;
            if (logModeChanges) Debug.Log($"[{name}] DigMode = {digMode}");
            ClearPlan();
        }
        else if (Input.GetKeyDown(oresKey))
        {
            digMode = DigMode.Ores;
            if (logModeChanges) Debug.Log($"[{name}] DigMode = {digMode}");
            ClearPlan();
        }
    }

    bool TryPickBestTarget(Vector3Int bunnyCell, out Vector3Int bestSolid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Chooses a target based on dig mode and available frontier candidates
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        var candidates = digManager.GetFrontierCandidates(bunnyCell, searchRadius, maxCandidates);
        if (candidates == null || candidates.Count == 0) return false;

        // Split by tile type for mode decisions
        var oreFrontier = new List<Vector3Int>();
        var stoneFrontier = new List<Vector3Int>();
        var mudFrontier = new List<Vector3Int>();

        foreach (var c in candidates)
        {
            if (digManager.IsOreCell(c)) oreFrontier.Add(c);
            else if (digManager.IsStoneCell(c)) stoneFrontier.Add(c);
            else if (digManager.IsMudCell(c)) mudFrontier.Add(c);
        }

        if (digMode == DigMode.Balanced)
            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);

        if (digMode == DigMode.Materials)
        {
            if (stoneFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, stoneFrontier, out bestSolid, out bestStand, out bestPath);

            if (materialsPreferStonePocket)
            {
                if (TryGetSensedStoneGoal(bunnyCell, out var sensedStone))
                {
                    if (PickTowardGoalByPath(bunnyCell, candidates, sensedStone, stoneGoalBiasWeight,
                        out bestSolid, out bestStand, out bestPath))
                        return true;
                }
            }

            if (TryFindReachableStoneGoal(bunnyCell, out var reachableStone))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, reachableStone, stoneGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            if (mudFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, mudFrontier, out bestSolid, out bestStand, out bestPath);

            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
        }

        if (digMode == DigMode.Ores)
        {
            if (oreFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, oreFrontier, out bestSolid, out bestStand, out bestPath);

            if (TryGetSensedOreGoal(bunnyCell, out var sensedOre))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, sensedOre, oreGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            if (TryFindReachableOreGoal(bunnyCell, out var reachableOre))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, reachableOre, oreGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
        }

        return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
    }

    bool PickNearestByPath(Vector3Int bunnyCell, List<Vector3Int> solids,
        out Vector3Int bestSolid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Picks the target with the shortest path to a valid stand cell
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        int bestLen = int.MaxValue;

        foreach (var solid in solids)
        {
            if (!TryPickStandAndPath(bunnyCell, solid, out var stand, out var p)) continue;

            int len = p.Count;
            if (len < bestLen)
            {
                bestLen = len;
                bestSolid = solid;
                bestStand = stand;
                bestPath = p;
            }
        }

        if (bestPath != null && logSelections)
            Debug.Log($"[{name}] {digMode} picked {TileTag(bestSolid)} @ {bestSolid} (pathLen={bestLen})");

        return bestPath != null;
    }

    bool PickTowardGoalByPath(Vector3Int bunnyCell, List<Vector3Int> solids, Vector3Int goal, float goalWeight,
        out Vector3Int bestSolid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Picks a frontier target that moves the bunny toward a specific goal pocket
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        // Precompute paths and find the shortest for distance capping
        int shortest = int.MaxValue;
        var cached = new List<(Vector3Int solid, Vector3Int stand, List<Vector3Int> path, int len)>(solids.Count);

        foreach (var s in solids)
        {
            if (!TryPickStandAndPath(bunnyCell, s, out var stand, out var p)) continue;

            int len = p.Count;
            if (len < shortest) shortest = len;

            cached.Add((s, stand, p, len));
        }

        if (cached.Count == 0) return false;

        // Reject paths that are much longer than the shortest path
        int cap = shortest + Mathf.Max(0, extraTravelAllowanceSteps);

        float bestScore = float.NegativeInfinity;

        // Starting distance from bunny to the goal pocket
        int startGoalDist = Mathf.Abs(bunnyCell.x - goal.x) + Mathf.Abs(bunnyCell.y - goal.y);

        foreach (var c in cached)
        {
            if (c.len > cap) continue;

            // Distance from candidate solid to the goal pocket
            int candGoalDist = Mathf.Abs(c.solid.x - goal.x) + Mathf.Abs(c.solid.y - goal.y);

            // Prefer candidates that reduce distance to the goal
            int improvement = startGoalDist - candGoalDist;
            float improveScore = improvement * goalImproveWeight * goalWeight;

            // Prefer shorter paths
            float pathScore = -(c.len * pathCostWeight);

            // Add a short bonus for continuing in the same dig direction
            float dirBonus = 0f;
            if (digDirStreakTimer > 0f)
            {
                Vector3Int candDir = ClampToDir4(c.solid - c.stand);
                if (candDir == lastDigDir)
                    dirBonus = digDirectionBonus;
            }

            float score = improveScore + pathScore + dirBonus;

            if (score > bestScore)
            {
                bestScore = score;
                bestSolid = c.solid;
                bestStand = c.stand;
                bestPath = c.path;
            }
        }

        if (bestPath != null && logSelections)
            Debug.Log($"[{name}] {digMode} goalBias toward {goal} picked {TileTag(bestSolid)} @ {bestSolid} (score={bestScore:0.0})");

        return bestPath != null;
    }

    bool TryFindReachableOreGoal(Vector3Int bunnyCell, out Vector3Int oreGoal)
    {
        // Finds an ore tile that has a reachable adjacent empty stand cell
        oreGoal = default;
        int bestLen = int.MaxValue;
        int bestValue = 0;

        foreach (var oreCell in digManager.GetAllOreCells())
        {
            int v = digManager.GetGemsValue(oreCell);
            if (v <= 0) continue;

            foreach (var n in Neighbors4(oreCell))
            {
                if (!nodeGrid.IsWalkable(n)) continue;

                var p = nodeGrid.FindPathAStar(bunnyCell, n);
                if (p == null) continue;

                int len = p.Count;

                if (len < bestLen || (len == bestLen && v > bestValue))
                {
                    bestLen = len;
                    bestValue = v;
                    oreGoal = oreCell;
                }
            }
        }

        return bestLen != int.MaxValue;
    }

    bool TryFindReachableStoneGoal(Vector3Int bunnyCell, out Vector3Int stoneGoal)
    {
        // Finds a stone tile that has a reachable adjacent empty stand cell
        stoneGoal = default;
        int bestLen = int.MaxValue;

        foreach (var stoneCell in digManager.GetAllStoneCells())
        {
            foreach (var n in Neighbors4(stoneCell))
            {
                if (!nodeGrid.IsWalkable(n)) continue;

                var p = nodeGrid.FindPathAStar(bunnyCell, n);
                if (p == null) continue;

                int len = p.Count;
                if (len < bestLen)
                {
                    bestLen = len;
                    stoneGoal = stoneCell;
                }
            }
        }

        return bestLen != int.MaxValue;
    }

    bool HasPlan() => path != null && path.Count > 0;

    void ClearPlan()
    {
        // Clears the current target and path so a new plan can be chosen
        path = null;
        pathIndex = 0;
        targetSolid = default;
        standCell = default;
        hitTimer = 0f;
        planLockTimer = 0f;
    }

    bool AtDigStandPosition()
    {
        // Checks world distance to the dig stance position
        if (standCell == default && targetSolid == default) return false;

        Vector3 digPos = GetDigPosition(standCell, targetSolid);
        return Vector3.Distance(transform.position, digPos) <= arriveDistance;
    }

    void MoveAlongPath()
    {
        // If path is finished, move toward the final dig stance position
        if (pathIndex >= path.Count)
        {
            Vector3 digPos = GetDigPosition(standCell, targetSolid);
            transform.position = Vector3.MoveTowards(transform.position, digPos, moveSpeed * Time.deltaTime);
            return;
        }

        var next = path[pathIndex];

        // If path becomes blocked, allow replanning once the lock expires
        if (!nodeGrid.IsWalkable(next))
        {
            if (planLockTimer <= 0f)
                ClearPlan();
            return;
        }

        // Move toward the next cell center
        Vector3 targetPos = nodeGrid.CellCenterWorld(next);
        transform.position = Vector3.MoveTowards(transform.position, targetPos, moveSpeed * Time.deltaTime);

        if (Vector3.Distance(transform.position, targetPos) <= arriveDistance)
            pathIndex++;
    }

    bool TryPickStandAndPath(Vector3Int bunnyCell, Vector3Int solid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Chooses the shortest path to any walkable neighbor of the target solid
        bestStand = default;
        bestPath = null;

        int bestLen = int.MaxValue;

        foreach (var n in Neighbors4(solid))
        {
            if (!nodeGrid.IsWalkable(n)) continue;

            var p = nodeGrid.FindPathAStar(bunnyCell, n);
            if (p == null) continue;

            if (p.Count < bestLen)
            {
                bestLen = p.Count;
                bestStand = n;
                bestPath = p;
            }
        }

        return bestPath != null;
    }

    void SetDigging(bool digging)
    {
        // Controls digging animation state
        if (animator == null) return;

        animator.SetBool(IsDiggingHash, digging);
        if (digging) animator.SetFloat(SpeedHash, 0f);
    }

    void FaceCell(Vector3Int fromCell, Vector3Int toCell)
    {
        // Faces the bunny left or right based on where the dig target is
        if (visualsRoot == null) return;

        int dx = toCell.x - fromCell.x;
        if (dx > 0) SetFacingRight(true);
        else if (dx < 0) SetFacingRight(false);
    }

    void FaceMovement()
    {
        // Faces toward the next path step
        if (visualsRoot == null || path == null) return;

        if (pathIndex < path.Count)
        {
            Vector3 nextPos = nodeGrid.CellCenterWorld(path[pathIndex]);
            FaceWorld(nextPos);
        }
    }

    void FaceWorld(Vector3 worldTarget)
    {
        // Faces toward a world position target
        if (visualsRoot == null) return;

        float dx = worldTarget.x - transform.position.x;
        if (dx > 0.001f) SetFacingRight(true);
        else if (dx < -0.001f) SetFacingRight(false);
    }

    void SetFacingRight(bool right)
    {
        // Flips the visuals by mirroring local scale on X
        var s = visualsRoot.localScale;
        float absX = Mathf.Abs(s.x);
        visualsRoot.localScale = new Vector3(right ? absX : -absX, s.y, s.z);
    }

    Vector3 GetDigPosition(Vector3Int stand, Vector3Int solid)
    {
        // Returns a stance position slightly nudged toward the dig target
        Vector3 standCenter = nodeGrid.CellCenterWorld(stand);
        Vector3 solidCenter = nodeGrid.CellCenterWorld(solid);

        Vector3 dir = (solidCenter - standCenter);
        if (dir.sqrMagnitude < 0.000001f) return standCenter;

        dir.Normalize();
        return standCenter + dir * digNudge;
    }

    bool TryFindNearbyEmpty(Vector3Int start, int radius, out Vector3Int found)
    {
        // Finds the closest walkable cell near a starting cell
        found = default;
        int best = int.MaxValue;
        bool ok = false;

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

    static bool IsAdjacent(Vector3Int a, Vector3Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) == 1;

    static IEnumerable<Vector3Int> Neighbors4(Vector3Int p)
    {
        // Four direction neighbors
        yield return p + Vector3Int.right;
        yield return p + Vector3Int.left;
        yield return p + Vector3Int.up;
        yield return p + Vector3Int.down;
    }

    static Vector3Int ClampToDir4(Vector3Int v)
    {
        // Converts a vector into one of the four cardinal directions
        if (Mathf.Abs(v.x) >= Mathf.Abs(v.y))
            return v.x >= 0 ? Vector3Int.right : Vector3Int.left;

        return v.y >= 0 ? Vector3Int.up : Vector3Int.down;
    }

    string TileTag(Vector3Int cell)
    {
        // Used for debug logging
        if (digManager.IsOreCell(cell)) return "ORE";
        if (digManager.IsStoneCell(cell)) return "STONE";
        if (digManager.IsMudCell(cell)) return "MUD";
        return "OTHER";
    }
}
