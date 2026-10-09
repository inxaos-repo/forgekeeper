namespace Forgekeeper.Core.Enums;

public enum AcquisitionMethod
{
    Unknown,
    Purchase,
    Subscription,
    Free,
    Campaign,
    Gift,
    /// <summary>Creator tribe (MMF TRIBE) membership.</summary>
    Tribe,
    /// <summary>Group / shared-library access (MMF USER_GROUP).</summary>
    UserGroup
}
