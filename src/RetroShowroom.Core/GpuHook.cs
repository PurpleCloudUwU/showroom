using System.Text;

namespace RetroShowroom.Core;

/// <summary>
/// Kleines Blender-Skript, das VOR vitrine.py läuft: Es wählt die Grafikkarte in den Cycles-Einstellungen aus
/// und stellt die Szene direkt vor dem Rendern auf GPU um (vitrine.py setzt fest CPU und bleibt unverändert).
/// Szene, Samples, Licht und Entrauscher (OpenImageDenoise) bleiben gleich, nur das Rechengerät ändert sich.
/// </summary>
public static class GpuHook
{
    public const string Marker = "RS_GPU ";

    private const string Script = """
        # Retro Showroom: Rendern auf der Grafikkarte. vitrine.py bleibt unveraendert.
        import bpy
        from bpy.app.handlers import persistent

        def _waehle_gpu():
            try:
                prefs = bpy.context.preferences.addons['cycles'].preferences
            except Exception as e:
                print('RS_GPU keine Cycles-Einstellungen: %r' % (e,), flush=True)
                return None
            for typ in ('OPTIX', 'CUDA', 'HIP', 'ONEAPI', 'METAL'):
                try:
                    prefs.compute_device_type = typ
                except Exception:
                    continue
                try:
                    prefs.refresh_devices()
                except Exception:
                    try:
                        prefs.get_devices()
                    except Exception:
                        pass
                gpus = [d for d in prefs.devices if d.type == typ]
                if gpus:
                    for d in prefs.devices:
                        d.use = (d.type == typ)
                    print('RS_GPU gefunden %s: %s' % (typ, ', '.join(d.name for d in gpus)), flush=True)
                    return typ
            try:
                prefs.compute_device_type = 'NONE'
            except Exception:
                pass
            print('RS_GPU keine kompatible Grafikkarte, es bleibt bei der CPU', flush=True)
            return None

        _TYP = _waehle_gpu()

        @persistent
        def _auf_gpu(scene, *args):
            if _TYP is None:
                return
            scene.cycles.device = 'GPU'
            if hasattr(scene.cycles, 'denoising_use_gpu'):
                scene.cycles.denoising_use_gpu = True
            print('RS_GPU aktiv %s' % _TYP, flush=True)

        bpy.app.handlers.render_init.append(_auf_gpu)
        bpy.app.handlers.render_pre.append(_auf_gpu)
        """;

    /// <summary>Schreibt das Skript in den Cache-Ordner und gibt den Pfad zurück.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppPaths.Ensure(AppPaths.CacheDir), "grafikkarte_verwenden.py");
        File.WriteAllText(path, Script.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return path;
    }

    public static string BackendName(string type) => type.ToUpperInvariant() switch
    {
        "OPTIX" => "OptiX",
        "CUDA" => "CUDA",
        "HIP" => "HIP",
        "ONEAPI" => "oneAPI",
        "METAL" => "Metal",
        _ => type,
    };
}
