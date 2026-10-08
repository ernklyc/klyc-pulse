namespace Pulse.Core.Diagnostics;

/// <summary>Yapılan her işlemin kalıcı günlüğü (%LOCALAPPDATA%\Pulse\logs).</summary>
public static class Journal
{
    private static readonly object Gate = new();

    public static string Directory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "logs");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var file = System.IO.Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.log");
                // Başka bir Pulse süreci (örn. yönetici kopyası) aynı dosyaya yazıyor olabilir: paylaşımlı aç, satır kaybolmasın.
                var bytes = System.Text.Encoding.UTF8.GetBytes($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Günlük yazılamıyorsa uygulama çalışmaya devam eder.
        }
    }
}
