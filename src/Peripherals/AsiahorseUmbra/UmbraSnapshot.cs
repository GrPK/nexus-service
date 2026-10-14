namespace Nexus.Service.Peripherals.AsiahorseUmbra;

// Immutable; swapped whole under the hub lock. Readers take the reference once.
public sealed class UmbraSnapshot
{
    public static readonly UmbraSnapshot Empty = new("", "", true, true, EmptyPorts(), 765, -1);

    public UmbraSnapshot(string deviceId, string firmware, bool bootMemory, bool selfCheck, UmbraPort[] ports, int brightnessCap, int fanDuty)
    {
        DeviceId = deviceId;
        Firmware = firmware;
        BootMemory = bootMemory;
        SelfCheck = selfCheck;
        Ports = ports;
        BrightnessCap = brightnessCap;
        FanDuty = fanDuty;
    }

    public string DeviceId { get; }
    public string Firmware { get; }
    public bool BootMemory { get; }
    public bool SelfCheck { get; }
    public UmbraPort[] Ports { get; }
    public int BrightnessCap { get; }
    // Last duty written, -1 until the first write.
    public int FanDuty { get; }

    public UmbraSnapshot With(string? deviceId = null, string? firmware = null, bool? bootMemory = null, bool? selfCheck = null, UmbraPort[]? ports = null, int? fanDuty = null) =>
        new(deviceId ?? DeviceId, firmware ?? Firmware, bootMemory ?? BootMemory, selfCheck ?? SelfCheck, ports ?? Ports, BrightnessCap, fanDuty ?? FanDuty);

    public int TotalLeds()
    {
        var total = 0;
        foreach (var p in Ports)
        {
            total += p.LedCount;
        }
        return total;
    }

    private static UmbraPort[] EmptyPorts()
    {
        var ports = new UmbraPort[UmbraProtocol.PortCount];
        for (var i = 0; i < ports.Length; i++)
        {
            ports[i] = UmbraPort.Empty;
        }
        return ports;
    }
}
