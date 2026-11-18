namespace PdfNetStack;

/// <summary>
/// How far a long operation has come, reported through <see cref="IProgress{T}"/>: the items
/// done so far, such as pages or documents, out of how many there are.
/// </summary>
/// <remarks>
/// Each operation says what it counts. Reports arrive on the thread doing the work, unless the
/// <see cref="IProgress{T}"/> given is a <see cref="Progress{T}"/>, which posts them to the
/// context it was created on. A handler that throws stops the operation with its exception.
/// </remarks>
/// <param name="Completed">Items done so far.</param>
/// <param name="Total">How many items there are, or null when that is not known in advance.</param>
public readonly record struct PdfProgress(int Completed, int? Total)
{
    /// <summary>The share done, from 0 to 1, or null when <see cref="Total"/> is not known.</summary>
    public double? Fraction => Total switch
    {
        null => null,
        <= 0 => 1,
        int total => Math.Min(1, (double)Completed / total),
    };
}
