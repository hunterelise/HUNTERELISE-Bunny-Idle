public enum CellType { Empty, Mud, Stone, OreGreen, OreBlue, OrePurple, OrePink, OreYellow }

public struct CellState
{
    public CellType type;
    public int hp;
    public int maxHp;
}
