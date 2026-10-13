namespace Nexus.Service.Models.Sensors;

/// <summary>
/// One ROG Astral card's Level Sense calibration: whether a calibration is
/// being taken right now, and when the one in use was taken (Unix ms; null when
/// none was saved yet). GpuId is the card's GpuReadout id; Name is the card as
/// the GPU list names it (e.g. "ASUS ROG Astral RTX 5090").
/// </summary>
public sealed record GpuLevelSenseState(string GpuId, string Name, bool Calibrating, long? CalibratedAtMs);
