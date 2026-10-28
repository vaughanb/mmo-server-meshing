using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.Demo.EditorTools
{
    /// <summary>
    /// Transport controls for <see cref="DemoSkillPreview"/>, and the timeline they are
    /// really there to read.
    ///
    /// The bar is the point of this inspector. A skill's timing is spread across three
    /// places — the cast duration on the skill, the clip length on the animation and the
    /// trigger rates on top of it — and none of them mean anything on their own. Drawn end
    /// to end with the trigger marks on it, the question "does the arrow leave on the loose"
    /// becomes a thing you can look at, and the scrub puts the character on any frame of it.
    /// </summary>
    [CustomEditor(typeof(DemoSkillPreview))]
    public class DemoSkillPreviewEditor : Editor
    {
        private static readonly Color CastColor = new Color(0.30f, 0.42f, 0.62f);
        private static readonly Color ActivateColor = new Color(0.34f, 0.55f, 0.36f);
        private static readonly Color ExtendColor = new Color(0.33f, 0.33f, 0.33f);
        private static readonly Color TriggerColor = new Color(0.95f, 0.72f, 0.25f);
        private static readonly Color HeadColor = new Color(0.95f, 0.95f, 0.95f);

        /// <summary>So the playhead moves while the skill is running.</summary>
        public override bool RequiresConstantRepaint()
        {
            return ((DemoSkillPreview)target).IsPlaying;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var preview = (DemoSkillPreview)target;
            DemoSkillPreview.Shot shot = preview.Resolve();

            EditorGUILayout.Space();

            if (shot.Problem != null)
            {
                EditorGUILayout.HelpBox(shot.Problem, MessageType.Warning);
                return;
            }

            DrawSummary(shot);
            DrawTimeline(preview, shot);
            DrawScrub(preview, shot);
            DrawTransport(preview);
        }

        private static void DrawSummary(DemoSkillPreview.Shot shot)
        {
            string weapon = shot.WeaponType != null ? shot.WeaponType.name : "unarmed";
            var text = $"{weapon} — {shot.Source}: {shot.ActivateClip.name}\n";
            if (shot.CastDuration > 0f)
            {
                // the clip is only worth naming when there is a cast to play it over; with no
                // cast the model still hands back its generic one and naming it just misleads
                text += $"cast {shot.CastDuration:0.00}s";
                if (shot.CastClip != null)
                    text += $" ({shot.CastClip.name})";
                text += ", ";
            }
            text += $"activate {shot.ActivateDuration:0.00}s";
            if (!Mathf.Approximately(shot.ActivateSpeed, 1f))
                text += $" at {shot.ActivateSpeed:0.##}x";
            if (shot.ExtendDuration > 0f)
                text += $", then {shot.ExtendDuration:0.00}s of recovery";
            text += $"\ntotal {shot.Total:0.00}s";

            if (shot.TriggerTimes.Length == 0)
                text += ", no trigger — this skill applies nothing";
            else
                text += $", trigger at {string.Join(", ", System.Array.ConvertAll(shot.TriggerTimes, t => t.ToString("0.00") + "s"))}";

            if (shot.Audio.Length == 0)
                text += "\nno sound on the activate animation";
            if (shot.CastEffects.Length == 0 && shot.ActivateEffects.Length == 0)
                text += "\nno cast or activate effects on the skill";

            EditorGUILayout.HelpBox(text, MessageType.None);
        }

        /// <summary>
        /// Cast, activate and recovery drawn to scale, with the triggers on top and the
        /// playhead over everything.
        /// </summary>
        private static void DrawTimeline(DemoSkillPreview preview, DemoSkillPreview.Shot shot)
        {
            Rect bar = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint || shot.Total <= 0f)
                return;

            EditorGUI.DrawRect(bar, new Color(0.16f, 0.16f, 0.16f));

            float scale = bar.width / shot.Total;
            float x = bar.x;

            float castWidth = shot.CastDuration * scale;
            if (castWidth > 0f)
            {
                EditorGUI.DrawRect(new Rect(x, bar.y, castWidth, bar.height), CastColor);
                x += castWidth;
            }

            float activateWidth = shot.ActivateDuration * scale;
            EditorGUI.DrawRect(new Rect(x, bar.y, activateWidth, bar.height), ActivateColor);
            x += activateWidth;

            float extendWidth = shot.ExtendDuration * scale;
            if (extendWidth > 0f)
                EditorGUI.DrawRect(new Rect(x, bar.y, extendWidth, bar.height), ExtendColor);

            foreach (float trigger in shot.TriggerTimes)
                EditorGUI.DrawRect(new Rect(bar.x + trigger * scale - 1f, bar.y, 2f, bar.height), TriggerColor);

            if (preview.Time > 0f)
                EditorGUI.DrawRect(new Rect(bar.x + preview.Time * scale - 1f, bar.y - 2f, 2f, bar.height + 4f), HeadColor);
        }

        private static void DrawScrub(DemoSkillPreview preview, DemoSkillPreview.Shot shot)
        {
            EditorGUI.BeginChangeCheck();
            float time = EditorGUILayout.Slider("Time", preview.Time, 0f, shot.Total);
            if (EditorGUI.EndChangeCheck())
                preview.Scrub(time);
        }

        private static void DrawTransport(DemoSkillPreview preview)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(preview.IsPlaying ? "Restart" : "Play", GUILayout.Height(24f)))
                    preview.Play();
                if (GUILayout.Button("Stop", GUILayout.Height(24f)))
                    preview.Stop();
            }

            EditorGUILayout.HelpBox(
                "Runs in the editor — do not enter play mode, the bench holds bare models with no entity " +
                "behind them.\n\n" +
                "Stop puts the character back in its bind pose; the scrub holds whatever frame you leave it on.",
                MessageType.None);
        }
    }
}
