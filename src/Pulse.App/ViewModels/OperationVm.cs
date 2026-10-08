using CommunityToolkit.Mvvm.ComponentModel;

namespace Pulse.App.ViewModels;

/// <summary>
/// Bir uzun işlemin görünür durumu (BusyBar'a bağlanır). Begin ile başlar, Step ile ilerler, End ile biter.
/// Toplam adım biliniyorsa çubuk dolar, bilinmiyorsa kayar.
/// </summary>
public partial class OperationVm : ObservableObject
{
    private int _total;
    private int _done;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private double _fraction = -1;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _counter = "";

    /// <summary>total 0 ise adım sayısı bilinmiyor demektir.</summary>
    public void Begin(string text, int total = 0)
    {
        _total = total;
        _done = 0;
        Text = text;
        Fraction = total > 0 ? 0 : -1;
        Counter = total > 0 ? $"0/{total}" : "";
        IsActive = true;
    }

    /// <summary>Yeni bir adıma geçildi. Adım başlarken çağrılır; çubuk önceki adımların oranını gösterir.</summary>
    public void Step(string text)
    {
        Text = text;
        if (_total <= 0) return;
        Fraction = (double)_done / _total;
        _done++;
        Counter = $"{Math.Min(_done, _total)}/{_total}";
    }

    public void Message(string text) => Text = text;

    public void End()
    {
        if (_total > 0) Fraction = 1;
        IsActive = false;
    }
}
