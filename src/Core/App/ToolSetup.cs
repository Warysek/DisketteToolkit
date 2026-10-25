using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Drives.Gw;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Core.App;

/// <summary>Kam program ukládá stažené doplňky: přednostně do své vlastní složky.</summary>
public static class AppPaths
{
    public const string AppFolderName = "DisketteToolkit";

    /// <summary>Verze programu („0.8“, „0.8.1“) — jediný zdroj je &lt;Version&gt; v Directory.Build.props.</summary>
    public static string Version
    {
        get
        {
            var v = typeof(AppPaths).Assembly.GetName().Version!;
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }

    /// <summary>Podsložka vedle programu, když je zapisovatelná; jinak %LOCALAPPDATA%\DisketteToolkit\…</summary>
    public static string ToolDir(string name)
    {
        string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        if (CanWrite(AppDomain.CurrentDomain.BaseDirectory)) return beside;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, name);
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Průběh stahování: staženo / celkem bajtů (celkem 0 = neznámé).</summary>
public delegate void DownloadProgress(long done, long total);

/// <summary>Stahování přes HTTPS (GitHub, simonowen.com).</summary>
internal static class Http
{
    private const string Agent = "DisketteToolkit (floppy tool for Atari ST and other 3.5in systems)";

    static Http()
    {
        // .NET Framework 4.8 na starších Windows jinak nemusí nabídnout TLS 1.2.
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
    }

    public static string GetString(string url, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        Copy(url, ms, null, ct, "application/vnd.github+json");
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void Download(string url, string path, DownloadProgress? progress, CancellationToken ct)
    {
        string tmp = path + ".part";
        try
        {
            using (var fs = File.Create(tmp)) Copy(url, fs, progress, ct, null);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static void Copy(string url, Stream target, DownloadProgress? progress, CancellationToken ct, string? accept)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = Agent;
        req.AllowAutoRedirect = true;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 30000;
        if (accept != null) req.Accept = accept;
        using var reg = ct.Register(() => { try { req.Abort(); } catch { } });
        try
        {
            using var rsp = (HttpWebResponse)req.GetResponse();
            long total = rsp.ContentLength > 0 ? rsp.ContentLength : 0;
            using var s = rsp.GetResponseStream()!;
            var buf = new byte[65536];
            long done = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buf, 0, n);
                done += n;
                progress?.Invoke(done, total);
            }
        }
        catch (WebException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (WebException e)
        {
            throw new OperationException(Tr.F("Stažení z {0} selhalo: {1}", new Uri(url).Host, e.Message) + " " +
                                         Tr.T("Zkontroluj připojení k internetu (a jestli ho nebrání firewall nebo antivir)."));
        }
    }
}

/// <summary>Vydání programu na GitHubu.</summary>
public sealed record ReleaseInfo(string Tag, string Version, string AssetName, string AssetUrl, long Size);

/// <summary>
/// Greaseweazle Host Tools (gw.exe): stažení nejnovější verze z GitHubu a rozbalení do
/// složky „greaseweazle“ vedle programu (viz AppPaths.ToolDir). Program je volné dílo (public domain / Unlicense).
/// Windows balík obsahuje i CAPSImg.dll, takže funguje i zápis .IPF.
/// </summary>
public static class GreaseweazleSetup
{
    /// <summary>Adresa GitHub API (testy ji přesměrují na místní server).</summary>
    public static string ApiUrl { get; set; } = "https://api.github.com/repos/keirf/greaseweazle/releases/latest";

    /// <summary>Složka „greaseweazle“ vedle programu (když tam nejde zapisovat, tak v %LOCALAPPDATA%).</summary>
    public static string InstallDir => AppPaths.ToolDir("greaseweazle");

    private const string VersionFile = "afloppy-version.txt";

    [DataContract]
    private sealed class GhRelease
    {
        [DataMember(Name = "tag_name")] public string? Tag { get; set; }
        [DataMember(Name = "assets")] public GhAsset[]? Assets { get; set; }
    }

    [DataContract]
    private sealed class GhAsset
    {
        [DataMember(Name = "name")] public string? Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string? Url { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    /// <summary>Z odpovědi GitHub API vybere balík pro Windows (64bitový, když je systém 64bitový).</summary>
    public static ReleaseInfo ParseRelease(string json, bool win64)
    {
        GhRelease? r;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            r = new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(ms) as GhRelease;
        if (r?.Tag == null || r.Assets == null) throw new OperationException(Tr.T("GitHub vrátil nečekanou odpověď — zkus to později."));
        string want = win64 ? "-win64.zip" : "-win32.zip";
        var asset = r.Assets.FirstOrDefault(a => a.Name != null && a.Name.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                    ?? r.Assets.FirstOrDefault(a => a.Name != null && Regex.IsMatch(a.Name, @"-win(32)?\.zip$", RegexOptions.IgnoreCase));
        if (asset?.Url == null || asset.Name == null)
            throw new OperationException(Tr.F("Ve vydání {0} jsem nenašel balík pro Windows.", r.Tag));
        return new ReleaseInfo(r.Tag, r.Tag.TrimStart('v', 'V'), asset.Name, asset.Url, asset.Size);
    }

    /// <summary>
    /// 64bitový balík gw jen na 64bitových Windows 8 a novějších — na Windows 7 běží jen 32bitový
    /// (hlášení testera; i na 64bitových Windows 7). Windows 7 = verze 6.1.
    /// </summary>
    public static ReleaseInfo GetLatest(CancellationToken ct = default) =>
        ParseRelease(Http.GetString(ApiUrl, ct), Environment.Is64BitOperatingSystem && Environment.OSVersion.Version >= new Version(6, 2));

    /// <summary>Verze gw.exe, pokud se dá zjistit (náš zápis po instalaci, nebo název složky greaseweazle-1.23).</summary>
    public static string? VersionOf(string gwPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(gwPath)) ?? "";
            string vf = Path.Combine(dir, VersionFile);
            if (File.Exists(vf)) return File.ReadAllText(vf).Trim();
            var m = Regex.Match(Path.GetFileName(dir), @"greaseweazle-v?(\d+\.\d+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>Porovná verze „1.9“ a „1.23“ číselně.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] P(string s) => Regex.Matches(s, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();
        var x = P(a); var y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Stáhne a nainstaluje vydání; vrací cestu ke gw.exe.</summary>
    public static string Install(ReleaseInfo r, DownloadProgress? progress = null, CancellationToken ct = default, string? installDir = null)
    {
        string dir = installDir ?? InstallDir;
        string parent = Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(parent);
        string zip = Path.Combine(parent, r.AssetName);
        Http.Download(r.AssetUrl, zip, progress, ct);
        try { return InstallZip(zip, r.Version, dir); }
        finally { try { File.Delete(zip); } catch { } }
    }

    /// <summary>Rozbalí balík tak, aby gw.exe leželo přímo ve složce; předchozí verzi nahradí.</summary>
    public static string InstallZip(string zipPath, string version, string dir)
    {
        string fresh = dir + ".new", old = dir + ".old";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        ZipFile.ExtractToDirectory(zipPath, fresh);

        // Balík má obvykle jednu složku greaseweazle-1.23/ — obsah přesuneme o úroveň výš.
        string? gw = Directory.GetFiles(fresh, "gw.exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
        if (gw == null)
        {
            Directory.Delete(fresh, true);
            throw new OperationException(Tr.T("Stažený balík neobsahuje gw.exe."));
        }
        string root = Path.GetDirectoryName(gw)!;
        if (!string.Equals(root.TrimEnd('\\', '/'), fresh.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            string tmp = fresh + ".x";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.Move(root, tmp);
            Directory.Delete(fresh, true);
            Directory.Move(tmp, fresh);
        }
        File.WriteAllText(Path.Combine(fresh, VersionFile), version);

        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(dir))
        {
            try { Directory.Move(dir, old); }
            catch (IOException) { throw new OperationException(Tr.T("Stará verze gw.exe je právě spuštěná — dokonči operaci s Greaseweazle a zkus to znovu.")); }
        }
        Directory.Move(fresh, dir);
        try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        return Path.Combine(dir, "gw.exe");
    }
}

/// <summary>
/// Ovladač fdrawcmd.sys (Simon Owen, MIT licence): stažení oficiálního podepsaného instalátoru FdInstall.exe
/// ze simonowen.com a jeho spuštění. Ovladač sám si Windows nainstalují jen z podepsaného balíku — proto
/// nestavíme vlastní verzi, ale používáme autorův instalátor.
/// </summary>
public static class FdrawcmdSetup
{
    public static string DownloadUrl { get; set; } = "https://simonowen.com/fdrawcmd/FdInstall.exe";

    public static string Download(DownloadProgress? progress = null, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DisketteToolkit");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "FdInstall.exe");
        Http.Download(DownloadUrl, path, progress, ct);
        return path;
    }

    /// <summary>Kdo soubor digitálně podepsal (null = nepodepsaný nebo to nejde zjistit).</summary>
    public static string? Signer(string file)
    {
        try
        {
            var cert = X509Certificate.CreateFromSignedFile(file);
            return cert.Subject;
        }
        catch { return null; }
    }

    /// <summary>Spustí instalátor a počká na něj (instalátor má vlastní okno se třemi kliknutími).</summary>
    public static int RunInstaller(string path, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) ?? "" };
        using var p = Process.Start(psi) ?? throw new OperationException(Tr.T("Instalátor se nepodařilo spustit."));
        while (!p.WaitForExit(300)) ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}
