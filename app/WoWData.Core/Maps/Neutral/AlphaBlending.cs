namespace WoWData.Core.Maps.Neutral;

/// <summary>
/// Conversions between the two meanings of layer alpha (<see cref="AlphaBlendMode"/>), per texel:
/// visible weight of layer L = sequential alpha_L x product of (1 - alpha_M) for the layers M above it (the base gets the
/// rest), and back: sequential alpha_j = weight_j / (sum of the weights of layer j and every layer below it).
/// </summary>
public static class AlphaBlending
{
    /// <summary>The visible share (0..1) of each layer in each of the 4,096 texels. Layer 0 (the base) has no alpha map;
    /// a layer without one counts as alpha 0.</summary>
    public static float[][] VisibleWeights(IReadOnlyList<NeutralLayer> layers, AlphaBlendMode mode)
    {
        int n = layers.Count;
        var w = new float[n][];
        for (int L = 0; L < n; L++) w[L] = new float[4096];
        if (n == 0) return w;
        for (int t = 0; t < 4096; t++)
        {
            if (mode == AlphaBlendMode.Sequential)
            {
                float rest = 1f;
                for (int L = n - 1; L >= 1; L--)
                {
                    float a = (layers[L].Alpha?[t] ?? 0) / 255f;
                    w[L][t] = a * rest;
                    rest -= a * rest;
                }
                w[0][t] = rest;
            }
            else
            {
                float sum = 0;
                for (int L = 1; L < n; L++) sum += w[L][t] = (layers[L].Alpha?[t] ?? 0) / 255f;
                if (sum > 1f)
                {
                    for (int L = 1; L < n; L++) w[L][t] /= sum;
                    sum = 1f;
                }
                w[0][t] = 1f - sum;
            }
        }
        return w;
    }

    /// <summary>Sequential alphas (0..255, unrounded) of the given layers' weights, renormalised to 1 per texel. Index 0
    /// (the base) is all zero. A texel where every kept weight is 0 shows the base.</summary>
    public static float[][] SequentialFromWeights(IReadOnlyList<float[]> weights)
    {
        int n = weights.Count;
        var a = new float[n][];
        for (int j = 0; j < n; j++) a[j] = new float[4096];
        for (int t = 0; t < 4096; t++)
        {
            float sum = 0;
            for (int j = 0; j < n; j++) sum += weights[j][t];
            float below = 0;
            for (int j = 0; j < n; j++)
            {
                float wj = sum > 0 ? weights[j][t] / sum : (j == 0 ? 1f : 0f);
                below += wj;
                if (j > 0) a[j][t] = below > 0 ? wj / below * 255f : 0f;
            }
        }
        return a;
    }

    /// <summary>8-bit weights of the upper layers (index 0 = base: null) whose sum per texel never exceeds 255 (rounding
    /// excess taken from the largest). <paramref name="hidden"/> counts texels where a layer had paint (a sequential
    /// alpha above 0) but no visible share left (it is under an opaque layer).</summary>
    public static byte[]?[] WeightBytes(IReadOnlyList<NeutralLayer> layers, out long hidden)
    {
        int n = layers.Count;
        var w = VisibleWeights(layers, AlphaBlendMode.Sequential);
        var outB = new byte[]?[n];
        for (int L = 1; L < n; L++) outB[L] = layers[L].Alpha != null ? new byte[4096] : null;
        hidden = 0;
        var v = new int[n];
        for (int t = 0; t < 4096; t++)
        {
            int sum = 0, big = -1;
            for (int L = 1; L < n; L++)
            {
                v[L] = (int)MathF.Round(w[L][t] * 255f);
                if ((layers[L].Alpha?[t] ?? 0) > 0 && v[L] == 0) hidden++;
                sum += v[L];
                if (big < 0 || v[L] > v[big]) big = L;
            }
            if (sum > 255 && big > 0) v[big] -= sum - 255;
            for (int L = 1; L < n; L++)
                if (outB[L] is { } o) o[t] = (byte)Math.Clamp(v[L], 0, 255);
        }
        return outB;
    }

    /// <summary>The nearest value a 1.12 alpha can hold in 8 bits (0 or 16n + 15, n = 1..15).</summary>
    public static byte Level4(float v)
    {
        if (v < 15.5f) return 0;
        int n = Math.Clamp((int)MathF.Round((v - 15f) / 16f), 1, 15);
        return NeutralLayer.Expand4(n);
    }
}
