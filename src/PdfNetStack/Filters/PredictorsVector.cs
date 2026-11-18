#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PdfNetStack.Filters;

/// <summary>
/// PNG prediction 16 bytes at a time with the cross-platform 128 bit vector API, which the JIT and
/// Native AOT compile to SSE2 or AdvSimd. Every lane computes exactly what the scalar code in
/// <see cref="Predictors"/> computes for its byte, so the encoded rows are the same.
/// </summary>
internal static class PredictorsVector
{
    /// <summary>
    /// Fills the Sub, Up, Average and Paeth candidates from byte <paramref name="start"/>, which is
    /// at least the bytes per pixel, in whole vectors.
    /// </summary>
    /// <returns>The first byte left for the scalar code.</returns>
    public static int Filter(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int bpp, int start, Span<byte> sub, Span<byte> up, Span<byte> avg, Span<byte> paeth)
    {
        ref byte x0 = ref MemoryMarshal.GetReference(row);
        ref byte b0 = ref MemoryMarshal.GetReference(previous);
        ref byte sub0 = ref MemoryMarshal.GetReference(sub);
        ref byte up0 = ref MemoryMarshal.GetReference(up);
        ref byte avg0 = ref MemoryMarshal.GetReference(avg);
        ref byte paeth0 = ref MemoryMarshal.GetReference(paeth);
        int i = start;
        for (; i + Vector128<byte>.Count <= row.Length; i += Vector128<byte>.Count)
        {
            var at = (nuint)i;
            var left = (nuint)(i - bpp);
            Vector128<byte> x = Vector128.LoadUnsafe(ref x0, at);
            Vector128<byte> a = Vector128.LoadUnsafe(ref x0, left);
            Vector128<byte> b = Vector128.LoadUnsafe(ref b0, at);
            Vector128<byte> c = Vector128.LoadUnsafe(ref b0, left);
            (x - a).StoreUnsafe(ref sub0, at);
            (x - b).StoreUnsafe(ref up0, at);

            // floor((a + b) / 2) without leaving 8 bits.
            (x - ((a & b) + Vector128.ShiftRightLogical(a ^ b, 1))).StoreUnsafe(ref avg0, at);
            (x - Paeth(a, b, c)).StoreUnsafe(ref paeth0, at);
        }

        return i;
    }

    /// <summary>The scalar score of the whole vectors of <paramref name="row"/>.</summary>
    /// <param name="end">The first byte left for the scalar code.</param>
    public static long Score(ReadOnlySpan<byte> row, out int end)
    {
        ref byte r0 = ref MemoryMarshal.GetReference(row);
        long total = 0;
        int i = 0;
        while (i + Vector128<byte>.Count <= row.Length)
        {
            // Each pass adds at most 512 to a 32 bit lane; flushing every 64 Ki passes keeps it exact.
            Vector128<uint> sum = Vector128<uint>.Zero;
            for (int n = 0; n < 1 << 16 && i + Vector128<byte>.Count <= row.Length; n++, i += Vector128<byte>.Count)
            {
                // |v| of a signed byte, with -128 read back as 128.
                Vector128<byte> magnitude = Vector128.Abs(Vector128.LoadUnsafe(ref r0, (nuint)i).AsSByte()).AsByte();
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(magnitude);
                (Vector128<uint> first, Vector128<uint> second) = Vector128.Widen(low + high);
                sum += first + second;
            }

            total += Vector128.Sum(sum);
        }

        end = i;
        return total;
    }

    /// <summary>The Paeth predictor of every lane, chosen as <see cref="Predictors.Paeth"/> chooses it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Paeth(Vector128<byte> a, Vector128<byte> b, Vector128<byte> c)
    {
        (Vector128<ushort> aLow, Vector128<ushort> aHigh) = Vector128.Widen(a);
        (Vector128<ushort> bLow, Vector128<ushort> bHigh) = Vector128.Widen(b);
        (Vector128<ushort> cLow, Vector128<ushort> cHigh) = Vector128.Widen(c);
        return Vector128.Narrow(Half(aLow.AsInt16(), bLow.AsInt16(), cLow.AsInt16()), Half(aHigh.AsInt16(), bHigh.AsInt16(), cHigh.AsInt16()));

        // With p = a + b - c: |p - a| = |b - c|, |p - b| = |a - c|, |p - c| = |a + b - 2c|, in 16 bits.
        static Vector128<ushort> Half(Vector128<short> a, Vector128<short> b, Vector128<short> c)
        {
            Vector128<short> pa = Vector128.Abs(b - c);
            Vector128<short> pb = Vector128.Abs(a - c);
            Vector128<short> pc = Vector128.Abs(a + b - c - c);
            Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
            Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
            return Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c)).AsUInt16();
        }
    }
}
#endif
