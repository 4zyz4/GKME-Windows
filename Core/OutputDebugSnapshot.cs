using System;
using HIDMaestro;

namespace GKME.Core;

/// <summary>
/// 驱动（HIDMaestro 虚拟控制器）最近一次收到的输出报文快照，以及从最近一份
/// vendor-blob 输出报告解析出的自适应扳机字节块。仅用于 UI 调试窗口的“最新快照”
/// 展示；<see cref="RawBytes"/> / <see cref="LeftTriggerEffect"/> /
/// <see cref="RightTriggerEffect"/> 均为独立副本，可跨线程安全读取。
/// </summary>
public sealed class OutputDebugSnapshot
{
    /// <summary>快照生成时的时间（本地时间）。</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>主机使用的输出 API。</summary>
    public HMOutputSource Source { get; init; }

    /// <summary>HID Report ID（非 HID 来源为 0）。</summary>
    public byte ReportId { get; init; }

    /// <summary>驱动分配的单调序号。</summary>
    public uint SeqNo { get; init; }

    /// <summary>是否已经收到过至少一份输出报文。</summary>
    public bool HasPacket { get; init; }

    /// <summary>原始输出报文字节（不含 Report ID）。</summary>
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();

    /// <summary>最近一份 vendor-blob 报告的 CRC32 校验结果。</summary>
    public bool CrcValid { get; init; } = true;

    /// <summary>右扳机效果 11 字节块；未收到为 null。</summary>
    public byte[]? RightTriggerEffect { get; init; }

    /// <summary>左扳机效果 11 字节块；未收到为 null。</summary>
    public byte[]? LeftTriggerEffect { get; init; }
}
