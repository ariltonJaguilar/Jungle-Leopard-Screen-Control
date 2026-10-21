using System.Windows.Threading;

namespace JungleLeopardPoc;

// Small state holders used by the LCD service. They carry values shared with
// the WPF views; they are not visual controls.
internal sealed class BackendTextState(string text = "")
{
    public string Text { get; set; } = text;
}

internal sealed class BackendToggleState
{
    public bool Checked { get; set; }
    public bool Enabled { get; set; } = true;
}

internal sealed class BackendActionState
{
    public bool Enabled { get; set; } = true;
}

internal sealed class BackendProgressState
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Value { get; set; }
}

internal sealed class BackendChoiceState
{
    public List<object> Items { get; } = [];
    public int SelectedIndex { get; set; } = -1;
    public bool Enabled { get; set; } = true;
    public object? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
    public string? GetItemText(object? item) => item?.ToString();
    public void BeginUpdate() { }
    public void EndUpdate() { }
}

internal sealed class BackendNumberState
{
    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; } = 100;
    public decimal Value { get; set; }
    public bool Enabled { get; set; } = true;
}

internal sealed class BackendListState
{
    public List<object> Items { get; } = [];
    public int SelectedIndex { get; set; } = -1;
    public object? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;
    public void BeginUpdate() { }
    public void EndUpdate() { }
}

internal sealed class BackendPreviewState
{
    public System.Drawing.Image? Image { get; set; }
    public bool Visible { get; set; }
}

internal sealed class BackendTimer
{
    private readonly DispatcherTimer _timer = new();
    public int Interval
    {
        get => (int)_timer.Interval.TotalMilliseconds;
        set => _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, value));
    }
    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}

