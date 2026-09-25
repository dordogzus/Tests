using BepInEx.Configuration;
using BepInEx.Logging;
using MorePlayersMod.Logic;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace MorePlayersMod;

/// <summary>
/// Screen-space UGUI overlay (IMGUI boxes are stripped from this game build):
///  * crew badge "CREW 7 / 24" while in a lobby,
///  * crew roster with one slot per possible player (24 by default) showing each
///    member's name, colour, HOST / YOU tags and open slots,
///  * Frontier HUD (reactors, radiation, oxygen, plasma) and a hazard tint.
/// Keyboard driven and raycast-transparent, so it never steals game clicks.
/// </summary>
public class ModOverlay : MonoBehaviour
{
    public ModOverlay(IntPtr pointer) : base(pointer) { }

    private static ManualLogSource Log;
    private static int MaxPlayers = CrewProgression.DefaultMaxPlayers;
    private static ConfigEntry<KeyCode> CfgRosterKey, CfgInviteKey, CfgHudKey;
    private static ConfigEntry<bool> CfgBadge, CfgAutoRoster;

    private const float SlotW = 380f, SlotH = 34f, Gap = 6f, Chrome = 150f;
    private static readonly Color PanelColor = new Color(0.05f, 0.07f, 0.10f, 0.88f);
    private static readonly Color SlotColor = new Color(0.12f, 0.15f, 0.20f, 0.92f);
    private static readonly Color EmptySlotColor = new Color(0.10f, 0.12f, 0.16f, 0.55f);
    private static readonly Color Accent = new Color(1f, 0.72f, 0.20f, 1f);
    private static readonly Color Dim = new Color(0.65f, 0.70f, 0.78f, 1f);

    private sealed class Slot
    {
        internal Image Background, Swatch;
        internal Text Number, Name, Tag;
    }

    private bool _built, _failed, _rosterOpen, _hudOpen = true, _wasInLobby;
    private int _page, _visibleRows, _columns;
    private float _nextRefresh;
    private Font _font;
    private GameObject _root, _roster, _badge, _hud;
    private Text _badgeText, _rosterTitle, _rosterSub, _rosterFooter, _hudText;
    private Image _tint;
    private readonly List<Slot> _slots = new List<Slot>();
    private readonly List<LobbyMember> _members = new List<LobbyMember>();
    private readonly StringBuilder _sb = new StringBuilder(512);

    internal static void Bind(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        Log = log;
        MaxPlayers = maxPlayers;
        CfgRosterKey = config.Bind("UI", "RosterKey", KeyCode.F6, "Open/close the crew roster (all lobby slots).");
        CfgInviteKey = config.Bind("UI", "InviteKey", KeyCode.F5, "Open the Steam invite dialog for the current lobby while the roster is open.");
        CfgHudKey = config.Bind("UI", "FrontierHudKey", KeyCode.F7, "Show/hide the Frontier ship status HUD.");
        CfgBadge = config.Bind("UI", "ShowCrewBadge", true, "Show the small 'CREW n / max' badge in the top-right corner while in a lobby.");
        CfgAutoRoster = config.Bind("UI", "AutoOpenRosterInLobby", true, "Open the roster automatically when you enter a lobby (close with the roster key).");
    }

    public void Update()
    {
        if (_failed) return;
        try
        {
            if (!_built) Build();
            HandleKeys();
            VanillaLobbyList.Tick();
            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 0.25f;
            Refresh();
        }
        catch (Exception e)
        {
            _failed = true;
            Log?.LogWarning($"Overlay disabled after error: {e.GetType().Name}: {e.Message}");
            try { if (_root != null) _root.SetActive(false); } catch { }
        }
    }

    private void HandleKeys()
    {
        if (Input.GetKeyDown(CfgRosterKey.Value)) { _rosterOpen = !_rosterOpen; _nextRefresh = 0; }
        if (Input.GetKeyDown(CfgHudKey.Value)) { _hudOpen = !_hudOpen; _nextRefresh = 0; }
        if (_rosterOpen && Input.GetKeyDown(CfgInviteKey.Value)) SteamLobby.OpenInviteDialog();
        if (_rosterOpen && Input.GetKeyDown(KeyCode.PageDown)) { _page++; _nextRefresh = 0; }
        if (_rosterOpen && Input.GetKeyDown(KeyCode.PageUp)) { _page = Math.Max(0, _page - 1); _nextRefresh = 0; }
    }

    // ---------- construction ----------

    private void Build()
    {
        _built = true;
        _font = FindFont();
        _root = new GameObject("MorePlayers_Overlay");
        DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000;
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _tint = Panel(_root.transform, "HazardTint", new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0)).GetComponent<Image>();

        // Crew badge
        var badge = Panel(_root.transform, "CrewBadge", new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-18, -18), new Vector2(250, 34), PanelColor);
        _badge = badge.gameObject;
        _badgeText = Label(badge, "", 17, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.zero, true);

        // Roster
        _columns = RosterLayout.Columns(MaxPlayers);
        _visibleRows = RosterLayout.VisibleRows(MaxPlayers, 1080f, SlotH + Gap, Chrome);
        float width = _columns * (SlotW + Gap) + Gap + 24f;
        float height = Chrome + _visibleRows * (SlotH + Gap);
        var roster = Panel(_root.transform, "CrewRoster", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width, height), PanelColor);
        _roster = roster.gameObject;
        _rosterTitle = Label(roster, "CREW ROSTER", 30, TextAnchor.UpperLeft, Accent, new Vector2(18, -14), new Vector2(width - 36, 40), false);
        _rosterSub = Label(roster, "", 17, TextAnchor.UpperLeft, Dim, new Vector2(18, -56), new Vector2(width - 36, 26), false);
        _rosterFooter = Label(roster, "", 16, TextAnchor.LowerLeft, Dim, new Vector2(18, 14), new Vector2(width - 36, 26), false, bottom: true);
        float top = -96f;
        for (int row = 0; row < _visibleRows; row++)
        {
            for (int col = 0; col < _columns; col++)
            {
                var pos = new Vector2(12f + Gap + col * (SlotW + Gap), top - row * (SlotH + Gap));
                var bg = Panel(roster, $"Slot{row}_{col}", new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(SlotW, SlotH), SlotColor);
                var swatch = Panel(bg, "Swatch", new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(6, 0), new Vector2(8, SlotH - 10), Color.white);
                _slots.Add(new Slot
                {
                    Background = bg.GetComponent<Image>(),
                    Swatch = swatch.GetComponent<Image>(),
                    Number = Label(bg, "", 15, TextAnchor.MiddleLeft, Dim, new Vector2(20, 0), new Vector2(34, SlotH), false, middle: true),
                    Name = Label(bg, "", 17, TextAnchor.MiddleLeft, Color.white, new Vector2(54, 0), new Vector2(SlotW - 140, SlotH), false, middle: true),
                    Tag = Label(bg, "", 14, TextAnchor.MiddleRight, Accent, new Vector2(SlotW - 90, 0), new Vector2(82, SlotH), false, middle: true),
                });
            }
        }

        // Frontier HUD
        var hud = Panel(_root.transform, "FrontierHud", new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(18, 18), new Vector2(430, 178), PanelColor);
        _hud = hud.gameObject;
        _hudText = Label(hud, "", 16, TextAnchor.UpperLeft, Color.white, new Vector2(14, -10), new Vector2(402, 160), false);

        _roster.SetActive(false);
        _badge.SetActive(false);
        _hud.SetActive(false);
        Log?.LogInfo($"Overlay ready: roster {MaxPlayers} slots ({_columns} col x {_visibleRows} rows), keys roster={CfgRosterKey.Value} invite={CfgInviteKey.Value} hud={CfgHudKey.Value}.");
    }

    private static Font FindFont()
    {
        foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try { var f = Resources.GetBuiltinResource<Font>(name); if (f != null) return f; } catch { }
        }
        try { foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) if (f != null) return f; } catch { }
        return null;
    }

    private static RectTransform Panel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return rt;
    }

    private Text Label(Transform parent, string text, int size, TextAnchor anchor, Color color, Vector2 pos, Vector2 box, bool fill,
        bool bottom = false, bool middle = false)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        if (fill) { rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero; }
        else
        {
            var a = bottom ? new Vector2(0, 0) : middle ? new Vector2(0, 0.5f) : new Vector2(0, 1);
            rt.anchorMin = a; rt.anchorMax = a; rt.pivot = a;
            rt.anchoredPosition = pos; rt.sizeDelta = box;
        }
        var t = go.AddComponent<Text>();
        if (_font != null) t.font = _font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        t.text = text;
        return t;
    }

    // ---------- refresh ----------

    private void Refresh()
    {
        bool inLobby = SteamLobby.TryRead(_members, out int limit, out bool isHost);
        if (inLobby && !_wasInLobby && CfgAutoRoster.Value) _rosterOpen = true;
        _wasInLobby = inLobby;
        int capacity = Math.Max(MaxPlayers, Math.Max(limit, _members.Count));

        _badge.SetActive(inLobby && CfgBadge.Value);
        if (inLobby) SetText(_badgeText, $"CREW  {_members.Count} / {capacity}   <color=#9aa6b8>[{CfgRosterKey.Value}]</color>");

        _roster.SetActive(_rosterOpen);
        if (_rosterOpen) RefreshRoster(inLobby, capacity, isHost, limit);

        RefreshHud();
    }

    private void RefreshRoster(bool inLobby, int capacity, bool isHost, int limit)
    {
        string host = "-";
        foreach (var m in _members) if (m.IsOwner) host = m.Name;
        SetText(_rosterTitle, inLobby ? $"CREW ROSTER   <color=#ffffff>{_members.Count} / {capacity}</color>" : "CREW ROSTER");
        SetText(_rosterSub, inLobby
            ? $"Host: {host}   ·   lobby limit {(limit > 0 ? limit : capacity)}   ·   {(isHost ? "you are hosting" : "connected")}"
            : $"Not in a lobby. Host or join a game - up to {MaxPlayers} players.");

        int rowsTotal = RosterLayout.Rows(capacity);
        int pages = Math.Max(1, (int)Math.Ceiling(rowsTotal / (double)_visibleRows));
        if (_page >= pages) _page = pages - 1;
        int perPage = _visibleRows * _columns;
        int first = _page * perPage;
        SetText(_rosterFooter, $"[{CfgInviteKey.Value}] Invite friends     [{CfgRosterKey.Value}] Close" +
                               (pages > 1 ? $"     [PgUp/PgDn] Page {_page + 1}/{pages}" : ""));

        for (int i = 0; i < _slots.Count; i++)
        {
            // Slots are laid out column-major within the page (like the vanilla list, top to bottom).
            int col = i % _columns, row = i / _columns;
            int index = first + col * _visibleRows + row;
            var slot = _slots[i];
            bool exists = index < capacity;
            slot.Background.gameObject.SetActive(exists);
            if (!exists) continue;
            SetText(slot.Number, $"#{index + 1}");
            if (index < _members.Count)
            {
                var m = _members[index];
                slot.Background.color = SlotColor;
                slot.Swatch.color = PlayerColors.ColorFor(index);
                SetText(slot.Name, m.Name);
                SetText(slot.Tag, m.IsOwner && m.IsLocal ? "HOST · YOU" : m.IsOwner ? "HOST" : m.IsLocal ? "YOU" : "");
            }
            else
            {
                slot.Background.color = EmptySlotColor;
                slot.Swatch.color = new Color(1, 1, 1, 0.12f);
                SetText(slot.Name, "<color=#6b7688>open slot</color>");
                SetText(slot.Tag, "");
            }
        }
    }

    private void RefreshHud()
    {
        var s = FrontierRuntime.Status;
        bool show = s.Active && _hudOpen;
        _hud.SetActive(show);
        float tintAlpha = 0f;
        Color tint = new Color(0, 0, 0, 0);
        if (s.Active)
        {
            if (s.Radiation >= HazardLevel.Dangerous) { tintAlpha = s.Radiation == HazardLevel.Lethal ? 0.28f : 0.14f; tint = new Color(0.35f, 1f, 0.2f, tintAlpha); }
            if (s.OxygenHazard >= HazardLevel.Dangerous && s.OxygenCapacity > 0)
            {
                float a = s.OxygenHazard == HazardLevel.Lethal ? 0.45f : 0.2f;
                if (a > tintAlpha) tint = new Color(0.02f, 0.03f, 0.12f, a);
            }
        }
        _tint.color = tint;
        if (!show) return;
        _sb.Clear();
        _sb.Append("<b><color=#ffb833>FRONTIER SHIP STATUS</color></b>   <color=#9aa6b8>[").Append(CfgHudKey.Value).Append("]</color>\n");
        _sb.Append("Reactors ").Append(s.Reactors).Append("   shield walls ").Append(s.Shields).Append("   vents ").Append(s.Vents).Append('\n');
        _sb.Append("Radiation ").Append(Hazard(s.Radiation)).Append("  ").Append(s.DoseRate.ToString("0.00")).Append(" rad/s   dose ").Append(s.Dose.ToString("0.0")).Append('\n');
        if (s.OxygenCapacity > 0)
            _sb.Append("Oxygen ").Append(Hazard(s.OxygenHazard)).Append("  ").Append(s.Oxygen.ToString("0")).Append("%   supports ")
               .Append(s.OxygenCapacity.ToString("0")).Append(" / crew ").Append(s.Crew).Append('\n');
        else _sb.Append("Oxygen  <color=#9aa6b8>no life support installed</color>\n");
        if (s.PlasmaCapacity > 0 || s.Accelerators > 0)
            _sb.Append("Plasma bank ").Append(s.Plasma.ToString("0")).Append(" / ").Append(s.PlasmaCapacity.ToString("0"))
               .Append(s.Reactors == 0 && s.Accelerators > 0 ? "  <color=#ff6655>(accelerators need a reactor)</color>" : "").Append('\n');
        _sb.Append("<color=#9aa6b8>Supply runs ").Append(s.SupplyRuns).Append(" · tiers unlock at 1 / 3 / 6</color>");
        SetText(_hudText, _sb.ToString());
    }

    private static string Hazard(HazardLevel level) => level switch
    {
        HazardLevel.Safe => "<color=#66dd77>SAFE</color>",
        HazardLevel.Elevated => "<color=#ffd24d>ELEVATED</color>",
        HazardLevel.Dangerous => "<color=#ff8a3d>DANGER</color>",
        _ => "<color=#ff3b3b>LETHAL</color>",
    };

    private static void SetText(Text t, string value)
    {
        if (t != null && t.text != value) t.text = value;
    }
}
