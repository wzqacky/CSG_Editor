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

        double xCells = Math.Ceiling(cellsPerAxis * (size.x / shortest));
        double yCells = Math.Ceiling(cellsPerAxis * (size.y / shortest));
        double zCells = Math.Ceiling(cellsPerAxis * (size.z / shortest));
        if ((xCells + 1) * (yCells + 1) * (zCells + 1) > MaxSampleCount)
            throw new InvalidOperationException("CSG preview exceeds the sampling limit. Reduce the size or distance between shapes.");

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