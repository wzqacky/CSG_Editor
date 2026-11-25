using System;
using UnityEngine;

public static class Sdf
{
    private const int MaxSampleCount = 8000000;

    public static float[,,] Sample(Func<Vector3, float> sdf_func, Bounds bounds, int cellsPerAxis)
    {
        if (cellsPerAxis < 1)
        {
           throw new ArgumentOutOfRangeException("cellsPerAxis must be greater than 0");
        }

        Vector3 size = bounds.size;
        double shortest = Math.Min(size.x, Math.Min(size.y, size.z));
        if (shortest <= 0 || double.IsNaN(shortest) || double.IsInfinity(shortest) ||
            float.IsNaN(size.x) || float.IsNaN(size.y) || float.IsNaN(size.z) ||
            float.IsInfinity(size.x) || float.IsInfinity(size.y) || float.IsInfinity(size.z))
            throw new InvalidOperationException("CSG sampling bounds must have finite, positive dimensions.");

        (double x, double y, double z) CellCounts(int resolution) => (
            Math.Ceiling(resolution * (size.x / shortest)),
            Math.Ceiling(resolution * (size.y / shortest)),
            Math.Ceiling(resolution * (size.z / shortest)));

        bool FitsBudget(int resolution)
        {
            // First guess: use the shortest side to get the res
            var (x, y, z) = CellCounts(resolution);
            return (x + 1) * (y + 1) * (z + 1) <= MaxSampleCount;
        }

        if (!FitsBudget(cellsPerAxis))
        {
            double aspectProduct = (size.x / shortest) * (size.y / shortest) * (size.z / shortest);
            // solve n^3 * m_1 * m_2 <= budget
            int estimate = (int)Math.Floor(Math.Cbrt(MaxSampleCount / aspectProduct));
            cellsPerAxis = Math.Max(1, Math.Min(cellsPerAxis, estimate));

            while (cellsPerAxis > 1 && !FitsBudget(cellsPerAxis))
                // Safe check
                cellsPerAxis--;

            if (!FitsBudget(cellsPerAxis))
                throw new InvalidOperationException(
                    "CSG preview exceeds the sampling limit. Reduce the size or distance between shapes.");
        }

        var (xCells, yCells, zCells) = CellCounts(cellsPerAxis);
        int nx = (int)xCells;
        int ny = (int)yCells;
        int nz = (int)zCells;
        var vals = new float[nx + 1, ny + 1, nz + 1];
        Vector3 start = bounds.min;
        Vector3 step = new Vector3(size.x / nx, size.y / ny, size.z / nz);

        for (int x = 0; x <= nx; x++) {
            for (int y = 0; y <= ny; y++) {
                for (int z = 0; z <= nz; z++) {
                    Vector3 point = start + new Vector3(x * step.x, y * step.y, z * step.z);
                    vals[x, y, z] = sdf_func(point);
                }
            }
        }
        return vals;
    }
}
