using System.Diagnostics;
using Microsoft.Win32;

namespace Pulse.Core.Hardware;

/// <summary>
/// Ekran kartına gerçek yük bindirir: Edge'de ağır bir WebGL sayfası açar (kare hızını pencere başlığına yazar) ve Edge'in NVIDIA kartını
/// kullanması için geçici bir Windows grafik tercihi ekler. Kapatınca (Dispose) pencereyi, geçici dosyaları ve tercihi geri alır.
/// Ekran kartı hız denemesi (<see cref="GpuTuner"/>) ve ısı denemesi (<see cref="HeatBenchmark"/>) aynı yükü kullanır.
/// </summary>
public sealed class GpuLoad : IDisposable
{
    private const string TempName = "pulse-gputune";

    private readonly string _edge;
    private readonly string _temp;
    private readonly bool _addedPref;
    private Process? _proc;

    private GpuLoad(string edge, string temp, bool addedPref, Process? proc)
    {
        _edge = edge; _temp = temp; _addedPref = addedPref; _proc = proc;
    }

    /// <summary>Yükü başlatır. Edge bulunamazsa null döner. Sayfanın kendini ayarlaması için ~9 sn ısınma beklenmelidir.</summary>
    public static GpuLoad? Start()
    {
        var edge = FindEdge();
        if (edge is null) return null;
        var temp = Path.Combine(Path.GetTempPath(), TempName);
        Directory.CreateDirectory(temp);
        var page = Path.Combine(temp, "yuk.html");
        File.WriteAllText(page, LoadPage);
        var addedPref = SetEdgeGpuPreference(edge);
        try
        {
            var proc = Process.Start(new ProcessStartInfo(edge, $"--user-data-dir=\"{Path.Combine(temp, "profil")}\" --no-first-run --disable-features=CalculateNativeWinOcclusion --app=\"file:///{page.Replace('\\', '/')}\" --window-position=40,40 --window-size=1100,700") { UseShellExecute = false });
            return new GpuLoad(edge, temp, addedPref, proc);
        }
        catch
        {
            if (addedPref) RemoveEdgeGpuPreference(edge);
            try { Directory.Delete(temp, true); } catch { }
            throw;
        }
    }

    /// <summary>Yük sayfasının son bildirdiği kare/sn; henüz yoksa null.</summary>
    public static double? ReadFps()
    {
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            string t;
            try { t = p.MainWindowTitle; } catch { continue; }
            if (t.StartsWith("F:") && double.TryParse(t[2..].Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
        }
        return null;
    }

    /// <summary>Ekran kartı bağlamı kaybolduysa (sürücü sıfırlandı) true.</summary>
    public static bool Lost() => Process.GetProcessesByName("msedge").Any(p => { try { return p.MainWindowTitle.StartsWith("LOST"); } catch { return false; } });

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) { _proc.CloseMainWindow(); Thread.Sleep(1500); } } catch { }
        foreach (var p in FindEdgeProcesses()) { try { p.Kill(); } catch { } }
        if (_addedPref) RemoveEdgeGpuPreference(_edge);
        try { Directory.Delete(_temp, true); } catch { }
        _proc = null;
    }

    private static string? FindEdge()
    {
        string[] c = [@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"];
        return c.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<Process> FindEdgeProcesses()
    {
        var list = new List<Process>();
        foreach (var row in Health.WmiReader.Query(@"root\cimv2", "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe'"))
        {
            if (!row.TryGetValue("CommandLine", out var cl) || cl?.ToString()?.Contains(TempName, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (Health.WmiReader.Get<int>(row, "ProcessId") is { } pid) { try { list.Add(Process.GetProcessById(pid)); } catch { } }
        }
        return list;
    }

    // Edge'in yüksek performanslı ekran kartını (NVIDIA) kullanması için geçici Windows grafik tercihi.
    private const string PrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static bool SetEdgeGpuPreference(string edge)
    {
        using var k = Registry.CurrentUser.CreateSubKey(PrefKey, true);
        if (k.GetValue(edge) is not null) return false;
        k.SetValue(edge, "GpuPreference=2;", RegistryValueKind.String);
        return true;
    }

    private static void RemoveEdgeGpuPreference(string edge)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(PrefKey, true); k?.DeleteValue(edge, false); } catch { }
    }

    private const string LoadPage = """
        <!doctype html><meta charset="utf-8"><title>F:0</title>
        <style>html,body{margin:0;background:#000;overflow:hidden}canvas{display:block;width:100vw;height:100vh}</style>
        <canvas id="c"></canvas>
        <script>
        const c=document.getElementById('c');c.width=1024;c.height=640;
        const gl=c.getContext('webgl',{powerPreference:'high-performance',antialias:false});
        c.addEventListener('webglcontextlost',e=>{e.preventDefault();document.title='LOST';});
        let iters=200,locked=false;
        const vs='attribute vec2 p;void main(){gl_Position=vec4(p,0.,1.);}';
        const fs=n=>`precision highp float;uniform float t;uniform vec2 r;
        void main(){vec2 uv=(gl_FragCoord.xy/r-.5)*vec2(r.x/r.y,1.)*2.5;uv+=vec2(sin(t*.3),cos(t*.2))*.2;
        vec2 z=vec2(0.);float s=0.;for(int i=0;i<${n};i++){z=vec2(z.x*z.x-z.y*z.y,2.*z.x*z.y)+uv*.6-vec2(.5,0.);s+=sin(dot(z,z)+t);if(dot(z,z)>1e6)break;}
        gl_FragColor=vec4(.5+.5*sin(s*.01),.5+.5*cos(s*.013),.5+.5*sin(s*.007),1.);}`;
        let prog,U;
        function build(n){const mk=(t,s)=>{const o=gl.createShader(t);gl.shaderSource(o,s);gl.compileShader(o);return o};
          const p=gl.createProgram();gl.attachShader(p,mk(gl.VERTEX_SHADER,vs));gl.attachShader(p,mk(gl.FRAGMENT_SHADER,fs(n)));gl.linkProgram(p);gl.useProgram(p);
          const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,3,-1,-1,3]),gl.STATIC_DRAW);
          const l=gl.getAttribLocation(p,'p');gl.enableVertexAttribArray(l);gl.vertexAttribPointer(l,2,gl.FLOAT,false,0,0);
          prog=p;U={t:gl.getUniformLocation(p,'t'),r:gl.getUniformLocation(p,'r')};}
        build(iters);
        let frames=0,last=performance.now(),t0=last;
        function loop(now){
          gl.uniform1f(U.t,(now-t0)/1000);gl.uniform2f(U.r,c.width,c.height);gl.drawArrays(gl.TRIANGLES,0,3);gl.finish&&gl.finish();frames++;
          if(now-last>=1000){const fps=frames*1000/(now-last);document.title='F:'+fps.toFixed(1);
            if(!locked){if(fps>45&&iters<4000){iters=Math.min(4000,Math.round(iters*1.5));build(iters);}else if(now-t0>14000){locked=true;}}
            frames=0;last=now;}
          requestAnimationFrame(loop);}
        requestAnimationFrame(loop);
        </script>
        """;
}
