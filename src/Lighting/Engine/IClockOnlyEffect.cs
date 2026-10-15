namespace Nexus.Service.Lighting.Engine.Effects;

/// <summary>An effect whose frame is a function of the render time and its own fixed settings alone, so it can be rendered at a future time.</summary>
public interface IClockOnlyEffect : IEffect
{
}
