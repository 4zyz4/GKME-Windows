namespace GKME.Core;

public enum ControllerMode
{
    Ds4 = 0,
    Xbox360 = 1,
    Dualsense = 2,
    SwitchPro = 3,
    /// <summary>左右两只 Joy-Con，同时以两个独立 USB HID 设备
    /// (057E:2006 / 057E:2007) 呈现，eden 会识别为真正的 Joy-Con。</summary>
    JoyCon = 4,
    /// <summary>Xbox One 手柄（Model 1708，VID 045E:02EA），以厂商类
    /// GIP (Gaming Input Protocol) USB 设备呈现；Windows 由
    /// xboxgip.sys + xinputhid.sys 原生识别为 XInput 设备。</summary>
    XboxOne = 5,
    /// <summary>Xbox Series X|S 手柄（Model 1914，VID 045E:0B12），同样是
    /// 厂商类 GIP 设备，但元数据包含 ConsoleFunctionMap / DynamicLatency 接口，
    /// 输入报文 40 字节并带 Share（分享）键；Windows 同样以 XInput 呈现。</summary>
    XboxSeries = 6,
}
