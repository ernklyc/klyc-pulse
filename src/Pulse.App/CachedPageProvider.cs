using Wpf.Ui.Abstractions;

namespace Pulse.App;

/// <summary>
/// Her sayfadan tek bir örnek tutar. Eskiden sekme değiştirince sayfa yeniden oluşuyordu: süren bir işlem (güncelleme, disk analizi, temizlik...)
/// ekranda durmuş gibi görünüyor ve geri dönünce sonucu kayboluyordu. Şimdi sayfa ve içindeki durum, sekmeler arası gezerken korunur.
/// </summary>
public sealed class CachedPageProvider : INavigationViewPageProvider
{
    private readonly Dictionary<Type, object> _pages = new();

    public object? GetPage(Type pageType)
    {
        if (!_pages.TryGetValue(pageType, out var page)) _pages[pageType] = page = Activator.CreateInstance(pageType)!;
        return page;
    }
}
