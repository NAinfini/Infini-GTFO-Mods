using InfiniTweaks;
using UnityEngine;

int count = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
foreach (uint id in new uint[] { 117, 116, 114, 130, 167, 174, 115, 139, 144, 140, 142, 30, 131, 133, 138, 146, 148 })
{
    var marker = new NavMarker();
    var icon = MarkerIcon.Create(marker, MarkerCategory.Consumable, id, "");
    Check(icon != null, $"Missing icon {id}");
    var renderer = marker.m_carryitem.Renderer;
    renderer.color = new Color(.3f, .4f, .5f, .27f);
    icon!.Show();
    Check(marker.m_iconHolder.Renderer.sprite == null, "Custom artwork never overwrites unrelated marker renderers");
    var sprite = renderer.sprite!;
    Check(sprite != null && sprite.rect.width > 0 && sprite.rect.height > 0, "Valid sprite region");
    Check(Math.Abs(Math.Max(sprite!.rect.width, sprite.rect.height) / sprite.ppu - 1) < .001f, "Higher resolution keeps the previous 64px / 64ppu footprint");
    Check(renderer.color.a == 1, "Visible artwork is opaque despite native fade");
    Check(renderer.color.r == 1, "All artwork bypasses category tint");
    renderer.sprite = null; renderer.color = new Color(.1f, .2f, .3f, .6f);
    icon.Show();
    Check(renderer.sprite == sprite && renderer.color.a == 1, "Reassert after native style restores defaults");
    renderer.color = new Color(1, 1, 1, 0);
    icon.Show();
    Check(renderer.color.a == 1, "Alpha-only native fade is corrected even when RGB is already white");
    var other = new NavMarker();
    MarkerIcon.Create(other, MarkerCategory.Consumable, id, "")!.Show();
    Check(other.m_carryitem.Renderer.sprite == sprite, "Sprites shared between marker instances");
}
foreach (var category in new[] { MarkerCategory.Health, MarkerCategory.Ammo, MarkerCategory.Tool, MarkerCategory.Disinfection, MarkerCategory.Terminal })
    Check(MarkerIcon.Create(new NavMarker(), category, 0, "") != null, "Resources and terminals use custom artwork");
Check(MarkerIcon.Create(new NavMarker(), MarkerCategory.Other, 9999, "") != null, "Unknown objects use custom unknown-item artwork");
var data = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "marker-items.json")));
foreach (var row in data.RootElement.EnumerateArray())
{
    uint id = row.GetProperty("id").GetUInt32();
    var category = id switch { 101 => MarkerCategory.Ammo, 102 => MarkerCategory.Health, 127 => MarkerCategory.Tool, 132 => MarkerCategory.Disinfection, _ => MarkerCategory.Objective };
    var icon = MarkerIcon.Create(new NavMarker(), category, id, "");
    Check(icon != null, $"Upstream inventory coverage for ID {id}");
}
foreach (var category in new[] { MarkerCategory.Generator, MarkerCategory.DisinfectionStation, MarkerCategory.BulkheadController, MarkerCategory.HSU, MarkerCategory.HSUActivator })
    Check(MarkerIcon.Create(new NavMarker(), category, 0, "") != null, "Device artwork is registered");
Check(MarkerIconCatalog.Select(MarkerCategory.HSU, 0, "") == "hsu" && MarkerIconCatalog.Select(MarkerCategory.HSUActivator, 0, "") == "hsu-activator", "Adult HSU and insertion machine have distinct artwork");
Check(MarkerIconCatalog.Select(MarkerCategory.Consumable, 136, "") == "glow", "Alternate Glow Stick uses its actual model artwork");
foreach (var category in Enum.GetValues<MarkerCategory>())
    Check(MarkerIcon.Create(new NavMarker(), category, uint.MaxValue, "") != null, "Every category has custom artwork even for unknown mod items");
Check(ImageConversion.LoadCount == 57, "One shared texture per distinct artwork, never per marker");
Console.WriteLine($"PASS: {count} marker loading, mapping, tint, opacity and cache checks (test doubles, not Unity rendering).");

public class NavMarker { public Holder m_carryitem = new(); public Holder m_iconHolder = new(); }
public class Holder
{
    public SpriteRenderer Renderer = new();
    public T GetComponentInChildren<T>(bool inactive) => (T)(object)Renderer;
}
namespace UnityEngine
{
    public class Object { public static void Destroy(Object obj) { } }
    public enum HideFlags { HideAndDontSave }
    public enum FilterMode { Bilinear }
    public enum TextureWrapMode { Clamp }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
    public class Texture2D : Object
    {
        public string name = ""; public HideFlags hideFlags; public FilterMode filterMode; public TextureWrapMode wrapMode;
        public int width, height;
        public Texture2D(int w, int h) { width = w; height = h; }
    }
    public static class ImageConversion
    {
        public static int LoadCount;
        public static bool LoadImage(Texture2D texture, byte[] data, bool unreadable)
        {
            LoadCount++;
            texture.width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
            texture.height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
            return data[0] == 137 && data[1] == 80;
        }
    }
    public record struct Rect(float x, float y, float width, float height);
    public record struct Vector2(float x, float y);
    public class Sprite : Object
    {
        public HideFlags hideFlags; public Rect rect; public float ppu; public Texture2D texture = null!;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float ppu) => new() { texture = texture, rect = rect, ppu = ppu };
    }
    public class SpriteRenderer { public Sprite? sprite; public Color color; }
}
