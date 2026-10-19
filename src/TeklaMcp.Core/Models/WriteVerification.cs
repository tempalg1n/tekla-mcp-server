using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Whether the database holds what a write asked for, field by field (issue #22). A commit and
/// an accepted <c>Modify()</c> do not prove it: Tekla can return success and keep the old value
/// (a Class 14 → 50 edit read back as 14, live Tekla 2021).
/// </summary>
public sealed class WriteVerification
{
    /// <summary>
    /// verified (every checked object holds every requested value) / mismatch (at least one does
    /// not — see <see cref="Mismatches"/>) / incomplete (no mismatch found, but some objects could
    /// not be read back) / not_checked (nothing was written).
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>Objects read back after the commit — every written object, not a sample.</summary>
    public int CheckedObjects { get; set; }

    /// <summary>Objects whose requested fields all match.</summary>
    public int VerifiedObjects { get; set; }

    /// <summary>Objects with at least one field that differs from the request.</summary>
    public int MismatchedObjects { get; set; }

    /// <summary>Written objects that could not be read back.</summary>
    public int UnreadObjects { get; set; }

    /// <summary>Every mismatching field (capped at <see cref="MismatchLimit"/>; see the truncation flag).</summary>
    public List<FieldMismatch> Mismatches { get; set; } = new List<FieldMismatch>();

    /// <summary>True when more mismatches exist than <see cref="Mismatches"/> lists.</summary>
    public bool MismatchesTruncated { get; set; }

    /// <summary>Requested fields that were deliberately not compared, with the reason.</summary>
    public List<string> NotCompared { get; set; } = new List<string>();

    /// <summary>Cap on <see cref="Mismatches"/>; a batch is at most 200 objects.</summary>
    public const int MismatchLimit = 500;
}

/// <summary>One requested field the database does not hold.</summary>
public sealed class FieldMismatch
{
    public string Guid { get; set; } = "";

    /// <summary>profile, material, class, name, start or end.</summary>
    public string Field { get; set; } = "";

    public string Requested { get; set; } = "";

    /// <summary>What the database holds; "(not readable)" when the read-back has no such value.</summary>
    public string Stored { get; set; } = "";
}
