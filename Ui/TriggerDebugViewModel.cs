using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using GKME.Core;

namespace GKME.Ui;

/// <summary>自适应扳机调试窗口的根 ViewModel：跟踪 <see cref="MainViewModel.Sessions"/>，
/// 为每个已连接会话维护一个“最新驱动报文字节”快照条目。</summary>
public class TriggerDebugViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainViewModel _main;

    public ObservableCollection<TriggerDebugEntryViewModel> Entries { get; } = new();

    public bool HasEntries => Entries.Count > 0;

    public TriggerDebugViewModel(MainViewModel main)
    {
        _main = main;
        main.Sessions.CollectionChanged += OnSessionsChanged;
        foreach (var session in main.Sessions)
            AddSession(session);
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in Entries) entry.Detach();
            Entries.Clear();
        }
        else
        {
            if (e.OldItems != null)
                foreach (GamepadSession session in e.OldItems) RemoveSession(session);
            if (e.NewItems != null)
                foreach (GamepadSession session in e.NewItems) AddSession(session);
        }
        OnPropertyChanged(nameof(HasEntries));
    }

    private void AddSession(GamepadSession session)
    {
        if (Entries.Any(entry => entry.Session == session)) return;
        Entries.Add(new TriggerDebugEntryViewModel(session));
    }

    private void RemoveSession(GamepadSession session)
    {
        var entry = Entries.FirstOrDefault(item => item.Session == session);
        if (entry == null) return;
        entry.Detach();
        Entries.Remove(entry);
    }

    public void Dispose()
    {
        _main.Sessions.CollectionChanged -= OnSessionsChanged;
        foreach (var entry in Entries) entry.Detach();
        Entries.Clear();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>单个会话的驱动输出“最新快照”。事件在驱动轮询线程触发，UI 更新通过
/// Dispatcher 合并投递，避免高频报文刷爆 UI 线程。</summary>
public class TriggerDebugEntryViewModel : INotifyPropertyChanged
{
    private readonly GamepadSession _session;
    private int _pendingPost;

    private string _timeText = "等待数据";
    private string _rawInfoText = "尚未收到驱动输出报文";
    private string _rawHex = "";
    private string _crcText = "";
    private string _leftHex = "无数据";
    private string _leftEffectText = "尚未收到扳机效果";
    private string _rightHex = "无数据";
    private string _rightEffectText = "尚未收到扳机效果";

    internal GamepadSession Session => _session;

    public string Title => $"{_session.Device.DeviceName}  ({_session.Device.IpString})";
    public string Subtitle => $"模式: {DeviceCardViewModel.DisplayName(_session.Mode)}";

    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }
    public string RawInfoText { get => _rawInfoText; private set => Set(ref _rawInfoText, value); }
    public string RawHex { get => _rawHex; private set => Set(ref _rawHex, value); }
    public string CrcText { get => _crcText; private set => Set(ref _crcText, value); }
    public string LeftHex { get => _leftHex; private set => Set(ref _leftHex, value); }
    public string LeftEffectText { get => _leftEffectText; private set => Set(ref _leftEffectText, value); }
    public string RightHex { get => _rightHex; private set => Set(ref _rightHex, value); }
    public string RightEffectText { get => _rightEffectText; private set => Set(ref _rightEffectText, value); }

    public TriggerDebugEntryViewModel(GamepadSession session)
    {
        _session = session;
        session.OnOutputDebug += OnOutputDebug;
        Apply(session.OutputDebug);
    }

    public void Detach() => _session.OnOutputDebug -= OnOutputDebug;

    private void OnOutputDebug(GamepadSession session)
    {
        if (Interlocked.Exchange(ref _pendingPost, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _pendingPost, 0);
            Apply(session.OutputDebug);
        }, DispatcherPriority.Background);
    }

    private void Apply(OutputDebugSnapshot snap)
    {
        if (!snap.HasPacket && snap.LeftTriggerEffect == null && snap.RightTriggerEffect == null)
            return;

        if (snap.HasPacket)
        {
            TimeText = snap.Timestamp.ToString("HH:mm:ss.fff");
            RawInfoText = $"报源: {snap.Source}  |  Report ID: 0x{snap.ReportId:X2}  |  Seq: {snap.SeqNo}  |  长度: {snap.RawBytes.Length} 字节";
            RawHex = ToHex(snap.RawBytes);
            CrcText = $"CRC32: {(snap.CrcValid ? "通过" : "校验失败")}";
        }

        if (snap.LeftTriggerEffect != null)
        {
            LeftHex = ToHex(snap.LeftTriggerEffect);
            LeftEffectText = DescribeTriggerEffect(snap.LeftTriggerEffect);
        }

        if (snap.RightTriggerEffect != null)
        {
            RightHex = ToHex(snap.RightTriggerEffect);
            RightEffectText = DescribeTriggerEffect(snap.RightTriggerEffect);
        }
    }

    private static string ToHex(byte[] data)
    {
        if (data.Length == 0) return "(空)";
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>把 DualSense 自适应扳机效果块的首字节模式翻译成可读描述。</summary>
    private static string DescribeTriggerEffect(byte[] effect)
    {
        if (effect.Length == 0) return "无数据";
        byte mode = effect[0];
        bool has = effect.Length >= 4;

        return mode switch
        {
            0x00 => "Off（无效果 / 无阻力）",
            0x01 when has => $"Feedback（刚性）起始={effect[1]} 力度={effect[2]}",
            0x02 when has => $"Weapon（段落）起始={effect[1]} 结束={effect[2]} 力度={effect[3]}",
            0x05 when effect.Length >= 5 => $"Bow（弓）起始={effect[1]} 结束={effect[2]} 力度={effect[3]} 回弹={effect[4]}",
            0x06 when effect.Length >= 6 => $"Galloping（马蹄）起始={effect[1]} 结束={effect[2]} 首足={effect[3]} 次足={effect[4]} 频率={effect[5]}",
            0x21 => "Feedback（分段）",
            0x22 => "Weapon（分段）",
            0x23 => "Vibration（分段）",
            0x25 => "Bow（分段）",
            0x26 => "Galloping（分段）",
            0x27 => "Machine（分段）",
            0x81 => "Slope Feedback（斜坡刚性）",
            0x82 => "Slope Weapon（斜坡段落）",
            0x83 => "Slope Vibration（斜坡振动）",
            _ => $"未知模式 0x{mode:X2}",
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
