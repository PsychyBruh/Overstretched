using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Overstretched
{
    /// <summary>
    /// Hand-drawn IMGUI controls for the menu: rounded cards, animated switches, sliders with value pills,
    /// segmented tabs, accent buttons. Everything is drawn with GUI.DrawTexture's rounded-rect overload and
    /// invisible GUI controls laid on top for the input, so no asset bundles are needed.
    /// </summary>
    public static class UiKit
    {
        public static readonly Color Bg = Hex(0x0D0E12), Sidebar = Hex(0x111318), Card = Hex(0x181B22), CardHover = Hex(0x20242E),
            Line = Hex(0x2A2E3A), Text = Hex(0xE9EAEE), Dim = Hex(0x8B91A0), Faint = Hex(0x555B69),
            Accent = Hex(0xFF6A2B), AccentHot = Hex(0xFF8A52), Red = Hex(0xF0505A), Green = Hex(0x3DD68C),
            Cyan = Hex(0x4CC9F0), Blue = Hex(0x6EA8FE), Yellow = Hex(0xF5C451), Gold = Hex(0xFFD166), Orange = Hex(0xFF9F43),
            Purple = Hex(0xB18CFF);

        public enum Kind { Normal, Accent, Danger, Ghost, Good }

        public static Font Font { get; private set; }

        /// <summary>Usable width inside a card, set by the menu each frame so grids can split it evenly.</summary>
        public static float ContentWidth = 400f;
        public static GUIStyle Label, LabelDim, LabelSmall, LabelBold, Title, Section, ButtonText, ButtonTextBig, Input, CardStyle, Center;
        private static GUIStyle invisibleTrack, invisibleThumb;
        private static readonly Dictionary<int, float> anim = new Dictionary<int, float>();
        private static bool ready;

        public static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

        public static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        public static string Rich(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        public static void Init()
        {
            if (ready) return;
            ready = true;
            try { Font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Segoe UI Symbol", "Arial" }, 14); }
            catch { Font = null; }

            GUIStyle Make(int size, Color color, FontStyle style = FontStyle.Normal, TextAnchor anchor = TextAnchor.MiddleLeft)
            {
                var s = new GUIStyle { fontSize = size, fontStyle = style, alignment = anchor, richText = true, wordWrap = false, clipping = TextClipping.Overflow,
                                     padding = new RectOffset(0, 0, 2, 3) };
                s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = color;
                if (Font != null) s.font = Font;
                return s;
            }

            Label = Make(14, Text);
            LabelDim = Make(13, Dim);
            LabelDim.wordWrap = true;
            LabelSmall = Make(12, Dim);
            LabelBold = Make(14, Text, FontStyle.Bold);
            Title = Make(20, Text, FontStyle.Bold);
            Section = Make(12, Faint, FontStyle.Bold);
            Center = Make(14, Text, FontStyle.Normal, TextAnchor.MiddleCenter);
            ButtonText = Make(13, Text, FontStyle.Bold, TextAnchor.MiddleCenter);
            ButtonText.padding = new RectOffset(6, 6, 2, 3);
            ButtonText.margin = new RectOffset(3, 3, 3, 3);
            ButtonTextBig = Make(16, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);

            Input = Make(14, Text);
            Input.padding = new RectOffset(10, 10, 6, 6);
            Input.normal.background = Input.hover.background = RoundTex(Hex(0x0F1116), Line, 6);
            Input.focused.background = Input.active.background = RoundTex(Hex(0x0F1116), Accent, 6);
            Input.border = new RectOffset(7, 7, 7, 7);
            Input.fixedHeight = 32;

            CardStyle = new GUIStyle
            {
                normal = { background = RoundTex(Card, Hex(0x22252F), 10) },
                border = new RectOffset(11, 11, 11, 11),
                padding = new RectOffset(14, 14, 12, 12),
                margin = new RectOffset(0, 0, 0, 10),
            };

            invisibleTrack = new GUIStyle { fixedHeight = 20 };
            invisibleThumb = new GUIStyle { fixedWidth = 18, fixedHeight = 20 };
        }

        /// <summary>A small rounded texture (fill plus a 1px outline) that 9-slices cleanly as a style background.</summary>
        private static Texture2D RoundTex(Color fill, Color outline, int radius)
        {
            int size = radius * 2 + 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Abs(x + 0.5f - size / 2f) - (size / 2f - radius));
                    float dy = Mathf.Max(0f, Mathf.Abs(y + 0.5f - size / 2f) - (size / 2f - radius));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float inside = Mathf.Clamp01(radius - d + 0.5f);
                    float edge = Mathf.Clamp01(1f - Mathf.Abs(radius - 0.5f - d));
                    // Straight edges: outline on the outermost pixel row/column.
                    if (dx <= 0f && dy <= 0f)
                        edge = (x == 0 || y == 0 || x == size - 1 || y == size - 1) ? 1f : 0f;
                    var c = Color.Lerp(fill, outline, edge * outline.a);
                    c.a = inside * Mathf.Max(fill.a, edge * outline.a);
                    tex.SetPixel(x, y, c);
                }
            tex.Apply();
            return tex;
        }

        // ------------------------------------------------------------------ drawing primitives

        public static void RoundRect(Rect r, Color c, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        public static void RoundOutline(Rect r, Color c, float radius, float width = 1f)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, width, radius);
        }

        public static void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = c * old;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Smoothly animated value per control, eased toward target on repaint.</summary>
        public static float Anim(int key, float target, float speed = 14f)
        {
            if (!anim.TryGetValue(key, out float v)) v = target;
            if (Event.current.type == EventType.Repaint) v = Mathf.Lerp(v, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));
            if (anim.Count > 4000) anim.Clear();
            anim[key] = v;
            return v;
        }

        private static int Key(string id, Rect r) => unchecked(id.GetHashCode() * 31 + (int)r.x * 7919 + (int)r.y * 104729);

        public static bool MouseHeld => Mouse.current != null && Mouse.current.leftButton.isPressed;

        // ------------------------------------------------------------------ controls

        public static void SectionHeader(string text)
        {
            GUILayout.Space(4);
            GUILayout.Label(text.ToUpperInvariant(), Section);
            GUILayout.Space(2);
        }

        public static void BeginCard(string title = null, string subtitle = null, Color? accent = null)
        {
            GUILayout.BeginVertical(CardStyle);
            if (title == null) return;
            GUILayout.BeginHorizontal();
            var r = GUILayoutUtility.GetRect(4f, 18f, GUILayout.Width(4f));
            RoundRect(new Rect(r.x, r.y + 1f, 4f, 16f), accent ?? Accent, 2f);
            GUILayout.Space(6);
            GUILayout.Label(title, LabelBold);
            GUILayout.FlexibleSpace();
            if (subtitle != null) GUILayout.Label(subtitle, LabelSmall);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
        }

        public static void EndCard() => GUILayout.EndVertical();

        public static bool Button(string text, Kind kind = Kind.Normal, params GUILayoutOption[] options)
        {
            var content = new GUIContent(text);
            var opts = options.Length > 0 ? options : new[] { GUILayout.Height(32) };
            var r = GUILayoutUtility.GetRect(content, ButtonText, opts);
            return ButtonAt(r, text, kind);
        }

        public static bool ButtonAt(Rect r, string text, Kind kind = Kind.Normal, GUIStyle style = null)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            float h = Anim(Key(text, r), hover ? 1f : 0f);
            bool down = hover && MouseHeld;
            Color fill, outline, txt = Text;
            switch (kind)
            {
                case Kind.Accent: fill = Color.Lerp(Accent, AccentHot, h); outline = Color.clear; txt = Color.white; break;
                case Kind.Danger: fill = Color.Lerp(Alpha(Red, 0.14f), Alpha(Red, 0.32f), h); outline = Alpha(Red, 0.55f); txt = Color.Lerp(Red, Color.white, h * 0.6f); break;
                case Kind.Good: fill = Color.Lerp(Alpha(Green, 0.12f), Alpha(Green, 0.3f), h); outline = Alpha(Green, 0.5f); txt = Color.Lerp(Green, Color.white, h * 0.6f); break;
                case Kind.Ghost: fill = Color.Lerp(Color.clear, Alpha(Accent, 0.16f), h); outline = Alpha(Accent, 0.6f + 0.4f * h); txt = Color.Lerp(Accent, Color.white, h * 0.5f); break;
                default: fill = Color.Lerp(Hex(0x232733), Hex(0x2E3342), h); outline = Color.Lerp(Line, Faint, h); break;
            }
            var draw = down ? new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f) : r;
            RoundRect(draw, fill, 7f);
            if (outline.a > 0f) RoundOutline(draw, outline, 7f);
            var s = style ?? ButtonText;
            var old = s.normal.textColor;
            s.normal.textColor = s.hover.textColor = s.active.textColor = txt;
            bool clicked = GUI.Button(draw, text, s);
            s.normal.textColor = s.hover.textColor = s.active.textColor = old;
            return clicked;
        }

        /// <summary>Like Button but reports true every frame it's held down.</summary>
        public static bool HoldButton(Rect r, string text, Kind kind, out bool held)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            held = hover && MouseHeld;
            float h = Anim(Key(text, r), held ? 1f : hover ? 0.5f : 0f, 20f);
            var fill = Color.Lerp(Alpha(Accent, 0.12f), Alpha(Accent, 0.55f), h);
            RoundRect(r, fill, 8f);
            RoundOutline(r, Alpha(Accent, 0.8f), 8f, held ? 2f : 1f);
            var old = ButtonTextBig.normal.textColor;
            ButtonTextBig.normal.textColor = ButtonTextBig.active.textColor = ButtonTextBig.hover.textColor = Color.Lerp(Accent, Color.white, h);
            bool result = GUI.RepeatButton(r, text, ButtonTextBig);
            ButtonTextBig.normal.textColor = ButtonTextBig.active.textColor = ButtonTextBig.hover.textColor = old;
            return result;
        }

        public static bool Switch(string label, bool value, string hint = null, Color? onColor = null)
        {
            float height = hint != null ? 42f : 30f;
            var row = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
            bool hover = row.Contains(Event.current.mousePosition);
            float h = Anim(Key("row" + label, row), hover ? 1f : 0f);
            float t = Anim(Key("sw" + label, row), value ? 1f : 0f, 16f);
            RoundRect(new Rect(row.x - 6f, row.y, row.width + 12f, row.height), Alpha(Color.white, 0.035f * h), 6f);

            var on = onColor ?? Accent;
            var track = new Rect(row.xMax - 44f, row.y + (row.height - 22f) / 2f, 42f, 22f);
            RoundRect(track, Color.Lerp(Hex(0x2B2F3B), on, t), 11f);
            var knob = new Rect(Mathf.Lerp(track.x + 3f, track.xMax - 19f, t), track.y + 3f, 16f, 16f);
            RoundRect(new Rect(knob.x, knob.y + 1f, knob.width, knob.height), new Color(0f, 0f, 0f, 0.25f), 8f);
            RoundRect(knob, Color.white, 8f);

            var textRect = new Rect(row.x, row.y + (hint != null ? 3f : 0f), row.width - 54f, hint != null ? 20f : row.height);
            GUI.Label(textRect, label, value ? LabelBold : Label);
            if (hint != null) GUI.Label(new Rect(row.x, row.y + 22f, row.width - 54f, 18f), hint, LabelSmall);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none)) value = !value;
            return value;
        }

        public static float Slider(string label, float value, float min, float max, string format = "0.##", string suffix = "x")
        {
            var head = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(head.x, head.y, head.width - 70f, head.height), label, Label);
            string shown = value.ToString(format) + suffix;
            var pillSize = LabelBold.CalcSize(new GUIContent(shown));
            var pill = new Rect(head.xMax - pillSize.x - 14f, head.y + 1f, pillSize.x + 14f, 20f);
            RoundRect(pill, Alpha(Accent, 0.16f), 6f);
            var oldColor = LabelBold.normal.textColor;
            LabelBold.normal.textColor = AccentHot;
            LabelBold.alignment = TextAnchor.MiddleCenter;
            GUI.Label(pill, shown, LabelBold);
            LabelBold.alignment = TextAnchor.MiddleLeft;
            LabelBold.normal.textColor = oldColor;

            var track = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            bool hover = track.Contains(Event.current.mousePosition);
            float h = Anim(Key("sl" + label, track), hover ? 1f : 0f);
            float frac = Mathf.InverseLerp(min, max, value);
            var bar = new Rect(track.x, track.center.y - 3f, track.width, 6f);
            RoundRect(bar, Hex(0x2B2F3B), 3f);
            RoundRect(new Rect(bar.x, bar.y, Mathf.Max(6f, bar.width * frac), bar.height), Color.Lerp(Accent, AccentHot, h), 3f);
            float knobSize = 14f + 4f * h;
            var knob = new Rect(bar.x + bar.width * frac - knobSize / 2f, track.center.y - knobSize / 2f, knobSize, knobSize);
            RoundRect(new Rect(knob.x - 3f, knob.y - 3f, knob.width + 6f, knob.height + 6f), Alpha(Accent, 0.22f * h), (knobSize + 6f) / 2f);
            RoundRect(knob, Color.white, knobSize / 2f);
            float result = GUI.HorizontalSlider(track, value, min, max, invisibleTrack, invisibleThumb);
            GUILayout.Space(4);
            return result;
        }

        public static int Segmented(int selected, string[] options, float height = 32f)
        {
            var r = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
            RoundRect(r, Hex(0x0F1116), 8f);
            RoundOutline(r, Line, 8f);
            float w = r.width / options.Length;
            float x = Anim(Key("seg" + string.Join("|", options), new Rect(0, r.y, 0, 0)), selected, 18f);
            RoundRect(new Rect(r.x + 3f + x * w, r.y + 3f, w - 6f, r.height - 6f), Accent, 6f);
            for (int i = 0; i < options.Length; i++)
            {
                var cell = new Rect(r.x + i * w, r.y, w, r.height);
                bool hover = cell.Contains(Event.current.mousePosition);
                var old = ButtonText.normal.textColor;
                ButtonText.normal.textColor = ButtonText.hover.textColor = ButtonText.active.textColor =
                    i == selected ? Color.white : hover ? Text : Dim;
                if (GUI.Button(cell, options[i], ButtonText)) selected = i;
                ButtonText.normal.textColor = ButtonText.hover.textColor = ButtonText.active.textColor = old;
            }
            GUILayout.Space(6);
            return selected;
        }

        public static void Badge(string text, Color color)
        {
            var size = LabelSmall.CalcSize(new GUIContent(text));
            var r = GUILayoutUtility.GetRect(size.x + 14f, 20f, GUILayout.Width(size.x + 14f), GUILayout.Height(20f));
            BadgeAt(r, text, color);
        }

        public static void BadgeAt(Rect r, string text, Color color)
        {
            RoundRect(r, Alpha(color, 0.16f), 6f);
            var old = LabelSmall.normal.textColor;
            LabelSmall.normal.textColor = color;
            LabelSmall.alignment = TextAnchor.MiddleCenter;
            GUI.Label(r, text, LabelSmall);
            LabelSmall.alignment = TextAnchor.MiddleLeft;
            LabelSmall.normal.textColor = old;
        }

        public static string TextField(string value, string placeholder, params GUILayoutOption[] options)
        {
            value = GUILayout.TextField(value ?? "", Input, options);
            if (string.IsNullOrEmpty(value) && Event.current.type == EventType.Repaint)
            {
                var r = GUILayoutUtility.GetLastRect();
                var old = LabelDim.wordWrap;
                LabelDim.wordWrap = false;
                GUI.Label(new Rect(r.x + 10f, r.y, r.width - 20f, r.height), placeholder, LabelDim);
                LabelDim.wordWrap = old;
            }
            return value;
        }

        public static void Note(string text) => GUILayout.Label(text, LabelDim);

        /// <summary>A soft warning row (e.g. "host only").</summary>
        public static void Callout(string text, Color color)
        {
            var content = new GUIContent(text);
            var r = GUILayoutUtility.GetRect(content, LabelDim, GUILayout.ExpandWidth(true), GUILayout.MinHeight(34f));
            RoundRect(r, Alpha(color, 0.10f), 8f);
            RoundRect(new Rect(r.x, r.y, 3f, r.height), color, 1.5f);
            GUI.Label(new Rect(r.x + 12f, r.y, r.width - 16f, r.height), text, LabelDim);
            GUILayout.Space(6);
        }

        /// <summary>A grid of equally sized buttons; returns the clicked index or -1.</summary>
        public static int ButtonGrid(IList<string> labels, int columns, Kind kind = Kind.Normal, float height = 30f)
        {
            int clicked = -1;
            for (int i = 0; i < labels.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + columns; j++)
                {
                    float w = ContentWidth / columns - 6f;
                    if (j < labels.Count) { if (Button(labels[j], kind, GUILayout.Height(height), GUILayout.Width(w))) clicked = j; }
                    else GUILayout.Space(w + 6f);
                }
                GUILayout.EndHorizontal();
            }
            return clicked;
        }
    }
}
