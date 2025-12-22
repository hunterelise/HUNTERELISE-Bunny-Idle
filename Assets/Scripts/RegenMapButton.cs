// For debugging/testing
using UnityEngine;

public class RegenMapButton : MonoBehaviour
{
    public TilemapSpawner spawner;
    public bool randomizeSeed = true;

    public void ResetAndGenerateFirst()
    {
        if (spawner == null) return;
        spawner.ResetAndGenerateFirstChunk();
    }

    public void GenerateNextChunk()
    {
        if (spawner == null) return;
        spawner.GenerateNextChunk();
    }

}
