namespace Nexus.Service.Lighting.Engine.Effects;

/// <summary>Keeps the engine loop ticking while a studio session runs over an Off mode.</summary>
public sealed class StudioIdleEffect : IEffect
{
    public string Name => "studio-idle";
    public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Clear();
    public void Dispose() { }
}
