using System.Collections.Generic;
using UnityEngine;

public class BunnyAgent : MonoBehaviour
{
    // References to systems used for digging decisions and pathfinding
    public DigManager digManager;
    public NodeGrid nodeGrid;

    [Header("Movement")]
    // Base movement speed in world units per second
    public float moveSpeed = 2f;

    // How close the bunny must be to consider it arrived at a target position
    public float arriveDistance = 0.03f;

    [Header("Targeting")]
    // How far the bunny searches for diggable frontier tiles
    public int searchRadius = 35;

    // Minimum time between picking a new plan
    public float repathCooldown = 0.7f;

    // Limits how many frontier tiles we evaluate when planning
    [Tooltip("How many frontier candidates to evaluate each repath.")]
    public int maxCandidates = 30;

    [Header("Digging")]
    // How many hits per second the bunny performs while digging
    public float digHitsPerSecond = 1f;

    // Damage dealt per hit
    public int hitDamage = 3;

    // Controls what the bunny tries to focus on when choosing targets
    public enum DigMode { Balanced, Materials, Ores }

    [Header("Goal Mode")]
    // Current goal mode for target selection
    public DigMode digMode = DigMode.Balanced;

    [Header("Mode Bias")]
    // When in materials mode, this helps the bunny move toward stone pockets
    [Tooltip("How strongly materials mode biases toward a stone region when stone isn't on frontier.")]
    public float stoneGoalBiasWeight = 6f;

    // When in ores mode, this helps the bunny move toward ore pockets
    [Tooltip("How strongly ore mode biases toward an ore region when ore isn't on frontier.")]
    public float oreGoalBiasWeight = 8f;

    [Header("Awareness / Sensing")]
    // If true, bunny scans nearby cells to remember ores and stone even if buried
    public bool useSensing = true;

    // Radius in tiles used for scanning nearby solids
    [Tooltip("How far (in tiles) the bunny can 'sense' terrain around itself.")]
    public int senseRadius = 16;

    // Time between scans
    [Tooltip("How often to refresh sensed info (seconds).")]
    public float senseInterval = 0.6f;

    // If true, ores mode prefers the highest value ore within sensed range
    [Tooltip("If true, ores mode targets the highest-value ore in range; if false targets nearest ore.")]
    public bool orePreferHighestValue = true;

    // If true, materials mode tries to move toward stone pockets when no stone is on the frontier
    [Tooltip("If true, materials mode targets nearest stone pocket even if mud is closer.")]
    public bool materialsPreferStonePocket = true;

    // Timer that counts down to the next sensing scan
    float senseTimer;

    // Lists that store what the bunny currently knows about nearby solids
    readonly List<Vector3Int> knownOres = new();
    readonly List<Vector3Int> knownStone = new();

    [Header("Scoring / Limits")]
    // Allows some extra walking distance when chasing a goal pocket
    [Tooltip("Prevents insane detours. Higher = more willing to walk far for goal tiles.")]
    public int extraTravelAllowanceSteps = 10;

    [Header("Debug Hotkeys")]
    // Allows changing dig modes during play with keys
    public bool enableModeHotkeys = true;
    public KeyCode balancedKey = KeyCode.Alpha1;
    public KeyCode materialsKey = KeyCode.Alpha2;
    public KeyCode oresKey = KeyCode.Alpha3;
    public bool logModeChanges = true;

    [Header("Debug Selection")]
    // Logs what target the bunny picks
    public bool logSelections = false;

    [Header("Animation")]
    // Animator used to drive movement and digging animations
    public Animator animator;

    // Smoothing value for the speed parameter
    public float speedDampTime = 0.08f;

    [Header("Visual Facing + Dig Nudge")]
    // Root object that gets flipped left and right for facing direction
    public Transform visualsRoot;

    // Small offset toward the target solid when digging to look better
    public float digNudge = 0.08f;

    [Header("Recovery (anti-jank)")]
    // Extra speed used when trying to escape from being inside a solid tile
    public float recoverSpeedMultiplier = 1.25f;

    // Tighter arrive distance for recovery moves
    public float recoverArriveDistance = 0.02f;

    [Header("Plan Stability")]
    // Locks the plan for a short time to reduce constant replanning
    public float minPlanDuration = 1.0f;

    // Animator parameter hashes for performance
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int IsDiggingHash = Animator.StringToHash("IsDigging");

    // The solid cell we intend to dig
    Vector3Int targetSolid;

    // The empty cell we stand on to dig the target
    Vector3Int standCell;

    // The current path from bunny to the stand position
    List<Vector3Int> path;

    // Index into the path list
    int pathIndex;

    // Timer used to apply hits at a fixed rate
    float hitTimer;

    // Timer that prevents replanning too frequently
    float repathTimer;

    // Timer that prevents plan changes for a short minimum duration
    float planLockTimer;

    // Used to compute movement speed for animation
    Vector3 lastPos;

    // Recovery state if bunny gets stuck inside solid
    bool recovering;
    Vector3 recoverWorldTarget;

    void Awake()
    {
        // Auto assign optional references if not set
        if (animator == null) animator = GetComponent<Animator>();
        if (visualsRoot == null) visualsRoot = transform;

        // Store initial position for speed calculation
        lastPos = transform.position;
    }

    void Update()
    {
        // If required systems are missing, stop digging animation and do nothing
        if (digManager == null || nodeGrid == null || nodeGrid.groundTilemap == null)
        {
            SetDigging(false);
            return;
        }

        // Allow changing the mode with hotkeys
        HandleModeHotkeys();

        // Update plan timers
        repathTimer -= Time.deltaTime;
        planLockTimer -= Time.deltaTime;

        // Current cell position on the grid
        var bunnyCell = nodeGrid.groundTilemap.WorldToCell(transform.position);

        // Periodically rescan local area for ores and stone
        senseTimer -= Time.deltaTime;
        if (useSensing && senseTimer <= 0f)
        {
            senseTimer = Mathf.Max(0.05f, senseInterval);
            SenseTerrain(bunnyCell);
        }

        // If bunny is inside a solid tile, try to recover by moving to nearest empty
        if (!nodeGrid.IsWalkable(bunnyCell))
        {
            if (!recovering && TryFindNearbyEmpty(bunnyCell, 2, out var empty))
            {
                recovering = true;
                recoverWorldTarget = nodeGrid.CellCenterWorld(empty);

                // Clear any current plan because it might be invalid now
                ClearPlan();
                SetDigging(false);
            }
        }

        // Recovery movement runs until we reach an empty cell center
        if (recovering)
        {
            float spd = moveSpeed * recoverSpeedMultiplier;
            transform.position = Vector3.MoveTowards(transform.position, recoverWorldTarget, spd * Time.deltaTime);
            FaceWorld(recoverWorldTarget);

            if (Vector3.Distance(transform.position, recoverWorldTarget) <= recoverArriveDistance)
                recovering = false;

            return;
        }

        // Planning phase: choose a target and compute a path
        if (!HasPlan())
        {
            // Wait for cooldown before planning again
            if (repathTimer > 0f)
            {
                SetDigging(false);
                return;
            }

            repathTimer = repathCooldown;

            // Choose the best solid to dig based on mode and constraints
            if (!TryPickBestTarget(bunnyCell, out targetSolid, out standCell, out path))
            {
                SetDigging(false);
                return;
            }

            // Reset path and dig timers for the new plan
            pathIndex = 0;
            hitTimer = 0f;
            planLockTimer = minPlanDuration;
        }

        // Movement phase: walk toward the dig stand position
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

        // Check adjacency to the target before digging
        var currentCell = nodeGrid.groundTilemap.WorldToCell(transform.position);

        if (!IsAdjacent(currentCell, targetSolid))
        {
            // Allow a short lock time so the bunny does not instantly give up
            if (planLockTimer <= 0f)
                ClearPlan();

            SetDigging(false);
            return;
        }

        // Digging phase
        SetDigging(true);
        FaceCell(currentCell, targetSolid);

        // Nudge slightly toward the solid while digging for better visuals
        Vector3 digPos = GetDigPosition(standCell, targetSolid);
        transform.position = Vector3.MoveTowards(transform.position, digPos, moveSpeed * Time.deltaTime);

        // Apply hits at the desired rate
        hitTimer += Time.deltaTime;
        float secondsPerHit = 1f / Mathf.Max(0.01f, digHitsPerSecond);

        while (hitTimer >= secondsPerHit)
        {
            hitTimer -= secondsPerHit;

            // Apply a hit and check if the tile broke
            bool broke = digManager.ApplyHit(targetSolid, hitDamage);
            if (broke)
            {
                ClearPlan();
                SetDigging(false);
                break;
            }
        }
    }

    void LateUpdate()
    {
        // Update animation speed parameter based on actual movement per frame
        if (animator == null) return;

        float speed = 0f;
        if (Time.deltaTime > 0f)
            speed = (transform.position - lastPos).magnitude / Time.deltaTime;

        animator.SetFloat(SpeedHash, speed, speedDampTime, Time.deltaTime);
        lastPos = transform.position;
    }

    // Awareness
    void SenseTerrain(Vector3Int bunnyCell)
    {
        // Clear previous sensed data
        knownOres.Clear();
        knownStone.Clear();

        // Clamp sense radius to a reasonable range
        int r = Mathf.Clamp(senseRadius, 1, 64);
        int r2 = r * r;

        // Scan in a circle around the bunny
        for (int dx = -r; dx <= r; dx++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r2) continue;

                var c = new Vector3Int(bunnyCell.x + dx, bunnyCell.y + dy, bunnyCell.z);

                // Only care about solid cells that could be dug
                if (!digManager.IsSolid(c)) continue;

                // Classify the cell for goal behavior
                if (digManager.IsOreCell(c)) knownOres.Add(c);
                else if (digManager.IsStoneCell(c)) knownStone.Add(c);
            }
        }

        if (logSelections)
            Debug.Log($"[{name}] Sense: ores={knownOres.Count}, stone={knownStone.Count} (r={r})");
    }

    bool TryGetSensedOreGoal(Vector3Int bunnyCell, out Vector3Int oreGoal)
    {
        // Chooses an ore goal from the sensed ore list
        oreGoal = default;
        if (!useSensing || knownOres.Count == 0) return false;

        int bestDist = int.MaxValue;
        int bestValue = 0;

        foreach (var c in knownOres)
        {
            // Higher value is better, distance is used as a tiebreaker
            int v = digManager.GetGemsValue(c);
            int d = Mathf.Abs(c.x - bunnyCell.x) + Mathf.Abs(c.y - bunnyCell.y);

            if (orePreferHighestValue)
            {
                // Prefer higher value first, then closer distance
                if (v > bestValue || (v == bestValue && d < bestDist))
                {
                    bestValue = v;
                    bestDist = d;
                    oreGoal = c;
                }
            }
            else
            {
                // Prefer closer distance first, then higher value
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
        // Chooses the nearest sensed stone cell as a goal
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

    // Mode hotkeys for testing
    void HandleModeHotkeys()
    {
        // Hotkeys are optional debug controls
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

    // Target selection
    bool TryPickBestTarget(Vector3Int bunnyCell, out Vector3Int bestSolid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Chooses a target solid, a stand cell next to it, and a path to reach the stand cell
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        // Ask DigManager for a limited set of frontier candidates near the bunny
        var candidates = digManager.GetFrontierCandidates(bunnyCell, searchRadius, maxCandidates);
        if (candidates == null || candidates.Count == 0) return false;

        // Group candidates by what type of tile they are
        var oreFrontier = new List<Vector3Int>();
        var stoneFrontier = new List<Vector3Int>();
        var mudFrontier = new List<Vector3Int>();

        foreach (var c in candidates)
        {
            if (digManager.IsOreCell(c)) oreFrontier.Add(c);
            else if (digManager.IsStoneCell(c)) stoneFrontier.Add(c);
            else if (digManager.IsMudCell(c)) mudFrontier.Add(c);
        }

        // Balanced mode simply picks the closest reachable frontier by path
        if (digMode == DigMode.Balanced)
        {
            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
        }

        // Materials mode prefers stone, but will bias toward stone pockets when none are on the frontier
        if (digMode == DigMode.Materials)
        {
            // If any stone is directly diggable, go for the nearest stone frontier
            if (stoneFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, stoneFrontier, out bestSolid, out bestStand, out bestPath);

            // If enabled, use sensed stone as a goal to dig toward
            if (materialsPreferStonePocket)
            {
                if (TryGetSensedStoneGoal(bunnyCell, out var sensedStone))
                {
                    if (PickTowardGoalByPath(bunnyCell, candidates, sensedStone, stoneGoalBiasWeight,
                        out bestSolid, out bestStand, out bestPath))
                        return true;
                }
            }

            // If sensing did not help, try to find a stone cell that is reachable via tunnels
            if (TryFindReachableStoneGoal(bunnyCell, out var reachableStone))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, reachableStone, stoneGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            // If no stone goal is available, dig mud if possible
            if (mudFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, mudFrontier, out bestSolid, out bestStand, out bestPath);

            // Final fallback is any nearest frontier
            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
        }

        // Ores mode prefers ore, but will bias toward ore pockets when none are on the frontier
        if (digMode == DigMode.Ores)
        {
            // If any ore is directly diggable, go for the nearest ore frontier
            if (oreFrontier.Count > 0)
                return PickNearestByPath(bunnyCell, oreFrontier, out bestSolid, out bestStand, out bestPath);

            // Use sensed ore as a goal to dig toward even if it is buried
            if (TryGetSensedOreGoal(bunnyCell, out var sensedOre))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, sensedOre, oreGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            // If sensing did not help, try to find an ore cell that is reachable via tunnels
            if (TryFindReachableOreGoal(bunnyCell, out var reachableOre))
            {
                if (PickTowardGoalByPath(bunnyCell, candidates, reachableOre, oreGoalBiasWeight,
                    out bestSolid, out bestStand, out bestPath))
                    return true;
            }

            // Final fallback is any nearest frontier
            return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
        }

        // Safety fallback for unknown modes
        return PickNearestByPath(bunnyCell, candidates, out bestSolid, out bestStand, out bestPath);
    }

    // Pick helpers
    bool PickNearestByPath(Vector3Int bunnyCell, List<Vector3Int> solids,
        out Vector3Int bestSolid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Picks the target whose stand position has the shortest path
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        int bestLen = int.MaxValue;

        foreach (var solid in solids)
        {
            // For each solid, find a walkable neighbor and a path to it
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
        // Picks a frontier target that is both reachable and closer to the goal pocket
        bestSolid = default;
        bestStand = default;
        bestPath = null;

        // First pass: compute paths and track the shortest path length
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

        // Limit candidates so we do not accept paths that are much longer than the best one
        int cap = shortest + Mathf.Max(0, extraTravelAllowanceSteps);

        float bestScore = float.NegativeInfinity;

        // Second pass: score each candidate by path cost and how much it moves toward the goal
        foreach (var c in cached)
        {
            if (c.len > cap) continue;

            // Manhattan distance from this solid to the goal pocket
            int goalDist = Mathf.Abs(c.solid.x - goal.x) + Mathf.Abs(c.solid.y - goal.y);

            // Higher value when closer to the goal
            float toward = 1f / Mathf.Max(1f, goalDist);

            // Higher goalWeight makes the bunny prioritize the goal more strongly
            float score = (toward * 100f * goalWeight) - (c.len * 1.0f);

            if (score > bestScore)
            {
                bestScore = score;
                bestSolid = c.solid;
                bestStand = c.stand;
                bestPath = c.path;
            }
        }

        if (bestPath != null && logSelections)
            Debug.Log($"[{name}] {digMode} goalBias toward {goal} picked {TileTag(bestSolid)} @ {bestSolid}");

        return bestPath != null;
    }

    // Reachable goals
    bool TryFindReachableOreGoal(Vector3Int bunnyCell, out Vector3Int oreGoal)
    {
        // Searches all ore cells and finds one that has a reachable adjacent empty cell
        oreGoal = default;
        int bestLen = int.MaxValue;
        int bestValue = 0;

        foreach (var oreCell in digManager.GetAllOreCells())
        {
            int v = digManager.GetGemsValue(oreCell);
            if (v <= 0) continue;

            // Check each neighbor empty cell to see if we can path to it
            foreach (var n in Neighbors4(oreCell))
            {
                if (!nodeGrid.IsWalkable(n)) continue;

                var p = nodeGrid.FindPathAStar(bunnyCell, n);
                if (p == null) continue;

                int len = p.Count;

                // Prefer shorter paths, and break ties using ore value
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
        // Searches all stone cells and finds one that has a reachable adjacent empty cell
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

    // Plan + movement helpers
    // True if a valid path plan exists
    bool HasPlan() => path != null && path.Count > 0;

    void ClearPlan()
    {
        // Clears the current plan so the bunny will choose a new one
        path = null;
        pathIndex = 0;
        targetSolid = default;
        standCell = default;
        hitTimer = 0f;
        planLockTimer = 0f;
    }

    bool AtDigStandPosition()
    {
        // Checks if the bunny is close enough to its dig stance position
        if (standCell == default && targetSolid == default) return false;

        Vector3 digPos = GetDigPosition(standCell, targetSolid);
        return Vector3.Distance(transform.position, digPos) <= arriveDistance;
    }

    void MoveAlongPath()
    {
        // If we reached the end of the path, just move toward the dig stance position
        if (pathIndex >= path.Count)
        {
            Vector3 digPos = GetDigPosition(standCell, targetSolid);
            transform.position = Vector3.MoveTowards(transform.position, digPos, moveSpeed * Time.deltaTime);
            return;
        }

        var next = path[pathIndex];

        // If the next cell becomes blocked, only clear the plan if the lock expired
        if (!nodeGrid.IsWalkable(next))
        {
            if (planLockTimer <= 0f)
                ClearPlan();
            return;
        }

        // Move toward the next cell center
        Vector3 targetPos = nodeGrid.CellCenterWorld(next);
        transform.position = Vector3.MoveTowards(transform.position, targetPos, moveSpeed * Time.deltaTime);

        // Advance the path when close enough
        if (Vector3.Distance(transform.position, targetPos) <= arriveDistance)
            pathIndex++;
    }

    bool TryPickStandAndPath(Vector3Int bunnyCell, Vector3Int solid, out Vector3Int bestStand, out List<Vector3Int> bestPath)
    {
        // Picks the best stand cell next to a solid and the shortest path to that stand cell
        bestStand = default;
        bestPath = null;

        int bestLen = int.MaxValue;

        foreach (var n in Neighbors4(solid))
        {
            // Bunny can only stand on empty cells
            if (!nodeGrid.IsWalkable(n)) continue;

            // Path to that stand cell must exist
            var p = nodeGrid.FindPathAStar(bunnyCell, n);
            if (p == null) continue;

            // Choose the shortest path
            if (p.Count < bestLen)
            {
                bestLen = p.Count;
                bestStand = n;
                bestPath = p;
            }
        }

        return bestPath != null;
    }

    // Animation + visuals
    void SetDigging(bool digging)
    {
        // Controls animator digging state and stops speed while digging
        if (animator == null) return;

        animator.SetBool(IsDiggingHash, digging);
        if (digging) animator.SetFloat(SpeedHash, 0f);
    }

    void FaceCell(Vector3Int fromCell, Vector3Int toCell)
    {
        // Faces left or right based on the target cell direction
        if (visualsRoot == null) return;

        int dx = toCell.x - fromCell.x;
        if (dx > 0) SetFacingRight(true);
        else if (dx < 0) SetFacingRight(false);
    }

    void FaceMovement()
    {
        // Faces toward the next movement step on the path
        if (visualsRoot == null || path == null) return;

        if (pathIndex < path.Count)
        {
            Vector3 nextPos = nodeGrid.CellCenterWorld(path[pathIndex]);
            FaceWorld(nextPos);
        }
    }

    void FaceWorld(Vector3 worldTarget)
    {
        // Faces left or right based on the world position target
        if (visualsRoot == null) return;

        float dx = worldTarget.x - transform.position.x;
        if (dx > 0.001f) SetFacingRight(true);
        else if (dx < -0.001f) SetFacingRight(false);
    }

    void SetFacingRight(bool right)
    {
        // Flips visuals by mirroring the x scale
        var s = visualsRoot.localScale;
        float absX = Mathf.Abs(s.x);
        visualsRoot.localScale = new Vector3(right ? absX : -absX, s.y, s.z);
    }

    Vector3 GetDigPosition(Vector3Int stand, Vector3Int solid)
    {
        // Computes a world position slightly toward the solid for nicer digging visuals
        Vector3 standCenter = nodeGrid.CellCenterWorld(stand);
        Vector3 solidCenter = nodeGrid.CellCenterWorld(solid);

        Vector3 dir = (solidCenter - standCenter);
        if (dir.sqrMagnitude < 0.000001f) return standCenter;

        dir.Normalize();
        return standCenter + dir * digNudge;
    }

    bool TryFindNearbyEmpty(Vector3Int start, int radius, out Vector3Int found)
    {
        // Finds the closest empty cell near a given start cell
        found = default;
        int best = int.MaxValue;
        bool ok = false;

        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                var c = new Vector3Int(start.x + dx, start.y + dy, start.z);
                if (!nodeGrid.IsWalkable(c)) continue;

                // Manhattan distance works well on a grid
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

    string TileTag(Vector3Int cell)
    {
        // Used for debug logging of what type of tile was selected
        if (digManager.IsOreCell(cell)) return "ORE";
        if (digManager.IsStoneCell(cell)) return "STONE";
        if (digManager.IsMudCell(cell)) return "MUD";
        return "OTHER";
    }
}
