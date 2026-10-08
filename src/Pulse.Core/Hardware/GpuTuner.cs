using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Core.Diagnostics;
using Pulse.Core.Monitoring;

namespace Pulse.Core.Hardware;

public sealed record TuneStep(int Core, int Mem, double Fps, double CoreMhz, double MemMhz, double MaxTempC, double PowerW, bool Stable, string Note);
public sealed record TuneResult(IReadOnlyList<TuneStep> Steps, int BestCore, int BestMem, string Summary);

/// <summary>
/// Ekran kartı hızlandırmanı kendisi arayarak bulur: bir GPU yükü (Edge'de ağır bir WebGL sayfası) çalıştırır, ofseti kademe kademe
/// artırır; her kademede kare hızını, saatleri, sıcaklığı ve sürücü hatalarını ölçer. Kare hızı artmayı bırakınca, sıcaklık sınırı
/// aşılınca ya da sürücü hata verince durur ve en iyi kararlı kademeyi seçer. Bittiğinde ekran kartı fabrika hızına döner.
/// Görüntü bozulması (artifact) görülemez; bu yüzden tavan düşük tutulur ve sonuç yalnızca bir öneridir.
/// </summary>
public static class GpuTuner
{
    private static readonly (int Core, int Mem)[] Ladder = [(0, 0), (50, 250), (100, 500), (150, 700)];
    private const double HotLimitC = 84;
    private const double AbortC = 87;
    private const double MinGainPercent = 1.5;

    public static async Task<TuneResult> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<TuneStep>();
        var nvml = Nvml.TryOpen();
        if (nvml is null) return new(steps, 0, 0, "NVIDIA ekran kartı bulunamadı.");
        if (GpuOverclock.Read() is not { Editable: true }) return new(steps, 0, 0, "Bu ekran kartında hız ayarı kapalı.");

        var edge = FindEdge();
        if (edge is null) return new(steps, 0, 0, "Yük üretmek için Microsoft Edge bulunamadı.");

        var temp = Path.Combine(Path.GetTempPath(), "pulse-gputune");
        Directory.CreateDirectory(temp);
        var page = Path.Combine(temp, "yuk.html");
        File.WriteAllText(page, LoadPage);
        var tuneStart = DateTime.Now;

        var addedPref = SetEdgeGpuPreference(edge);
        Process? proc = null;
        try
        {
            progress?.Report("Yük sayfası açılıyor…");
            proc = Process.Start(new ProcessStartInfo(edge, $"--user-data-dir=\"{Path.Combine(temp, "profil")}\" --no-first-run --disable-features=CalculateNativeWinOcclusion --app=\"file:///{page.Replace('\\', '/')}\" --window-position=40,40 --window-size=1100,700") { UseShellExecute = false });
            await Task.Delay(9000, ct);   // pencere + sayfanın yükü kendi ayarlaması (ısınma)

            double bestFps = 0;
            var best = Ladder[0];
            foreach (var (core, mem) in Ladder)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(core == 0 ? "Fabrika hızı ölçülüyor…" : $"Deneniyor: çekirdek +{core}, bellek +{mem} MHz…");
                var apply = GpuOverclock.Apply(core, mem);
                if (!apply.Ok || !apply.Verified) { steps.Add(new(core, mem, 0, 0, 0, 0, 0, false, "Sürücü ofseti kabul etmedi")); break; }
                await Task.Delay(4000, ct);

                var step = await Measure(nvml, core, mem, tuneStart, ct);
                steps.Add(step);
                Journal.Write($"GPU ayar denemesi {core}/{mem}: fps {step.Fps:0.0}, çekirdek {step.CoreMhz:0}, bellek {step.MemMhz:0}, ısı {step.MaxTempC:0}, kararlı {step.Stable} ({step.Note})");
                if (!step.Stable) break;

                var gain = bestFps > 0 ? (step.Fps - bestFps) / bestFps * 100 : 0;
                if (core == 0) { bestFps = step.Fps; continue; }
                if (gain < MinGainPercent) { steps[^1] = step with { Note = $"Kazanç %{gain:0.0}, artık değmiyor" }; break; }
                bestFps = step.Fps;
                best = (core, mem);
            }

            var summary = best == (0, 0)
                ? "Hız aşırtma kayda değer kazanç vermedi (güç ya da ısı sınırı). Fabrika hızı en iyisi."
                : $"En iyi kararlı ayar: çekirdek +{best.Core}, bellek +{best.Mem} MHz. Fabrika hızına göre kare hızı %{(bestFps / Math.Max(steps[0].Fps, 0.1) - 1) * 100:0.0} daha yüksek.";
            return new(steps, best.Core, best.Mem, summary);
        }
        finally
        {
            GpuOverclock.Reset();
            try { if (proc is { HasExited: false }) { proc.CloseMainWindow(); await Task.Delay(1500); } } catch { }
            foreach (var p in FindEdgeProcesses(temp)) { try { p.Kill(); } catch { } }
            if (addedPref) RemoveEdgeGpuPreference(edge);
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static async Task<TuneStep> Measure(Nvml nvml, int core, int mem, DateTime since, CancellationToken ct)
    {
        var fps = new List<double>();
        double coreSum = 0, memSum = 0, powerSum = 0, maxTemp = 0;
        var n = 0;
        var hotSamples = 0;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, ct);
            var r = nvml.Read();
            n++;
            coreSum += r.CoreMhz ?? 0; memSum += r.MemMhz ?? 0; powerSum += r.PowerW ?? 0;
            maxTemp = Math.Max(maxTemp, r.TempC ?? 0);
            if ((r.ThrottleReasons & (0x20 | 0x40 | 0x8 | 0x80)) != 0) hotSamples++;
            if (ReadFps() is { } f) fps.Add(f);
            if (maxTemp >= AbortC) break;
        }

        var note = "";
        var stable = true;
        if (HadDriverReset(since)) { stable = false; note = "Ekran sürücüsü hata verdi (TDR)"; }
        else if (TitleLost()) { stable = false; note = "Grafik kartı yanıt vermedi"; }
        else if (fps.Count < 5) { stable = false; note = "Kare hızı ölçülemedi"; }
        else if (maxTemp >= HotLimitC) { stable = false; note = $"Sıcaklık sınırı aşıldı ({maxTemp:0} °C)"; }
        else if (hotSamples > n * 0.3) { stable = false; note = "Isı ya da donanım kısıtlaması sürekli devrede"; }

        var avgFps = fps.Count > 0 ? fps.Skip(fps.Count / 4).Average() : 0;   // ilk çeyrek ısınma, atılır
        return new(core, mem, avgFps, coreSum / Math.Max(n, 1), memSum / Math.Max(n, 1), maxTemp, powerSum / Math.Max(n, 1), stable, note);
    }

    // ---- Yük sayfası (kare hızını pencere başlığına yazar) -------------------

    private static double? ReadFps()
    {
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            string t;
            try { t = p.MainWindowTitle; } catch { continue; }
            if (t.StartsWith("F:") && double.TryParse(t[2..].Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
        }
        return null;
    }

    private static bool TitleLost() => Process.GetProcessesByName("msedge").Any(p => { try { return p.MainWindowTitle.StartsWith("LOST"); } catch { return false; } });

    private static bool HadDriverReset(DateTime since)
    {
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[(EventID=4101) and TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000Z}']]]");
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            return reader.ReadEvent() is not null;
        }
        catch { return false; }
    }

    private static string? FindEdge()
    {
        string[] c = [@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"];
        return c.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<Process> FindEdgeProcesses(string profileDir)
    {
        var list = new List<Process>();
        foreach (var row in Health.WmiReader.Query(@"root\cimv2", "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe'"))
        {
            if (!row.TryGetValue("CommandLine", out var cl) || cl?.ToString()?.Contains("pulse-gputune", StringComparison.OrdinalIgnoreCase) != true) continue;
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
