namespace Nexus.Service.Models.Lighting;

/// <summary>One stored custom effect as listed and returned by the custom-effects routes. The frag is served separately through the shaders route.</summary>
public sealed class CustomEffectResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Recipe { get; set; } = "";
    public long UpdatedAt { get; set; }
    public bool Audio { get; set; }
    public string? MarketId { get; set; }
    public string? MarketAuthor { get; set; }
    public bool? Downloaded { get; set; }
}

/// <summary>PUT body for /lighting/custom-effects/{id}.</summary>
public sealed class CustomEffectPutBody
{
    public string? Name { get; set; }
    public string? Recipe { get; set; }
    public string? Frag { get; set; }
    public string? MarketId { get; set; }
    public string? MarketAuthor { get; set; }
    public bool? Downloaded { get; set; }
}
