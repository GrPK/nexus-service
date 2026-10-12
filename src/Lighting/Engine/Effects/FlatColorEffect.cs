namespace Nexus.Service.Lighting.Engine.Effects;

/// <summary>One flat color over the whole canvas: the global Static look when the user picks an exact color. Needs no GPU.</summary>
public sealed class FlatColorEffect : IEffect
{
    private readonly byte _r, _g, _b;

    public FlatColorEffect(byte r, byte g, byte b)
    {
        _r = r;
        _g = g;
        _b = b;
    }

    public string Name => "flat";

    public void RenderFrame(CanvasBuffer canvas, double tickMs) => canvas.Fill(_r, _g, _b);

    public void Dispose() { }
}
