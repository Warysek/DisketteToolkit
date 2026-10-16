using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AtariFloppy.Core;
using AtariFloppy.Core.Analysis;
using AtariFloppy.Core.App;
using AtariFloppy.Core.Drives.Diag;
using AtariFloppy.Core.Drives;
using AtariFloppy.Core.Formats;
using AtariFloppy.Core.Fs;
using AtariFloppy.Core.I18n;

namespace AtariFloppy.Tests;

internal static class Tests
{
    private static int _pass, _fail;
    private static string _tmp = "";

    private static int Main(string[] args)
    {
        _tmp = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "afloppy-tests");
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
        Directory.CreateDirectory(_tmp);

        foreach (var m in typeof(Tests).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                                        .Where(m => m.Name.StartsWith("T_")))
        {
            try { m.Invoke(null, null); _pass++; Console.WriteLine("  ok    " + m.Name); }
            catch (System.Reflection.TargetInvocationException e)
            {
                _fail++;
                Console.WriteLine("  FAIL  " + m.Name + ": " + e.InnerException?.Message);
                Console.WriteLine(e.InnerException?.StackTrace?.Split('\n').FirstOrDefault());
            }
        }
        Console.WriteLine($"\n{_pass} ok, {_fail} selhalo");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ pomocné

    private static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }
    private static void Eq<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: čekal jsem {expected}, je {actual}");
    }

    private static string Sh(string exe, string args, out int code)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        code = p.ExitCode;
        return o;
    }

    private static DiskImage RandomImage(DiskGeometry g, int seed)
    {
        var img = new DiskImage(g);
        new Random(seed).NextBytes(img.Data);
        return img;
    }

    private static string Path_(string name) => Path.Combine(_tmp, name);

    // ------------------------------------------------------------------ formáty

    private static void T_Geometry_Basics()
    {
        Eq(737280L, DiskGeometry.Dd720.SizeBytes, "720K velikost");
        Eq(1474560L, DiskGeometry.Hd1440.SizeBytes, "1.44M velikost");
        Eq(9L, DiskGeometry.Dd720.Lba(0, 1), "LBA stopa 0 strana 1");
        Eq(18L, DiskGeometry.Dd720.Lba(1, 0), "LBA stopa 1 strana 0");
        Assert(DiskGeometry.TryParse("82x2x10", out var g) && g.TotalSectors == 1640, "parse 82x2x10");
        Assert(!DiskGeometry.TryParse("80x3x9", out _), "3 strany neplatné");
    }

    private static void T_St_GuessGeometry_FromSize()
    {
        (long size, string expect)[] cases =
        {
            (737280, "80x2x9"), (368640, "80x1x9"), (819200, "80x2x10"), (901120, "80x2x11"),
            (1474560, "80x2x18"), (829440, "81x2x10"), (839680, "82x2x10"), (746496, "81x2x9"),
        };
        foreach (var (size, expect) in cases)
        {
            var g = StFormat.GuessGeometry(new byte[size]); // bez BPB → podle velikosti
            Eq(expect, g?.Short, $"velikost {size}");
        }
    }

    private static void T_St_GuessGeometry_PrefersBootSector()
    {
        // 80×1×9 a 40×2×9 mají stejnou velikost — rozhodne boot sektor.
        var img = DiskFormatter.CreateBlank(new DiskGeometry(80, 1, 9), FormatStyle.Atari, null, new Random(1));
        var g = StFormat.GuessGeometry(img.Data);
        Eq("80x1x9", g?.Short, "SS podle BPB");
    }

    private static void T_Msa_RoundTrip_RandomAndRuns()
    {
        foreach (var geom in new[] { DiskGeometry.Dd720, new DiskGeometry(82, 2, 10), DiskGeometry.Ss360, new DiskGeometry(80, 2, 11) })
        {
            var img = RandomImage(geom, 42);
            // přidáme běhy, samotné E5, běhy E5 a prázdné stopy
            for (int i = 0; i < 3000; i++) img.Data[i] = 0;
            for (int i = 3000; i < 3100; i++) img.Data[i] = 0xE5;
            img.Data[4000] = 0xE5; img.Data[4002] = 0xE5; img.Data[4003] = 0xE5;
            Array.Clear(img.Data, geom.TrackSize * 5, geom.TrackSize * 3);
            var packed = MsaFormat.Save(img);
            var back = MsaFormat.Load(packed);
            Eq(geom, back.Geometry, "MSA geometrie");
            Assert(back.Data.SequenceEqual(img.Data), $"MSA data {geom.Short}");
        }
    }

    private static void T_Msa_CompatibleWithHatari()
    {
        // Náš MSA → hmsa (Hatari) → ST musí dát stejná data, a naopak.
        var img = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Atari, "HATARI", new Random(7));
        var rnd = new Random(5);
        for (int i = 20000; i < 60000; i++) img.Data[i] = (byte)(rnd.Next(4) == 0 ? 0xE5 : rnd.Next(3));
        // hmsa si výstupní jméno odvodí sám (x.msa → x.st), proto každý směr ve vlastní složce
        string d1 = Path_("hmsa1"), d2 = Path_("hmsa2");
        Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
        string msa = Path.Combine(d1, "ours.msa");
        File.WriteAllBytes(msa, MsaFormat.Save(img));
        Sh("hmsa", $"\"{msa}\"", out int code);
        _ = code; // hmsa vrací 1 i při úspěchu, rozhoduje obsah výsledku
        Assert(File.ReadAllBytes(Path.Combine(d1, "ours.st")).SequenceEqual(img.Data), "Hatari rozbalil náš MSA jinak");

        string st2 = Path.Combine(d2, "hat.st");
        File.WriteAllBytes(st2, img.Data);
        Sh("hmsa", $"\"{st2}\"", out code);

        var back = MsaFormat.Load(File.ReadAllBytes(Path.Combine(d2, "hat.msa")));
        Assert(back.Data.SequenceEqual(img.Data), "Hatari MSA jsme rozbalili jinak");
    }

    private static void T_Msa_RejectsTruncated()
    {
        var packed = MsaFormat.Save(RandomImage(DiskGeometry.Dd720, 1));
        var cut = packed.Take(packed.Length - 100).ToArray();
        try { MsaFormat.Load(cut); throw new Exception("useknutý MSA prošel"); }
        catch (FormatException) { }
    }

    private static void T_Dim_RoundTrip()
    {
        var img = DiskFormatter.CreateBlank(new DiskGeometry(80, 2, 10), FormatStyle.Atari, null, new Random(2));
        var dim = DimFormat.Save(img);
        Eq(32 + 819200, dim.Length, "DIM délka");
        var back = DimFormat.Load(dim);
        Eq("80x2x10", back.Geometry.Short, "DIM geometrie");
        Assert(back.Data.SequenceEqual(img.Data), "DIM data");
    }

    private static void T_ImageFile_DetectForeignFormats()
    {
        Eq(ImageKind.Stx, ImageFile.Detect("x.bin", System.Text.Encoding.ASCII.GetBytes("RSY\0abcd")), "STX");
        Eq(ImageKind.Ipf, ImageFile.Detect("x.bin", System.Text.Encoding.ASCII.GetBytes("CAPS1234")), "IPF");
        Eq(ImageKind.Scp, ImageFile.Detect("x.scp", System.Text.Encoding.ASCII.GetBytes("SCPxxxx")), "SCP");
        Eq(ImageKind.Hfe, ImageFile.Detect("x.hfe", System.Text.Encoding.ASCII.GetBytes("HXCPICFE")), "HFE");
        string p = Path_("game.stx");
        File.WriteAllBytes(p, System.Text.Encoding.ASCII.GetBytes("RSY\0" + new string('x', 100)));
        var l = ImageFile.Load(p);
        Assert(l.Image == null && l.NotLoadedReason != null, "STX zatím jen rozpoznat");
        Eq(Verdict.NotWritable, ImageAnalysis.AssessForStandardDrive(l).Verdict, "STX verdikt");
    }

    // ------------------------------------------------------------------ boot sektor a FAT

    private static void T_Boot_Checksum()
    {
        var b = new byte[512];
        new Random(4).NextBytes(b);
        BootSector.MakeExecutable(b);
        Assert(new BootSector(b).IsExecutable, "MakeExecutable");
        BootSector.EnsureNotExecutable(b);
        Assert(!new BootSector(b).IsExecutable, "EnsureNotExecutable");
    }

    private static void T_Blank_AllStyles_ValidForMtools()
    {
        foreach (FormatStyle style in Enum.GetValues(typeof(FormatStyle)))
            foreach (var g in new[] { DiskGeometry.Dd720, DiskGeometry.Hd1440, new DiskGeometry(80, 2, 10), DiskGeometry.Ss360 })
            {
                var img = DiskFormatter.CreateBlank(g, style, "TEST", new Random(11));
                var bs = img.BootSector;
                Assert(bs.BpbLooksValid, $"{style} {g.Short} BPB");
                Assert(!bs.IsExecutable, $"{style} {g.Short} nesmí být spustitelný");
                Eq(g.TotalSectors, bs.TotalSectors, "celkem sektorů");
                if (style == FormatStyle.Atari && !g.IsHighDensity) Eq(5, bs.SectorsPerFat, "Atari SPF");
                if (style != FormatStyle.Atari) Assert(bs.WindowsLikelyReadable, $"{style} má být čitelný pro Windows");

                string f = Path_($"blank_{style}_{g.Short}.st");
                File.WriteAllBytes(f, img.Data);
                string o = Sh("mdir", $"-i \"{f}\" ::", out int code);
                Eq(0, code, $"mdir {style} {g.Short}: {o}");
                var v = Fat12Volume.TryOpen(img.Data, out _)!;
                Eq("TEST", v.GetVolumeLabel(), "jmenovka");
                Eq(0, v.UsedClusters, "prázdný disk");
            }
    }

    private static void T_Hybrid_HasUniqueAtariSerial()
    {
        var a = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Hybrid, null, new Random(1));
        var b = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Hybrid, null, new Random(2));
        Assert(a.BootSector.AtariSerial != b.BootSector.AtariSerial, "sériová čísla se musí lišit");
        Eq(0xEB, (int)a.Data[0], "PC skok");
    }

    private static void T_Fat_ReadsFilesWrittenByMtools()
    {
        var img = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Atari, null, new Random(12));
        string st = Path_("files.st");
        File.WriteAllBytes(st, img.Data);

        var rnd = new Random(8);
        var big = new byte[150_000]; rnd.NextBytes(big);
        var small = System.Text.Encoding.ASCII.GetBytes("Ahoj Atari!\r\n");
        File.WriteAllBytes(Path_("BIG.PRG"), big);
        File.WriteAllBytes(Path_("README.TXT"), small);
        Sh("mmd", $"-i \"{st}\" ::AUTO", out int c0); Eq(0, c0, "mmd");
        Sh("mcopy", $"-i \"{st}\" \"{Path_("BIG.PRG")}\" ::AUTO/BIG.PRG", out int c1); Eq(0, c1, "mcopy big");
        Sh("mcopy", $"-i \"{st}\" \"{Path_("README.TXT")}\" ::README.TXT", out int c2); Eq(0, c2, "mcopy small");

        var loaded = ImageFile.Load(st);
        var v = Fat12Volume.TryOpen(loaded.Image!.Data, out _)!;
        var all = v.ListAll();
        var bigE = all.Single(e => e.Path == "AUTO\\BIG.PRG");
        var smallE = all.Single(e => e.Path == "README.TXT");
        Assert(all.Any(e => e.Path == "AUTO" && e.IsDirectory), "adresář AUTO");
        Assert(v.ReadFile(bigE).SequenceEqual(big), "obsah BIG.PRG");
        Assert(v.ReadFile(smallE).SequenceEqual(small), "obsah README.TXT");
        Assert(v.UsedClusters >= 150_000 / 1024, "obsazené clustery");
    }

    private static void T_Assessment()
    {
        var ok = new LoadedImage { Kind = ImageKind.St, Image = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Atari) };
        Eq(Verdict.Writable, ImageAnalysis.AssessForStandardDrive(ok).Verdict, "720 zelená");
        var ten = new LoadedImage { Kind = ImageKind.Msa, Image = DiskFormatter.CreateBlank(new DiskGeometry(80, 2, 10), FormatStyle.Atari) };
        Eq(Verdict.ContentOnly, ImageAnalysis.AssessForStandardDrive(ten).Verdict, "10 sektorů s FAT žlutá");
        var raw10 = new LoadedImage { Kind = ImageKind.Msa, Image = RandomImage(new DiskGeometry(80, 2, 10), 3) };
        Eq(Verdict.NotWritable, ImageAnalysis.AssessForStandardDrive(raw10).Verdict, "10 sektorů bez FAT červená");
        var extra = new LoadedImage { Kind = ImageKind.St, Image = DiskFormatter.CreateBlank(new DiskGeometry(82, 2, 9), FormatStyle.Atari) };
        Assert(ImageAnalysis.AssessForStandardDrive(extra).Verdict != Verdict.Writable, "82 stop není zelená");
    }

    // ------------------------------------------------------------------ operace s mechanikou (simulace)

    private static void T_Read_Standard720()
    {
        var src = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc, null, new Random(1));
        for (int i = 20 * 512; i < src.Data.Length; i++) src.Data[i] = (byte)(i * 7);
        var dev = new MemoryDevice(src);
        var r = new OperationResult();
        var img = DiskOperations.ReadDisk(dev, SideMode.Auto, r);
        Eq("80x2x9", img.Geometry.Short, "geometrie");
        Assert(img.Data.SequenceEqual(src.Data), "data");
        Assert(r.Success, "bez chyb");
    }

    private static void T_Read_SingleSidedByBpb()
    {
        var ss = DiskFormatter.CreateBlank(DiskGeometry.Ss360, FormatStyle.Atari, null, new Random(1));
        for (int i = 10 * 512; i < ss.Data.Length; i++) ss.Data[i] = (byte)(i * 3);
        // Na fyzické DS disketě leží SS data jen na straně 0.
        var dev = new MemoryDevice { Formatted = DiskGeometry.Dd720 };
        for (int t = 0; t < 80; t++)
            Buffer.BlockCopy(ss.Data, (int)ss.Geometry.Offset(t, 0), dev.Disk, (int)DiskGeometry.Dd720.Offset(t, 0), 9 * 512);
        var r = new OperationResult();
        var img = DiskOperations.ReadDisk(dev, SideMode.Auto, r);
        Eq("80x1x9", img.Geometry.Short, "SS geometrie");
        Assert(img.Data.SequenceEqual(ss.Data), "SS data");
    }

    private static void T_Read_BadAndFlakySectors()
    {
        var src = RandomImage(DiskGeometry.Dd720, 5);
        var dev = new MemoryDevice(src);
        long bad = DiskGeometry.Dd720.Lba(10, 1, 4);   // stopa 10, strana 1, sektor 5
        dev.UnreadableSectors.Add(bad);
        dev.FlakySectors[DiskGeometry.Dd720.Lba(20, 0, 0)] = 2; // napoprvé celá stopa selže, pak se přečte
        var r = new OperationResult();
        var img = DiskOperations.ReadDisk(dev, SideMode.Double, r);
        Eq(1, r.BadSectors.Count, "počet vadných");
        Eq(new BadSector(10, 1, 5, SectorErrors.Explain(new SectorIOException("CRC chyba", 23))), r.BadSectors[0], "vadný sektor");
        Assert(img.Data.Skip((int)bad * 512).Take(512).All(b => b == 0), "vadný sektor vynulovaný");
        // všechno ostatní sedí
        var expect = (byte[])src.Data.Clone();
        Array.Clear(expect, (int)bad * 512, 512);
        Assert(img.Data.SequenceEqual(expect), "zbytek dat");
    }

    private static void T_Read_WarnsOnTenSectorBpb()
    {
        var ten = DiskFormatter.CreateBlank(new DiskGeometry(80, 2, 10), FormatStyle.Atari, null, new Random(1));
        var dev = new MemoryDevice { Formatted = DiskGeometry.Dd720 };
        Buffer.BlockCopy(ten.Data, 0, dev.Disk, 0, 512);
        var r = new OperationResult();
        DiskOperations.ReadDisk(dev, SideMode.Auto, r);
        Assert(r.Warnings.Any(w => w.Contains("10 sektorů")), "varování o 10 sektorech");
    }

    private static void T_Read_Unformatted()
    {
        var dev = new MemoryDevice();
        try { DiskOperations.ReadDisk(dev, SideMode.Auto, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("nerozpoznala"), e.Message); }
    }

    private static void T_Write_WithFormatAndVerify()
    {
        var src = RandomImage(DiskGeometry.Dd720, 6);
        var dev = new MemoryDevice(); // nenaformátovaná
        var r = new OperationResult();
        DiskOperations.WriteImage(dev, src, format: true, verify: true, r);
        Eq(80, dev.FormatCalls, "formát 80 stop");
        Assert(r.Success, "bez chyb");
        Assert(dev.Disk.Take(src.Data.Length).SequenceEqual(src.Data), "zapsaná data");
        Assert(!dev.Locked, "odemčeno");
    }

    private static void T_Write_SingleSidedOntoDoubleSided()
    {
        var ss = DiskFormatter.CreateBlank(DiskGeometry.Ss360, FormatStyle.Atari, null, new Random(1));
        for (int i = 10 * 512; i < ss.Data.Length; i++) ss.Data[i] = (byte)(i * 5);
        var dev = new MemoryDevice(DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc));
        var r = new OperationResult();
        DiskOperations.WriteImage(dev, ss, false, true, r);
        Assert(r.Success, "bez chyb");
        // a zpětné přečtení dá stejný SS image
        var back = DiskOperations.ReadDisk(dev, SideMode.Auto, new OperationResult());
        Eq("80x1x9", back.Geometry.Short, "SS zpět");
        Assert(back.Data.SequenceEqual(ss.Data), "SS data zpět");
    }

    private static void T_Write_VerifyDetectsCorruption()
    {
        var src = RandomImage(DiskGeometry.Dd720, 7);
        var dev = new MemoryDevice(DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc));
        dev.SilentlyCorrupt.Add(DiskGeometry.Dd720.Lba(33, 0, 2));
        var r = new OperationResult();
        DiskOperations.WriteImage(dev, src, false, true, r);
        Eq(1, r.VerifyErrors.Count, "chyby kontroly");
        Eq(33, r.VerifyErrors[0].Track, "stopa");
        Eq(3, r.VerifyErrors[0].Sector, "sektor");
    }

    private static void T_Write_RejectsWrongMediaAndFormats()
    {
        var src = RandomImage(DiskGeometry.Dd720, 8);
        var hdDisk = new MemoryDevice(DiskFormatter.CreateBlank(DiskGeometry.Hd1440, FormatStyle.Pc));
        try { DiskOperations.WriteImage(hdDisk, src, false, false, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("9 sektorů") && e.Message.Contains("18"), e.Message); }

        var tenSector = RandomImage(new DiskGeometry(80, 2, 10), 9);
        try { DiskOperations.WriteImage(new MemoryDevice(), tenSector, true, false, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("neumí"), e.Message); }

        var prot = new MemoryDevice(DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc)) { WriteProtected = true };
        try { DiskOperations.WriteImage(prot, src, false, false, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("chráněná"), e.Message); }
        Assert(!prot.Locked, "po chybě odemčeno");
    }

    private static void T_Format_MarksBadClusters()
    {
        var dev = new MemoryDevice();
        long bad = DiskGeometry.Dd720.Lba(40, 1, 3);
        dev.UnreadableSectors.Add(bad);
        var r = new OperationResult();
        DiskOperations.Format(dev, DiskGeometry.Dd720, FormatStyle.Hybrid, "HOROPC", quick: false, verify: true, r);
        Eq(1, r.BadSectors.Count, "nalezený vadný sektor");

        var disk = dev.Disk.Take((int)DiskGeometry.Dd720.SizeBytes).ToArray();
        dev.UnreadableSectors.Clear();
        var v = Fat12Volume.TryOpen(disk, out _)!;
        var layout = DiskFormatter.ComputeLayout(DiskGeometry.Dd720, FormatStyle.Hybrid);
        int cluster = (int)((bad - layout.SystemSectors) / layout.SectorsPerCluster) + 2;
        Eq(0xFF7, v.NextCluster(cluster), "cluster označen jako vadný");
        Eq("HOROPC", v.GetVolumeLabel(), "jmenovka");

        string f = Path_("formatted.st");
        File.WriteAllBytes(f, disk);
        string o = Sh("mdir", $"-i \"{f}\" ::", out int code);
        Eq(0, code, "mdir: " + o);
        string info = Sh("minfo", $"-i \"{f}\" ::", out _);
        Assert(o.Contains("HOROPC"), "mtools vidí jmenovku");
        _ = info;
    }

    private static void T_Format_QuickNeedsFormattedDisk()
    {
        try { DiskOperations.Format(new MemoryDevice(), DiskGeometry.Dd720, FormatStyle.Atari, null, true, false, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("Rychlý"), e.Message); }

        var dev = new MemoryDevice(RandomImage(DiskGeometry.Dd720, 1));
        DiskOperations.Format(dev, DiskGeometry.Dd720, FormatStyle.Atari, null, true, false, new OperationResult());
        Eq(0, dev.FormatCalls, "rychlý formát bez nízkoúrovňového");
        Assert(Fat12Volume.TryOpen(dev.Disk.Take(737280).ToArray(), out _)!.ListAll().Count == 0, "prázdný adresář");
    }

    private static void T_NoDisk()
    {
        var dev = new MemoryDevice(RandomImage(DiskGeometry.Dd720, 1)) { Present = false };
        try { DiskOperations.ReadDisk(dev, SideMode.Auto, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("není disketa"), e.Message); }
    }


    // ------------------------------------------------------------------ test povrchu

    private static void T_Surface_ReadOnly_MapsWeakAndBad()
    {
        var src = RandomImage(DiskGeometry.Dd720, 21);
        var dev = new MemoryDevice(src);
        long bad = DiskGeometry.Dd720.Lba(5, 0, 2), weak = DiskGeometry.Dd720.Lba(60, 1, 8);
        dev.UnreadableSectors.Add(bad);
        dev.FlakySectors[weak] = 2; // celá stopa selže, sektor sám napoprvé taky, pak projde
        var r = SurfaceTest.Run(dev, WriteTestMode.None);
        Eq(SectorStatus.Bad, r.Map[bad], "vadný");
        Eq(SectorStatus.Weak, r.Map[weak], "slabý");
        Eq(1, r.Map.Count(SectorStatus.Bad), "počet vadných");
        var expect = (byte[])src.Data.Clone();
        Array.Clear(expect, (int)bad * 512, 512);
        Assert(r.Contents.Data.SequenceEqual(expect), "obsah");
        Eq(0, dev.FormatCalls, "nic nezapsáno");
    }

    private static void T_Surface_PreserveData_RestoresContent()
    {
        var src = RandomImage(DiskGeometry.Dd720, 22);
        var dev = new MemoryDevice(src);
        long corrupt = DiskGeometry.Dd720.Lba(30, 1, 4);
        long unreadable = DiskGeometry.Dd720.Lba(70, 0, 0);
        dev.SilentlyCorrupt.Add(corrupt);
        dev.UnreadableSectors.Add(unreadable);
        bool backupCalled = false;
        var r = SurfaceTest.Run(dev, WriteTestMode.PreserveData, beforeWrite: res => { backupCalled = true; Assert(res.Contents.Data.Length == 737280, "záloha"); });
        Assert(backupCalled, "záloha před zápisem");
        Eq(SectorStatus.WriteFailed, r.Map[corrupt], "zápis selhal");
        Assert(r.RestoreFailures.Contains(corrupt), "hlášené neobnovení");
        Eq(1, r.TracksSkippedForWrite, "stopa s nečitelným sektorem vynechána");
        // všechno kromě poškozeného sektoru je zpět beze změny
        for (long l = 0; l < 1440; l++)
        {
            if (l == corrupt) continue;
            for (int k = 0; k < 512; k++)
                if (dev.Disk[l * 512 + k] != src.Data[l * 512 + k]) throw new Exception($"sektor {l} se změnil");
        }
        Assert(!dev.Locked, "odemčeno");
    }

    private static void T_Surface_Destructive()
    {
        var dev = new MemoryDevice(RandomImage(DiskGeometry.Dd720, 23));
        var r = SurfaceTest.Run(dev, WriteTestMode.Destructive);
        Eq(1440, r.Map.Count(SectorStatus.Ok), "vše OK");
        var fresh = DiskOperations.BuildFilesystem(DiskGeometry.Dd720, FormatStyle.Hybrid, null, r.Map.Problems, out _);
        Assert(fresh.BootSector.BpbLooksValid, "nový FS");
    }

    // ------------------------------------------------------------------ kontrola a oprava FAT

    private static string MakeFatImage(string name, FormatStyle style, out byte[] big, out byte[] small)
    {
        var img = DiskFormatter.CreateBlank(DiskGeometry.Dd720, style, "TEST", new Random(31));
        string st = Path_(name);
        File.WriteAllBytes(st, img.Data);
        var rnd = new Random(32);
        big = new byte[60_000]; rnd.NextBytes(big);
        small = new byte[1500]; rnd.NextBytes(small);
        File.WriteAllBytes(Path_("A.BIN"), big);
        File.WriteAllBytes(Path_("B.BIN"), small);
        Sh("mmd", $"-i \"{st}\" ::GAMES", out int c0); Eq(0, c0, "mmd");
        Sh("mcopy", $"-i \"{st}\" \"{Path_("A.BIN")}\" ::GAMES/A.BIN", out int c1); Eq(0, c1, "mcopy A");
        Sh("mcopy", $"-i \"{st}\" \"{Path_("B.BIN")}\" ::B.BIN", out int c2); Eq(0, c2, "mcopy B");
        return st;
    }

    private static bool FsckClean(byte[] disk, string name, out string output)
    {
        string f = Path_(name);
        File.WriteAllBytes(f, disk);
        output = Sh("fsck.fat", $"-n \"{f}\"", out int code);
        return code == 0;
    }

    private static void T_Fat_CleanDiskHasNoProblems()
    {
        foreach (FormatStyle style in Enum.GetValues(typeof(FormatStyle)))
        {
            string st = MakeFatImage($"clean_{style}.st", style, out _, out _);
            var rep = FatDoctor.Check(File.ReadAllBytes(st));
            Assert(rep.IsFat, "FAT");
            Assert(!rep.HasProblems, $"{style}: " + string.Join(" | ", rep.Issues.Select(i => i.Message)));
            Eq(2, rep.Files, "souborů");
            Eq(1, rep.Directories, "adresářů");
        }
    }

    private static void T_Fat_RepairsTypicalDamage()
    {
        string st = MakeFatImage("damaged.st", FormatStyle.Pc, out byte[] big, out byte[] small);
        var d = File.ReadAllBytes(st);
        var bs = new BootSector(d);
        int fat1 = bs.ReservedSectors * 512, fat2 = fat1 + bs.SectorsPerFat * 512;
        var v = Fat12Volume.TryOpen(d, out _)!;
        var a = v.ListAll().Single(e => e.Path == "GAMES\\A.BIN");
        var b = v.ListAll().Single(e => e.Path == "B.BIN");
        var aChain = new List<int>(); for (int c = a.FirstCluster; c < 0xFF8; c = FatTable.Get(d, fat1, c)) aChain.Add(c);
        var bChain = new List<int>(); for (int c = b.FirstCluster; c < 0xFF8; c = FatTable.Get(d, fat1, c)) bChain.Add(c);

        // 1) ztracený řetězec 3 clusterů
        FatTable.Set(d, fat1, 300, 301); FatTable.Set(d, fat1, 301, 302); FatTable.Set(d, fat1, 302, 0xFFF);
        // 2) B.BIN pokračuje do clusteru souboru A (cross-link) — B má 1500 B = 2 clustery, přidáme 3.
        FatTable.Set(d, fat1, bChain[bChain.Count - 1], aChain[10]);
        // 3) FAT2 zůstane původní → kopie se liší
        // 4) velikost A.BIN zvětšená nad řetězec
        int aEntry = FindEntry(d, "A       BIN");
        BitConverter.GetBytes(200_000).CopyTo(d, aEntry + 28);

        Assert(!FsckClean(d, "damaged_before.st", out _), "fsck.fat má chyby vidět");
        var rep = FatDoctor.Check(d);
        Assert(rep.Issues.Any(i => i.Message.Contains("Ztracené")), "ztracené clustery");
        Assert(rep.Issues.Any(i => i.Message.StartsWith("B.BIN: řetězec má")), "B.BIN přerostlý řetězec");
        Assert(!rep.Issues.Any(i => i.Message.Contains("cross-link")), "přesah za konec souboru není cross-link");
        Assert(rep.Issues.Any(i => i.Message.Contains("Kopie FAT")), "rozdílné FAT");
        Assert(rep.Issues.Any(i => i.Message.Contains("A.BIN") && i.Message.Contains("200000")), "velikost");

        var fixedDisk = (byte[])d.Clone();
        var done = FatDoctor.Repair(fixedDisk);
        Assert(done.Actions.Count > 0, "akce");
        var after = FatDoctor.Check(fixedDisk);
        Assert(!after.HasRepairable, "po opravě: " + string.Join(" | ", after.Issues.Select(i => i.Message)));
        Assert(FsckClean(fixedDisk, "damaged_after.st", out string fsck), "fsck.fat po opravě: " + fsck);

        // A.BIN má původní data (začátek 60 000 B), B.BIN taky; ztracená data jsou ve FILE0000.CHK
        var v2 = Fat12Volume.TryOpen(fixedDisk, out _)!;
        var all = v2.ListAll();
        var a2 = all.Single(e => e.Path == "GAMES\\A.BIN");
        Assert(v2.ReadFile(a2).Take(big.Length).SequenceEqual(big), "data A.BIN");
        var b2 = all.Single(e => e.Path == "B.BIN");
        Assert(v2.ReadFile(b2).Take(small.Length).SequenceEqual(small), "data B.BIN");
        Assert(all.Any(e => e.Name == "FILE0000.CHK"), "FILE0000.CHK");
        var changed = FatDoctor.ChangedSectors(d, fixedDisk);
        Assert(changed.Count > 0 && changed.Count < 40, $"změněných sektorů {changed.Count}");
    }


    private static void T_Fat_RepairFuzz()
    {
        // Náhodně poškozená FAT: oprava nesmí nic rozbít — po ní musí být čisto (fsck.fat) a druhá oprava nic nemění.
        string st = MakeFatImage("fuzz_base.st", FormatStyle.Pc, out _, out _);
        var baseDisk = File.ReadAllBytes(st);
        // pár dalších souborů, ať je co kazit
        for (int i = 0; i < 6; i++)
        {
            var data = new byte[3000 + i * 7000]; new Random(100 + i).NextBytes(data);
            File.WriteAllBytes(Path_($"F{i}.DAT"), data);
            File.WriteAllBytes(st, baseDisk);
            Sh("mcopy", $"-i \"{st}\" \"{Path_($"F{i}.DAT")}\" ::F{i}.DAT", out _);
            baseDisk = File.ReadAllBytes(st);
        }
        var bs = new BootSector(baseDisk);
        int fat1 = bs.ReservedSectors * 512;
        var used = Enumerable.Range(2, 700).Where(c => FatTable.Get(baseDisk, fat1, c) != 0).ToList();
        var rnd = new Random(7);
        int strong = 0;
        for (int iter = 0; iter < 150; iter++)
        {
            var d = (byte[])baseDisk.Clone();
            int hits = rnd.Next(1, 6);
            for (int h = 0; h < hits; h++)
            {
                int c = rnd.Next(4) == 0 ? rnd.Next(2, 700) : used[rnd.Next(used.Count)];
                int[] vals = { 0, 0xFFF, rnd.Next(2, 700), used[rnd.Next(used.Count)], 0xFF3, 1 };
                FatTable.Set(d, fat1, c, vals[rnd.Next(vals.Length)]);
            }
            if (rnd.Next(3) == 0) FatTable.Set(d, fat1 + bs.SectorsPerFat * 512, used[rnd.Next(used.Count)], 0);

            var fx = (byte[])d.Clone();
            FatDoctor.Repair(fx);
            var after = FatDoctor.Check(fx);
            Assert(!after.HasRepairable, $"iterace {iter}: po opravě zbývá: " + string.Join(" | ", after.Issues.Where(i => i.Repairable).Select(i => i.Message)));
            var again = (byte[])fx.Clone();
            FatDoctor.Repair(again);
            Assert(FatDoctor.ChangedSectors(fx, again).Count == 0, $"iterace {iter}: druhá oprava ještě něco měnila");
            if (after.Count(IssueSeverity.Error) == 0)
            {
                Assert(FsckClean(fx, "fuzz.st", out string o), $"iterace {iter}: fsck.fat: " + o);
                strong++;
            }
        }
        Assert(strong > 100, $"jen {strong} iterací ověřeno fsck");
    }


    private static void T_Fat_RealCrossLinkIsCloned()
    {
        string st = MakeFatImage("xlink.st", FormatStyle.Pc, out byte[] big, out _);
        var d = File.ReadAllBytes(st);
        var bs = new BootSector(d);
        int fat1 = bs.ReservedSectors * 512;
        var a = Fat12Volume.TryOpen(d, out _)!.ListAll().Single(e => e.Path == "GAMES\\A.BIN");
        var aChain = new List<int>(); for (int c = a.FirstCluster; c < 0xFF8; c = FatTable.Get(d, fat1, c)) aChain.Add(c);
        // B.BIN (1500 B = 2 clustery) začne uprostřed A → oba soubory potřebují tytéž clustery
        int bEntry = FindEntry(d, "B       BIN");
        int oldB = d[bEntry + 26] | (d[bEntry + 27] << 8);
        d[bEntry + 26] = (byte)aChain[40]; d[bEntry + 27] = (byte)(aChain[40] >> 8);
        int oldNext = FatTable.Get(d, fat1, oldB); FatTable.Set(d, fat1, oldB, 0); FatTable.Set(d, fat1, oldNext, 0);
        Buffer.BlockCopy(d, fat1, d, fat1 + bs.SectorsPerFat * 512, bs.SectorsPerFat * 512);

        var rep = FatDoctor.Check(d);
        Assert(rep.Issues.Any(i => i.Message.Contains("cross-link") && i.Repairable), "cross-link: " + string.Join(" | ", rep.Issues.Select(i => i.Message)));
        var fx = (byte[])d.Clone();
        FatDoctor.Repair(fx);
        Assert(!FatDoctor.Check(fx).HasRepairable, "po opravě čisto");
        Assert(FsckClean(fx, "xlink_after.st", out string o), "fsck: " + o);
        var v = Fat12Volume.TryOpen(fx, out _)!;
        var all = v.ListAll();
        Assert(v.ReadFile(all.Single(e => e.Path == "GAMES\\A.BIN")).SequenceEqual(big), "A.BIN nedotčený");
        var b = all.Single(e => e.Path == "B.BIN");
        var a2 = all.Single(e => e.Path == "GAMES\\A.BIN");
        var bs2 = new BootSector(fx); int f1 = bs2.ReservedSectors * 512;
        var ca = new HashSet<int>(); for (int c = a2.FirstCluster; c < 0xFF8; c = FatTable.Get(fx, f1, c)) ca.Add(c);
        for (int c = b.FirstCluster; c < 0xFF8; c = FatTable.Get(fx, f1, c)) Assert(!ca.Contains(c), "soubory už nesdílí clustery");
        Assert(v.ReadFile(b).SequenceEqual(big.Skip(40 * 1024).Take(1500)), "B.BIN má zkopírovaná data");
    }


    private static int FindEntry(byte[] d, string name83)
    {
        var target = System.Text.Encoding.ASCII.GetBytes(name83);
        for (int o = 0; o + 32 <= d.Length; o += 32)
            if (target.Select((b, i) => d[o + i] == b).All(x => x)) return o;
        throw new Exception("záznam nenalezen: " + name83);
    }

    private static void T_Fat_BrokenFatHeaderRepaired()
    {
        string st = MakeFatImage("hdr.st", FormatStyle.Pc, out _, out _);
        var d = File.ReadAllBytes(st);
        int fat1 = new BootSector(d).ReservedSectors * 512;
        int spf = new BootSector(d).SectorsPerFat;
        d[fat1] = 0; d[fat1 + 1] = 0; d[fat1 + 2] = 0;
        Buffer.BlockCopy(d, fat1, d, fat1 + spf * 512, spf * 512);
        Assert(FatDoctor.Check(d).Issues.Any(i => i.Repairable && i.Message.Contains("Začátek FAT")), "poškozená hlavička FAT");
        var fx = (byte[])d.Clone();
        FatDoctor.Repair(fx);
        Assert(FsckClean(fx, "hdr_after.st", out string o), "fsck: " + o);
    }

    private static void T_Fat_LoopAndInvalidLink()
    {
        string st = MakeFatImage("loop.st", FormatStyle.Hybrid, out _, out _);
        var d = File.ReadAllBytes(st);
        var bs = new BootSector(d);
        int fat1 = bs.ReservedSectors * 512;
        var a = Fat12Volume.TryOpen(d, out _)!.ListAll().Single(e => e.Path == "GAMES\\A.BIN");
        var chain = new List<int>(); for (int c = a.FirstCluster; c < 0xFF8; c = FatTable.Get(d, fat1, c)) chain.Add(c);
        FatTable.Set(d, fat1, chain[20], chain[5]);     // smyčka
        // FAT2 sjednotit, ať testujeme jen smyčku
        Buffer.BlockCopy(d, fat1, d, fat1 + bs.SectorsPerFat * 512, bs.SectorsPerFat * 512);
        var rep = FatDoctor.Check(d);
        Assert(rep.Issues.Any(i => i.Message.Contains("smyčku")), "smyčka nalezena");
        var fx = (byte[])d.Clone();
        FatDoctor.Repair(fx);
        Assert(!FatDoctor.Check(fx).HasRepairable, "po opravě čisto");
        Assert(FsckClean(fx, "loop_after.st", out string o), "fsck: " + o);
    }

    private static void T_Fat_SurfaceMappingToFiles()
    {
        string st = MakeFatImage("surf.st", FormatStyle.Pc, out _, out _);
        var d = File.ReadAllBytes(st);
        var bs = new BootSector(d);
        var layout = DiskFormatter.ComputeLayout(DiskGeometry.Dd720, FormatStyle.Pc);
        var a = Fat12Volume.TryOpen(d, out _)!.ListAll().Single(e => e.Path == "GAMES\\A.BIN");
        var map = new SurfaceMap(DiskGeometry.Dd720);
        for (long l = 0; l < 1440; l++) map[l] = SectorStatus.Ok;
        long inFile = layout.SystemSectors + (a.FirstCluster - 2) * 2 + 3;
        long inFree = 1400;
        map[inFile] = SectorStatus.Bad;
        map[inFree] = SectorStatus.Weak;
        var rep = FatDoctor.Check(d, map);
        Assert(rep.DamagedFiles.ContainsKey("GAMES\\A.BIN"), "poškozený soubor");
        Assert(rep.Issues.Any(i => i.Repairable && i.Message.Contains("volném místě")), "vadné ve volném místě");
        Assert(!rep.SystemAreaDamaged, "systém OK");

        var fx = (byte[])d.Clone();
        FatDoctor.Repair(fx, map);
        int cluster = (int)((inFree - layout.SystemSectors) / 2) + 2;
        Eq(0xFF7, FatTable.Get(fx, bs.ReservedSectors * 512, cluster), "cluster označen vadný");

        map[3] = SectorStatus.Bad; // FAT
        Assert(FatDoctor.Check(d, map).SystemAreaDamaged, "poškozená systémová oblast");
    }

    // ------------------------------------------------------------------ rozpoznání systémů

    private static void T_Detect_FileSystems()
    {
        Eq(FsKind.FatHybrid, FileSystemDetector.Detect(DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Hybrid).Data).Kind, "hybrid");
        Eq(FsKind.FatAtari, FileSystemDetector.Detect(DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Atari).Data).Kind, "atari");
        Eq(FsKind.FatPc, FileSystemDetector.Detect(DiskFormatter.CreateBlank(DiskGeometry.Hd1440, FormatStyle.Pc).Data).Kind, "pc");
        Eq(FsKind.Empty, FileSystemDetector.Detect(new byte[737280]).Kind, "prázdná");

        var msx = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Pc).Data;
        msx[0] = 0xEB; msx[1] = 0xFE; msx[2] = 0x90;
        Eq(FsKind.FatMsx, FileSystemDetector.Detect(msx).Kind, "msx");

        // HFS přes hfsutils
        string hfs = Path_("mac.img");
        File.WriteAllBytes(hfs, new byte[1474560]);
        Sh("hformat", $"-l \"Muj Mac\" \"{hfs}\"", out int hc);
        var mac = FileSystemDetector.Detect(File.ReadAllBytes(hfs));
        Eq(FsKind.MacHfs, mac.Kind, "hfs");
        Assert(mac.Details != null && mac.Details.Contains("Muj Mac"), "jméno HFS: " + mac.Details);

        string mx = Path_("minix.img");
        File.WriteAllBytes(mx, new byte[737280]);
        Sh("mkfs.minix", $"\"{mx}\"", out _);
        Eq(FsKind.Minix, FileSystemDetector.Detect(File.ReadAllBytes(mx)).Kind, "minix");

        string e2 = Path_("ext2.img");
        File.WriteAllBytes(e2, new byte[1474560]);
        Sh("mke2fs", $"-q -F -t ext2 -L ZACHRANA \"{e2}\"", out _);
        var ext = FileSystemDetector.Detect(File.ReadAllBytes(e2));
        Eq(FsKind.Ext2, ext.Kind, "ext2");
        Assert(ext.Details!.Contains("ZACHRANA"), "jméno ext2");

        var adf = new byte[901120];
        adf[0] = (byte)'D'; adf[1] = (byte)'O'; adf[2] = (byte)'S'; adf[3] = 1;
        adf[880 * 512 + 432] = 9; System.Text.Encoding.ASCII.GetBytes("Workbench").CopyTo(adf, 880 * 512 + 433);
        var am = FileSystemDetector.Detect(adf);
        Eq(FsKind.Amiga, am.Kind, "amiga");
        Assert(am.Name.Contains("FFS") && am.Details!.Contains("Workbench"), "amiga detail");
        string adfPath = Path_("wb.adf");
        File.WriteAllBytes(adfPath, adf);
        var l = ImageFile.Load(adfPath);
        Eq("80x2x11", l.Image!.Geometry.Short, "ADF geometrie");
        Eq(Verdict.NotWritable, ImageAnalysis.AssessForStandardDrive(l).Verdict, "ADF nejde na USB");

        var ql = new byte[737280];
        System.Text.Encoding.ASCII.GetBytes("QL5ADATA      ").CopyTo(ql, 0);
        Eq(FsKind.SinclairQl, FileSystemDetector.Detect(ql).Kind, "QL");
    }

    private static void T_Format_640K()
    {
        var dev = new MemoryDevice();
        DiskOperations.Format(dev, DiskGeometry.Dd640, FormatStyle.Pc, null, false, true, new OperationResult());
        var disk = dev.Disk.Take((int)DiskGeometry.Dd640.SizeBytes).ToArray();
        Eq(0xFB, (int)new BootSector(disk).MediaDescriptor, "média 640K");
        Assert(FsckClean(disk, "f640.img", out string o), "fsck 640K: " + o);
    }

    private static void T_Catalog_HasDescriptions()
    {
        Assert(FormatCatalog.All.Count >= 15, "katalog");
        Assert(FormatCatalog.All.All(f => f.UsedFor.Length > 10), "popisy");
    }


    // ------------------------------------------------------------------ tvorba cizích souborových systémů

    private static byte[] MaskHfsDates(byte[] d)
    {
        var c = (byte[])d.Clone();
        foreach (int mdb in new[] { 1024, (c.Length / 512 - 2) * 512 })
            for (int k = 2; k < 10; k++) c[mdb + k] = 0;
        // data složky v katalogu: najdeme záznam typu 1 v listu katalogu
        int ct = (4 + 22) * 512 + 512;
        int keyLen = c[ct + 14];
        int rec = ct + 14 + 1 + keyLen;
        for (int k = 10; k < 18; k++) c[rec + k] = 0;
        return c;
    }

    private static void T_Hfs_MatchesHformatAndMounts()
    {
        foreach (string name in new[] { "Untitled", "Muj Mac 1" })
        {
            string refImg = Path_("hfs_ref.img");
            File.WriteAllBytes(refImg, new byte[1474560]);
            Sh("hformat", $"-l \"{name}\" \"{refImg}\"", out _);
            var ours = AtariFloppy.Core.Fs.Builders.HfsBuilder.Create(DiskGeometry.Hd1440, name);
            var a = MaskHfsDates(File.ReadAllBytes(refImg));
            var b = MaskHfsDates(ours.Data);
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) throw new Exception($"„{name}“: liší se bajt {i} (sektor {i / 512}, offset {i % 512}): hformat {a[i]:X2}, my {b[i]:X2}");

            string img = Path_("hfs_ours.img");
            File.WriteAllBytes(img, ours.Data);
            string o = Sh("hmount", $"\"{img}\"", out int c1);
            Eq(0, c1, "hmount: " + o);
            File.WriteAllText(Path_("readme.txt"), "Ahoj Macu");
            o = Sh("hcopy", $"\"{Path_("readme.txt")}\" :", out int c2);
            Eq(0, c2, "hcopy: " + o);
            o = Sh("hls", "", out _);
            Sh("humount", "", out _);
            Assert(o.Contains("readme"), "hls: " + o);
        }
        Eq(FsKind.MacHfs, FileSystemDetector.Detect(AtariFloppy.Core.Fs.Builders.HfsBuilder.Create(DiskGeometry.Hd1440, "X").Data).Kind, "rozpoznání");
    }


    private static void T_Minix_ValidForFsck()
    {
        foreach (var g in new[] { DiskGeometry.Dd720, DiskGeometry.Hd1440 })
        {
            var img = AtariFloppy.Core.Fs.Builders.MinixBuilder.Create(g, bigEndian: false);
            string f = Path_($"minix_{g.Short}.img");
            File.WriteAllBytes(f, img.Data);
            string o = Sh("fsck.minix", $"-fs \"{f}\"", out int code);
            Eq(0, code, "fsck.minix: " + o);
            Eq(FsKind.Minix, FileSystemDetector.Detect(img.Data).Kind, "rozpoznání");

            // Atari varianta = stejná struktura, jen všechna 16/32bitová čísla a slova bitmap prohozená
            var be = AtariFloppy.Core.Fs.Builders.MinixBuilder.Create(g, bigEndian: true, now: DateTime.Now).Data;
            var le = AtariFloppy.Core.Fs.Builders.MinixBuilder.Create(g, bigEndian: false, now: DateTime.Now).Data;
            var sw = (byte[])be.Clone();
            int imapB = sw[1024 + 4] << 8 | sw[1024 + 5], zmapB = sw[1024 + 6] << 8 | sw[1024 + 7];
            int firstData = sw[1024 + 8] << 8 | sw[1024 + 9];
            int[] sb16 = { 0, 2, 4, 6, 8, 10, 16, 18 };
            foreach (int k in sb16) Swap(sw, 1024 + k, 2);
            Swap(sw, 1024 + 12, 4);
            for (int w = 2048; w < (2 + imapB + zmapB) * 1024; w += 2) Swap(sw, w, 2);    // bitmapy po slovech
            int ino = (2 + imapB + zmapB) * 1024;
            Swap(sw, ino, 2); Swap(sw, ino + 2, 2); Swap(sw, ino + 4, 4); Swap(sw, ino + 8, 4);
            for (int z = 0; z < 9; z++) Swap(sw, ino + 14 + z * 2, 2);
            Swap(sw, firstData * 1024, 2); Swap(sw, firstData * 1024 + 16, 2);
            // časová značka se může lišit o sekundu
            for (int k = 8; k < 12; k++) { sw[ino + k] = 0; le[ino + k] = 0; }
            Assert(sw.SequenceEqual(le), "Atari varianta není přesné zrcadlo PC varianty");
            Eq(FsKind.Minix, FileSystemDetector.Detect(be).Kind, "rozpoznání BE");
        }
    }

    private static void Swap(byte[] b, int o, int n) => Array.Reverse(b, o, n);

    private static void T_Ext2_ValidForE2fsck()
    {
        foreach (var g in new[] { DiskGeometry.Hd1440, DiskGeometry.Dd720 })
        {
            var img = AtariFloppy.Core.Fs.Builders.Ext2Builder.Create(g, "ZACHRANA");
            string f = Path_($"ext2_{g.Short}.img");
            File.WriteAllBytes(f, img.Data);
            string o = Sh("e2fsck", $"-fn \"{f}\"", out int code);
            Eq(0, code, "e2fsck: " + o);
            var det = FileSystemDetector.Detect(img.Data);
            Eq(FsKind.Ext2, det.Kind, "rozpoznání");
            Assert(det.Details!.Contains("ZACHRANA"), "jméno");
        }
    }

    private static void T_Cpm_PcwWithCpmtools()
    {
        var img = AtariFloppy.Core.Fs.Builders.CpmBuilder.CreatePcw720();
        string f = Path_("pcw.img");
        File.WriteAllBytes(f, img.Data);
        File.WriteAllText(Path_("dopis.txt"), "Ahoj z PCW\r\n");
        string o = Sh("cpmcp", $"-f cf2dd \"{f}\" \"{Path_("dopis.txt")}\" 0:DOPIS.TXT", out int c1);
        Eq(0, c1, "cpmcp: " + o);
        o = Sh("cpmls", $"-f cf2dd \"{f}\"", out int c2);
        Eq(0, c2, "cpmls: " + o);
        Assert(o.ToLowerInvariant().Contains("dopis.txt"), "cpmls: " + o);
        o = Sh("fsck.cpm", $"-f cf2dd -n \"{f}\"", out int c3);
        Eq(0, c3, "fsck.cpm: " + o);
        Eq(FsKind.CpmAmstrad, FileSystemDetector.Detect(img.Data).Kind, "rozpoznání");
    }

    private static void T_Msx_BootSector()
    {
        foreach (var g in new[] { DiskGeometry.Dd720, DiskGeometry.Ss360 })
        {
            var img = DiskFormatter.CreateBlank(g, FormatStyle.Msx, null);
            Eq(FsKind.FatMsx, FileSystemDetector.Detect(img.Data).Kind, "MSX " + g.Short);
            Eq(g.Sides == 2 ? 0xF9 : 0xF8, (int)img.Data[512], "médium ve FAT");
            Eq(0xC9, (int)img.Data[0x1E], "Z80 RET");
            string f = Path_($"msx_{g.Short}.dsk");
            File.WriteAllBytes(f, img.Data);
            Assert(FsckClean(img.Data, $"msx_{g.Short}.img", out string o), "fsck.fat: " + o);
            Assert(!FatDoctor.Check(img.Data).HasProblems, "FatDoctor");
        }
    }


    private static void T_Profiles_EvaluateByDriveAndMedia()
    {
        var usb = new DetectedDrive { Kind = DriveKind.Usb, Letter = "A", Supports720 = true, Supports1440 = true, Media = InsertedMedia.DD, MediaState = MediaState.Ready };
        usb.ComputeFeatures();
        ProfileStatus S(string id, DetectedDrive d) => FormatProfiles.Evaluate(FormatProfiles.Find(id)!, d, d.Media).Status;
        Eq(ProfileStatus.Ready, S("atari", usb), "atari na DD");
        Eq(ProfileStatus.Ready, S("msx", usb), "msx na DD");
        Eq(ProfileStatus.WrongMedia, S("mac-hfs", usb), "HFS potřebuje HD");
        Eq(ProfileStatus.Unsupported, S("amiga", usb), "Amiga na USB");
        Eq(ProfileStatus.Unsupported, S("atari-800", usb), "10 sektorů na USB");
        Eq(ProfileStatus.Planned, S("ql", usb), "QL zatím neumíme");
        usb.Media = InsertedMedia.HD;
        Eq(ProfileStatus.Ready, S("mac-hfs", usb), "HFS na HD");
        Eq(ProfileStatus.WrongMedia, S("atari", usb), "720 na HD bez pásky");
        usb.Media = InsertedMedia.None;
        Eq(ProfileStatus.NoDisk, S("pc", usb), "bez diskety");

        var intern = new DetectedDrive { Kind = DriveKind.Internal, Letter = "A", Supports720 = true, Supports1440 = true, Media = InsertedMedia.DD };
        intern.ComputeFeatures();
        Eq(ProfileStatus.NeedsDriver, S("atari-800", intern), "interní bez fdrawcmd");
        var gwNoTool = new DetectedDrive { Kind = DriveKind.Greaseweazle, Port = "COM3" };
        gwNoTool.ComputeFeatures();
        Eq(ProfileStatus.NeedsDriver, S("amiga", gwNoTool), "GW bez gw.exe");
        var gw = new DetectedDrive { Kind = DriveKind.Greaseweazle, Port = "COM3", GwToolPath = "gw.exe" };
        gw.ComputeFeatures();
        Eq(ProfileStatus.Ready, S("amiga", gw), "Amiga přes GW");
        Eq(ProfileStatus.Ready, S("atari-820", gw), "Atari 820 KB přes GW");
        Eq(ProfileStatus.Planned, S("c1581", gw), "C1581: prázdný formát zatím neumím");
        Eq(ProfileStatus.Unsupported, S("pc-ed", gw), "ED potřebuje ED mechaniku");
        var fd = new DetectedDrive { Kind = DriveKind.Internal, Letter = "A", FdrawcmdInstalled = true, Supports720 = true, Supports1440 = true, Media = InsertedMedia.DD };
        fd.ComputeFeatures();
        Eq(ProfileStatus.Ready, S("atari-820", fd), "Atari 820 KB přes fdrawcmd");
        Eq(ProfileStatus.Unsupported, S("amiga", fd), "Amiga přes fdrawcmd ne");

        Eq(InsertedMedia.DD, MediaSupport.Density(new[] { 14, 5 }), "DD čidlo");
        Eq(InsertedMedia.HD, MediaSupport.Density(new[] { 17, 2 }), "HD čidlo");
        Eq(InsertedMedia.Unknown, MediaSupport.Density(new[] { 2, 5 }), "nejisté");
    }

    private static void T_Profiles_FormatEveryStandardProfileOnSimulatedDrive()
    {
        foreach (var p in FormatProfiles.All.Where(p => p.Build != null && p.Need == HardwareNeed.Standard))
        {
            var dev = new MemoryDevice();
            var result = new OperationResult();
            if (p.FatStyle != null && p.Geometry.Sides == 2)
                DiskOperations.Format(dev, p.Geometry, p.FatStyle.Value, "TEST", false, true, result);
            else
                DiskOperations.WriteImage(dev, p.Build!("TEST"), format: true, verify: true, result);
            Assert(result.Success, p.Id + ": chyby při formátu");

            // záloha zpět do image a rozpoznání systému
            var back = DiskOperations.ReadDisk(dev, SideMode.Auto, new OperationResult());
            var kind = FileSystemDetector.Detect(back.Data).Kind;
            Assert(kind != FsKind.Unknown && kind != FsKind.Empty, $"{p.Id}: po formátu nerozpoznáno ({kind})");
            var surface = SurfaceTest.Run(dev, WriteTestMode.None);
            Eq(0, surface.Map.Problems.Count(), p.Id + ": test povrchu");
            if (p.Geometry.Sides == 1) Eq(1, back.Geometry.Sides, p.Id + ": jednostranná záloha");
        }
    }


    private static void T_Amiga_AdfWithXdftool()
    {
        foreach (var (type, g, tag) in new[] {
            (AtariFloppy.Core.Fs.Amiga.AmigaFsType.Ffs, new DiskGeometry(80, 2, 11), "ffs"),
            (AtariFloppy.Core.Fs.Amiga.AmigaFsType.Ofs, new DiskGeometry(80, 2, 11), "ofs"),
            (AtariFloppy.Core.Fs.Amiga.AmigaFsType.Ffs, new DiskGeometry(80, 2, 22), "hd") })
        {
            var img = AtariFloppy.Core.Fs.Amiga.AmigaDos.CreateBlank(g, type, "Moje Hry");
            string f = Path_($"amiga_{tag}.adf");
            File.WriteAllBytes(f, img.Data);
            string o = Sh("xdftool", $"\"{f}\" list", out int c0);
            Eq(0, c0, "xdftool list: " + o);
            Assert(o.Contains("Moje Hry"), "jméno svazku: " + o);
            o = Sh("xdftool", $"\"{f}\" validate", out int cv);
            Assert(!o.ToLowerInvariant().Contains("error"), "xdftool validate: " + o);

            // soubory nahrané xdftoolem musí jít vypsat a přečíst naším kódem
            var data = new byte[70_000]; new Random(tag.Length).NextBytes(data);
            File.WriteAllBytes(Path_("hra.bin"), data);
            File.WriteAllText(Path_("ctime.txt"), "Ahoj Amigo");
            Sh("xdftool", $"\"{f}\" makedir C", out _);
            o = Sh("xdftool", $"\"{f}\" write \"{Path_("hra.bin")}\" C/Hra", out int c1); Eq(0, c1, "xdftool write: " + o);
            o = Sh("xdftool", $"\"{f}\" write \"{Path_("ctime.txt")}\" ReadMe", out int c2); Eq(0, c2, "xdftool write 2: " + o);

            var bytes = File.ReadAllBytes(f);
            var det = FileSystemDetector.Detect(bytes);
            Eq(FsKind.Amiga, det.Kind, "rozpoznání");
            var vol = AtariFloppy.Core.Fs.Amiga.AmigaDos.TryOpen(bytes)!;
            Assert(vol != null, "otevření svazku");
            var all = vol.ListAll();
            var hra = all.Single(e => e.Path == "C/Hra");
            Eq(70_000L, hra.Size, "velikost");
            Assert(vol.ReadFile(hra).SequenceEqual(data), $"{tag}: obsah C/Hra");
            Assert(vol.ReadFile(all.Single(e => e.Path == "ReadMe")).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("Ahoj Amigo")), "ReadMe");
            Assert(all.Single(e => e.Path == "C").IsDirectory, "adresář C");
        }
    }

    private static void T_Mac800_MatchesHformat()
    {
        var g = new DiskGeometry(80, 2, 10);
        string refImg = Path_("mac800_ref.dsk");
        File.WriteAllBytes(refImg, new byte[819200]);
        Sh("hformat", $"-l \"Untitled\" \"{refImg}\"", out _);
        var ours = AtariFloppy.Core.Fs.Builders.HfsBuilder.Create(g, "Untitled");
        var a = File.ReadAllBytes(refImg);
        // porovnat bez časových razítek
        var ms = new[] { 1024, (a.Length / 512 - 2) * 512 };
        foreach (var d in new[] { a, ours.Data })
            foreach (int m in ms) for (int k = 2; k < 10; k++) d[m + k] = 0;
        int diffs = 0, first = -1;
        for (int i = 0; i < a.Length; i++) if (a[i] != ours.Data[i]) { diffs++; if (first < 0) first = i; }
        // katalogová data složky mají taky datum — ta tolerujeme (max 8 bajtů)
        Assert(diffs <= 8, $"800K HFS se liší v {diffs} bajtech, první {first} (sektor {first / 512})");
        string img = Path_("mac800_ours.dsk");
        File.WriteAllBytes(img, ours.Data);
        Eq(0, (Sh("hmount", $"\"{img}\"", out int c) == null ? 1 : c), "hmount 800K");
        Sh("humount", "", out _);
    }


    // ------------------------------------------------------------------ fdrawcmd (simulovaný řadič)

    private static void T_Fdraw_DetectsAtariExtendedFormats()
    {
        foreach (var g in new[] { new DiskGeometry(82, 2, 10), new DiskGeometry(80, 2, 11), DiskGeometry.Dd720, new DiskGeometry(83, 2, 9), DiskGeometry.Ss360 })
        {
            var img = DiskFormatter.CreateBlank(g, FormatStyle.Atari, "HRA", new Random(1));
            for (int i = 20000; i < img.Data.Length; i++) img.Data[i] = (byte)(i * 13);
            var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(FakeFdc.WithImage(img), "A: (fdraw)");
            Eq(MediaState.Ready, dev.MediaState, "stav " + g.Short);
            Eq(g, dev.MediaGeometry, "rozpoznaná geometrie " + g.Short);

            var r = new OperationResult();
            var back = DiskOperations.ReadDisk(dev, SideMode.Auto, r);
            Assert(r.Success && r.Warnings.Count == 0, g.Short + ": " + string.Join(" | ", r.Warnings));
            Eq(g, back.Geometry, "geometrie zálohy");
            Assert(back.Data.SequenceEqual(img.Data), "data " + g.Short);
        }
    }

    private static void T_Fdraw_HdDmfAndAcorn()
    {
        var dmf = DiskFormatter.CreateBlank(new DiskGeometry(80, 2, 21), FormatStyle.Pc, null, new Random(2));
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(FakeFdc.WithImage(dmf, AtariFloppy.Core.Drives.Fdraw.DataRate.R500K), "A:");
        Eq("80x2x21", dev.MediaGeometry!.Short, "DMF");
        Eq(AtariFloppy.Core.Drives.Fdraw.DataRate.R500K, dev.Rate, "HD rychlost");

        var acorn = new DiskImage(new DiskGeometry(80, 2, 5, 1024));
        new Random(3).NextBytes(acorn.Data);
        var dev2 = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(FakeFdc.WithImage(acorn, firstId: 0), "A:");
        Eq(1024, dev2.MediaGeometry!.BytesPerSector, "1024 B sektory");
        Eq(0, dev2.FirstSectorId, "Acorn čísluje od 0");
        var back = DiskOperations.ReadDisk(dev2, SideMode.Auto, new OperationResult());
        Assert(back.Data.SequenceEqual(acorn.Data), "Acorn data");
    }

    private static void T_Fdraw_WriteAndFormatExtended()
    {
        var fdc = new FakeFdc { DiskPresent = true };
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "A:");
        Eq(MediaState.Unrecognized, dev.MediaState, "prázdná disketa");

        var img = DiskFormatter.CreateBlank(new DiskGeometry(82, 2, 10), FormatStyle.Atari, "DEMO", new Random(4));
        for (int i = 30000; i < img.Data.Length; i++) img.Data[i] = (byte)(i * 7);
        var r = new OperationResult();
        DiskOperations.WriteImage(dev, img, format: true, verify: true, r);
        Assert(r.Success, "zápis 820 KB");
        Eq(164, fdc.FormatLog.Count, "82 stop × 2 strany naformátováno");
        Assert(fdc.FormatLog.All(f => f.gap == 30 && f.count == 10), "gap3 30 pro 10 sektorů");

        dev.RefreshMedia();
        Eq("82x2x10", dev.MediaGeometry!.Short, "po zápisu");
        var back = DiskOperations.ReadDisk(dev, SideMode.Auto, new OperationResult());
        Assert(back.Data.SequenceEqual(img.Data), "data po zápisu");

        // formát profilem Atari 900 KB (11 sektorů) — řadič to umí / neumí
        var fdc2 = new FakeFdc();
        var dev3 = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc2, "A:");
        DiskOperations.Format(dev3, new DiskGeometry(80, 2, 11), FormatStyle.Atari, null, false, true, new OperationResult());
        Assert(fdc2.FormatLog.All(f => f.gap == 3), "gap3 3 pro 11 sektorů");
        var fdc3 = new FakeFdc { Supports11Sectors = false };
        var dev4 = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc3, "A:");
        try { DiskOperations.Format(dev4, new DiskGeometry(80, 2, 11), FormatStyle.Atari, null, false, false, new OperationResult()); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("první stopě"), e.Message); }
    }

    private static void T_Fdraw_BadSectorsAndLimits()
    {
        var img = DiskFormatter.CreateBlank(new DiskGeometry(80, 2, 10), FormatStyle.Atari, null, new Random(5));
        var fdc = FakeFdc.WithImage(img);
        fdc.Tracks[(40, 1)][3].CrcError = true;
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "A:", stepRateMs: 3);
        Eq(3, fdc.StepRateMs, "step rate");
        var map = SurfaceTest.Run(dev, WriteTestMode.None).Map;
        Eq(SectorStatus.Bad, map.Get(40, 1, 3), "CRC chyba = vadný sektor");
        Eq(1, map.Count(SectorStatus.Bad), "jen jeden");

        Assert(dev.SupportsGeometry(new DiskGeometry(84, 2, 10)), "84 stop OK");
        Assert(!dev.SupportsGeometry(new DiskGeometry(85, 2, 9)), "85 stop ne");
        Assert(!dev.SupportsGeometry(new DiskGeometry(80, 2, 12)), "12 sektorů DD ne");
        Assert(dev.SupportsGeometry(new DiskGeometry(80, 2, 21)), "DMF ano");

        var none = new FakeFdc { DiskPresent = false };
        Eq(MediaState.NoDisk, new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(none, "A:").MediaState, "bez diskety");
    }


    // ------------------------------------------------------------------ Greaseweazle (simulovaný přes gw convert)

    private static void T_Diag_RpmStats()
    {
        var st = new AtariFloppy.Core.Drives.Diag.RpmStats();
        foreach (var ms in new[] { 199.8, 200.0, 200.2, 50.0 }) st.Add(ms);   // 50 ms = nesmysl, zahodí se
        Eq(3, st.Count, "počet otáček");
        Assert(Math.Abs(st.Rpm - 300.0) < 0.01, "průměr 300");
        Eq(300, st.Target, "cíl");
        Assert(st.Wobble > 0.25 && st.Wobble < 0.35, "kolísání ±0,3");
        Eq(AtariFloppy.Core.Drives.Diag.RpmStats.Grade.Good, st.SpeedGrade, "rychlost OK");
        var slow = new AtariFloppy.Core.Drives.Diag.RpmStats();
        slow.Add(60000.0 / 294);
        Eq(AtariFloppy.Core.Drives.Diag.RpmStats.Grade.OutOfSpec, slow.SpeedGrade, "294 je mimo normu");
        var hd = new AtariFloppy.Core.Drives.Diag.RpmStats();
        hd.Add(60000.0 / 359);
        Eq(360, hd.Target, "5,25\" HD cíl 360");
        Eq(AtariFloppy.Core.Drives.Diag.RpmStats.Grade.Good, hd.SpeedGrade, "359 OK");
        var live = new AtariFloppy.Core.Drives.Diag.RpmStats(window: 2);
        foreach (var ms in new[] { 210.0, 200.0, 200.0 }) live.Add(ms);
        Assert(Math.Abs(live.Rpm - 300) < 0.01, "živé měření bere jen poslední otáčky");
    }

    private static void T_Diag_TrackLayout()
    {
        AtariFloppy.Core.Drives.Fdraw.SectorId Id(int c, int h, int r, int n = 2) => new((byte)c, (byte)h, (byte)r, (byte)n);
        var std = new TrackLayout(3, 1, 200_000, Enumerable.Range(1, 9).Select(r => ((int?)(r * 20000), Id(3, 1, r))).ToList(), 9);
        Assert(std.IsStandard, "běžná stopa: " + string.Join("; ", std.Problems));
        Assert(std.Describe().Contains("1 2 3 4 5 6 7 8 9"), "pořadí ve výpisu");
        var odd = new TrackLayout(79, 0, null, new List<(int?, AtariFloppy.Core.Drives.Fdraw.SectorId)>
            { (null, Id(79, 0, 1)), (null, Id(79, 0, 2)), (null, Id(79, 0, 2)), (null, Id(66, 0, 4, 3)) }, 9);
        Assert(!odd.IsStandard, "ochrana");
        Assert(odd.Problems.Any(p => p.Contains("2×")), "duplicitní sektor");
        Assert(odd.Problems.Any(p => p.Contains("66")), "cizí stopa v hlavičce");
        Assert(odd.Problems.Any(p => p.Contains("3")), "chybí sektor 3");
        var empty = new TrackLayout(5, 0, null, new List<(int?, AtariFloppy.Core.Drives.Fdraw.SectorId)>(), 9);
        Assert(!empty.IsStandard && empty.Describe().Contains("MFM"), "prázdná stopa");
        var spare = new TrackLayout(4, 0, null, new[] { 1, 2, 0xF0, 3 }.Select(r => ((int?)null, Id(4, 0, r))).ToList(), 3);
        Assert(spare.IsStandard && spare.Describe().Contains("F0*"), "sektor navíc z opravného formátu není chyba");
    }

    private static void T_Diag_FdrawSensorsAndTrackRead()
    {
        var img = FormatProfiles.Find("atari")!.Build!("DIAG");
        var fdc = FakeFdc.WithImage(img);
        fdc.WriteProtected = true;
        using var diag = new AtariFloppy.Core.Drives.Diag.FdrawDiag(new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "B:"));
        var s = diag.Sensors(default);
        Eq(true, s.Track0AtZero, "stopa 0 na stopě 0");
        Eq(false, s.Track0Elsewhere, "stopa 0 mimo");
        Eq(true, s.WriteProtected, "ochrana");
        Eq(true, s.IndexSeen, "index");
        fdc.Track0SensorStuck = true;
        Eq(true, diag.Sensors(default).Track0Elsewhere, "zaseknuté čidlo stopy 0");

        var st = new AtariFloppy.Core.Drives.Diag.RpmStats();
        diag.MeasureRpm(5, st.Add, default);
        Eq(5, st.Count, "5 otáček");
        Assert(Math.Abs(st.Rpm - 300.75) < 0.1, "otáčky z doby otáčky");
        Assert(!fdc.MotorOn, "motor po měření vypnutý");

        diag.PrepareTrackRead(default);
        fdc.Tracks[(22, 0)][5].CrcError = true;
        var pass = diag.ReadTrack(22, 0, default);
        Eq(8, pass.Found, "8 z 9");
        Eq(6, pass.FailedSectors.Single(), "vadný sektor 6");
        Eq(0, fdc.Cyl - 22, "hlava na stopě 22");

        var layout = diag.Layout(0, 0, 9);
        Assert(layout.IsStandard && layout.TrackTimeUs == 199_500 && layout.Sectors[0].Us == 3000, "časovaný sken");
        diag.Clean(80, 1, _ => { }, default);
        Eq(0, fdc.Cyl, "po čištění hlava na stopě 0");
    }

    private static void T_Diag_ErrorReasons()
    {
        Assert(SectorErrors.Explain(new SectorIOException("x", 23)).Contains("CRC"), "CRC");
        Assert(SectorErrors.Explain(new SectorIOException("x", 27)).Contains("hlavičk"), "sektor nenalezen");
        Eq("jiná chyba", SectorErrors.Explain(new SectorIOException("jiná chyba", 0)), "neznámý kód = původní text");
        var img = FormatProfiles.Find("atari")!.Build!("REASON");
        var fdc = FakeFdc.WithImage(img);
        fdc.Tracks[(5, 1)][3].CrcError = true;
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "B:");
        var model = new SectorMapModel();
        SurfaceTestResult r;
        using (TrackActivity.Attach(model)) r = SurfaceTest.Run(dev, WriteTestMode.None);
        long lba = img.Geometry.Lba(5, 1, 3);
        Eq(SectorStatus.Bad, r.Map[lba], "vadný sektor");
        Assert(r.Map.Reasons[lba].Contains("CRC"), "důvod v mapě testu: " + r.Map.Reasons[lba]);
        Assert((model.ReasonFor(5, 1, 3) ?? "").Contains("CRC"), "důvod v živé mapě");
        Eq(null, model.ReasonFor(5, 1, 2), "dobrý sektor bez důvodu");
    }

    private static void T_Diag_ScpHistogram()
    {
        var rnd = new Random(1);
        double G() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        double[] Rev(double jitter, double[] cells) { var l = new List<double>(); double sum = 0; while (sum < 200_000) { double v = cells[rnd.Next(cells.Length)] + jitter * G(); l.Add(v); sum += v; } return l.ToArray(); }
        var dd = new[] { 4.0, 6.0, 8.0 };
        var tracks = new Dictionary<(int, int), List<double[]>>
        {
            [(0, 0)] = new() { Rev(0.06, dd), Rev(0.06, dd) },
            [(22, 1)] = new() { Rev(0.4, dd), Rev(0.4, dd) },
            [(3, 0)] = new() { Rev(0.05, new[] { 2.0, 3.0, 4.0 }), Rev(0.05, new[] { 2.0, 3.0, 4.0 }) },
        };
        var scp = AtariFloppy.Core.Formats.ScpFile.Parse(AtariFloppy.Core.Formats.ScpFile.Build(tracks, 2));
        Assert(scp.HasTrack(22, 1) && !scp.HasTrack(22, 0), "stopy v souboru");
        Eq(2, scp.RevolutionMs(0, 0).Count, "2 otáčky");
        Assert(Math.Abs(scp.RevolutionMs(0, 0)[0] - 200) < 1, "doba otáčky 200 ms");
        var good = FluxHistogram.Build(scp.FluxUs(0, 0));
        Eq(3, good.Peaks.Count, "3 špičky DD: " + string.Join(",", good.Peaks));
        Assert(Math.Abs(good.Peaks[0] - 4) < 0.15 && Math.Abs(good.Peaks[2] - 8) < 0.15, "špičky 4 a 8 µs");
        Eq(FluxHistogram.Quality.Sharp, good.Grade, "zdravá stopa");
        Assert(good.Encoding.Contains("DD"), "DD zápis");
        var bad = FluxHistogram.Build(scp.FluxUs(22, 1));
        Assert(bad.Grade is FluxHistogram.Quality.Blurred or FluxHistogram.Quality.Fair, "rozmazaná stopa: " + bad.SpreadNs);
        Assert(bad.SpreadNs > good.SpreadNs * 2, "rozptyl větší");
        Assert(FluxHistogram.Build(scp.FluxUs(3, 0)).Encoding.Contains("HD"), "HD zápis");
        Eq(FluxHistogram.Quality.Empty, FluxHistogram.Build(new List<double>()).Grade, "prázdná stopa");
        // skutečná poškozená HD stopa (Greaseweazle, stopa 7/1): špičky 1,9 / 2,9 / 3,8 a falešná 4,6 — pořád MFM HD
        var damaged = FluxHistogram.Build(Rev(0.08, new[] { 1.9, 1.9, 1.9, 2.9, 3.8, 4.6 }));
        Assert(damaged.Encoding.Contains("HD") && damaged.Encoding.StartsWith("MFM"), "poškozená HD stopa: " + damaged.Encoding);
        Assert(FluxHistogram.Build(Rev(0.05, new[] { 2.0, 4.0, 6.0 })).Encoding.Contains("GCR"), "GCR 1:2:3");
    }

    private static void T_Diag_MediaChecks()
    {
        Assert(!MediaChecks.IsDeteriorating(new[] { 0, 1, 0, 1, 0, 1 }), "kolísání není rozpad");
        Assert(MediaChecks.IsDeteriorating(new[] { 0, 0, 1, 2, 3, 4 }), "chyb přibývá = rozpad");
        Assert(!MediaChecks.IsDeteriorating(new[] { 0, 1, 3 }), "málo čtení");
        Assert(!MediaChecks.IsDeteriorating(new[] { 5, 5, 5, 5, 5 }), "stále stejně vadná není rozpad");
        Eq('A', MediaChecks.Grade(1440, 0, 0).grade, "A");
        Eq('B', MediaChecks.Grade(1440, 3, 0).grade, "B");
        Eq('C', MediaChecks.Grade(1440, 40, 0).grade, "C slabé");
        Eq('C', MediaChecks.Grade(1440, 0, 1).grade, "C vadné");
    }

    private static void T_Diag_ReadHistory()
    {
        string file = Path.Combine(_tmp, "history.csv");
        if (File.Exists(file)) File.Delete(file);
        var h = new ReadHistory(file);
        var t = DateTime.Now;
        // stejná disketa: v A: chyby, v B: bez chyb → problém v A:
        h.Add(new ReadRecord(t, "B:", "DISK1", 1440, 0, 0, 0));
        var cur = new ReadRecord(t.AddMinutes(1), "A:", "DISK1", 1440, 6, 0, 6);
        h.Add(cur);
        var m = h.Analyze(cur);
        Assert(m.Any(x => x.warn && x.text.Contains("B:")), "problém je v této mechanice");
        Assert(m.Any(x => x.text.Contains("strany 1") || x.text.Contains("straně 1")), "chyby jen na straně 1");
        // opakovaně horší strana 1 a skoro každá disketa vadná
        for (int i = 2; i <= 5; i++) h.Add(new ReadRecord(t.AddMinutes(i), "A:", "DISK" + i, 1440, 9, 1, 8));
        var last = new ReadRecord(t.AddMinutes(9), "A:", "DISK9", 1440, 7, 0, 7);
        h.Add(last);
        m = h.Analyze(last);
        Assert(m.Any(x => x.warn && x.text.Contains("horní hlava")), "horší strana 1 = horní hlava");
        Assert(m.Any(x => x.warn && x.text.Contains("Skoro každá")), "vadná mechanika");
        var reload = new ReadHistory(file);
        Eq(h.Records.Count, reload.Records.Count, "historie se uloží a načte");
        Eq("", ReadHistory.DiskKey(new byte[512]), "prázdný boot sektor nemá otisk");
        Eq(16, ReadHistory.DiskKey(new byte[] { 1, 2, 3 }).Length, "otisk 8 bajtů");
    }

    private static void T_Diag_StepCheck()
    {
        var img = FormatProfiles.Find("atari")!.Build!("STEP");
        var fdc = FakeFdc.WithImage(img);
        using var diag = new AtariFloppy.Core.Drives.Diag.FdrawDiag(new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "B:"));
        diag.PrepareTrackRead(default);
        Assert(diag.StepCheck(40, 0, false, default).ok && diag.StepCheck(40, 0, true, default).ok, "zdravá mechanika");
        fdc.LosesStepsOutward = true;
        Assert(diag.StepCheck(40, 0, false, default).ok, "zvenku v pořádku");
        var (ok, detail) = diag.StepCheck(40, 0, true, default);
        Assert(!ok && detail.Contains("41"), "zevnitř ztratí krok: " + detail);
    }

    private static void T_Gw_ImdCylinderIds()
    {
        // stopa 79 normálně, „stopa 80“ s hlavičkami stopy 79 (hlava dál nedojela) → mapa válců
        var ms = new MemoryStream();
        var hdr = System.Text.Encoding.ASCII.GetBytes("IMD 1.18: test\r\n"); ms.Write(hdr, 0, hdr.Length); ms.WriteByte(0x1A);
        void Track(int cyl, int[]? cmap)
        {
            ms.WriteByte(5); ms.WriteByte((byte)cyl); ms.WriteByte((byte)(cmap != null ? 0x80 : 0)); ms.WriteByte(2); ms.WriteByte(2);
            ms.WriteByte(1); ms.WriteByte(2);
            if (cmap != null) foreach (int c in cmap) ms.WriteByte((byte)c);
            ms.WriteByte(1); ms.Write(new byte[512], 0, 512);      // celý sektor
            ms.WriteByte(2); ms.WriteByte(0xE5);                   // komprimovaný sektor
        }
        Track(79, null);
        Track(80, new[] { 79, 79 });
        string f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, ms.ToArray());
            var ids = AtariFloppy.Core.Drives.Gw.GwSession.ImdCylinderIds(f)!;
            Assert(ids[(79, 0)].SequenceEqual(new[] { 79, 79 }), "stopa 79");
            Assert(ids[(80, 0)].SequenceEqual(new[] { 79, 79 }), "stopa 80 má hlavičky stopy 79");
        }
        finally { File.Delete(f); }
    }

    private static void T_Flux_MfmDecodeAndRescue()
    {
        // malá „disketa“ 3 stopy × 2 strany × 9 sektorů DD, zakódovaná do toku MFM s chvěním
        var g = new DiskGeometry(3, 2, 9);
        var rnd = new Random(7);
        byte[] Data(int c, int h, int r) { var d = new byte[512]; for (int i = 0; i < 512; i++) d[i] = (byte)(c * 31 + h * 7 + r * 13 + i); return d; }
        double[] Track(int c, int h, bool corrupt) =>
            MfmDecoder.Encode(Enumerable.Range(1, 9).Select(r => (c, h, r, Data(c, h, r))), 2.0, corrupt).Select(x => x + (rnd.NextDouble() - 0.5) * 0.3).ToArray();
        string dir = Path.Combine(Path.GetTempPath(), "dtk-flux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // soubor A: stopa 1 strana 1 má ve všech otáčkách vadná data; soubor B (jiná mechanika) ji má dobře, ale chybí mu stopa 2
            var a = new Dictionary<(int, int), List<double[]>>();
            var b = new Dictionary<(int, int), List<double[]>>();
            for (int c = 0; c < 3; c++)
                for (int h = 0; h < 2; h++)
                {
                    bool bad = c == 1 && h == 1;
                    a[(c, h)] = new() { Track(c, h, bad), Track(c, h, bad) };
                    if (c != 2) b[(c, h)] = new() { Track(c, h, false), Track(c, h, false) };
                }
            string pa = Path.Combine(dir, "a.scp"), pb = Path.Combine(dir, "b.scp");
            File.WriteAllBytes(pa, ScpFile.Build(a, 2));
            File.WriteAllBytes(pb, ScpFile.Build(b, 2));

            var dec = MfmDecoder.Decode(ScpFile.Load(pa).FluxUs(0, 0), 2.0, 0.05, 0);
            Eq(18, dec.Count(s => s.Ok), "dvě otáčky × 9 sektorů s platným CRC");
            Assert(dec.First(s => s.Sector == 5).Data.SequenceEqual(Data(0, 0, 5)), "obsah sektoru");
            Assert(MfmDecoder.Decode(ScpFile.Load(pa).FluxUs(1, 1), 2.0, 0.05, 0).All(s => !s.Ok), "poškozená data mají špatné CRC");

            var one = FluxRescue.Run(new[] { pa });
            Eq(g, one.Geometry, "formát z toku");
            Eq(9, one.MissingLbas().Count(), "z jednoho souboru chybí stopa 1/1");
            var both = FluxRescue.Run(new[] { pa, pb });
            Eq(0, both.MissingLbas().Count(), "dva soubory se doplní");
            Eq(9, both.FromFile[1], "druhý soubor dodal jen chybějící");
            var img = new DiskImage(g);
            both.FillImage(img);
            Assert(img.Data.Skip((int)g.Offset(1, 1, 3)).Take(512).SequenceEqual(Data(1, 1, 4)), "sektor z druhého souboru v obrazu");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void T_Rescue_VoteAndRetry()
    {
        // hlasování: 9 kopií, každá s jinými 6 pokaženými bity → původní obsah
        var rnd = new Random(3);
        var orig = new byte[512]; rnd.NextBytes(orig);
        var copies = Enumerable.Range(0, 9).Select(_ => { var c = (byte[])orig.Clone(); for (int i = 0; i < 6; i++) c[rnd.Next(512)] ^= (byte)(1 << rnd.Next(8)); return c; }).ToList();
        var (voted, uncertain) = MediaChecks.Vote(copies);
        Assert(voted.SequenceEqual(orig), "hlasování vrátí původní data");
        Eq(0, uncertain, "žádné nejisté bity");

        // fdrawcmd: trvale vadný sektor se poskládá hlasováním, ostatní data zůstanou
        var img = FormatProfiles.Find("atari")!.Build!("RESCUE");
        new Random(9).NextBytes(img.Data);
        var fdc = FakeFdc.WithImage(img);
        fdc.Tracks[(5, 0)][2].CrcError = true;
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "B:");
        var result = new OperationResult();
        var read = DiskOperations.ReadDisk(dev, SideMode.Auto, result);
        long lba = img.Geometry.Lba(5, 0, 2);
        var st = new RescueState(read.Geometry);
        foreach (var bsec in result.BadSectors) st.Missing.Add(read.Geometry.Lba(bsec.Track, bsec.Side, bsec.Sector - 1));
        Eq(1, st.Missing.Count, "jeden chybějící sektor");
        var rr = Rescue.RetryMissing(dev, read, st, 15, vote: true);
        Eq(1, rr.NewlyVoted, "poskládán hlasováním");
        Assert(st.Voted.Contains(lba) && st.Missing.Contains(lba), "poskládaný zůstává mezi chybějícími");
        Assert(read.Data.Skip((int)lba * 512).Take(512).SequenceEqual(img.Data.Skip((int)lba * 512).Take(512)), "poskládaná data = původní");

        // bez hlasování: sektor, který se napodruhé přečte, se doplní
        var mem = new MemoryDevice(RandomImage(DiskGeometry.Dd720, 4));
        long flaky = DiskGeometry.Dd720.Lba(12, 1, 6);
        mem.FlakySectors[flaky] = 9;
        var r2 = new OperationResult();
        var img2 = DiskOperations.ReadDisk(mem, SideMode.Double, r2);
        var st2 = new RescueState(img2.Geometry);
        foreach (var bsec in r2.BadSectors) st2.Missing.Add(img2.Geometry.Lba(bsec.Track, bsec.Side, bsec.Sector - 1));
        Assert(st2.Missing.Contains(flaky), "napoprvé nepřečtený");
        var rr2 = Rescue.RetryMissing(mem, img2, st2, 10, vote: false);
        Eq(1, rr2.Recovered, "doplněn opakovaným čtením");
        Eq(0, st2.Missing.Count, "nic nechybí");

        // stav se uloží a načte
        string path = Path.Combine(_tmp, "zachrana.st");
        st.History.Add("test");
        st.Save(path);
        var back = RescueState.Load(path, read.Geometry)!;
        Assert(back.Missing.SetEquals(st.Missing) && back.Voted.SetEquals(st.Voted) && back.History.Contains("test"), "stav záchrany");
    }

    private static void T_Diag_GwParsers()
    {
        Eq(200.123, AtariFloppy.Core.Drives.Gw.GwSession.ParsePeriodMs("Rate: 299.815 rpm ; Period: 200.123 ms") ?? 0, "gw rpm");
        Eq(null, AtariFloppy.Core.Drives.Gw.GwSession.ParsePeriodMs("FASTEST:  Rate"), "bez čísla");
        Eq(false, AtariFloppy.Core.Drives.Gw.GwSession.ParsePin("Pin 26 is Low (0v)"), "pin nízko");
        Eq(true, AtariFloppy.Core.Drives.Gw.GwSession.ParsePin("Pin 28 is High (5v)"), "pin vysoko");
        Eq(null, AtariFloppy.Core.Drives.Gw.GwSession.ParsePin("Command Failed: Bad Pin"), "nečitelný pin");
        var o = new AtariFloppy.Core.Drives.Gw.GwOutput();
        foreach (var l in new[] { "T5.0: IBM MFM (7/9 sectors) from Raw Flux (100000 flux in 200.00ms)", "Cyl-> 0", "H. S: 5",
                                  "0. 0: .", "0. 1: .", "0. 2: X", "0. 3: .", "0. 4: .", "0. 5: X", "0. 6: .", "0. 7: .", "0. 8: .", "Found 7 sectors of 9 (77%)" })
            o.Feed(l);
        Eq("2,5", string.Join(",", AtariFloppy.Core.Drives.Gw.GwSession.MissingSectors(o, 5, 0)), "chybějící sektory ze souhrnu");
    }

    private static void T_Gw_OutputParser()
    {
        var o = new AtariFloppy.Core.Drives.Gw.GwOutput();
        foreach (var l in new[] {
            "Reading c=0-1:h=0-1 revs=2",
            "T0.0: IBM MFM (9/9 sectors) from Raw Flux (100000 flux in 200.00ms)",
            "T0.1: IBM MFM (8/9 sectors) from Raw Flux (100000 flux in 200.00ms)",
            "T0.1: IBM MFM (9/9 sectors) from Raw Flux (100000 flux in 200.00ms) (Retry #0.1)",
            "T1.0: IBM MFM (7/9 sectors) from Raw Flux (100000 flux in 200.00ms)",
            "T1.0: Giving up: 2 sectors missing",
            "T1.1: IBM MFM (9/9 sectors) from Raw Flux (100000 flux in 200.00ms)",
            "Cyl-> 0 ", "H. S: 01",
            "0. 0: ..", "0. 1: ..", "0. 2: .X", "0. 3: .X", "0. 4: ..", "0. 5: ..", "0. 6: ..", "0. 7: ..", "0. 8: ..",
            "1. 0: ..", "1. 1: ..", "1. 2: ..", "1. 3: ..", "1. 4: ..", "1. 5: ..", "1. 6: ..", "1. 7: ..", "1. 8: ..",
            "Found 34 sectors of 36 (94%)" })
            o.Feed(l);
        Eq(34, o.FoundSectors ?? -1, "nalezeno");
        Assert(o.GaveUp.Contains((1, 0)), "vzdal stopu 1.0");
        var map = o.BuildMap(new DiskGeometry(2, 2, 9));
        Eq(SectorStatus.Bad, map.Get(1, 0, 2), "X = vadný");
        Eq(SectorStatus.Weak, map.Get(0, 1, 0), "opakování = slabý");
        Eq(SectorStatus.Ok, map.Get(0, 0, 5), "OK");
        Eq(2, map.Count(SectorStatus.Bad), "2 vadné");

        var raw = new AtariFloppy.Core.Drives.Gw.GwOutput();
        Eq((12, 1), raw.Feed("T12.1: Raw Flux (100000 flux in 600.00ms)") ?? (-1, -1), "průběh zálohy toku");

        var f = new AtariFloppy.Core.Drives.Gw.GwOutput();
        f.Feed("** FATAL ERROR:"); f.Feed("No Index Pulse Detected");
        Eq("No Index Pulse Detected", f.Fatal, "fatální chyba");
        var w = new AtariFloppy.Core.Drives.Gw.GwOutput();
        w.Feed("T5.1: Writing Track (Verify Failure: Retry #1)");
        w.Feed("Command Failed: Failed to verify Track 5.1");
        Assert(w.VerifyRetries.Contains((5, 1)) && w.Fatal != null, "selhání ověření");
    }

    private static AtariFloppy.Core.Drives.Gw.GwSession FakeGw(string diskFile)
    {
        Environment.SetEnvironmentVariable("AFLOPPY_FAKE_DISK", diskFile);
        string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "fake-gw.py");
        var tool = AtariFloppy.Core.Drives.Gw.GwTool.Find(Path.GetFullPath(script)) ?? throw new Exception("fake-gw.py nenalezen");
        return new AtariFloppy.Core.Drives.Gw.GwSession(tool, "COM9", "A");
    }

    private static void ToFlux(DiskImage img, AtariFloppy.Core.Drives.Gw.GwFormat f, string flux)
    {
        string raw = Path_("src" + f.ImageExt);
        File.WriteAllBytes(raw, img.Data);
        string args = f.DiskDefs != null ? $"--diskdefs \"{Path_("defs.cfg")}\" " : "";
        if (f.DiskDefs != null) File.WriteAllText(Path_("defs.cfg"), f.DiskDefs);
        if (File.Exists(flux)) File.Delete(flux);
        string o = Sh("gw", $"convert {args}--format {f.Name} \"{raw}\" \"{flux}\"", out int c);
        Eq(0, c, "gw convert: " + o);
    }

    private static void T_Gw_ProbeReadWriteAtariExtended()
    {
        var g = new DiskGeometry(82, 2, 10);
        var img = DiskFormatter.CreateBlank(g, FormatStyle.Atari, "DEMO", new Random(6));
        for (int i = 40000; i < img.Data.Length; i++) img.Data[i] = (byte)(i * 3);
        var fmt = AtariFloppy.Core.Drives.Gw.GwFormat.ForGeometry(g);
        Assert(fmt.DiskDefs != null && fmt.DiskDefs.Contains("cyls = 82") && fmt.DiskDefs.Contains("gap3 = 30"), "vlastní diskdef");
        string disk = Path_("atari82.hfe");
        ToFlux(img, fmt, disk);

        var gw = FakeGw(disk);
        var probe = gw.Probe();
        Assert(probe.Format != null, "rozpoznáno: " + probe.Summary);
        Eq(g, probe.Format!.Geometry, "geometrie z rozpoznání");

        int progressCalls = 0;
        var r = gw.Read(probe.Format, (d, t, _) => progressCalls++);
        Assert(r.Image.Data.SequenceEqual(img.Data), "přečtená data");
        Eq(g.TotalSectors, r.Map.Count(SectorStatus.Ok), "mapa: vše OK");
        Assert(progressCalls >= g.TrackCount, "průběh po stopách");

        // zápis jiného obsahu a zpětné čtení
        var img2 = DiskFormatter.CreateBlank(g, FormatStyle.Atari, "NOVA", new Random(7));
        for (int i = 50000; i < img2.Data.Length; i++) img2.Data[i] = (byte)(i * 5);
        var wmap = new SectorMapModel();
        AtariFloppy.Core.Drives.Gw.GwOutput w;
        using (TrackActivity.Attach(wmap)) w = gw.Write(img2, fmt, verify: true);
        Eq(g.TotalSectors, wmap.Count(CellState.Verified), "mapa po zápisu gw");
        Eq(g.TrackCount, w.Written.Count, "zapsané stopy");
        var r2 = gw.Read(fmt);
        Assert(r2.Image.Data.SequenceEqual(img2.Data), "data po zápisu");
    }

    private static void T_Gw_MissingSectorsAndAmiga()
    {
        // disketa s 9 sektory, čtená jako 10sektorová → 10. sektor chybí na každé stopě
        var img9 = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Atari, null, new Random(8));
        string disk9 = Path_("atari9.hfe");
        ToFlux(img9, AtariFloppy.Core.Drives.Gw.GwFormat.ForGeometry(DiskGeometry.Dd720), disk9);
        var gw = FakeGw(disk9);
        var gmap = new SectorMapModel();
        AtariFloppy.Core.Drives.Gw.GwSession.ReadResult r;
        using (TrackActivity.Attach(gmap)) r = gw.Read(AtariFloppy.Core.Drives.Gw.GwFormat.ForGeometry(new DiskGeometry(80, 2, 10)));
        Eq(160, r.Map.Count(SectorStatus.Bad), "10. sektor chybí na 160 stopách");
        Eq(160, gmap.Count(CellState.Bad), "mapa: chybějící sektory");
        Eq(1440, gmap.Count(CellState.Read), "mapa: přečtené sektory");
        Eq(SectorStatus.Bad, r.Map.Get(33, 1, 9), "konkrétní chybějící sektor");
        Eq(SectorStatus.Ok, r.Map.Get(33, 1, 8), "9. sektor je");

        // Amiga
        var adf = AtariFloppy.Core.Fs.Amiga.AmigaDos.CreateBlank(new DiskGeometry(80, 2, 11), AtariFloppy.Core.Fs.Amiga.AmigaFsType.Ffs, "Hry");
        string diskA = Path_("amiga.scp");
        ToFlux(adf, AtariFloppy.Core.Drives.Gw.GwFormat.Amiga(false), diskA);
        var gwa = FakeGw(diskA);
        var p = gwa.Probe();
        Eq("amiga.amigados", p.Format?.Name, "Amiga rozpoznána: " + p.Summary);
        var ra = gwa.Read(p.Format!);
        Assert(ra.Image.Data.SequenceEqual(adf.Data), "Amiga data");
        Eq(FsKind.Amiga, FileSystemDetector.Detect(ra.Image.Data).Kind, "Amiga FS");

        // chybová hláška bez diskety
        var none = FakeGw(Path_("neexistuje.hfe"));
        try { none.Read(AtariFloppy.Core.Drives.Gw.GwFormat.ForGeometry(DiskGeometry.Dd720)); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("indexový puls") || e.Message.Contains("index"), e.Message); }
    }

    // ------------------------------------------------------------------ schopnosti mechanik

    private static void T_MediaSupport_IgnoresInsertedDisk()
    {
        // Sony USB-FDU s DD disketou vrací jen 640 KB + 720 KB, s HD jen 1,2 + 1,44 MB.
        var dd = new[] { 14, 5 };
        var hd = new[] { 17, 2 };
        Assert(MediaSupport.Supports720(dd) && MediaSupport.Supports1440(dd), "DD vložená → umí obojí");
        Assert(MediaSupport.Supports720(hd) && MediaSupport.Supports1440(hd), "HD vložená → umí obojí");
        Assert(MediaSupport.Supports1440(new int[0]), "prázdný seznam → předpoklad 3,5\"");
        Assert(!MediaSupport.Supports1440(new[] { 6, 9 }), "5,25\" 360 KB mechanika 1,44 neumí");
    }

    private static void T_Capabilities_PerKind()
    {
        var usb = new DetectedDrive { Kind = DriveKind.Usb, Letter = "A", Supports720 = true, Supports1440 = true };
        usb.ComputeFeatures();
        Eq(Availability.Maybe, usb.Get(Feature.Standard720), "USB 720 bez informací o disketě = neověřeno");
        Eq(Availability.Unsupported, usb.Get(Feature.ExtendedGeometry), "USB 10 sektorů");
        Eq(Availability.Unsupported, usb.Get(Feature.StepRate), "USB step rate");

        var intNo = new DetectedDrive { Kind = DriveKind.Internal, Letter = "A", Supports720 = true, Supports1440 = true };
        intNo.ComputeFeatures();
        Eq(Availability.NeedsDriver, intNo.Get(Feature.StepRate), "interní bez fdrawcmd");
        Eq(Availability.Unsupported, intNo.Get(Feature.FluxProtection), "interní weak bits");

        var intFd = new DetectedDrive { Kind = DriveKind.Internal, Letter = "A", FdrawcmdInstalled = true, Supports720 = true };
        intFd.ComputeFeatures();
        Eq(Availability.Available, intFd.Get(Feature.ExtendedGeometry), "interní s fdrawcmd: 10 sektorů");
        Eq(Availability.Available, intFd.Get(Feature.StepRate), "interní s fdrawcmd: step rate");
        Eq(Availability.Maybe, intFd.Get(Feature.ElevenSectors), "11 sektorů záleží na řadiči");

        var gw = new DetectedDrive { Kind = DriveKind.Greaseweazle, Port = "COM5" };
        gw.ComputeFeatures();
        Assert(gw.Features.Values.All(a => a == Availability.NeedsTool), "GW bez gw.exe");
        gw.GwToolPath = "gw.exe"; gw.ComputeFeatures();
        Eq(Availability.Available, gw.Get(Feature.FluxProtection), "GW s gw.exe umí tok");
        Assert(!gw.CanUseStandardAccess, "GW ne přes Windows API");
    }

    // ------------------------------------------------------------------ překlady

    private static void T_I18n_English()
    {
        try
        {
            Tr.SetLanguage("en");
            Tr.CollectMissing = true;
            Eq("en", Tr.Code, "kód jazyka");
            Assert(Tr.EmbeddedText("en") != null, "en.lang není zabudovaný");
            var ts = FormatProfiles.Find("atari")!;
            Assert(ts.UsedFor.StartsWith("Exactly like"), "profil nepřeložen: " + ts.UsedFor);
            foreach (var p in FormatProfiles.All) { _ = p.System + p.Name + p.UsedFor + p.WhyNotUsb + p.Note; }
            foreach (var f in FormatCatalog.All) { _ = f.System + f.Format + f.UsedFor + f.Note; }
            _ = DiskGeometry.Dd720.ToString();
            var cz = Tr.MissingKeys.Where(k => k.Any(c => "áčďéěíňóřšťúůýž".IndexOf(char.ToLowerInvariant(c)) >= 0)).ToList();
            Assert(cz.Count == 0, "chybí překlad: " + string.Join(" | ", cz));
            Eq("The disk is not ready: ", Tr.T("Disketa není připravená: "), "mezery na kraji");
            Assert(Tr.T("Sinclair QL (1984) s disketovým rozhraním (Trump Card, Miracle, Gold Card). QL5A = 720 KB, QL5B = 1,44 MB.").StartsWith("Sinclair QL (1984) with"), "escapované =");
            Eq("Track 5", Tr.F("Stopa {0}", 5).Replace("Stopa", "Track"), "F formát");
        }
        finally { Tr.CollectMissing = false; Tr.SetLanguage("cs"); }
        Eq("cs", Tr.Code, "zpět na češtinu");
    }

    // ------------------------------------------------------------------ živá mapa diskety

    private static void T_Map_ReadShowsBadSector()
    {
        var src = RandomImage(DiskGeometry.Dd720, 5);
        var dev = new MemoryDevice(src);
        dev.UnreadableSectors.Add(DiskGeometry.Dd720.Lba(10, 1, 4));
        var map = new SectorMapModel();
        using (TrackActivity.Attach(map)) DiskOperations.ReadDisk(dev, SideMode.Double, new OperationResult());
        Eq(CellState.Bad, map.Get(10, 1, 4), "vadný sektor v mapě");
        Eq(1439, map.Count(CellState.Read), "ostatní přečtené");
        Assert(!map.Running && map.HeadTrack == -1, "operace skončila");
        Assert(TrackActivity.Current == null, "pozorovatel odpojený");
    }

    private static void T_Map_WriteFormatVerify()
    {
        var src = RandomImage(DiskGeometry.Dd720, 6);
        var dev = new MemoryDevice();
        dev.SilentlyCorrupt.Add(DiskGeometry.Dd720.Lba(3, 0, 2));
        var map = new SectorMapModel();
        var phases = new List<TrackPhase>();
        var spy = new SpyObserver(map, phases);
        using (TrackActivity.Attach(spy)) DiskOperations.WriteImage(dev, src, format: true, verify: true, new OperationResult());
        Assert(phases.Contains(TrackPhase.Formatting) && phases.Contains(TrackPhase.Writing) && phases.Contains(TrackPhase.Verifying), "fáze");
        Eq(CellState.WriteFailed, map.Get(3, 0, 2), "nesouhlasící sektor");
        Eq(1439, map.Count(CellState.Verified), "ověřené");
    }

    private static void T_Map_SurfaceTestAndFormat()
    {
        var src = RandomImage(DiskGeometry.Dd720, 7);
        var dev = new MemoryDevice(src);
        dev.FlakySectors[DiskGeometry.Dd720.Lba(5, 1, 0)] = 2;
        var map = new SectorMapModel();
        using (TrackActivity.Attach(map)) SurfaceTest.Run(dev, WriteTestMode.None);
        Eq(CellState.Weak, map.Get(5, 1, 0), "slabý sektor");

        var dev2 = new MemoryDevice();
        var map2 = new SectorMapModel();
        using (TrackActivity.Attach(map2)) DiskOperations.Format(dev2, DiskGeometry.Dd720, FormatStyle.Atari, null, quick: false, verify: false, new OperationResult());
        Eq(CellState.Written, map2.Get(0, 0, 0), "boot sektor zapsán");
        Eq(CellState.Formatted, map2.Get(79, 1, 8), "poslední stopa naformátovaná");
    }

    private sealed class SpyObserver : ITrackObserver
    {
        private readonly ITrackObserver _inner; private readonly List<TrackPhase> _phases;
        public SpyObserver(ITrackObserver inner, List<TrackPhase> phases) { _inner = inner; _phases = phases; }
        public void Begin(DiskGeometry g, string title) => _inner.Begin(g, title);
        public void Head(int t, int s, TrackPhase p) { if (!_phases.Contains(p)) _phases.Add(p); _inner.Head(t, s, p); }
        public void Sector(int t, int s, int n, CellState st) => _inner.Sector(t, s, n, st);
        public void Track(int t, int s, CellState st) => _inner.Track(t, s, st);
        public void Reason(int t, int s, int n, string text) => _inner.Reason(t, s, n, text);
        public void End() => _inner.End();
    }

    // ------------------------------------------------------------------ stažení doplňků (místní „GitHub“)

    private static void T_Setup_GreaseweazleDownload()
    {
        string web = Path_("web");
        Directory.CreateDirectory(web);
        // balík jako z GitHubu: greaseweazle-1.23/gw.exe + CAPSImg.dll
        string pkg = Path_("pkg"), inner = Path.Combine(pkg, "greaseweazle-1.23");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "gw.exe"), "fake gw");
        File.WriteAllText(Path.Combine(inner, "CAPSImg.dll"), "fake dll");
        foreach (var n in new[] { "greaseweazle-1.23-win64.zip", "greaseweazle-1.23-win32.zip" })
            System.IO.Compression.ZipFile.CreateFromDirectory(pkg, Path.Combine(web, n));
        File.WriteAllBytes(Path.Combine(web, "FdInstall.exe"), new byte[] { 0x4D, 0x5A, 1, 2, 3 });

        var srv = new TcpListenerPort();
        int port = srv.Port;
        var py = Process.Start(new ProcessStartInfo("python3", $"-m http.server {port} --bind 127.0.0.1")
        { WorkingDirectory = web, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            string baseUrl = $"http://127.0.0.1:{port}/";
            string json = "{\"tag_name\":\"v1.23\",\"name\":\"Greaseweazle Tools v1.23\",\"assets\":[" +
                          "{\"name\":\"greaseweazle-1.23.zip\",\"size\":100,\"browser_download_url\":\"" + baseUrl + "x.zip\"}," +
                          "{\"name\":\"greaseweazle-1.23-win32.zip\",\"size\":200,\"browser_download_url\":\"" + baseUrl + "greaseweazle-1.23-win32.zip\"}," +
                          "{\"name\":\"greaseweazle-1.23-win64.zip\",\"size\":300,\"browser_download_url\":\"" + baseUrl + "greaseweazle-1.23-win64.zip\",\"extra\":{\"a\":1}}]}";
            File.WriteAllText(Path.Combine(web, "latest.json"), json);
            for (int i = 0; i < 50; i++) { try { new System.Net.WebClient().DownloadString(baseUrl + "latest.json"); break; } catch { System.Threading.Thread.Sleep(100); } }

            var r64 = AtariFloppy.Core.App.GreaseweazleSetup.ParseRelease(json, true);
            Eq("greaseweazle-1.23-win64.zip", r64.AssetName, "win64 balík");
            Eq("1.23", r64.Version, "verze");
            Eq("greaseweazle-1.23-win32.zip", AtariFloppy.Core.App.GreaseweazleSetup.ParseRelease(json, false).AssetName, "win32 balík");
            Assert(AtariFloppy.Core.App.GreaseweazleSetup.CompareVersions("1.9", "1.23") < 0, "1.9 < 1.23");
            Assert(AtariFloppy.Core.App.GreaseweazleSetup.CompareVersions("1.23", "1.23") == 0, "rovnost");

            AtariFloppy.Core.App.GreaseweazleSetup.ApiUrl = baseUrl + "latest.json";
            var latest = AtariFloppy.Core.App.GreaseweazleSetup.GetLatest();
            string dir = Path_("install/greaseweazle");
            long seen = 0;
            string gw = AtariFloppy.Core.App.GreaseweazleSetup.Install(latest, (d, t) => seen = d, default, dir);
            Eq(Path.Combine(dir, "gw.exe"), gw, "cesta ke gw.exe");
            Assert(File.Exists(Path.Combine(dir, "CAPSImg.dll")), "CAPSImg.dll vedle gw.exe");
            Eq("1.23", AtariFloppy.Core.App.GreaseweazleSetup.VersionOf(gw), "zapsaná verze");
            Assert(seen > 0, "průběh stahování");
            // druhá instalace nahradí první
            File.WriteAllText(Path.Combine(dir, "stary.txt"), "x");
            AtariFloppy.Core.App.GreaseweazleSetup.Install(latest, null, default, dir);
            Assert(!File.Exists(Path.Combine(dir, "stary.txt")) && File.Exists(gw), "nahrazení");
            Assert(!Directory.Exists(dir + ".new") && !Directory.Exists(dir + ".old"), "úklid");

            AtariFloppy.Core.App.FdrawcmdSetup.DownloadUrl = baseUrl + "FdInstall.exe";
            string fd = AtariFloppy.Core.App.FdrawcmdSetup.Download();
            Eq(5L, new FileInfo(fd).Length, "FdInstall.exe staženo");
            Assert(AtariFloppy.Core.App.FdrawcmdSetup.Signer(fd) == null, "nepodepsaný soubor");

            AtariFloppy.Core.App.GreaseweazleSetup.ApiUrl = baseUrl + "neni.json";
            try { AtariFloppy.Core.App.GreaseweazleSetup.GetLatest(); throw new Exception("prošlo"); }
            catch (OperationException e) { Assert(e.Message.Contains("127.0.0.1"), e.Message); }
        }
        finally { try { py.Kill(); } catch { } }
    }

    private sealed class TcpListenerPort
    {
        public int Port { get; }
        public TcpListenerPort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            Port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
        }
    }

    // ------------------------------------------------------------------ spouštěcí diskety

    private static string DataFile(string name) => Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "data", name));

    private static void T_Fat12Writer_FilesAndFolders()
    {
        var img = DiskFormatter.CreateBlank(DiskGeometry.Dd720, FormatStyle.Hybrid, "ZAPIS", new Random(3));
        var w = new Fat12Writer(img.Data);
        var big = new byte[100_000]; new Random(4).NextBytes(big);
        w.AddFile("VELKY.BIN", big);
        for (int i = 0; i < 40; i++) w.AddFile($"SLOZKA\\PODSLOZ\\SOUBOR{i:D2}.TXT", System.Text.Encoding.ASCII.GetBytes("obsah " + i));
        w.AddFile("dlouhé jméno.textovy", new byte[] { 1, 2, 3 });
        string f = Path_("zapis.img");
        File.WriteAllBytes(f, img.Data);
        string o = Sh("fsck.fat", $"-n \"{f}\"", out int code);
        Eq(0, code, "fsck.fat: " + o);
        var vol = Fat12Volume.TryOpen(img.Data, out _)!;
        var all = vol.ListAll();
        Assert(vol.ReadFile(all.First(e => e.Name == "VELKY.BIN")).SequenceEqual(big), "velký soubor");
        Eq(40, all.Count(e => e.Path.StartsWith("SLOZKA\\PODSLOZ\\SOUBOR")), "40 souborů v podsložce (víc clusterů adresáře)");
        Eq("obsah 39", System.Text.Encoding.ASCII.GetString(vol.ReadFile(all.First(e => e.Name == "SOUBOR39.TXT"))), "obsah");
        Assert(all.Any(e => e.Name == "DLOUHE_J.TEX"), "jméno 8.3: " + string.Join(",", all.Select(e => e.Name)));
        o = Sh("mtype", $"-i \"{f}\" ::/SLOZKA/PODSLOZ/SOUBOR07.TXT", out code);
        Eq("obsah 7", o.Trim(), "mtools čte soubor");
        try { w.AddFile("PLNA.BIN", new byte[800_000]); throw new Exception("prošlo"); }
        catch (OperationException e) { Assert(e.Message.Contains("místa"), e.Message); }
    }

    private static void T_Boot_FreeDos()
    {
        Environment.SetEnvironmentVariable("AFLOPPY_FREEDOS", Path_("fdcache"));
        Directory.CreateDirectory(Path_("fdcache"));
        File.Copy(DataFile("KERNEL.SYS"), Path_("fdcache/KERNEL.SYS"), true);
        File.Copy(DataFile("COMMAND.COM"), Path_("fdcache/COMMAND.COM"), true);
        try
        {
            foreach (var g in new[] { DiskGeometry.Hd1440, DiskGeometry.Dd720 })
            {
                var img = AtariFloppy.Core.Boot.BootDisks.Build(new AtariFloppy.Core.Boot.BootOptions { System = AtariFloppy.Core.Boot.BootSystem.FreeDos, Geometry = g, FreeDosTools = false });
                Eq(g, img.Geometry, "geometrie");
                Assert(img.Data[0] == 0xEB && img.Data[510] == 0x55 && img.Data[511] == 0xAA, "zaváděcí sektor");
                var boot = AtariFloppy.Core.Boot.BootDisks.FreeDosBootSector;
                Assert(img.Data.Skip(0x3E).Take(512 - 0x3E).SequenceEqual(boot.Skip(0x3E).Take(512 - 0x3E)), "kód zavaděče FreeDOS");
                Assert(FileSystemDetector.Detect(img.Data).Details!.Contains("FreeDOS"), "rozpoznáno jako FreeDOS");
                string f = Path_($"freedos{g.Short}.img");
                File.WriteAllBytes(f, img.Data);
                Eq(0, RunCode("fsck.fat", $"-n \"{f}\""), "fsck.fat");
                var vol = Fat12Volume.TryOpen(img.Data, out _)!;
                var kernel = vol.ListAll().First(e => e.Name == "KERNEL.SYS");
                Assert(vol.ReadFile(kernel).SequenceEqual(File.ReadAllBytes(DataFile("KERNEL.SYS"))), "KERNEL.SYS");
                Assert(kernel.IsSystem && kernel.IsHidden, "atributy KERNEL.SYS");
            }
        }
        finally { Environment.SetEnvironmentVariable("AFLOPPY_FREEDOS", null); }
    }

    private static int RunCode(string exe, string args) { Sh(exe, args, out int c); return c; }

    private static void T_Boot_FreeDosDownloadAndPick()
    {
        // balíky jako v repozitáři FreeDOS 1.3: kernel.zip (BIN/KERNL86.SYS, BIN/KERNL386.SYS), freecom.zip (BIN/COMMAND.COM)
        string web = Path_("fdweb");
        Directory.CreateDirectory(web);
        using (var z = System.IO.Compression.ZipFile.Open(Path.Combine(web, "kernel.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            void Add(string name, byte[] data) { using var s = z.CreateEntry(name).Open(); s.Write(data, 0, data.Length); }
            Add("APPINFO/KERNEL.LSM", new byte[10]);
            Add("BIN/KERNL386.SYS", new byte[] { 3, 8, 6 });
            Add("BIN/KERNL86.SYS", File.ReadAllBytes(DataFile("KERNEL.SYS")));
            Add("SOURCE/KERNEL/BIN/KERNEL.SYS", new byte[] { 9 });
        }
        using (var z = System.IO.Compression.ZipFile.Open(Path.Combine(web, "freecom.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("BIN/COMMAND.COM").Open()) { var d = File.ReadAllBytes(DataFile("COMMAND.COM")); s.Write(d, 0, d.Length); }
            using (var s = z.CreateEntry("SOURCE/FREECOM/COMMAND.COM").Open()) s.WriteByte(1);
        }
        // nástroje: format.zip a edit.zip existují, ostatní na „serveru“ chybí (disketa vznikne i tak)
        foreach (var (pkg, file) in new[] { ("format", "FORMAT.EXE"), ("edit", "EDIT.EXE") })
            using (var z = System.IO.Compression.ZipFile.Open(Path.Combine(web, pkg + ".zip"), System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("BIN/" + file).Open()) { var d = new byte[30000]; d[0] = (byte)'M'; d[1] = (byte)'Z'; s.Write(d, 0, d.Length); }
                using (var s = z.CreateEntry("SOURCE/" + file).Open()) s.WriteByte(1);
            }
        int port = new TcpListenerPort().Port;
        var py = Process.Start(new ProcessStartInfo("python3", $"-m http.server {port} --bind 127.0.0.1")
        { WorkingDirectory = web, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var oldK = AtariFloppy.Core.Boot.FreeDosFiles.KernelUrls;
        var oldC = AtariFloppy.Core.Boot.FreeDosFiles.FreecomUrls;
        try
        {
            string b = $"http://127.0.0.1:{port}/";
            for (int i = 0; i < 50; i++) { try { new System.Net.WebClient().DownloadString(b); break; } catch { System.Threading.Thread.Sleep(100); } }
            AtariFloppy.Core.Boot.FreeDosFiles.KernelUrls = new[] { b + "neni.zip", b + "kernel.zip" };   // první adresa selže → záložní
            AtariFloppy.Core.Boot.FreeDosFiles.FreecomUrls = new[] { b + "freecom.zip" };
            Environment.SetEnvironmentVariable("AFLOPPY_FREEDOS", Path_("fddl"));
            Assert(!AtariFloppy.Core.Boot.FreeDosFiles.Cached, "prázdná cache");
            var (k, c, v) = AtariFloppy.Core.Boot.FreeDosFiles.Get(null, default);
            Assert(k.SequenceEqual(File.ReadAllBytes(DataFile("KERNEL.SYS"))), "vybrán KERNL86.SYS");
            Assert(c.SequenceEqual(File.ReadAllBytes(DataFile("COMMAND.COM"))), "vybrán BIN/COMMAND.COM");
            Eq("1.3", v, "verze");
            Assert(AtariFloppy.Core.Boot.FreeDosFiles.Cached && !File.Exists(Path_("fddl/kernel.zip")), "cache a úklid");

            AtariFloppy.Core.Boot.FreeDosFiles.BaseUrl = b;
            var tools = AtariFloppy.Core.Boot.FreeDosFiles.GetTools(null, default);
            Assert(tools.ContainsKey("FORMAT.EXE") && tools.ContainsKey("EDIT.EXE") && !tools.ContainsKey("SYS.COM") && tools.Count == 2, "nástroje: " + string.Join(",", tools.Keys));
            var img = AtariFloppy.Core.Boot.BootDisks.Build(new AtariFloppy.Core.Boot.BootOptions { System = AtariFloppy.Core.Boot.BootSystem.FreeDos, Geometry = DiskGeometry.Hd1440 });
            var files = Fat12Volume.TryOpen(img.Data, out _)!.ListAll();
            Assert(files.Any(f => f.Name == "FORMAT.EXE") && files.Any(f => f.Name == "EDIT.EXE"), "nástroje na disketě");
            string autoexec = System.Text.Encoding.ASCII.GetString(Fat12Volume.TryOpen(img.Data, out _)!.ReadFile(files.First(f => f.Name == "AUTOEXEC.BAT")));
            Assert(autoexec.Contains("EDIT") && autoexec.Contains("FORMAT"), autoexec);
            string fimg = Path_("fd-tools.img");
            File.WriteAllBytes(fimg, img.Data);
            Eq(0, RunCode("fsck.fat", $"-n \"{fimg}\""), "fsck.fat s nástroji");
        }
        finally
        {
            AtariFloppy.Core.Boot.FreeDosFiles.KernelUrls = oldK;
            AtariFloppy.Core.Boot.FreeDosFiles.BaseUrl = "https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/repositories/1.3/base/";
            AtariFloppy.Core.Boot.FreeDosFiles.FreecomUrls = oldC;
            Environment.SetEnvironmentVariable("AFLOPPY_FREEDOS", null);
            try { py.Kill(); } catch { }
        }
    }

    private static void T_Boot_MsDosFromDiskcopyDll()
    {
        foreach (var dll in new[] { "diskcopy-x86_64.dll", "diskcopy-i686.dll" })
        {
            var pe = File.ReadAllBytes(DataFile(dll));
            var res = AtariFloppy.Core.Boot.PeResources.Find(pe, "BINFILE", 1);
            Assert(res != null && res.Length == 300000, dll + ": prostředek BINFILE #1");
            Assert(AtariFloppy.Core.Boot.PeResources.Find(pe, "BINFILE", 2)!.Length == 3, "prostředek #2");
            Assert(AtariFloppy.Core.Boot.PeResources.Find(pe, "RCDATA", 1) == null, "jiný typ není");
            Assert(AtariFloppy.Core.Boot.PeResources.Find(pe, "BINFILE", 7) == null, "jiné číslo není");
            Environment.SetEnvironmentVariable("AFLOPPY_DISKCOPY", DataFile(dll));
            try
            {
                var img = AtariFloppy.Core.Boot.WindowsMsDos.Load();
                Eq(DiskGeometry.Hd1440, img.Geometry, "1,44 MB");
                Assert(Fat12Volume.TryOpen(img.Data, out _)!.ListAll().Any(e => e.Name == "IO.SYS"), "IO.SYS");
                Assert(AtariFloppy.Core.Boot.WindowsMsDos.IsAvailable(out _), "k dispozici");
            }
            finally { Environment.SetEnvironmentVariable("AFLOPPY_DISKCOPY", null); }
        }
        Assert(AtariFloppy.Core.Boot.PeResources.Find(new byte[100], "BINFILE", 1) == null, "nesmysl");
        Assert(!AtariFloppy.Core.Boot.WindowsMsDos.IsAvailable(out string why) && why.Length > 0, "na Linuxu diskcopy.dll není");
    }

    private static void T_Boot_AmigaInstall()
    {
        foreach (var (sys, ofs) in new[] { (AtariFloppy.Core.Boot.BootSystem.AmigaFfs, false), (AtariFloppy.Core.Boot.BootSystem.AmigaOfs, true) })
        {
            var img = AtariFloppy.Core.Boot.BootDisks.Build(new AtariFloppy.Core.Boot.BootOptions { System = sys, Label = "Boot" });
            Assert(AtariFloppy.Core.Fs.Amiga.AmigaDos.IsBootable(img.Data), "spustitelná");
            Assert(FileSystemDetector.Detect(img.Data).Details!.Contains("spustitelná"), "detektor");
            string f = Path_($"boot{(ofs ? "ofs" : "ffs")}.adf");
            File.WriteAllBytes(f, img.Data);
            string o = Sh("xdftool", $"\"{f}\" boot show", out _);
            Assert(o.Contains("valid") && !o.Contains("invalid") && !o.ToLowerInvariant().Contains("bad") , "xdftool: " + o);
            o = Sh("xdftool", $"\"{f}\" info", out int code);
            Eq(0, code, "xdftool info: " + o);
            Assert(AtariFloppy.Core.Fs.Amiga.AmigaDos.TryOpen(img.Data) != null, "souborový systém zůstal");
        }
        Assert(!AtariFloppy.Core.Fs.Amiga.AmigaDos.IsBootable(AtariFloppy.Core.Fs.Amiga.AmigaDos.CreateBlank(new DiskGeometry(80, 2, 11), AtariFloppy.Core.Fs.Amiga.AmigaFsType.Ffs, "x").Data), "prázdná není spustitelná");
    }

    private static void T_Boot_AtariAuto()
    {
        var prg = new byte[200];
        prg[0] = 0x60; prg[1] = 0x1A; prg[5] = 100;    // hlavička GEMDOS programu, 100 B kódu
        var img = AtariFloppy.Core.Boot.BootDisks.Build(new AtariFloppy.Core.Boot.BootOptions
        { System = AtariFloppy.Core.Boot.BootSystem.AtariAuto, Geometry = DiskGeometry.Dd720, AtariProgram = prg, AtariProgramName = "hra.prg" });
        string f = Path_("auto.st");
        File.WriteAllBytes(f, img.Data);
        string md = Sh("mdir", $"-i \"{f}\" ::/AUTO", out _);   // Atari boot sektor nemá rozšířený BPB — fsck.fat hlásí jen jmenovku
        Assert(md.Contains("HRA      PRG") || md.Contains("HRA  PRG") || md.Contains("HRA PRG"), "mtools: " + md);
        var e = Fat12Volume.TryOpen(img.Data, out _)!.ListAll();
        Assert(e.Any(x => x.Path == "AUTO" && x.IsDirectory) && e.Any(x => x.Path == "AUTO\\HRA.PRG" && x.Size == 200), "AUTO\\HRA.PRG");
        Eq(FsKind.FatAtari, FileSystemDetector.Detect(img.Data).Kind, "Atari disketa");
        try
        {
            AtariFloppy.Core.Boot.BootDisks.Build(new AtariFloppy.Core.Boot.BootOptions
            { System = AtariFloppy.Core.Boot.BootSystem.AtariAuto, Geometry = DiskGeometry.Dd720, AtariProgram = new byte[100], AtariProgramName = "x.prg" });
            throw new Exception("prošlo");
        }
        catch (OperationException ex) { Assert(ex.Message.Contains("601A"), ex.Message); }
    }

    // ------------------------------------------------------------------ USB a 720 KB

    private static DetectedDrive UsbDrive(MediaState state, DiskGeometry? fmt, bool reports720, params string[] media)
    {
        var d = new DetectedDrive
        {
            Kind = DriveKind.Usb, Letter = "A", Name = "test", Supports720 = true, Supports1440 = true,
            Reports720 = reports720, MediaTypes = media.ToList(),
        };
        d.MediaState = state;
        d.FormattedAs = fmt;
        d.Media = state == MediaState.NoDisk ? InsertedMedia.None : reports720 ? InsertedMedia.DD : InsertedMedia.Unknown;
        d.ComputeFeatures();
        return d;
    }

    private static void T_Usb720_Detection()
    {
        var read = UsbDrive(MediaState.Ready, DiskGeometry.Dd720, false, "3,5\" 1,44 MB");
        Eq(Availability.Available, read.Get(Feature.Standard720), "přečtená DD disketa");
        var offered = UsbDrive(MediaState.Unrecognized, null, true, "3,5\" 720 KB");
        Eq(Availability.Available, offered.Get(Feature.Standard720), "nabízí 720 KB");
        var none = UsbDrive(MediaState.NoDisk, null, false);
        Eq(Availability.Maybe, none.Get(Feature.Standard720), "bez diskety");
        Assert(!none.Likely720Missing, "bez diskety nesmí hlásit „nejspíš ne“");
        var bad = UsbDrive(MediaState.Unrecognized, null, false, "3,5\" 1,44 MB");
        Eq(Availability.Maybe, bad.Get(Feature.Standard720), "nečitelná disketa bez 720 KB");
        Assert(bad.Likely720Missing && bad.Note720!.Contains("nejspíš ne"), bad.Note720 ?? "");

        // formát Atari zůstane povolený, jen s varováním; „neověřeno“ bez varování
        var atari = FormatProfiles.Find("atari")!;
        var v = FormatProfiles.Evaluate(atari, bad, InsertedMedia.Unknown);
        Assert(v.Status == ProfileStatus.Ready && v.Reason.Contains("nejspíš neumí"), v.Status + " " + v.Reason);
        v = FormatProfiles.Evaluate(atari, UsbDrive(MediaState.Ready, DiskGeometry.Hd1440, false, "3,5\" 1,44 MB"), InsertedMedia.DD);
        Assert(v.Status == ProfileStatus.Ready && !v.Reason.Contains("nejspíš"), v.Status + " " + v.Reason);
    }

    // ------------------------------------------------------------------ opravný formát (posun sektorů)

    private static (FakeFdc fdc, OperationResult result) RepairFormat(DiskGeometry g, bool shift, params (int cyl, int head, int from, int to)[] defects)
    {
        var fdc = FakeFdc.WithImage(DiskFormatter.CreateBlank(g, FormatStyle.Atari),
                                    g.IsHighDensity ? AtariFloppy.Core.Drives.Fdraw.DataRate.R500K : AtariFloppy.Core.Drives.Fdraw.DataRate.R250K);
        foreach (var d in defects) fdc.Defects[(d.cyl, d.head)] = (d.from, d.to);
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "A: (fdraw)");
        var result = new OperationResult();
        DiskOperations.Format(dev, g, FormatStyle.Atari, null, quick: false, verify: true, result, shiftSectors: shift);
        return (fdc, result);
    }

    private static void T_RepairFormat_ShiftsAroundDefect()
    {
        var g = DiskGeometry.Dd720;
        // bez opravného formátu: vada zasáhne sektory → označí se ve FAT
        var (_, plain) = RepairFormat(g, false, (10, 0, 2000, 2200));
        Assert(plain.BadSectors.Count > 0, "bez posunu má být vadný sektor");

        // s opravným formátem: sektor navíc padne na vadu, disketa je bez chyb
        var (fdc, r) = RepairFormat(g, true, (10, 0, 2000, 2200), (0, 0, 2600, 2900));
        Eq(0, r.BadSectors.Count, "vadné sektory po opravném formátu");
        Assert(r.Notes.Any(n => n.Contains("Stopa 10, strana 0")), string.Join(" | ", r.Notes));
        Assert(r.Warnings.Any(w => w.Contains("experimentální")), "chybí varování");
        var t10 = fdc.Tracks[(10, 0)];
        Eq(10, t10.Count, "sektorů na opravené stopě");
        Assert(t10.Any(x => x.Id.Sector == AtariFloppy.Core.Drives.Fdraw.FdrawDevice.SpareSectorId && x.Damaged), "sektor navíc má ležet na vadě");
        Assert(t10.Where(x => x.Id.Sector != AtariFloppy.Core.Drives.Fdraw.FdrawDevice.SpareSectorId).All(x => !x.Damaged), "skutečné sektory mimo vadu");

        // stopa 0 se sektorem navíc: disketa se dál rozpozná jako 720 KB a dá se přečíst celá
        var dev = new AtariFloppy.Core.Drives.Fdraw.FdrawDevice(fdc, "A:");
        Eq(9, dev.MediaGeometry!.SectorsPerTrack, "rozpoznaná geometrie");
        var read = new OperationResult();
        var img = DiskOperations.ReadDisk(dev, SideMode.Auto, read);
        Eq(0, read.BadSectors.Count, "čtení opravené diskety");
        Eq(737280, img.Data.Length, "velikost obrazu");
    }

    private static void T_RepairFormat_FallsBackWhenDefectTooBig()
    {
        // vada přes víc než jeden sektor: posun nepomůže → běžný formát a označení ve FAT
        var (fdc, r) = RepairFormat(DiskGeometry.Dd720, true, (20, 1, 1000, 3500));
        Assert(r.BadSectors.Count > 0, "vadné sektory mají zůstat");
        Eq(9, fdc.Tracks[(20, 1)].Count, "stopa vrácená do běžného formátu");
        Assert(r.Warnings.Any(w => w.Contains("označil jsem")), string.Join(" | ", r.Warnings));

        // 10 sektorů (Atari 800 KB): na stopě není místo → poznámka a běžný formát
        var g10 = new DiskGeometry(80, 2, 10, 512);
        var (_, r10) = RepairFormat(g10, true);
        Assert(r10.Notes.Any(n => n.Contains("není místo")), string.Join(" | ", r10.Notes));
    }

    private static void T_RepairFormat_HighDensity()
    {
        var (_, r) = RepairFormat(DiskGeometry.Hd1440, true, (40, 1, 5150, 5400));
        Eq(0, r.BadSectors.Count, "HD: vadné sektory po opravném formátu");
    }

    // ------------------------------------------------------------------ hromadná záloha

    private static void T_BatchBackup_Naming()
    {
        string dir = Path.Combine(_tmp, "batch");
        Directory.CreateDirectory(dir);
        string P(string n) => Path.Combine(dir, n);
        Eq(P("hra-001.st"), AtariFloppy.Core.App.Backups.FirstBatchPath(P("hra.st")), "bez čísla → -001");
        Eq(P("disk07.msa"), AtariFloppy.Core.App.Backups.FirstBatchPath(P("disk07.msa")), "číslo zůstane");
        Eq(P("hra-002.st"), AtariFloppy.Core.App.Backups.NextBatchPath(P("hra-001.st")), "další");
        Eq(P("disk10.msa"), AtariFloppy.Core.App.Backups.NextBatchPath(P("disk09.msa")), "přetečení šířky");
        File.WriteAllText(P("hra-001.st"), "x");
        File.WriteAllText(P("hra-002.st"), "x");
        Eq(P("hra-003.st"), AtariFloppy.Core.App.Backups.FirstBatchPath(P("hra.st")), "existující se přeskočí");
        Eq(P("hra-003.st"), AtariFloppy.Core.App.Backups.NextBatchPath(P("hra-001.st")), "další volný");
    }
}
