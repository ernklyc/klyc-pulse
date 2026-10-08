using System.Runtime.InteropServices;

namespace Pulse.Core.Health;

/// <summary>
/// Salt okunur WMI sorguları (SWbem COM). Yazma/yöntem çağrısı yapmaz: o yol sağlayıcıyı kilitleyebiliyor.
/// COM nesneleri her sorguda açıkça serbest bırakılır.
/// </summary>
public static class WmiReader
{
    public static IReadOnlyList<Dictionary<string, object?>> Query(string wmiNamespace, string query)
    {
        var rows = new List<Dictionary<string, object?>>();
        object? locator = null, svc = null, set = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            svc = ((dynamic)locator!).ConnectServer(".", wmiNamespace);
            set = ((dynamic)svc!).ExecQuery(query);
            foreach (object item in (System.Collections.IEnumerable)set!)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                object? props = null;
                try
                {
                    props = ((dynamic)item).Properties_;
                    foreach (object p in (System.Collections.IEnumerable)props!)
                    {
                        try { row[(string)((dynamic)p).Name] = ((dynamic)p).Value; } catch { }
                        Release(p);
                    }
                }
                finally { Release(props); Release(item); }
                rows.Add(row);
            }
        }
        catch { /* sınıf yok ya da erişim yok */ }
        finally { Release(set); Release(svc); Release(locator); }
        return rows;
    }

    public static T? Get<T>(Dictionary<string, object?> row, string key) where T : struct
    {
        if (!row.TryGetValue(key, out var v) || v is null) return null;
        try { return (T)Convert.ChangeType(v, typeof(T)); } catch { return null; }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
