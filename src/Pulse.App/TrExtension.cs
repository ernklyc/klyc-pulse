using System.Windows.Markup;
using Pulse.Core.Localization;

namespace Pulse.App;

/// <summary>XAML'de tetikleyici/ayarlayıcı gibi sonradan atanan metinleri çevirir: <c>Value="{app:Tr Yeniden uygula}"</c>. Dil Türkçeyse metin aynen kalır.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension(string text) => Text = text;
    public string Text { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
