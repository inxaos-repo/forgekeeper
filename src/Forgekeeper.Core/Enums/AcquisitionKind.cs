namespace Forgekeeper.Core.Enums;

/// <summary>
/// Multi-source attribution phase 3 (#93).
/// Origin = where the model was published/made (creator's platform).
/// Via = how it reached the library (e.g. a Telegram channel). Telegram is always Via, never a creator.
/// </summary>
public enum AcquisitionKind
{
    Origin,
    Via,
}
