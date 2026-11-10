// Prüfprogramm für den Kern: YTD lesen, PNG schreiben, Dateinamen erkennen, Blender-Aufruf zusammenbauen.
// Aufruf: CoreCheck <teil.ydd> <textur.ytd> <ausgabeordner>
//         CoreCheck --gpu-test <teil.ydd> <textur.ytd> <ausgabeordner>
using System.Diagnostics;
using RetroShowroom.Core;

if (args[0] == "--skript")
{
    Console.WriteLine(RenderScript.Extract());
    return;
}

if (args[0] == "--gpu-test")
{
    await GpuTest.RunAsync(args[1], args[2], Directory.CreateDirectory(args[3]).FullName);
    return;
}

var ydd = args[0];
var ytd = args[1];
var outDir = Directory.CreateDirectory(args[2]).FullName;

Console.WriteLine("== YTD ==");
Console.WriteLine(YtdReader.Describe(ytd));
var sw = Stopwatch.StartNew();
var textures = YtdReader.Read(ytd);
foreach (var t in textures)
    Console.WriteLine($"  {t.Name}: {t.Width}x{t.Height}, {t.FormatName}, Mips {t.MipLevels}, Mip0 {t.Mip0.Length} B");
Console.WriteLine($"  gelesen in {sw.ElapsedMilliseconds} ms");

sw.Restart();
var prepared = TexturePreparer.Prepare(ytd);
Console.WriteLine($"  PNG: {prepared.PngPath} ({new FileInfo(prepared.PngPath).Length / 1024} KB) in {sw.ElapsedMilliseconds} ms");
var info = PngWriter.ReadInfo(prepared.PngPath);
Console.WriteLine($"  PNG-Kopf: {info}");
File.Copy(prepared.PngPath, Path.Combine(outDir, "textur.png"), true);

// verkleinerte Vorschau zum Anschauen (jeder 4. Pixel)
var t0 = textures[0];
var rgba = TextureDecoder.ToRgba(t0);
int f = Math.Max(1, t0.Width / 512), w = t0.Width / f, h = t0.Height / f;
var small = new byte[w * h * 4];
for (int y = 0; y < h; y++)
for (int x = 0; x < w; x++)
    Buffer.BlockCopy(rgba, ((y * f) * t0.Width + x * f) * 4, small, (y * w + x) * 4, 4);
PngWriter.WriteRgba(Path.Combine(outDir, "textur_klein.png"), w, h, small);

Console.WriteLine("\n== Teil ==");
var part = PartInfo.FromPath(ydd);
Console.WriteLine($"  Geschlecht {part.Gender}, Typ {part.Kind}, Komponente {part.Component} {part.Index}, Präfix '{part.Prefix}'");
foreach (var c in part.FindTextureCandidates()) Console.WriteLine("  Textur-Kandidat: " + Path.GetFileName(c));

Console.WriteLine("\n== Blender ==");
Console.WriteLine("  gefunden: " + (BlenderLocator.Find(null) ?? "nein"));

Console.WriteLine("\n== Aufruf (Beispiel) ==");
var req = new RenderRequest
{
    BlenderPath = @"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe",
    ScriptPath = @"C:\Daten\Assets\Script\vitrine.py",
    PartPath = part.FullPath,
    TexturePng = prepared.PngPath,
    LogoPath = @"C:\Daten\Assets\Logo\retro_logo_glas.png",
    RenderTarget = @"C:\Daten\Cache\render_test.png",
    OutputPath = OutputNaming.Next(outDir, "YAZURAH 893", Quality.Final),
    Title = "YAZURAH 893",
    Gender = part.Gender ?? 'm',
    Kind = part.Kind,
};
Console.WriteLine("  " + req.CommandLinePreview());
Console.WriteLine("  Ausgabe: " + req.OutputPath);

Console.WriteLine("\n== Diagnose ==");
Console.WriteLine("  " + RenderRunner.Diagnose(["Traceback (most recent call last):", "  SK = next(p.name for p in prefs.external_skeleton_paths if ...)", "StopIteration"]));
Console.WriteLine("  " + RenderRunner.Diagnose(["Traceback (most recent call last):", "KeyError: 'bl_ext.user_default.sollumz'"]));
