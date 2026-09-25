using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MorePlayersMod.Logic;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MorePlayersMod;

/// <summary>
/// Makes the game's own lobby member list show every player. The panel is found
/// at runtime from the texts that display lobby member names (no hard-coded UI
/// class names). If it has fewer rows than members, a vanilla row is cloned per
/// missing member, so the list keeps the vanilla look. Clones are inert:
/// game row scripts, buttons and avatars are disabled so they can never act on
/// the wrong player. Nothing happens when vanilla already lists everyone.
/// </summary>
internal static class VanillaLobbyList
{
    private sealed class TextRef
    {
        internal Component Component;
        internal object Wrapper;
        internal PropertyInfo Property;
        internal string Get() { try { return Property.GetValue(Wrapper) as string ?? ""; } catch { return ""; } }
        internal void Set(string v) { try { if (Get() != v) Property.SetValue(Wrapper, v); } catch { } }
    }

    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgEnabled;
    private static int MaxPlayers;
    private static float _next;
    private static readonly List<Type> TextTypes = new List<Type>();
    private static readonly Dictionary<IntPtr, PropertyInfo> TextProps = new Dictionary<IntPtr, PropertyInfo>();
    private static readonly List<LobbyMember> Members = new List<LobbyMember>();
    private static readonly List<GameObject> Clones = new List<GameObject>();
    private static Transform _container;
    private static List<int> _namePath;
    private static GameObject _holder;
    private static bool _typesResolved, _loggedAttach;

    internal static void Bind(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        Log = log;
        MaxPlayers = maxPlayers;
        CfgEnabled = config.Bind("UI", "ExtendVanillaLobbyList", true,
            "Add rows to the game's own lobby member list for players beyond the rows it shows (vanilla-style clones of its own rows).");
    }

    internal static void Tick()
    {
        if (CfgEnabled == null || !CfgEnabled.Value) return;
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + 1f;
        try { Update(); }
        catch (Exception e) { Log?.LogDebug($"Lobby list extension skipped: {e.Message}"); }
    }

    private static void Update()
    {
        if (!SteamLobby.TryRead(Members, out _, out _) || Members.Count < 2) { HideClones(); return; }
        ResolveTypes();
        if (TextTypes.Count == 0) return;

        var names = new List<string>();
        foreach (var m in Members) names.Add(m.Name);
        var matched = FindMemberTexts(names);
        if (!FindRows(matched, out var container, out var rows, out var templateText))
        {
            HideClones();
            return;
        }
        if (_container == null || _container.Pointer != container.Pointer) { DropClones(); _container = container; }

        var shown = new List<string>();
        foreach (var row in rows) foreach (var t in TextsIn(row)) shown.Add(t.Get());
        var overflow = LobbyRows.Overflow(shown, names);
        int wanted = Math.Min(overflow.Count, Math.Max(0, MaxPlayers - rows.Count));
        if (wanted == 0) { HideClones(); return; }

        var template = RowOf(templateText.Component.transform, container);
        _namePath = PathFrom(template, templateText.Component.transform);
        int insertAt = 0;
        foreach (var row in rows) insertAt = Math.Max(insertAt, row.GetSiblingIndex() + 1);
        while (Clones.Count < wanted) Clones.Add(MakeClone(template, container));
        for (int i = 0; i < Clones.Count; i++)
        {
            var clone = Clones[i];
            if (clone == null) continue;
            bool use = i < wanted;
            if (clone.activeSelf != use) clone.SetActive(use);
            if (!use) continue;
            clone.transform.SetSiblingIndex(insertAt + i);
            var nameText = TextAt(clone.transform, _namePath);
            foreach (var t in TextsIn(clone.transform)) t.Set(nameText != null && t.Component.Pointer == nameText.Component.Pointer ? overflow[i] : "");
        }
        if (!_loggedAttach)
        {
            _loggedAttach = true;
            Log?.LogInfo($"Vanilla lobby list extended under '{container.name}': {rows.Count} vanilla rows + {wanted} added rows.");
        }
    }

    // ---------- discovery ----------

    private static void ResolveTypes()
    {
        if (_typesResolved) return;
        _typesResolved = true;
        foreach (var name in new[] { "Il2CppTMPro.TMP_Text", "TMPro.TMP_Text", "UnityEngine.UI.Text" })
        {
            var t = AccessTools.TypeByName(name);
            if (t != null && AccessTools.Property(t, "text") != null) TextTypes.Add(t);
        }
        Log?.LogInfo($"Lobby list: text types available = {TextTypes.Count}.");
    }

    private static List<(TextRef text, int member)> FindMemberTexts(List<string> names)
    {
        var found = new List<(TextRef, int)>();
        foreach (var type in TextTypes)
        {
            var il2cpp = Il2CppType.From(type);
            foreach (var obj in Resources.FindObjectsOfTypeAll(il2cpp))
            {
                var comp = obj?.TryCast<Component>();
                if (comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy || IsOurs(comp.transform)) continue;
                var tr = Wrap(comp, type);
                string text = tr.Get();
                for (int i = 0; i < names.Count; i++)
                    if (LobbyRows.TextShowsName(text, names[i])) { found.Add((tr, i)); break; }
            }
        }
        return found;
    }

    /// <summary>Rows = children of the deepest common ancestor of the member texts, all with the same normalized name.</summary>
    private static bool FindRows(List<(TextRef text, int member)> matched, out Transform container, out List<Transform> rows, out TextRef template)
    {
        container = null; rows = new List<Transform>(); template = null;
        if (matched.Count < 2) return false;
        var chain = Ancestors(matched[0].text.Component.transform);
        foreach (var m in matched)
        {
            var other = Ancestors(m.text.Component.transform);
            chain = chain.FindAll(a => other.Exists(b => b.Pointer == a.Pointer));
        }
        if (chain.Count == 0) return false;
        container = chain[0]; // deepest common ancestor
        // Screen-space UI only: never touch world-space name tags or player objects.
        if (container.TryCast<RectTransform>() == null) return false;
        var canvas = container.GetComponentInParent<Canvas>(true);
        if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) return false;
        string rowName = null;
        foreach (var m in matched)
        {
            var row = RowOf(m.text.Component.transform, container);
            if (row == null || row.name.Contains(LobbyRows.CloneTag)) continue;
            if (row.TryCast<RectTransform>() == null) return false;
            string n = LobbyRows.NormalizeRowName(row.name);
            if (rowName == null) rowName = n;
            else if (n != rowName) return false;
            if (!rows.Exists(r => r.Pointer == row.Pointer)) rows.Add(row);
            // Prefer a plain member row as template (no "(You)"/host decorations).
            var member = Members[m.member];
            if (template == null || (!member.IsLocal && !member.IsOwner)) template = m.text;
        }
        return rows.Count >= 2 && template != null;
    }

    private static List<Transform> Ancestors(Transform t)
    {
        var list = new List<Transform>();
        for (var p = t.parent; p != null && list.Count < 16; p = p.parent) list.Add(p);
        return list;
    }

    private static Transform RowOf(Transform t, Transform container)
    {
        for (var cur = t; cur != null; cur = cur.parent)
            if (cur.parent != null && cur.parent.Pointer == container.Pointer) return cur;
        return null;
    }

    private static bool IsOurs(Transform t)
    {
        for (var cur = t; cur != null; cur = cur.parent)
            if (cur.name == "MorePlayers_Overlay") return true;
        return false;
    }

    private static readonly Dictionary<IntPtr, Type> ConcreteTypes = new Dictionary<IntPtr, Type>();

    /// <summary>Wraps with the concrete managed class (TMP_Text itself is abstract).</summary>
    private static TextRef Wrap(Component comp, Type baseType)
    {
        var il2cpp = comp.GetIl2CppType();
        var klass = il2cpp.Pointer;
        if (!ConcreteTypes.TryGetValue(klass, out var type))
        {
            string full = il2cpp.FullName ?? "";
            type = AccessTools.TypeByName(full) ?? AccessTools.TypeByName("Il2Cpp" + full);
            if (type == null || type.IsAbstract || !baseType.IsAssignableFrom(type)) type = baseType;
            ConcreteTypes[klass] = type;
        }
        if (!TextProps.TryGetValue(klass, out var prop)) TextProps[klass] = prop = AccessTools.Property(type, "text");
        return new TextRef { Component = comp, Wrapper = Activator.CreateInstance(type, comp.Pointer), Property = prop };
    }

    private static List<TextRef> TextsIn(Transform root)
    {
        var list = new List<TextRef>();
        foreach (var type in TextTypes)
            foreach (var c in root.GetComponentsInChildren(Il2CppType.From(type), true))
                if (c != null) list.Add(Wrap(c, type));
        return list;
    }

    private static List<int> PathFrom(Transform root, Transform target)
    {
        var path = new List<int>();
        for (var cur = target; cur != null && cur.Pointer != root.Pointer; cur = cur.parent) path.Insert(0, cur.GetSiblingIndex());
        return path;
    }

    private static TextRef TextAt(Transform root, List<int> path)
    {
        var cur = root;
        foreach (int i in path) { if (i >= cur.childCount) return null; cur = cur.GetChild(i); }
        foreach (var t in TextsIn(cur)) if (t.Component.transform.Pointer == cur.Pointer) return t;
        return null;
    }

    // ---------- clones ----------

    private static GameObject MakeClone(Transform template, Transform container)
    {
        if (_holder == null)
        {
            _holder = new GameObject("MPM_LobbyRowHolder");
            _holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_holder);
        }
        // Instantiate under an inactive holder so the row's game scripts never Awake/OnEnable.
        var clone = UnityEngine.Object.Instantiate(template.gameObject, _holder.transform, false);
        clone.name = template.name + LobbyRows.CloneTag;
        foreach (var c in clone.transform.GetComponentsInChildren(Il2CppType.Of<Behaviour>(), true))
        {
            var b = c?.TryCast<Behaviour>();
            if (b == null) continue;
            string type = c.GetIl2CppType().FullName ?? "";
            bool gameScript = !type.Contains(".");
            bool interactive = type.Contains("Button") || type.Contains("Toggle") || type.Contains("Selectable") ||
                               type.Contains("EventTrigger") || type.Contains("RawImage");
            if (gameScript || interactive) b.enabled = false;
        }
        clone.transform.SetParent(container, false);
        return clone;
    }

    private static void HideClones()
    {
        foreach (var c in Clones) if (c != null && c.activeSelf) c.SetActive(false);
    }

    private static void DropClones()
    {
        foreach (var c in Clones) if (c != null) UnityEngine.Object.Destroy(c);
        Clones.Clear();
        _loggedAttach = false;
    }
}
