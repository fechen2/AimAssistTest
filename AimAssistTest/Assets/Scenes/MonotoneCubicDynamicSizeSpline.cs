#nullable enable
using System;
using UnityEngine;

//https://en.wikipedia.org/wiki/Monotone_cubic_interpolation
public class MonotoneCubicDynamicSizeSpline
{
    private readonly float[] _x;
    private readonly float[] _y;
    private readonly float[] _slopes;
    private int _dynamicSize;
    public int DynamicSize => _dynamicSize;

    // NOTE(dan): assume x is in ascending order, and y is matched with x
    public MonotoneCubicDynamicSizeSpline(float[] x, float[] y)
    {
        Debug.Assert(x.Length == y.Length);
        _x = x;
        _y = y;
        int length = _x.Length;
        _slopes = new float[length];
    }

    public void CalculateSpline(int dynamicSize)
    {
        Debug.Assert(dynamicSize <= _x.Length);
        _dynamicSize = dynamicSize;
        Span<float> secants = stackalloc float[dynamicSize - 1];

        // calculate secants
        for (int i = 0; i != dynamicSize - 1; i += 1)
        {
            float dx = _x[i + 1] - _x[i];
            Debug.Assert(dx > 0);
            secants[i] = (_y[i + 1] - _y[i]) / dx;
        }

        _slopes[0] = secants[0];
        // averaging
        for (int i = 1; i != dynamicSize - 1; i += 1)
        {
            _slopes[i] = (secants[i - 1] + secants[i]) / 2;
        }

        _slopes[dynamicSize - 1] = secants[^1];

        for (int i = 0; i != dynamicSize - 1; i += 1)
        {
            if (Mathf.Approximately(secants[i], 0))
            {
                _slopes[i] = 0;
                secants[i + 1] = 0;
                continue;
            }

            float alpha = _slopes[i] / secants[i];
            float beta = _slopes[i + 1] / secants[i];
            float hypot = Mathf.Sqrt(Mathf.Pow(alpha, 2) + Mathf.Pow(beta, 2));
            if (hypot > 3)
            {
                float t = 3 / hypot;
                _slopes[i] = t * alpha * secants[i];
                _slopes[i + 1] = t * beta * secants[i];
            }
        }
    }

    //https://en.wikipedia.org/wiki/Cubic_Hermite_spline
    private static float Hermite(float point, (float, float) x, (float, float) y, (float, float) m)
    {
        float h = x.Item2 - x.Item1;
        float t = (point - x.Item1) / h;
        return (y.Item1 * (1 + 2 * t) + h * m.Item1 * t) * (1 - t) * (1 - t)
               + (y.Item2 * (3 - 2 * t) + h * m.Item2 * (t - 1)) * t * t;
    }

    public float Interpolate(float point)
    {
        if (point <= _x[0])
        {
            return _y[0];
        }

        if (point >= _x[_dynamicSize - 1])
        {
            return _y[_dynamicSize - 1];
        }

        int i = 0;
        while (point >= _x[i + 1])
        {
            i += 1;
            if (Mathf.Approximately(point, _x[i]))
            {
                return _y[i];
            }
        }

        return Hermite(point, (_x[i], _x[i + 1]), (_y[i], _y[i + 1]), (_slopes[i], _slopes[i + 1]));
    }
}
