using System;
using UnityEngine;

public static class Sdf
{
    public static float[,,] Sample(Func<Vector3, float> sdf_func, Bounds bounds, int cellsPerAxis)
    {
        if (cellsPerAxis < 1)
        {
           throw new ArgumentOutOfRangeException("cellsPerAxis must be greater than 0");
        }

        int cnt = cellsPerAxis + 1;
        var vals = new float[cnt, cnt, cnt];
        Vector3 start = bounds.min;
        Vector3 step = bounds.size / cellsPerAxis;

        for (int x = 0; x < cnt; x++) {
            for (int y = 0; y < cnt; y++) {
                for (int z = 0; z < cnt; z++) {
                    Vector3 point = start + new Vector3(x * step.x, y * step.y, z * step.z);
                    vals[x, y, z] = sdf_func(point);
                }
            }
        }
        return vals;
    }
}