namespace Forgekeeper.Core.Enums;

/// <summary>
/// Where a model was acquired. Stored as a string column, so new members are
/// appended (order is not persisted).
/// </summary>
public enum SourceType
{
    Mmf,
    Thangs,
    Patreon,
    Cults3d,
    Thingiverse,
    Manual,
    Gumroad,

    /// <summary>
    /// Lives in a catch-all folder (e.g. sources/mmf) with no evidence of which
    /// platform it came from. Distinct from Manual (a deliberate manual import).
    /// </summary>
    Unknown,
}
