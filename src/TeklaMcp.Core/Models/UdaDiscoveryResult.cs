using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Occurrence count for one distinct value of a discovered UDA field.</summary>
public sealed class UdaValueCount
{
    /// <summary>The stored value, converted to string.</summary>
    public string Value { get; set; } = "";

    /// <summary>How many sampled objects carry this value.</summary>
    public int Count { get; set; }
}

/// <summary>Statistics for one UDA field found on the sampled objects.</summary>
public sealed class UdaFieldStat
{
    /// <summary>UDA field name as stored in the model, e.g. "USER_FIELD_1".</summary>
    public string Name { get; set; } = "";

    /// <summary>Sampled objects carrying a non-empty value for this field (fill count).</summary>
    public int ObjectCount { get; set; }

    /// <summary>Number of distinct non-empty values seen in the sample.</summary>
    public int DistinctValueCount { get; set; }

    /// <summary>Most frequent values (capped), ordered by count descending.</summary>
    public List<UdaValueCount> TopValues { get; set; } = new List<UdaValueCount>();
}

/// <summary>
/// Result of sampling objects for "which UDA fields exist in this model, and what do they
/// hold?" (<c>ITeklaModelService.DiscoverUdas</c>). This answers the question a session
/// usually starts with — "in which field is X stored?" — without guessing candidate names.
///
/// SAMPLE-BASED by design: reading every UDA of an object is one expensive remoting call
/// (~100+ ms each on live Tekla), so implementations inspect at most <c>sampleSize</c>
/// objects and report that honestly via <see cref="SampledObjects"/> / <see cref="Truncated"/>.
/// A field missing here may still exist on unsampled objects; verify a specific field with
/// a filtered count (e.g. udaName + attributeContains) before concluding it is absent.
/// </summary>
public sealed class UdaDiscoveryResult
{
    /// <summary>Discovered fields, ordered by fill count descending.</summary>
    public List<UdaFieldStat> Fields { get; set; } = new List<UdaFieldStat>();

    /// <summary>Objects whose UDAs were actually read.</summary>
    public int SampledObjects { get; set; }

    /// <summary>True when more matching objects existed than the sample covered.</summary>
    public bool Truncated { get; set; }

    /// <summary>How the sample was drawn (scope, per-type quotas) and any failures.</summary>
    public string Message { get; set; } = "";

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";
}
