using System;
using System.Collections.Generic;

namespace Snitch.Engine
{
    /// <summary>
    /// Groups the ~130 PlayerLoop steps into a dozen kinds of work a player can read: "Scripts", "Rendering",
    /// "Physics" and so on. A step name is "Phase.Step" (e.g. "Update.ScriptRunBehaviourUpdate"); the rules below
    /// match on the step part, first match wins, so the specific rules come before the broad ones.
    /// </summary>
    internal static class EngineCategories
    {
        internal const int Scripts = 0, Coroutines = 1, Startup = 2, Rendering = 3, WaitGpu = 4, Physics = 5,
            Animation = 6, Navigation = 7, Ui = 8, Loading = 9, Audio = 10, Particles = 11, Input = 12,
            Memory = 13, Other = 14;

        internal static readonly string[] Labels =
        {
            "Scripts", "Coroutines", "New objects starting", "Rendering", "Waiting for GPU / frame cap",
            "Physics", "Animation", "NPC navigation", "UI", "Loading", "Audio", "Particles", "Input",
            "Memory upkeep", "Other engine work",
        };

        internal static int Count => Labels.Length;

        // (substring of the step part, category). Order matters: first match wins.
        private static readonly KeyValuePair<string, int>[] Rules =
        {
            R("ScriptRunBehaviour", Scripts),
            R("ScriptRunDelayedStartupFrame", Startup),
            R("ScriptRunDelayed", Coroutines),
            R("TriggerEndOfFrameCallbacks", Coroutines),
            R("WaitForLastPresentation", WaitGpu),
            R("PresentAfterDraw", WaitGpu),
            R("Director", Animation),
            R("Animation", Animation),
            R("Constraint", Animation),
            R("Physics", Physics),
            R("Cloth", Physics),
            R("AIUpdate", Navigation),
            R("Canvas", Ui),
            R("RectTransform", Ui),
            R("IMGUI", Ui),
            R("UIElements", Ui),
            R("Preload", Loading),
            R("AsyncInstantiate", Loading),
            R("AsyncUpload", Loading),
            R("AsyncRead", Loading),
            R("Streaming", Loading),
            R("ContentLoading", Loading),
            R("Mouse", Input),
            R("Audio", Audio),
            R("Particle", Particles),
            R("VFX", Particles),
            R("Input", Input),
            R("MemoryFrameMaintenance", Memory),
            R("Render", Rendering),
            R("Renderer", Rendering),
            R("SkinnedMesh", Rendering),
            R("Graphics", Rendering),
            R("Camera", Rendering),
            R("Shader", Rendering),
            R("Texture", Rendering),
            R("LightProbe", Rendering),
            R("Enlighten", Rendering),
            R("Lod", Rendering),
            R("LOD", Rendering),
            R("Terrain", Rendering),
            R("Sprite", Rendering),
            R("Video", Rendering),
            R("FinishFrame", Rendering),
            R("BeginFrame", Rendering),
            R("Batch", Rendering),
        };

        private static KeyValuePair<string, int> R(string s, int c) => new KeyValuePair<string, int>(s, c);

        internal static int Of(string stepName)
        {
            if (string.IsNullOrEmpty(stepName)) return Other;
            int dot = stepName.IndexOf('.');
            string step = dot >= 0 ? stepName.Substring(dot + 1) : stepName;
            foreach (var r in Rules)
                if (step.IndexOf(r.Key, StringComparison.Ordinal) >= 0) return r.Value;
            return Other;
        }

        internal static string Label(int category) => (uint)category < (uint)Labels.Length ? Labels[category] : "Other engine work";
    }
}
