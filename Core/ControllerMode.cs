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
}
