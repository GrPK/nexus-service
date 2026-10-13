using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.AsiahorseUmbra;

// Answers 00 (info) and 01 FF (port config) like the controller; 02 port writes update the modeled ports.
internal sealed class UmbraDeviceFake : IHidDevice
{
    private readonly Queue<byte[]> _replies = new();
    private readonly bool _leadingReportId;

    public UmbraDeviceFake(bool leadingReportId = false)
    {
        _leadingReportId = leadingReportId;
        for (var p = 0; p < UmbraProtocol.PortCount; p++)
        {
            Ports[p] = new[] { 0, 0, 1, p, 0 };
        }
    }

    // Per port: led count, multiplier, direction, order, type.
    public int[][] Ports { get; } = new int[UmbraProtocol.PortCount][];

    public int ModelCode { get; set; } = 5;

    public bool BootMemory { get; set; } = true;
    public bool SelfCheck { get; set; } = true;

    // Frames written, without the leading zero report id.
    public List<byte[]> Writes { get; } = new();

    // Reads return the gone status once set.
    public bool Gone { get; set; }

    // When set, Write fails.
    public bool FailWrites { get; set; }

    public int VendorId => UmbraProtocol.VendorId;
    public int ProductId => UmbraProtocol.ProductId;
    public string Path => "fake";
    public string? Serial => null;
    public int UsagePage => 0xFF00;
    public int Usage => 0x01;

    public static byte[] InfoReply(bool bootMemory = true, bool selfCheck = true, int modelCode = 5)
    {
        var r = new byte[64];
        var head = new byte[] { 0x52, 0x42, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0xCD, 0xAB, 0x2F, 0x96, 0x48, 0xBC, 0x44, 0xFE, 0x01, 0x00, 0x06, 0x00, 0x00, 0x00 };
        head.CopyTo(r, 0);
        r[8] = (byte)modelCode;
        r[23] = bootMemory ? (byte)0xFF : (byte)0x01;
        r[24] = selfCheck ? (byte)0xFF : (byte)0x01;
        return r;
    }

    public static byte[] PortReply(int[][] ports, int cap = 765)
    {
        var r = new byte[64];
        new byte[] { 0x52, 0x42, 0x39, 0x00, 0x01, 0xFF }.CopyTo(r, 0);
        for (var p = 0; p < ports.Length; p++)
        {
            for (var f = 0; f < 5; f++)
            {
                r[6 + 5 * p + f] = (byte)ports[p][f];
            }
        }
        r[56] = (byte)(cap >> 8);
        r[57] = (byte)(cap & 0xFF);
        return r;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (FailWrites)
        {
            return false;
        }
        var frame = report.Length == 65 ? report[1..].ToArray() : report.ToArray();
        Writes.Add(frame);
        if (frame[0] != 0x52)
        {
            return true;
        }
        switch (frame[4])
        {
            case UmbraProtocol.CmdInfo:
                Enqueue(InfoReply(BootMemory, SelfCheck, ModelCode));
                break;
            case UmbraProtocol.CmdReadPorts:
                Enqueue(PortReply(Ports));
                break;
            case UmbraProtocol.CmdWritePort:
                Ports[frame[5]][frame[6]] = frame[7];
                break;
            case UmbraProtocol.CmdBootMode:
                BootMemory = frame[5] == 0xFF;
                break;
            case UmbraProtocol.CmdSelfCheck:
                SelfCheck = frame[5] == 0xFF;
                break;
        }
        return true;
    }

    private void Enqueue(byte[] reply)
    {
        if (!_leadingReportId)
        {
            _replies.Enqueue(reply);
            return;
        }
        var prefixed = new byte[reply.Length + 1];
        reply.CopyTo(prefixed, 1);
        _replies.Enqueue(prefixed);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (Gone)
        {
            return -1;
        }
        if (_replies.Count == 0)
        {
            return 0;
        }
        var reply = _replies.Dequeue();
        var n = Math.Min(buffer.Length, reply.Length);
        reply.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public bool SetFeature(ReadOnlySpan<byte> report) => false;
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
    public void Dispose() { }
}
