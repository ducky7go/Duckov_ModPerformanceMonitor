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
using Mono.Cecil;
using Mono.Cecil.Cil;
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

        private List<KeyValuePair<string, float>> displayList = new List<KeyValuePair<string, float>>();

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

        public static bool DontPatch(MethodInfo? methodInfo)
        {
            // 1. 检查 methodInfo 是否为 null
            if (methodInfo == null)
            {
                return true;
            }

            // 2. 获取程序集路径，并检查文件是否存在
            string assemblyPath = methodInfo.DeclaringType.Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                return true;
            }

            // 3. 尝试加载程序集
            AssemblyDefinition? assembly = null;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
            }
            catch (Exception)
            {
                // 如果程序集加载失败，直接返回 true
                return true;
            }

            if (assembly == null)
            {
                // 如果程序集仍然为 null，直接返回 true
                return true;
            }

            // 4. 获取目标类型
            var typeDefinition = assembly.MainModule.GetType(methodInfo.DeclaringType.FullName);
            if (typeDefinition == null)
            {
                // 如果找不到类型，直接跳过并返回 true
                return true;
            }

            // 5. 查找目标方法
            var methodDefinition = typeDefinition.Methods.FirstOrDefault(m => m.Name == methodInfo.Name && m.HasBody);
            if (methodDefinition == null)
            {
                // 如果没有找到目标方法，直接跳过并返回 true
                return true;
            }

            // 6. 遍历 IL 指令并检查是否调用了 Harmony 类中的 'patch' 方法
            foreach (var instruction in methodDefinition.Body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
                {
                    var methodReference = instruction.Operand as MethodReference;
                    if (methodReference != null)
                    {
                        // 检查是否调用了 Harmony 的 patch 方法
                        if (methodReference.DeclaringType.FullName.Contains("HarmonyLib.Harmony") &&
                            methodReference.Name.ToLower().Contains("patch"))
                        {
                            return true; // 找到匹配的调用，返回 true
                        }
                    }
                }
            }

            return false; // 没有找到调用
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
                        !m.Name.Contains(">") &&
                        m.GetMethodBody()?.GetILAsByteArray().Length > 10 &&
                        DontPatch(m) == false
                    );

                    if(methods.Any())
                        Debug.Log($"# [ModPerformanceMonitor] Patching {asm.GetName().Name} {type.Name}: {string.Join(" ", methods.Select(m => m.Name))}");

                    foreach (var method in methods)
                    {
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
                displayList.Add(new KeyValuePair<string, float>(GetModDisplayName(asm), total));
            }

            displayList.Add(new KeyValuePair<string, float>(
                "游戏本体+监测外",
                timeWindowLength * 1000 - displayList.Sum(kv => kv.Value)
            ));

            displayList.Sort((a, b) => b.Value.CompareTo(a.Value));

            // 更新UI
            TextMeshProUGUI text = textGO.GetComponent<TextMeshProUGUI>();

            // 窗口期总耗时/窗口期时间 ≈ 帧耗时/帧生成时间
            // kv.Value(ms) / 1000 / timeWindowLength(s) * 100(%)
            text.text = $"FPS: {fps:F0}\n帧生成时间: {frameTime:F2} ms\n耗时/帧生成时间(近{timeWindowLength}秒):\n" + string.Join("\n", displayList.Take(20).Select(kv => $"{kv.Key}: {kv.Value / timeWindowLength / 10:F2}%"));
        }
    }
}
