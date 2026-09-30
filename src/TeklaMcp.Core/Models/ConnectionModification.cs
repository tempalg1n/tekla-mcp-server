using System.ComponentModel;

namespace TeklaMcp.Core.Models;

/// <summary>
/// Declarative change to ONE existing connection/component.
///
/// Orientation quirk this DTO exists to tame: Tekla only honours a written
/// <c>Connection.UpVector</c> when the component's auto-direction is
/// <c>AUTODIR_NA</c>. Under any other mode <c>Modify()</c> still returns true while the
/// vector is silently recomputed from the members. Implementations therefore switch to
/// <c>AUTODIR_NA</c> whenever <see cref="UpVector"/> is set and
/// <see cref="AutoDirection"/> does not explicitly ask for something else.
/// </summary>
public sealed class ConnectionModification
{
    [Description("Connection GUID as returned by tekla_list_connections/tekla_find_connections.")]
    public string Guid { get; set; } = "";

    [Description("Connection integer ID; used only when the GUID is empty or unknown.")]
    public int? Id { get; set; }

    [Description("New up vector in global model coordinates. Null keeps the current vector.")]
    public Point3D? UpVector { get; set; }

    [Description("Tekla AutoDirectionType name (NA, BASIC, DIAGONAL, ...). Null keeps the " +
                 "current mode, except that setting UpVector forces NA so the write sticks.")]
    public string? AutoDirection { get; set; }

    [Description("Saved connection attributes file to load into the component. Empty = none.")]
    public string AttributesFile { get; set; } = "";
}
