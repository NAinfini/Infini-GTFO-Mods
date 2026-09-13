using InfiniTweaks;
using UnityEngine;
using TMPro;

var owner = new PlaceNavMarkerOnGO();
owner.m_marker.m_playerName.alpha = 0.8f;
owner.m_marker.m_playerName.faceColor = new Color(.2f, .1f, .1f, .5f);
owner.m_marker.m_playerName.overrideColorTags = true;
owner.m_marker.m_playerName.enableVertexGradient = true;
owner.m_marker.m_distance.alpha = 0.7f;
ResourceHudView.Show(owner, "Main 50%\nSpecial 20%");
var text = UnityEngine.Object.Clones.OfType<TextMeshPro>().Single();
Check(text.alignment == TextAlignmentOptions.Top && text.rectTransform.pivot == new Vector2(.5f, 1), "Unequal weapon lines each center beneath the rendered player name");
Check(text.faceColor == Color.white && owner.m_marker.m_playerName.faceColor == new Color(.2f, .1f, .1f, .5f), "Resource material face color is opaque white without changing the native name material");
Check(text.richText && !text.overrideColorTags && !text.enableVertexGradient,
    "Resource tags retain independent colors despite inherited name color overrides and gradients.");
Check(owner.m_marker.m_playerName.overrideColorTags && owner.m_marker.m_playerName.enableVertexGradient,
    "Clearing resource color overrides leaves the native name style unchanged.");
Check(!UnityEngine.Object.Clones.OfType<NavMarkerComponent>().Any() && text.text.Contains("Special 20%"), "Resource percentages never clone category icons.");
Check(text.transform.parent == owner.m_marker.m_playerName.transform && text.transform.localScale == Vector3.one, "Rows inherit the name transform without doubling its scale.");
Check(owner.m_marker.m_playerName.text == "Teammate", "Native teammate name is unchanged.");
ResourceHudView.Alpha(owner, 0.15f, true);
Check(text.alpha == 0.15f && owner.m_marker.m_playerName.alpha == 1 && owner.m_marker.m_distance.alpha == 1, "Resource fade is independent of name and distance.");
Check(text.transform.localPosition == new Vector3(20, -12, 0), "Rows begin below the rendered name bounds, not the marker origin.");
owner.m_marker.m_playerName.transform.localPosition = new Vector3(100, 400, 0);
owner.m_marker.m_playerName.textBounds = new Bounds(new Vector3(-40, -16, 0), new Vector3(70, 0, 0));
ResourceHudView.Alpha(owner, .15f, true);
Check(text.transform.parent == owner.m_marker.m_playerName.transform && text.transform.localPosition == new Vector3(70, -20, 0), "Native name movement stays in the parent; resized name bounds update the row offset.");
Settings.HudDistance.Value = false;
ResourceHudView.Alpha(owner, 0.5f, true);
Check(owner.m_marker.m_distance.alpha == 0 && owner.m_marker.m_playerName.alpha == 1 && text.alpha == 0.5f, "Distance-only hiding keeps name and resource rows.");
int writes = text.Writes, clones = UnityEngine.Object.Clones.Count;
ResourceHudView.Show(owner, "Main 50%\nSpecial 20%");
Check(text.Writes == writes && UnityEngine.Object.Clones.Count == clones, "Unchanged content neither rebuilds text nor clones icons.");
ResourceHudView.Show(owner, "HP 20%");
Check(text.text == "HP 20%" && !UnityEngine.Object.Clones.OfType<NavMarkerComponent>().Any(), "Switching pack changes text without adding an icon.");
ResourceHudView.Alpha(owner, 1, false);
Check(!text.gameObject.activeSelf && owner.m_marker.m_playerName.alpha == 0.8f && owner.m_marker.m_distance.alpha == 0.7f, "Rescue/menu state hides resource rows and restores native text alpha.");
ResourceHudView.Show(owner, ""); ResourceHudView.Alpha(owner, 1, true);
Check(!text.gameObject.activeSelf, "No held pack leaves no resource content.");
ResourceHudView.Clear();
Check(text.gameObject.Destroyed && owner.m_marker.m_playerName.alpha == 0.8f && owner.m_marker.m_distance.alpha == 0.7f, "Disabling destroys only clones and restores owned native fields.");
ResourceHudView.Show(owner, "HP 20%");
var staleText = UnityEngine.Object.Clones.OfType<TextMeshPro>().Last();
var oldMarker = owner.m_marker;
owner.m_marker = new NavMarker();
ResourceHudView.Show(owner, "Main 80%");
var reboundText = UnityEngine.Object.Clones.OfType<TextMeshPro>().Last();
Check(staleText.gameObject.Destroyed, "Replacing native marker destroys old owned meshes.");
Check(reboundText.transform.parent == owner.m_marker.m_playerName.transform && reboundText != staleText, "Replacement text belongs to the current native marker.");
ResourceHudView.Alpha(owner, .25f, true);
Check(reboundText.alpha == .25f && oldMarker.m_playerName.alpha == .8f, "Alpha uses new marker and restores the old marker.");
owner.m_marker = new NavMarker();
ResourceHudView.Alpha(owner, 1, true);
Check(reboundText.gameObject.Destroyed, "Alpha-before-Show also releases a replaced marker.");
ResourceHudView.Clear();
Console.WriteLine("PASS: 20 production HUD-view checks. Managed lifecycle/layout-input test, not Unity rendering.");
void Check(bool value, string message) { if (!value) throw new Exception(message); }

namespace UnityEngine
{
    public class Object
    {
        public string name = "";
        public static readonly List<Object> Clones = new();
        public static T Instantiate<T>(T source, Transform parent) where T : Object
        {
            Object result = source switch
            {
                TextMeshPro text => new TextMeshPro { fontSize = text.fontSize, faceColor = text.faceColor, overrideColorTags = text.overrideColorTags, enableVertexGradient = text.enableVertexGradient },
                NavMarkerComponent icon => new NavMarkerComponent { Kind = icon.Kind },
                _ => throw new Exception("Unsupported clone")
            };
            ((Component)result).transform.parent = parent; Clones.Add(result); return (T)result;
        }
        public static void Destroy(GameObject value) { value.Destroyed = true; value.activeSelf = false; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new();
        public Transform transform = new RectTransform { parent = new Transform() };
    }
    public class GameObject { public bool activeSelf = true, Destroyed; public bool activeInHierarchy => activeSelf; public void SetActive(bool value) => activeSelf = value; }
    public class Transform { public Transform parent = null!; public Vector3 localScale = Vector3.one, position, localPosition; public Quaternion localRotation; public Vector3 TransformPoint(Vector3 value) => value; }
    public class RectTransform : Transform { public Vector2 anchoredPosition, anchorMin, anchorMax, pivot; }
    public readonly record struct Vector2(float x, float y) { public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y); }
    public readonly record struct Vector3(float x, float y, float z) { public static Vector3 one => new(1, 1, 1); public static Vector3 operator *(Vector3 a, float n) => new(a.x * n, a.y * n, a.z * n); }
    public readonly record struct Bounds(Vector3 min, Vector3 center);
    public readonly record struct Quaternion { public static Quaternion identity => new(); }
    public readonly record struct Vector4 { public static Vector4 zero => new(); }
    public readonly record struct Color(float r, float g, float b, float a) { public static Color white => new(1,1,1,1); }
}
namespace TMPro
{
    public enum TextAlignmentOptions { TopLeft, Top }
    public enum TextOverflowModes { Overflow }
    public class TextMeshPro : Component
    {
        public string text = "Teammate";
        public bool richText, enableWordWrapping, enableAutoSizing, overrideColorTags, enableVertexGradient;
        public Vector4 margin;
        public Bounds textBounds = new(new Vector3(-30, -8, 0), new Vector3(20, 0, 0));
        public Color color, faceColor;
        public float alpha = 1, fontSize = 20, preferredHeight = 22;
        public int Writes;
        public TextAlignmentOptions alignment;
        public TextOverflowModes overflowMode;
        public RectTransform rectTransform => (RectTransform)transform;
        public void SetText(string value) { text = value; Writes++; }
    }
}
public class NavMarkerComponent : Component
{
    public string Kind = "";
    public void CheckSprites() { }
    public void SetEnabled(bool value) => gameObject.SetActive(value);
    public void SetColor(Color value) { }
    public void SetAlphaScale(float value) { }
}
public class NavMarker
{
    public TextMeshPro m_playerName = new(), m_distance = new();
    public NavMarkerComponent m_health = new() { Kind = "health" }, m_ammo = new() { Kind = "ammo" }, m_toolRefill = new() { Kind = "tool" }, m_disinfection = new() { Kind = "disinfection" };
}
public class PlaceNavMarkerOnGO { public IntPtr Pointer = (IntPtr)1; public NavMarker m_marker = new(); }
public enum eResourceContainerSpawnType { Health, AmmoWeapon, AmmoTool, Disinfection }
namespace InfiniTweaks
{
    internal enum MarkerCategory { Health, Ammo, Tool, Disinfection }
    internal static class Settings
    {
        internal sealed class Entry<T> { public T Value; public Entry(T value) => Value = value; }
        internal sealed class Category { public Color Color = default; }
        internal static Entry<float> HudNameOpacity = new(1), HudDistanceOpacity = new(1);
        internal static Entry<bool> HudDistance = new(true);
        internal static Dictionary<MarkerCategory, Category> MarkerCategories = Enum.GetValues<MarkerCategory>().ToDictionary(x => x, _ => new Category());
    }
}
