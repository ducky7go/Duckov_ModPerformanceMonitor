using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Duckov.Modding;
using HarmonyLib;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace ModPerformanceMonitor
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // 常量
        public const string harmonyName = "com.fshelix.modperformancemonitor";
        public const int timeWindowLength = 10;

        private static bool patched = false;
        
        private static Harmony? harmony;

        // 每个 Assembly 的最近1分钟记录队列
        private static readonly Dictionary<
            Assembly, Queue<(float timestamp, float duration)>> records
            = new Dictionary<Assembly, Queue<(float, float)>>();
        // FIXME Thread Safety?
        private static readonly Dictionary<Assembly, float> assemblyTotals
            = new Dictionary<Assembly, float>();

        // 每个 Assembly 的 Stopwatch，线程本地
        [ThreadStatic]
        private static Dictionary<Assembly, Stopwatch>? stopwatches;

        private List<KeyValuePair<Assembly, float>> displayList = new List<KeyValuePair<Assembly, float>>();

        // GUI刷新
        public const float displayFlushInterval = 1f;
        public const float lineHeight = 20f;    // 每行高度
        private float lastGuiUpdate = 0f;
        private int frameCount = 0;

        GameObject? textGO;
    

        public void OnEnable()
        {
            InitializeAndPatch();
            ModManager.OnModActivated += OnModActivated;
            CreateUI();
        }

        public void OnDisable()
        {
            Destroy(textGO);
            textGO = null;
            ModManager.OnModActivated -= OnModActivated;
            CleanupPatches();
        }
        public void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            try
            {
                PatchAssembly(behaviour.GetType().Assembly);
            }
            catch
            {
                // 忽略反射异常
            }
        }

        private void PatchMethod(MethodInfo method)
        {
            var prefix = typeof(ModBehaviour).GetMethod(
                nameof(OnMethodStart), BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(ModBehaviour).GetMethod(
                nameof(OnMethodEnd), BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                harmony?.Patch(method,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix)
                );
            }
            catch (Exception)
            {
                // 忽略反射异常
            }
        }

        private bool HarmonyLike(Type? type)
        {
            return type?.Name.Contains("Harmony") == true;
        }

        private void PatchAssembly(Assembly asm)
        {
            try
            {
                var types = asm.GetTypes();

                if (!types.Any(t => t.IsSubclassOf(typeof(Duckov.Modding.ModBehaviour))))
                    return;

                var filtered = types.Where(t =>
                    !t.Name.Contains("<") &&
                    !t.Name.Contains(">") &&
                    t.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly‌
                    ).Any(m =>
                        m.GetCustomAttributes(false).Any(attr =>
                            HarmonyLike(attr.GetType())
                        ) ||
                        m.DeclaringType.GetCustomAttributes(false).Any(attr =>
                            HarmonyLike(attr.GetType())
                        ) ||
                        m.DeclaringType.GetNestedTypes().Any(t =>
                            t.GetCustomAttributes(false).Any(attr =>
                                HarmonyLike(attr.GetType())
                            )
                        )
                    ) == false
                );

                foreach (var type in filtered)
                {
                    var methods = type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly‌
                    ).Where(m =>
                        !m.Name.Contains("<") &&
                        !m.Name.Contains(">")
                    );

                    if(methods.Any())
                        Debug.Log($"# [ModPerformanceMonitor] Patching {asm.GetName().Name} {type.Name}: {string.Join(" ", methods.Select(m => m.Name))}");

                    foreach (var method in methods)
                    {
                        // if (type.Namespace.Contains("NoConsumption") && (method.Name.Contains("Start") || method.Name.Contains("OnDestroy")))
                        //     continue;

                        if (asm == Assembly.GetExecutingAssembly() &&
                            method.Name != nameof(Update) &&
                            method.Name != nameof(OnGUI))
                            continue;

                        try
                        {
                            // Debug.Log($"[ModPerformanceMonitor] Patching method {method} in {asm}");
                            PatchMethod(method);
                        }
                        catch
                        {
                            // 忽略反射异常
                        }
                    }
                }
            }
            catch
            {
                // 忽略反射异常
            }
        }

        private void InitializeAndPatch()
        {
            if (patched)
                CleanupPatches();

            harmony = new Harmony(harmonyName);

            records.Clear();
            assemblyTotals.Clear();
            stopwatches = new Dictionary<Assembly, Stopwatch>();

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly asm in assemblies)
            {
                try
                {
                    PatchAssembly(asm);
                }
                catch
                {
                    // 忽略反射异常
                }
            }

            patched = true;
            UnityEngine.Debug.Log("[ModPerformanceMonitor] All ModBehaviour methods patched.");
        }

        private void CleanupPatches()
        {
            try
            {
                harmony?.UnpatchAll(harmonyName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModPerformanceMonitor] Failed to unpatch: {ex.Message}");
            }

            records.Clear();
            assemblyTotals.Clear();
            stopwatches?.Clear();
            harmony = null;
            patched = false;

            Debug.Log("[ModPerformanceMonitor] Cleaned up patches and resources.");
        }

        public string GetModDisplayName(Assembly asm)
        {
            foreach (ModInfo info in ModManager.modInfos)
                if (Path.GetFullPath(info.dllPath) == Path.GetFullPath(asm.Location))
                    return info.displayName;
            return asm.GetName().Name;
        }

        // Harmony 前缀
        private static void OnMethodStart(MethodBase __originalMethod)
        {
            var asm = __originalMethod?.DeclaringType?.Assembly;
            if (asm == null)
                return;

            if (stopwatches == null)
                stopwatches = new Dictionary<Assembly, Stopwatch>();

            if (!stopwatches.TryGetValue(asm, out var sw))
            {
                sw = new Stopwatch();
                stopwatches[asm] = sw;
            }

            // 只有在 Stopwatch 没在运行时才计时，避免内层重复计时
            if (!sw.IsRunning)
            {
                sw.Reset();
                sw.Start();
            }
        }

        // Harmony 后缀
        private static void OnMethodEnd(MethodBase __originalMethod)
        {
            
            var asm = __originalMethod?.DeclaringType?.Assembly;
            if (asm == null)
                return;

            if (stopwatches?.TryGetValue(asm, out Stopwatch sw) != true)
                return;

            if (!sw.IsRunning)
                return;

            sw.Stop();
            float now = Time.realtimeSinceStartup;
            float duration = (float)sw.Elapsed.TotalMilliseconds;

            if (!records.TryGetValue(asm, out var queue))
            {
                queue = new Queue<(float, float)>();
                records[asm] = queue;
            }

            queue.Enqueue((now, duration));

            if (!assemblyTotals.ContainsKey(asm))
                assemblyTotals[asm] = 0f;

            assemblyTotals[asm] += duration;
        }

        public void CreateUI()
        {
            Canvas existingCanvas = FindObjectOfType<Canvas>();

            textGO = new GameObject("TMP_Text", typeof(TextMeshProUGUI));
            textGO.transform.SetParent(existingCanvas.transform, false); // false 保持本地缩放
            TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
            text.enableWordWrapping = true;
            text.enableAutoSizing = false;
            text.raycastTarget = false;
            text.fontSize = 22;
            text.fontSizeMin = 22;
            text.fontSizeMax = 22;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.TopLeft;

            RectTransform transform = text.GetComponent<RectTransform>();
            transform.localScale = Vector3.one;
            transform.sizeDelta = new Vector2(350, 500);
            transform.anchorMin = new Vector2(1, 1);
            transform.anchorMax = new Vector2(1, 1);
            transform.pivot = new Vector2(1, 1);
            transform.anchoredPosition = new Vector2(-20, -200);

            Material mat = text.fontMaterial;
            mat.SetFloat("_FaceDilate", 0.1f);
            mat.SetFloat("_OutlineSoftness", 0.2f);

            textGO.SetActive(true);
        }

        public void Update()
        {
            if (textGO == null)
                return;

            ++frameCount;

            if (Input.GetKeyDown(KeyCode.F3))
            {
                textGO.SetActive(!textGO.activeSelf);
            }
        }

        public void OnGUI()
        {
            if (textGO == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now - lastGuiUpdate <= displayFlushInterval)
                return;

            // 更新数据
            float fps = frameCount / (now - lastGuiUpdate);
            float frameTime = 1000 / fps;
            frameCount = 0;
            lastGuiUpdate = now;

            displayList.Clear();

            foreach (var (asm, queue) in records)
            {
                // 统一清理超过时间的记录 避免线程同步问题
                while (queue.Count > 0 && now - queue.Peek().timestamp > timeWindowLength)
                    assemblyTotals[asm] -= queue.Dequeue().duration;

                float total = assemblyTotals[asm];
                displayList.Add(new KeyValuePair<Assembly, float>(asm, total));
            }

            displayList.Sort((a, b) => b.Value.CompareTo(a.Value));

            // 更新UI
            TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();
            // Debug.Log($"Updating text...{text}");
            text.text = $"FPS: {fps:F0}\n帧时间: {frameTime:F2} ms\n耗时占比(近{timeWindowLength}秒):\n" + string.Join("\n", displayList.Take(20).Select(kv => $"{GetModDisplayName(kv.Key)}: {kv.Value / timeWindowLength / 10:F2}%"));
        }
    }
}
