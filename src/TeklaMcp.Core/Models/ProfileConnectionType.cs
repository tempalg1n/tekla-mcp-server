namespace TeklaMcp.Core.Models;

/// <summary>
/// One unique connection "type" for a source profile.
/// </summary>
public sealed class ProfileConnectionType
{
    /// <summary>
    /// Signature that describes what is connected near a beam end,
    /// e.g. "Beam:IPE400 + Beam:HEA300 + ContourPlate:PL20*400".
    /// </summary>
    public string Signature { get; set; } = "";

    /// <summary>How many source members produced this signature.</summary>
    public int Occurrences { get; set; }
}
