using System.Collections.Generic;
using System.ComponentModel;

namespace TeklaMcp.Core.Models;

/// <summary>Declarative custom/system connection to create.</summary>
public sealed class ConnectionSpec
{
    [Description("Exact component/connection name; copying from a listed connection is safest for non-ASCII names.")]
    public string Name { get; set; } = "";
    [Description("Tekla component number; -1 for a custom component.")]
    public int Number { get; set; } = -1;
    [Description("Primary model-part GUID.")]
    public string PrimaryGuid { get; set; } = "";
    [Description("One or more secondary model-part GUIDs.")]
    public List<string> SecondaryGuids { get; set; } = new List<string>();
    [Description("Optional connection up vector in global model coordinates.")]
    public Point3D? UpVector { get; set; }
    [Description("Optional saved connection attributes filename.")]
    public string AttributesFile { get; set; } = "";
    [Description("Tekla AutoDirectionType enum name; normally NA.")]
    public string AutoDirection { get; set; } = "NA";
}
