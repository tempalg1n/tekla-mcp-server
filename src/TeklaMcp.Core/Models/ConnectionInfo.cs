namespace TeklaMcp.Core.Models;

/// <summary>Result of probing the connection to a running Tekla Structures model.</summary>
public sealed class ConnectionInfo
{
    /// <summary>True if a model is open and the API answered.</summary>
    public bool Connected { get; set; }

    /// <summary>Model name, e.g. "MyTower". Empty when not connected.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Absolute path to the model folder. Empty when not connected.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Tekla Structures version reported by the running instance, if available.</summary>
    public string? TeklaVersion { get; set; }

    /// <summary>Which backend produced this answer: "Mock" or "Tekla".</summary>
    public string Backend { get; set; } = "";

    /// <summary>Human-readable note, e.g. why the connection failed.</summary>
    public string? Message { get; set; }
}
