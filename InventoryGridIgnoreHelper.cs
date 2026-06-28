using System;
using ExileCore2.PoEMemory.MemoryObjects;

namespace Follower;

internal static class InventoryGridIgnoreHelper
{
    public const int Rows = 5;
    public const int Columns = 12;

    public static bool[,] Normalize(bool[,] cells)
    {
        if (cells != null && cells.GetLength(0) == Rows && cells.GetLength(1) == Columns)
            return cells;

        var normalized = new bool[Rows, Columns];
        if (cells == null)
            return normalized;

        var rowsToCopy = Math.Min(Rows, cells.GetLength(0));
        var columnsToCopy = Math.Min(Columns, cells.GetLength(1));

        for (var y = 0; y < rowsToCopy; y++)
        {
            for (var x = 0; x < columnsToCopy; x++)
                normalized[y, x] = cells[y, x];
        }

        return normalized;
    }

    public static bool IsIgnored(ServerInventory.InventSlotItem item, bool[,] ignoredCells)
    {
        if (item == null)
            return true;

        var posX = item.PosX;
        var posY = item.PosY;

        if (posX < 0 || posX >= Columns)
            return true;

        if (posY < 0 || posY >= Rows)
            return true;

        ignoredCells = Normalize(ignoredCells);
        return ignoredCells[posY, posX];
    }

    public static int CountIgnoredCells(bool[,] ignoredCells)
    {
        ignoredCells = Normalize(ignoredCells);
        var count = 0;

        for (var y = 0; y < Rows; y++)
        {
            for (var x = 0; x < Columns; x++)
            {
                if (ignoredCells[y, x])
                    count++;
            }
        }

        return count;
    }
}
