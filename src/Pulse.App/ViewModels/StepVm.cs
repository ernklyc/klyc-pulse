using System.Windows.Media;
using Pulse.Core.Modes;

namespace Pulse.App.ViewModels;

/// <summary>Bir doğrulama adımının listedeki satırı.</summary>
public sealed class StepVm
{
    public StepVm(StepResult r)
    {
        Name = r.Name;
        Detail = r.Detail;
        (Glyph, Brush) = r.Status switch
        {
            StepStatus.Verified => ("", Resource("GoodBrush")),
            StepStatus.Applied => ("", Resource("InfoBrush")),
            StepStatus.Warning => ("", Resource("WarnBrush")),
            StepStatus.Failed => ("", Resource("BadBrush")),
            _ => ("", Resource("MutedBrush")),
        };
    }

    public string Name { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public Brush Brush { get; }

    private static Brush Resource(string key) =>
        (Brush)System.Windows.Application.Current.FindResource(key);
}
