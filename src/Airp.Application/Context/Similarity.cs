namespace Airp.Application.Context;

/// <summary>Compares embedding vectors.</summary>
/// <remarks>
/// Cosine over plain <c>float[]</c>, computed in memory. At this corpus size — one user, a
/// handful of conversations, a few thousand vectors at most — a scan is microseconds and an
/// index would be machinery guarding nothing. <c>sqlite-vec</c> is the answer if that ever
/// stops being true, and not before.
/// </remarks>
public static class Similarity
{
    /// <summary>Cosine similarity between two vectors.</summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>
    /// A value from -1 to 1, or 0 when either vector is empty, of a different length, or has
    /// no magnitude. Those are all "cannot be compared", and a caller ranking by score treats
    /// 0 as "not similar", which is the right answer for all three.
    /// </returns>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0f;
        }

        double dot = 0, magnitudeA = 0, magnitudeB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            magnitudeA += a[i] * (double)a[i];
            magnitudeB += b[i] * (double)b[i];
        }

        if (magnitudeA == 0 || magnitudeB == 0)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB)));
    }

    /// <summary>Packs a vector for storage as a BLOB.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>Its bytes, little-endian.</returns>
    public static byte[] ToBytes(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var bytes = new byte[vector.Count * sizeof(float)];

        for (var i = 0; i < vector.Count; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>Unpacks a vector stored as a BLOB.</summary>
    /// <param name="bytes">The bytes, or null.</param>
    /// <returns>The vector, or an empty one when there is nothing to read.</returns>
    public static float[] FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < sizeof(float))
        {
            return [];
        }

        var vector = new float[bytes.Length / sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BitConverter.ToSingle(bytes, i * sizeof(float));
        }

        return vector;
    }
}
