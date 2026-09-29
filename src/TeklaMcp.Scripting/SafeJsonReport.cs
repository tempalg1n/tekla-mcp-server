using System.Collections.Generic;

namespace TeklaMcp.Scripting;

/// <summary>
/// Which <see cref="SafeJson"/> caps fired while rendering one value. Every cap drops data
/// while the JSON stays valid — a list capped at 100 still parses as a list — so an agent
/// cannot tell a complete value from a cut one by looking at it. A field report (141 parts
/// returned as the first 100 rows, accepted as the full audit) is why this exists.
/// </summary>
public sealed class SafeJsonReport
{
    /// <summary>Strings (values, keys or property names) cut to the per-string cap.</summary>
    public int TruncatedStrings { get; internal set; }

    /// <summary>Length of the longest string before it was cut.</summary>
    public int LongestTruncatedString { get; internal set; }

    /// <summary>Lists/dictionaries that had more items than the per-collection cap.</summary>
    public int CappedCollections { get; internal set; }

    /// <summary>Largest known item count among capped collections; -1 when none was countable.</summary>
    public int LargestCappedCollection { get; internal set; } = -1;

    /// <summary>Objects whose public properties exceeded the per-object cap.</summary>
    public int PropertyCappedObjects { get; internal set; }

    /// <summary>Containers past the depth cap, rendered as "[depth limit reached: …]" markers.</summary>
    public int DepthLimitHits { get; internal set; }

    /// <summary>Property getters that threw and were rendered as "&lt;threw: …&gt;".</summary>
    public int ThrowingProperties { get; internal set; }

    /// <summary>The whole value exceeded the total-size cap; the JSON is a preview envelope.</summary>
    public bool SizeCapExceeded { get; internal set; }

    /// <summary>The renderer itself failed; the JSON is a failure string.</summary>
    public bool SerializationFailed { get; internal set; }

    /// <summary>
    /// True when data was dropped. Throwing getters are reported in <see cref="Notes"/> but do
    /// not count: their failure is visible in place and is not a cap.
    /// </summary>
    public bool Truncated =>
        TruncatedStrings > 0 || CappedCollections > 0 || PropertyCappedObjects > 0 ||
        DepthLimitHits > 0 || SizeCapExceeded || SerializationFailed;

    internal void NoteTruncatedString(int originalLength)
    {
        TruncatedStrings++;
        if (originalLength > LongestTruncatedString) LongestTruncatedString = originalLength;
    }

    internal void NoteCappedCollection(int knownCount)
    {
        CappedCollections++;
        if (knownCount > LargestCappedCollection) LargestCappedCollection = knownCount;
    }

    /// <summary>One plain-language line per cap that fired, in severity order.</summary>
    public List<string> Notes()
    {
        var notes = new List<string>();
        if (SerializationFailed)
            notes.Add("Serialization of the return value failed; returnValueJson carries only the error.");
        if (SizeCapExceeded)
            notes.Add(
                $"The value exceeded {SafeJson.MaxTotalLength} characters; returnValueJson is a " +
                "{truncated, capturedLength, preview} envelope, not the value itself.");
        if (CappedCollections > 0)
            notes.Add(
                $"{CappedCollections} collection(s) cut to the first {SafeJson.MaxItems} items" +
                (LargestCappedCollection >= 0 ? $" (largest had {LargestCappedCollection})." : "."));
        if (DepthLimitHits > 0)
            notes.Add(
                $"{DepthLimitHits} container(s) nested deeper than {SafeJson.MaxDepth} levels were " +
                "replaced by \"[depth limit reached: …]\" strings.");
        if (TruncatedStrings > 0)
            notes.Add(
                $"{TruncatedStrings} string(s) cut to {SafeJson.MaxStringLength} characters " +
                $"(longest was {LongestTruncatedString}).");
        if (PropertyCappedObjects > 0)
            notes.Add(
                $"{PropertyCappedObjects} object(s) had more than {SafeJson.MaxProperties} public " +
                "properties; the rest were omitted.");
        if (ThrowingProperties > 0)
            notes.Add(
                $"{ThrowingProperties} property getter(s) threw and were rendered as \"<threw: …>\".");
        return notes;
    }
}
