using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.CreativeAudio;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.CreativeAudio;

/// <summary>The Sound Blaster protocol against replies read from a G8 (firmware 1.0.240911.1010).</summary>
public class SoundBlasterTests
{
    private static byte[] Report(string hex)
    {
        var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();
        var report = new byte[SoundBlasterProtocol.ReportLength];
        bytes.CopyTo(report, 1);
        return report;
    }

    private static string Hex(byte[] report, int count) =>
        string.Join(' ', report.Skip(1).Take(count).Select(b => b.ToString("x2")));

    [Fact]
    public void Requests_HaveCreativeAppsShape()
    {
        Assert.Equal("5a 11 03 01 96 07", Hex(SoundBlasterProtocol.ReadParameter(0x96, 0x07), 6));
        Assert.Equal("5a 12 07 01 96 0b 00 00 40 c0", Hex(SoundBlasterProtocol.WriteParameter(0x96, 0x0B, -3f), 10));
        Assert.Equal("5a 26 03 08 ff ff", Hex(SoundBlasterProtocol.ReadProfile(), 6));
        Assert.Equal("5a 26 05 07 02 00 01 00", Hex(SoundBlasterProtocol.WriteProfile(0x02, true), 8));
        Assert.Equal("5a 2c 05 00 04 00 00 00", Hex(SoundBlasterProtocol.WriteOutput(SoundBlasterProtocol.OutputHeadphones), 8));
        Assert.Equal("5a 6c 03 00 03 00", Hex(SoundBlasterProtocol.WriteFilter(3), 6));
        Assert.Equal("5a 3c 04 00 00 02 14", Hex(SoundBlasterProtocol.WriteMicBoost(20), 7));
        Assert.Equal(0, SoundBlasterProtocol.ReadProfile()[0]);
        Assert.Equal(SoundBlasterProtocol.ReportLength, SoundBlasterProtocol.ReadProfile().Length);
    }

    [Fact]
    public void Replies_ParseAsTheG8SendsThem()
    {
        Assert.True(SoundBlasterProtocol.TryParseParameter(Report("5a 11 08 01 00 96 01 8f c2 f5 3d 00"), out var family, out var id, out var value));
        Assert.Equal((0x96, 0x01), (family, id));
        Assert.Equal(0.12f, value, 3);
        Assert.Equal(0x01, SoundBlasterProtocol.ParseProfile(Report("5a 26 0b 08 ff ff 01 00 00 00")));
        Assert.Equal(SoundBlasterProtocol.OutputSpeakers, SoundBlasterProtocol.ParseOutput(Report("5a 2c 05 01 02 00 00 00")));
        Assert.Equal(new byte[] { 2, 4 }, SoundBlasterProtocol.ParseOutputList(Report("5a 2c 0a 02 82 02 00 00 00 04 00 00 00")));
        Assert.Equal((byte)1, SoundBlasterProtocol.ParseFilter(Report("5a 6c 03 01 01 00")));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, SoundBlasterProtocol.ParseFilterList(Report("5a 6c 0e 02 05 85 00 01 00 02 00 03 00 04 00 05")));
        Assert.Equal((byte)0, SoundBlasterProtocol.ParseMicBoost(Report("5a 3c 04 01 00 02 00")));
        Assert.True(SoundBlasterProtocol.TryParseAck(Report("5a 02 0a 11 83"), out var command, out var status));
        Assert.Equal((0x11, 0x83), (command, status));
        Assert.Equal("1.0.240911.1010", SoundBlasterProtocol.ParseText(Report("5a 07 0f 31 2e 30 2e 32 34 30 39 31 31 2e 31 30 31 30"), 0));
    }

    [Fact]
    public void GetState_ReadsTheDevice_AndAChangeIsAcknowledged()
    {
        var device = new FakeG8();
        var service = new SoundBlasterService(device);

        var state = service.GetState();

        Assert.True(state.Present);
        Assert.Equal("Creative Sound Blaster G8", state.Name);
        Assert.True(state.Sbx);
        Assert.False(state.Scout);
        Assert.Equal(1f, state.Values["96:07"]);
        Assert.Equal(80f, state.Values["96:17"]);
        Assert.False(state.Values.ContainsKey("97:00"));
        Assert.Equal("speakers", state.Output);
        Assert.Equal(new[] { "speakers", "headphones" }, state.Outputs);
        Assert.Equal(1, state.DacFilter);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, state.DacFilters);
        Assert.Equal(0, state.MicBoost);
        Assert.Equal("1.0.240911.1010", state.Firmware);

        Assert.True(service.SetParameter(0x96, 0x0B, 4.5f).Ok);
        Assert.Equal("5a 12 07 01 96 0b 00 00 90 40", Hex(device.Writes[^1], 10));
        Assert.Equal(4.5f, service.GetState().Values["96:0b"]);

        Assert.True(service.SetScout(true).Ok);
        Assert.False(service.GetState().Sbx);
    }

    [Fact]
    public void Changes_AreCheckedBeforeAnythingIsSent()
    {
        var device = new FakeG8();
        var service = new SoundBlasterService(device);

        Assert.False(service.SetParameter(0x42, 0, 1).Ok);
        Assert.False(service.SetParameter(0x96, 0, float.NaN).Ok);
        Assert.False(service.SetMicBoost(15).Ok);
        Assert.False(service.SetOutput("line").Ok);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public void ARefusal_IsReported()
    {
        var device = new FakeG8 { RefuseWrites = true };
        var result = new SoundBlasterService(device).SetFilter(2);

        Assert.False(result.Ok);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void NoDevice_ReadsAsNotPresent()
    {
        var service = new SoundBlasterService(new FakeG8 { Connected = false });

        Assert.False(service.GetState().Present);
        Assert.Equal("not-present", service.SetSbx(true).Status);
    }

    /// <summary>A G8 answering as the real one did, holding the values it was read with.</summary>
    private sealed class FakeG8 : ISoundBlasterChannel
    {
        private readonly Dictionary<(byte, byte), float> _values = new()
        {
            [(0x96, 0x01)] = 0.12f, [(0x96, 0x07)] = 1f, [(0x96, 0x08)] = 0.5f,
            [(0x96, 0x17)] = 80f, [(0x96, 0x19)] = 0.5f, [(0x95, 0x2D)] = 0.74f, [(0x97, 0x02)] = 2f,
        };
        private int _profile = 0x01;

        public bool Connected { get; init; } = true;
        public bool RefuseWrites { get; init; }
        public List<byte[]> Writes { get; } = new();

        public SoundBlasterModel? Present() => Connected ? SoundBlasterChannel.Models[0] : null;

        public byte[]? Exchange(byte[] request, Func<byte[], bool> accept, Action<byte[]>? seen = null)
        {
            var command = request[2];
            var isWrite = command == SoundBlasterProtocol.SetParameter
                || (command == SoundBlasterProtocol.GlobalProfile && request[4] == 0x07)
                || (command is SoundBlasterProtocol.OutputSelect or SoundBlasterProtocol.DacFilter or SoundBlasterProtocol.MicGain && request[4] == 0x00);
            if (isWrite)
            {
                Writes.Add(request);
            }
            foreach (var reply in Answer(request))
            {
                seen?.Invoke(reply);
                if (accept(reply))
                {
                    return reply;
                }
            }
            return null;
        }

        private IEnumerable<byte[]> Answer(byte[] r)
        {
            switch (r[2])
            {
                case SoundBlasterProtocol.GetParameter:
                    if (r[5] == 0x97 && r[6] != 0x02)
                    {
                        yield return Report("5a 02 0a 11 83");
                        yield break;
                    }
                    var v = BitConverter.GetBytes(_values.GetValueOrDefault((r[5], r[6])));
                    yield return Report($"5a 11 08 01 00 {r[5]:x2} {r[6]:x2} {v[0]:x2} {v[1]:x2} {v[2]:x2} {v[3]:x2}");
                    break;
                case SoundBlasterProtocol.SetParameter:
                    if (!RefuseWrites)
                    {
                        _values[(r[5], r[6])] = BitConverter.ToSingle(r, 7);
                    }
                    yield return Report(RefuseWrites ? "5a 02 0a 12 83" : "5a 02 0a 12 00");
                    break;
                case SoundBlasterProtocol.GlobalProfile when r[4] == 0x08:
                    yield return Report($"5a 26 0b 08 ff ff {_profile:x2} 00");
                    break;
                case SoundBlasterProtocol.GlobalProfile:
                    if (r[5] == 0x02 && r[7] == 1)
                    {
                        _profile = 0x02;
                    }
                    yield return Report("5a 02 0a 26 00");
                    yield return Report($"5a 26 0b 08 ff ff {_profile:x2} 00");
                    break;
                case SoundBlasterProtocol.OutputSelect when r[4] == 0x01:
                    yield return Report("5a 2c 05 01 02 00 00 00");
                    break;
                case SoundBlasterProtocol.OutputSelect when r[4] == 0x02:
                    yield return Report("5a 2c 0a 02 82 02 00 00 00 04 00 00 00");
                    break;
                case SoundBlasterProtocol.DacFilter when r[4] == 0x01:
                    yield return Report("5a 6c 03 01 01 00");
                    break;
                case SoundBlasterProtocol.DacFilter when r[4] == 0x02:
                    yield return Report("5a 6c 0e 02 05 85 00 01 00 02 00 03 00 04 00 05");
                    break;
                case SoundBlasterProtocol.MicGain when r[4] == 0x01:
                    yield return Report("5a 3c 04 01 00 02 00");
                    break;
                case SoundBlasterProtocol.FirmwareString:
                    yield return Report("5a 07 0f 31 2e 30 2e 32 34 30 39 31 31 2e 31 30 31 30");
                    break;
                case SoundBlasterProtocol.DeviceInformation:
                    yield return Report("5a 09 13 03 59 44 53 42 31 39 30 30");
                    break;
                default:
                    yield return Report($"5a 02 0a {r[2]:x2} {(RefuseWrites ? "83" : "00")}");
                    break;
            }
        }
    }
}
