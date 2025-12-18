using UnityEngine;
using UnityEngine.Tilemaps;

public class DugTileSpawner : MonoBehaviour
{
    [Header("Tilemaps")]
    // Tilemap that holds the real ground tiles used for gameplay logic
    public Tilemap groundTilemap;

    // Separate tilemap used only for showing dug visuals
    public Tilemap dugTilemap;

    [Header("Dug Tile")]
    // Tile used to visually represent a dug out space
    public TileBase dugTile;

    // Call this when a ground tile is removed during gameplay
    public void PlaceDug(Vector3Int cell)
    {
        // Do nothing if required references are missing
        if (dugTilemap == null || dugTile == null || groundTilemap == null) return;

        // Only place a dug tile if the ground tilemap is empty at this cell
        if (groundTilemap.GetTile(cell) == null)
            dugTilemap.SetTile(cell, dugTile);
    }

    // Used during world generation or carving when the cell is known to be empty
    public void PlaceDugImmediate(Vector3Int cell)
    {
        // Place the dug tile without checking the ground tilemap
        if (dugTilemap == null || dugTile == null) return;
        dugTilemap.SetTile(cell, dugTile);
    }

    // Removes the dug visual tile from a cell
    public void ClearDug(Vector3Int cell)
    {
        if (dugTilemap == null) return;
        dugTilemap.SetTile(cell, null);
    }
}
