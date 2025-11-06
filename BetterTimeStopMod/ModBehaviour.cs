using Duckov.UI;
using Duckov.Utilities;
using ItemStatsSystem;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace better_time_stop
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private static ModBehaviour instance;
        private bool isInBase;
        private bool patchApplied;
        private FieldInfo? realTimeField;
        private object? harmonyInstance;
        private const string HarmonyAssemblyName = "0Harmony";
        private const string CoreAssemblyName = "TeamSoda.Duckov.Core";
        private const string HarmonyId = "duckov_demo1.time.pause";
        
        // 时间缩放相关
        private float baseFixedDeltaTime;
        private bool shouldPauseTime = false;
        
        // 基地内时间控制（地图开关）
        private bool baseTimePaused = false; // 基地内时间暂停状态（开关）
        private View? lastMapView = null; // 上次打开的地图View，用于检测地图打开事件
        
        // 战利品加载检测
        private bool lastLootLoadingState = false; // 上次检查的加载状态，用于减少日志
        
        // 物品检查完成检测
        private HashSet<object> lootItemsPendingInspection = new HashSet<object>(); // 等待检查的物品集合
        private Dictionary<object, Delegate> itemEventHandlers = new Dictionary<object, Delegate>(); // 物品事件处理器字典，用于取消订阅
        private float lastInspectionCompleteTime = 0f; // 最后一次物品检查完成的时间
        private const float INSPECTION_SOUND_DELAY = 0f; // 检查完成音效播放延迟（秒），确保音效播放完成
        private View? pendingLootViewForTracking = null; // 等待设置追踪的 LootView
        private float pendingLootViewTime = 0f; // 等待设置追踪的时间
        private const float MAX_WAIT_TIME = 2.0f; // 最大等待时间（秒）

        void Awake()
        {
            if (instance != null)
            {
                Destroy(this);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
            
            // 检查当前场景状态
            CheckBaseStatus(SceneManager.GetActiveScene().name);
            
            // 订阅View变化事件（用于调试和日志）
            View.OnActiveViewChanged += OnActiveViewChanged;
            
            Debug.Log("[时间暂停] Mod Awake完成！");
        }

        void Start()
        {
            // 保存原始的fixedDeltaTime
            baseFixedDeltaTime = Time.fixedDeltaTime;
            
            // 在Start中初始化Harmony补丁，确保所有程序集都已加载
            SetupHarmonyPatch();
            
            // 检查当前View状态
            CheckCurrentView();
            
            Debug.Log("[时间暂停] Mod加载完成！");
        }

        void LateUpdate()
        {
            // 在LateUpdate中设置Time.timeScale，确保在TimeScaleManager.Update之后执行
            // 这样我们的设置不会被游戏系统覆盖
            UpdateTimeScale();
            
            // 检查是否有待处理的 LootView 追踪设置
            CheckPendingLootViewTracking();
        }
        
        void Update()
        {
            // 检查View状态变化（在Update中检查，在LateUpdate中应用）
        }
        
        private void UpdateTimeScale()
        {
            float targetTimeScale = 1f; // 默认正常流速
            
            // 基地内不使用Time.timeScale暂停，只通过GameClock补丁暂停游戏时间
            // 基地外使用Time.timeScale实现全局暂停（包括动画）
            if (!isInBase)
            {
                // 基地外：首先检查 GameManager.Paused（暂停菜单是否打开）
                bool isPaused = IsGamePaused();
                if (isPaused)
                {
                    targetTimeScale = 0f; // 暂停菜单打开，暂停时间
                }
                else
                {
                    // 检查是否需要暂停时间（打开了相关View）
                    View? activeView = View.ActiveView;
                    if (activeView != null)
                    {
                        string viewTypeName = activeView.GetType().Name;
                        
                        // 特殊处理 LootView：检查加载状态和物品检查状态
                        bool shouldPauseForView = false;
                        if (viewTypeName == "LootView")
                        {
                            // 检查 UI 是否正在加载
                            bool isLoading = IsLootViewLoading(activeView);
                            
                            // 检查是否有物品正在等待检查或音效播放中
                            bool hasPendingInspection = instance.HasItemsPendingInspection();
                            
                            // 只在状态变化时输出日志，减少日志量
                            if (isLoading != instance.lastLootLoadingState || hasPendingInspection)
                            {
                                Debug.Log($"[时间暂停] LootView 状态: isLoading={isLoading}, hasPendingInspection={hasPendingInspection}, pendingCount={instance.lootItemsPendingInspection.Count}");
                                instance.lastLootLoadingState = isLoading;
                            }
                            
                            // 如果正在加载或有物品等待检查/音效播放，不暂停时间
                            shouldPauseForView = !isLoading && !hasPendingInspection;
                        }
                        else
                        {
                            // 非 LootView，重置状态
                            instance.lastLootLoadingState = false;
                            
                            // 其他View：检查是否是地图、背包、商店、玩家属性、任务、任务给予者、主钥匙、笔记、ATM或物品分解界面
                            shouldPauseForView = (viewTypeName == "MapSelectionView" || viewTypeName == "MiniMapView" || viewTypeName == "InventoryView" || viewTypeName == "StockShopView" || 
                                viewTypeName == "PlayerStatsView" || viewTypeName == "QuestView" || viewTypeName == "QuestGiverView" || viewTypeName == "MasterKeysView" || viewTypeName == "NoteIndexView" || 
                                viewTypeName == "ATMView" || viewTypeName == "ItemDecomposeView");
                        }

                        if (shouldPauseForView)
                        {
                            targetTimeScale = 0f; // 暂停时间
                        }
                    }
                }
                
                // 每帧强制设置Time.timeScale，确保在TimeScaleManager之后执行不会被重置
                // 使用直接赋值，确保立即生效
                Time.timeScale = targetTimeScale;
                Time.fixedDeltaTime = targetTimeScale == 0f ? 0f : (baseFixedDeltaTime * targetTimeScale);
            }
            // 基地内不设置Time.timeScale，保持正常流速，让动画和交互正常
            
            // 只在状态变化时打印日志（仅基地外）
            if (!isInBase)
            {
                bool newPauseState = (targetTimeScale == 0f);
                if (shouldPauseTime != newPauseState)
                {
                    shouldPauseTime = newPauseState;
                    if (shouldPauseTime)
                    {
                        Debug.Log("[时间暂停] 全局时间已暂停（Time.timeScale = 0）");
                    }
                    else
                    {
                        Debug.Log("[时间暂停] 全局时间已恢复（Time.timeScale = 1）");
                    }
                }
            }
        }

        private void CheckCurrentView()
        {
            View? activeView = View.ActiveView;
            if (activeView != null)
            {
                string viewTypeName = activeView.GetType().Name;
                Debug.Log($"[时间暂停] 当前已有View打开: {viewTypeName}");
            }
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
            SceneManager.sceneLoaded -= OnSceneLoaded;
            View.OnActiveViewChanged -= OnActiveViewChanged;
            try
            {
                if (patchApplied && harmonyInstance != null)
                {
                    Type? harmonyType = harmonyInstance.GetType();
                    MethodInfo? unpatchMethod = harmonyType?.GetMethod("UnpatchAll", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(string) }, null);
                    unpatchMethod?.Invoke(harmonyInstance, new object[] { HarmonyId });
                    Debug.Log("[时间暂停] Harmony Patch 已卸载");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[时间暂停] 卸载 Harmony Patch 失败: {ex.Message}");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            CheckBaseStatus(scene.name);
        }

        private void CheckBaseStatus(string sceneName)
        {
            bool wasInBase = isInBase;
            isInBase = sceneName?.Contains("Base_SceneV2", StringComparison.OrdinalIgnoreCase) ?? false;
            if (wasInBase != isInBase)
            {
                Debug.Log($"[时间暂停] 场景状态: {(isInBase ? "基地" : "非基地")}");
                
                // 进入基地时重置时间暂停状态
                if (isInBase)
                {
                    baseTimePaused = false;
                    lastMapView = null;
                    Debug.Log("[时间暂停] 进入基地 - 时间暂停状态已重置为正常流动");
                }
            }
        }

        // 检查并设置待处理的 LootView 追踪
        private void CheckPendingLootViewTracking()
        {
            if (pendingLootViewForTracking == null)
            {
                return;
            }
            
            // 检查是否超时
            float timeSincePending = Time.unscaledTime - pendingLootViewTime;
            if (timeSincePending > MAX_WAIT_TIME)
            {
                Debug.LogWarning($"[时间暂停] 等待 LootView TargetInventory 初始化超时，取消追踪设置");
                pendingLootViewForTracking = null;
                return;
            }
            
            // 尝试设置追踪
            if (SetupLootItemsInspectionTracking(pendingLootViewForTracking))
            {
                // 成功设置，清除待处理标记
                pendingLootViewForTracking = null;
                Debug.Log($"[时间暂停] LootView TargetInventory 已初始化，追踪设置成功");
            }
        }

        private void OnActiveViewChanged()
        {
            View? activeView = View.ActiveView;
            Debug.Log($"[时间暂停] View状态变化事件触发 - ActiveView: {(activeView != null ? activeView.GetType().Name : "null")}");
            
            if (activeView != null)
            {
                string viewTypeName = activeView.GetType().Name;
                Debug.Log($"[时间暂停] 检测到View打开: {viewTypeName}");
                
                // 基地内：检查是否是地图View（MapSelectionView或MiniMapView，不包括LootView）
                bool isBaseMapView = isInBase && (viewTypeName == "MapSelectionView" || viewTypeName == "MiniMapView");
                // 基地外：检查是否是地图View（包括MapSelectionView、MiniMapView或LootView）
                bool isOutBaseMapView = !isInBase && (viewTypeName == "MapSelectionView" || viewTypeName == "MiniMapView" || viewTypeName == "LootView");
                
                if (isBaseMapView)
                {
                    // 基地内：检测地图打开事件，切换时间暂停状态
                    // 只有当打开的是新的地图View（与上次不同）时才切换
                    if (lastMapView != activeView)
                    {
                        baseTimePaused = !baseTimePaused; // 切换暂停状态
                        lastMapView = activeView;
                        
                        string viewName = viewTypeName switch
                        {
                            "MapSelectionView" => "地图选择",
                            "MiniMapView" => "小地图",
                            _ => viewTypeName
                        };
                        Debug.Log($"[时间暂停] 基地内 - {viewName}已打开 - 时间状态切换为: {(baseTimePaused ? "已暂停（仅游戏时间）" : "正常流动")}");
                    }
                }
                else if (!isInBase && (isOutBaseMapView || viewTypeName == "InventoryView" || viewTypeName == "StockShopView" || 
                         viewTypeName == "PlayerStatsView" || viewTypeName == "QuestView" || viewTypeName == "QuestGiverView" || viewTypeName == "MasterKeysView" || viewTypeName == "NoteIndexView" || 
                         viewTypeName == "ATMView" || viewTypeName == "ItemDecomposeView"))
                {
                    // 如果是 LootView，重置加载状态并设置物品检查监听
                    if (viewTypeName == "LootView")
                    {
                        lastLootLoadingState = false; // 重置加载状态
                        // 尝试设置物品检查追踪
                        // 如果返回 false，表示 TargetInventory 正在初始化（仅限战利品箱），需要稍后重试
                        // 如果返回 true，表示设置成功或不需要追踪（打开背包时 TargetInventory 为 null）
                        if (!SetupLootItemsInspectionTracking(activeView))
                        {
                            // TargetInventory 可能正在初始化（仅限战利品箱），标记为待处理
                            pendingLootViewForTracking = activeView;
                            pendingLootViewTime = Time.unscaledTime;
                            Debug.Log($"[时间暂停] LootView TargetInventory 正在初始化（战利品箱），等待初始化后设置追踪");
                        }
                        else
                        {
                            // 设置成功（包括 TargetInventory 为 null 的情况），清除待处理标记
                            pendingLootViewForTracking = null;
                        }
                    }
                    else
                    {
                        // 非 LootView，清理检查追踪
                        CleanupLootItemsInspectionTracking();
                        pendingLootViewForTracking = null;
                    }
                    
                    // 基地外：显示View打开信息（这部分逻辑保持不变）
                    string viewName = viewTypeName switch
                    {
                        "MapSelectionView" => "地图选择",
                        "MiniMapView" => "小地图",
                        "InventoryView" => "背包",
                        "LootView" => "战利品/地图界面",
                        "StockShopView" => "商店界面",
                        "PlayerStatsView" => "玩家属性",
                        "QuestView" => "任务",
                        "QuestGiverView" => "任务给予者",
                        "MasterKeysView" => "主钥匙",
                        "NoteIndexView" => "笔记",
                        "ATMView" => "ATM界面",
                        "ItemDecomposeView" => "物品分解界面",
                        _ => viewTypeName
                    };
                    Debug.Log($"[时间暂停] {viewName}已打开 - 当前在非基地 - 时间将{(viewTypeName == "LootView" ? "等待加载完成" : "暂停")}");
                    Debug.Log($"[时间暂停] Harmony补丁状态: {(patchApplied ? "已应用" : "未应用")}");
                }
                else
                {
                    Debug.Log($"[时间暂停] 其他View已打开: {viewTypeName} - 时间正常流动");
                }
            }
            else
            {
                // View关闭时，如果是基地外，恢复正常流动；基地内不改变状态
                if (!isInBase)
                {
                    Debug.Log("[时间暂停] 所有View已关闭 - 时间恢复正常流动");
                    // 清理物品检查追踪
                    CleanupLootItemsInspectionTracking();
                    pendingLootViewForTracking = null;
                }
                else
                {
                    Debug.Log("[时间暂停] 所有View已关闭 - 基地内时间状态保持不变");
                }
                // 清空lastMapView引用，以便下次检测到地图打开时能正确切换
                lastMapView = null;
            }
        }

        private void SetupHarmonyPatch()
        {
            if (patchApplied)
            {
                Debug.Log("[时间暂停] Harmony Patch 已经应用，跳过");
                return;
            }
            try
            {
                Debug.Log("[时间暂停] 开始初始化Harmony补丁...");
                
                // 查找Harmony库
                Assembly? harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == HarmonyAssemblyName);
                
                if (harmonyAssembly == null)
                {
                    Debug.LogWarning("[时间暂停] 未在当前程序集中找到 Harmony 库，尝试从文件加载...");
                    
                    // 尝试从Mod文件夹加载Harmony DLL
                    try
                    {
                        string? dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        if (!string.IsNullOrEmpty(dllPath))
                        {
                            string? modDir = System.IO.Path.GetDirectoryName(dllPath);
                            if (!string.IsNullOrEmpty(modDir))
                            {
                                string harmonyDllPath = System.IO.Path.Combine(modDir, "0Harmony.dll");
                                if (System.IO.File.Exists(harmonyDllPath))
                                {
                                    Debug.Log($"[时间暂停] 从文件加载Harmony: {harmonyDllPath}");
                                    harmonyAssembly = Assembly.LoadFrom(harmonyDllPath);
                                }
                                else
                                {
                                    Debug.LogWarning($"[时间暂停] 未找到Harmony DLL文件: {harmonyDllPath}");
                                    Debug.LogWarning("[时间暂停] 请确保0Harmony.dll与你的Mod DLL在同一文件夹中！");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[时间暂停] 从文件加载Harmony失败: {ex.Message}");
                    }
                    
                    // 最后尝试通过程序集名称加载
                    if (harmonyAssembly == null)
                    {
                        try
                        {
                            harmonyAssembly = Assembly.Load(HarmonyAssemblyName);
                            Debug.Log("[时间暂停] 通过程序集名称加载Harmony成功");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[时间暂停] 通过程序集名称加载Harmony失败: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Debug.Log("[时间暂停] 在当前程序集中找到Harmony库");
                }

                if (harmonyAssembly == null)
                {
                    Debug.LogError("[时间暂停] 无法加载Harmony库！请确保0Harmony.dll与Mod DLL在同一文件夹中。");
                    return;
                }

                Debug.Log($"[时间暂停] Harmony程序集加载成功: {harmonyAssembly.GetName().Name}");

                Type? harmonyType = harmonyAssembly?.GetType("HarmonyLib.Harmony");
                Type? harmonyMethodType = harmonyAssembly?.GetType("HarmonyLib.HarmonyMethod");
                
                if (harmonyType == null || harmonyMethodType == null)
                {
                    Debug.LogError("[时间暂停] 无法找到 Harmony 核心类型");
                    return;
                }

                Debug.Log("[时间暂停] Harmony核心类型找到成功");

                // 创建Harmony实例
                harmonyInstance = Activator.CreateInstance(harmonyType, HarmonyId);
                if (harmonyInstance == null)
                {
                    Debug.LogError("[时间暂停] 创建 Harmony 实例失败");
                    return;
                }

                Debug.Log("[时间暂停] Harmony实例创建成功");

                // 查找GameClock.Update方法（用于游戏时间统计）
                MethodInfo? gameClockUpdateMethod = FindGameClockUpdateMethod();
                if (gameClockUpdateMethod == null)
                {
                    Debug.LogWarning("[时间暂停] 未找到 GameClock.Update 方法");
                    return;
                }

                Debug.Log("[时间暂停] GameClock.Update方法找到成功");

                // 查找realTimePlayed字段
                realTimeField = FindRealTimeField(gameClockUpdateMethod.DeclaringType);
                if (realTimeField == null)
                {
                    Debug.LogWarning("[时间暂停] 未找到 realTimePlayed 字段，但继续执行");
                }
                else
                {
                    Debug.Log("[时间暂停] realTimePlayed字段找到成功");
                }

                // 获取Harmony.Patch方法（需要先声明以便后续使用）
                MethodInfo? patchMethod = harmonyType.GetMethod("Patch", BindingFlags.Instance | BindingFlags.Public);
                if (patchMethod == null)
                {
                    Debug.LogError("[时间暂停] 未找到Harmony.Patch方法");
                    return;
                }

                // 查找TimeScaleManager.Update方法（用于全局时间控制）
                MethodInfo? timeScaleManagerUpdateMethod = FindTimeScaleManagerUpdateMethod();
                if (timeScaleManagerUpdateMethod == null)
                {
                    Debug.LogWarning("[时间暂停] 未找到 TimeScaleManager.Update 方法，使用LateUpdate方式");
                }
                else
                {
                    Debug.Log("[时间暂停] TimeScaleManager.Update方法找到成功");

                    // 创建TimeScaleManager补丁方法（Postfix，在TimeScaleManager.Update之后执行）
                    MethodInfo? timeScaleManagerPostfixMethod = typeof(ModBehaviour).GetMethod("TimeScaleManagerUpdatePostfix", 
                        BindingFlags.Static | BindingFlags.NonPublic);
                    
                    if (timeScaleManagerPostfixMethod != null)
                    {
                        try
                        {
                            object? timeScaleManagerHarmonyMethod = Activator.CreateInstance(harmonyMethodType, timeScaleManagerPostfixMethod);
                            if (timeScaleManagerHarmonyMethod != null)
                            {
                                // 应用TimeScaleManager补丁（Postfix）
                                // Harmony.Patch参数顺序: original, prefix, postfix, transpiler, finalizer
                                patchMethod.Invoke(harmonyInstance, new object[] { timeScaleManagerUpdateMethod, null, timeScaleManagerHarmonyMethod, null, null });
                                Debug.Log("[时间暂停] TimeScaleManager.Postfix Patch 应用成功");
                            }
                            else
                            {
                                Debug.LogWarning("[时间暂停] 创建TimeScaleManager HarmonyMethod失败");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[时间暂停] 应用TimeScaleManager补丁失败: {ex.Message}");
                            if (ex.InnerException != null)
                            {
                                Debug.LogError($"[时间暂停] 内部异常: {ex.InnerException.Message}");
                            }
                            // 继续执行，即使TimeScaleManager补丁失败，LateUpdate仍然可以工作
                        }
                    }
                }

                // 创建GameClock补丁方法（Prefix，用于游戏时间统计）
                MethodInfo? prefixMethod = typeof(ModBehaviour).GetMethod("GameClockUpdatePrefix", 
                    BindingFlags.Static | BindingFlags.NonPublic);
                
                if (prefixMethod == null)
                {
                    Debug.LogError("[时间暂停] 未找到GameClock补丁方法");
                    return;
                }

                Debug.Log("[时间暂停] GameClock补丁方法找到成功");

                object? harmonyMethod = Activator.CreateInstance(harmonyMethodType, prefixMethod);
                if (harmonyMethod == null)
                {
                    Debug.LogError("[时间暂停] 创建 HarmonyMethod 失败");
                    return;
                }

                // 应用GameClock补丁
                patchMethod.Invoke(harmonyInstance, new object[] { gameClockUpdateMethod, harmonyMethod, null, null, null });
                
                patchApplied = true;
                Debug.Log("[时间暂停] Harmony Patch 应用成功！时间暂停功能已启用");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[时间暂停] Harmony初始化失败: {ex.Message}");
                Debug.LogError($"[时间暂停] 堆栈跟踪: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Debug.LogError($"[时间暂停] 内部异常: {ex.InnerException.Message}");
                }
            }
        }

        private static MethodInfo? FindGameClockUpdateMethod()
        {
            Assembly? coreAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == CoreAssemblyName);
            
            if (coreAssembly == null)
            {
                return null;
            }

            Type? gameClockType = coreAssembly.GetType("GameClock", throwOnError: false);
            return gameClockType?.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static MethodInfo? FindTimeScaleManagerUpdateMethod()
        {
            Assembly? coreAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == CoreAssemblyName);
            
            if (coreAssembly == null)
            {
                return null;
            }

            Type? timeScaleManagerType = coreAssembly.GetType("TimeScaleManager", throwOnError: false);
            return timeScaleManagerType?.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

               private static void TimeScaleManagerUpdatePostfix()
               {
                   // 在TimeScaleManager.Update之后执行，强制覆盖它的设置
                   if (instance == null)
                   {
                       return;
                   }

                   // 基地内不使用Time.timeScale暂停，只通过GameClock补丁暂停游戏时间
                   // 基地外使用Time.timeScale实现全局暂停（包括动画）
                   if (!instance.isInBase)
                   {
                       float targetTimeScale = 1f; // 默认正常流速
                       
                       // 基地外：首先检查 GameManager.Paused（暂停菜单是否打开）
                       bool isPaused = IsGamePaused();
                       if (isPaused)
                       {
                           targetTimeScale = 0f; // 暂停菜单打开，暂停时间
                       }
                       else
                       {
                           // 检查是否需要暂停时间（打开了相关View）
                           View? activeView = View.ActiveView;
                           if (activeView != null)
                           {
                               string viewTypeName = activeView.GetType().Name;
                               
                               // 特殊处理 LootView：检查加载状态和物品检查状态
                               bool shouldPauseForView = false;
                               if (viewTypeName == "LootView")
                               {
                                   // 检查 UI 是否正在加载
                                   bool isLoading = IsLootViewLoading(activeView);
                                   
                                   // 检查是否有物品正在等待检查或音效播放中
                                   bool hasPendingInspection = instance.HasItemsPendingInspection();
                                   
                                   // 如果正在加载或有物品等待检查/音效播放，不暂停时间
                                   shouldPauseForView = !isLoading && !hasPendingInspection;
                               }
                               else
                               {
                                   // 其他View：检查是否是地图、背包、商店、玩家属性、任务、任务给予者、主钥匙、笔记、ATM或物品分解界面
                                   shouldPauseForView = (viewTypeName == "MapSelectionView" || viewTypeName == "MiniMapView" || viewTypeName == "InventoryView" || viewTypeName == "StockShopView" || 
                                       viewTypeName == "PlayerStatsView" || viewTypeName == "QuestView" || viewTypeName == "QuestGiverView" || viewTypeName == "MasterKeysView" || viewTypeName == "NoteIndexView" || 
                                       viewTypeName == "ATMView" || viewTypeName == "ItemDecomposeView");
                               }

                               if (shouldPauseForView)
                               {
                                   targetTimeScale = 0f; // 暂停时间
                               }
                           }
                       }
                       
                       // 强制覆盖TimeScaleManager的设置（仅基地外）
                       Time.timeScale = targetTimeScale;
                       Time.fixedDeltaTime = targetTimeScale == 0f ? 0f : (instance.baseFixedDeltaTime * targetTimeScale);
                   }
                   // 基地内不修改Time.timeScale，保持正常流速
               }

        private static FieldInfo? FindRealTimeField(Type? gameClockType)
        {
            return gameClockType?.GetField("realTimePlayed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        // 检查 GameManager.Paused 状态（用于检测暂停菜单是否打开）
        private static bool IsGamePaused()
        {
            try
            {
                Assembly? coreAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == CoreAssemblyName);
                
                if (coreAssembly == null)
                {
                    return false;
                }

                Type? gameManagerType = coreAssembly.GetType("GameManager", throwOnError: false);
                if (gameManagerType == null)
                {
                    return false;
                }

                PropertyInfo? pausedProperty = gameManagerType.GetProperty("Paused", 
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                
                if (pausedProperty == null)
                {
                    return false;
                }

                object? pausedValue = pausedProperty.GetValue(null);
                if (pausedValue is bool isPaused)
                {
                    return isPaused;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[时间暂停] 检查 GameManager.Paused 失败: {ex.Message}");
            }
            
            return false;
        }

        // 设置战利品物品检查追踪
        // 返回 true 表示成功设置，false 表示 TargetInventory 还未初始化
        private bool SetupLootItemsInspectionTracking(View? activeView)
        {
            if (activeView == null)
            {
                return false;
            }

            try
            {
                // 清理之前的追踪
                CleanupLootItemsInspectionTracking();

                // 通过反射获取 LootView 的 TargetInventory 属性
                Type lootViewType = activeView.GetType();
                PropertyInfo? targetInventoryProperty = lootViewType.GetProperty("TargetInventory", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (targetInventoryProperty == null)
                {
                    return false;
                }

                object? targetInventory = targetInventoryProperty.GetValue(activeView);
                if (targetInventory == null)
                {
                    // TargetInventory 为 null 表示打开的是背包（查看自己的物品），不是战利品箱
                    // 这种情况下不需要等待物品检查，可以直接暂停时间
                    Debug.Log($"[时间暂停] LootView TargetInventory 为 null（打开背包查看自己的物品），不需要追踪物品检查");
                    return true; // 返回 true 表示设置成功（实际上是不需要追踪）
                }

                // TargetInventory 不为 null，表示打开的是战利品箱，需要追踪物品检查
                // 获取 Inventory 的 Content 属性（List<Item>）
                Type inventoryType = targetInventory.GetType();
                PropertyInfo? contentProperty = inventoryType.GetProperty("Content", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (contentProperty == null)
                {
                    // Content 属性不存在，可能还在初始化中，返回 false 表示稍后重试
                    Debug.Log($"[时间暂停] LootView TargetInventory Content 属性不存在，可能正在初始化");
                    return false;
                }

                object? contentList = contentProperty.GetValue(targetInventory);
                if (contentList is System.Collections.IEnumerable items)
                {
                    // 遍历所有物品，找到需要检查的物品
                    foreach (object itemObj in items)
                    {
                        if (itemObj == null)
                        {
                            continue;
                        }

                        // 检查物品是否需要检查
                        Type itemType = itemObj.GetType();
                        PropertyInfo? needInspectionProperty = itemType.GetProperty("NeedInspection", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        PropertyInfo? inspectedProperty = itemType.GetProperty("Inspected", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        if (needInspectionProperty != null && inspectedProperty != null)
                        {
                            object? needInspectionValue = needInspectionProperty.GetValue(itemObj);
                            object? inspectedValue = inspectedProperty.GetValue(itemObj);

                            if (needInspectionValue is bool needInspection && needInspection &&
                                inspectedValue is bool inspected && !inspected)
                            {
                                // 需要检查且未检查，添加到追踪列表
                                lootItemsPendingInspection.Add(itemObj);

                                // 订阅检查状态变化事件
                                EventInfo? inspectionEvent = itemType.GetEvent("onInspectionStateChanged", 
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                                if (inspectionEvent != null)
                                {
                                    MethodInfo? addMethod = inspectionEvent.GetAddMethod();
                                    if (addMethod != null)
                                    {
                                        // 创建委托 - 使用反射创建泛型 Action<ItemType>
                                        Type actionType = typeof(System.Action<>).MakeGenericType(itemType);
                                        
                                        // 创建方法调用包装器
                                        MethodInfo? handlerMethod = typeof(ModBehaviour).GetMethod(
                                            nameof(OnLootItemInspectionStateChangedWrapper), 
                                            BindingFlags.NonPublic | BindingFlags.Instance);
                                        
                                        if (handlerMethod != null)
                                        {
                                            // 创建泛型方法实例
                                            MethodInfo genericHandler = handlerMethod.MakeGenericMethod(itemType);
                                            Delegate handler = Delegate.CreateDelegate(actionType, this, genericHandler);
                                            
                                            // 订阅事件
                                            addMethod.Invoke(itemObj, new object[] { handler });
                                            
                                            // 保存处理器以便后续取消订阅
                                            itemEventHandlers[itemObj] = handler;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    Debug.Log($"[时间暂停] LootView 物品检查追踪设置完成，待检查物品数量: {lootItemsPendingInspection.Count}");
                    return true; // 成功设置
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[时间暂停] 设置物品检查追踪失败: {ex.Message}");
            }
            
            return false; // 设置失败
        }

        // 清理物品检查追踪
        private void CleanupLootItemsInspectionTracking()
        {
            try
            {
                // 取消所有事件的订阅
                foreach (var kvp in itemEventHandlers)
                {
                    object itemObj = kvp.Key;
                    Delegate handler = kvp.Value;
                    
                    if (itemObj == null)
                    {
                        continue;
                    }

                    Type itemType = itemObj.GetType();
                    EventInfo? inspectionEvent = itemType.GetEvent("onInspectionStateChanged", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (inspectionEvent != null)
                    {
                        MethodInfo? removeMethod = inspectionEvent.GetRemoveMethod();
                        if (removeMethod != null)
                        {
                            try
                            {
                                removeMethod.Invoke(itemObj, new object[] { handler });
                            }
                            catch
                            {
                                // 忽略取消订阅失败
                            }
                        }
                    }
                }

                lootItemsPendingInspection.Clear();
                itemEventHandlers.Clear();
                lastInspectionCompleteTime = 0f;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[时间暂停] 清理物品检查追踪失败: {ex.Message}");
            }
        }

        // 物品检查状态变化事件处理包装器（泛型方法）
        private void OnLootItemInspectionStateChangedWrapper<T>(T item) where T : class
        {
            OnLootItemInspectionStateChanged(item);
        }

        // 物品检查状态变化事件处理
        private void OnLootItemInspectionStateChanged(object itemObj)
        {
            if (itemObj == null)
            {
                return;
            }

            try
            {
                Type itemType = itemObj.GetType();
                PropertyInfo? inspectedProperty = itemType.GetProperty("Inspected", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (inspectedProperty != null)
                {
                    object? inspectedValue = inspectedProperty.GetValue(itemObj);
                    if (inspectedValue is bool inspected && inspected)
                    {
                        // 物品检查完成
                        if (lootItemsPendingInspection.Contains(itemObj))
                        {
                            lootItemsPendingInspection.Remove(itemObj);
                            lastInspectionCompleteTime = Time.unscaledTime;
                            
                            // 取消事件订阅（类似 ItemLevelAndSearchSoundMod 的实现）
                            if (itemEventHandlers.TryGetValue(itemObj, out Delegate? handler))
                            {
                                EventInfo? inspectionEvent = itemType.GetEvent("onInspectionStateChanged", 
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                
                                if (inspectionEvent != null)
                                {
                                    MethodInfo? removeMethod = inspectionEvent.GetRemoveMethod();
                                    if (removeMethod != null)
                                    {
                                        try
                                        {
                                            removeMethod.Invoke(itemObj, new object[] { handler });
                                        }
                                        catch
                                        {
                                            // 忽略取消订阅失败
                                        }
                                    }
                                }
                                
                                itemEventHandlers.Remove(itemObj);
                            }
                            
                            Debug.Log($"[时间暂停] 物品检查完成，剩余待检查物品: {lootItemsPendingInspection.Count}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[时间暂停] 处理物品检查状态变化失败: {ex.Message}");
            }
        }

        // 检查是否有物品正在等待检查
        private bool HasItemsPendingInspection()
        {
            // 如果有待检查的物品，返回 true
            if (lootItemsPendingInspection.Count > 0)
            {
                return true;
            }

            // 如果最近刚完成检查，等待音效播放完成
            if (lastInspectionCompleteTime > 0f)
            {
                float timeSinceInspection = Time.unscaledTime - lastInspectionCompleteTime;
                if (timeSinceInspection < INSPECTION_SOUND_DELAY)
                {
                    return true; // 音效还在播放中
                }
            }

            return false;
        }

        // 检查 LootView 的 InventoryDisplay 是否正在加载
        // 根据 API 文档：InventoryDisplay.Setup() 会检查 Target.Loading，并调用 LoadEntriesTask()
        // LoadEntriesTask() 会显示 loadingIndcator，完成后隐藏并显示 contentFadeGroup
        private static bool IsLootViewLoading(View? activeView)
        {
            if (activeView == null)
            {
                return false;
            }

            string viewTypeName = activeView.GetType().Name;
            if (viewTypeName != "LootView")
            {
                return false;
            }

            try
            {
                // 首先检查 TargetInventory 是否为 null（打开背包的情况）
                // 如果 TargetInventory 为 null，表示打开的是背包，不需要检查 lootTargetInventoryDisplay 的加载状态
                Type lootViewType = activeView.GetType();
                PropertyInfo? targetInventoryProperty = lootViewType.GetProperty("TargetInventory", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                
                if (targetInventoryProperty != null)
                {
                    object? targetInventory = targetInventoryProperty.GetValue(activeView);
                    if (targetInventory == null)
                    {
                        // TargetInventory 为 null，表示打开的是背包，不需要等待加载
                        return false;
                    }
                }
                
                // TargetInventory 不为 null，表示打开的是战利品箱，需要检查 lootTargetInventoryDisplay 的加载状态
                // 通过反射获取 LootView 的 lootTargetInventoryDisplay 字段
                FieldInfo? inventoryDisplayField = lootViewType.GetField("lootTargetInventoryDisplay", 
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                
                if (inventoryDisplayField == null)
                {
                    // 如果找不到字段，保守处理：返回 true 以避免暂停
                    return true;
                }

                object? inventoryDisplay = inventoryDisplayField.GetValue(activeView);
                if (inventoryDisplay == null)
                {
                    // InventoryDisplay 为 null 可能表示正在初始化，返回 true 以避免暂停
                    return true;
                }

                Type inventoryDisplayType = inventoryDisplay.GetType();

                // 首先检查 Target.Loading（数据加载状态）
                PropertyInfo? targetProperty = inventoryDisplayType.GetProperty("Target", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                
                if (targetProperty != null)
                {
                    object? targetInventory = targetProperty.GetValue(inventoryDisplay);
                    if (targetInventory != null)
                    {
                        Type inventoryType = targetInventory.GetType();
                        PropertyInfo? loadingProperty = inventoryType.GetProperty("Loading", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        
                        if (loadingProperty != null)
                        {
                            object? loadingValue = loadingProperty.GetValue(targetInventory);
                            if (loadingValue is bool isLoading && isLoading)
                            {
                                // Target.Loading 为 true，说明数据还在加载
                                return true;
                            }
                        }
                    }
                }

                // 检查 InventoryDisplay 的 loadingIndcator 是否正在显示（UI加载中）
                FieldInfo? loadingIndicatorField = inventoryDisplayType.GetField("loadingIndcator", 
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                
                if (loadingIndicatorField != null)
                {
                    object? loadingIndicator = loadingIndicatorField.GetValue(inventoryDisplay);
                    if (loadingIndicator != null)
                    {
                        // 检查 FadeGroup 的 IsShown 属性（如果正在显示，说明还在加载）
                        Type fadeGroupType = loadingIndicator.GetType();
                        PropertyInfo? isShownProperty = fadeGroupType.GetProperty("IsShown", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        
                        if (isShownProperty != null)
                        {
                            object? isShownValue = isShownProperty.GetValue(loadingIndicator);
                            if (isShownValue is bool isShown && isShown)
                            {
                                // 加载指示器正在显示，说明还在加载
                                return true;
                            }
                        }
                        
                        // 检查 FadeGroup 的 IsShowingInProgress 属性（正在显示动画中）
                        PropertyInfo? isShowingProperty = fadeGroupType.GetProperty("IsShowingInProgress", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        
                        if (isShowingProperty != null)
                        {
                            object? isShowingValue = isShowingProperty.GetValue(loadingIndicator);
                            if (isShowingValue is bool isShowing && isShowing)
                            {
                                // 正在显示中，说明还在加载
                                return true;
                            }
                        }
                        
                        // 检查 FadeGroup 的 IsFading 属性（淡入淡出动画中）
                        PropertyInfo? isFadingProperty = fadeGroupType.GetProperty("IsFading", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        
                        if (isFadingProperty != null)
                        {
                            object? isFadingValue = isFadingProperty.GetValue(loadingIndicator);
                            if (isFadingValue is bool isFading && isFading)
                            {
                                // 淡入淡出动画中，说明还在加载
                                return true;
                            }
                        }
                    }
                }

                // 检查 contentFadeGroup 是否未显示（内容还未显示，说明还在加载）
                FieldInfo? contentFadeGroupField = inventoryDisplayType.GetField("contentFadeGroup", 
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                
                if (contentFadeGroupField != null)
                {
                    object? contentFadeGroup = contentFadeGroupField.GetValue(inventoryDisplay);
                    if (contentFadeGroup != null)
                    {
                        Type fadeGroupType = contentFadeGroup.GetType();
                        PropertyInfo? isShownProperty = fadeGroupType.GetProperty("IsShown", 
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        
                        if (isShownProperty != null)
                        {
                            object? isShownValue = isShownProperty.GetValue(contentFadeGroup);
                            if (isShownValue is bool isShown && !isShown)
                            {
                                // 内容淡入组未显示，说明还在加载
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 发生异常时，保守处理：返回 true 以避免暂停
                return true;
            }

            // 所有检查都通过，说明加载完成
            return false;
        }

        private static bool GameClockUpdatePrefix(object __instance)
        {
            if (instance == null)
            {
                return true; // 继续执行原方法
            }

            // 基地内：检查地图开关状态
            if (instance.isInBase)
            {
                if (instance.baseTimePaused)
                {
                    // 基地内时间暂停：只阻止GameClock.Update执行，不暂停动画和交互
                    // 仍需要更新真实时间（realTimePlayed）以确保游戏内的时间统计正确
                    UpdateRealTimeOnly(__instance, instance.realTimeField);
                    return false; // 阻止原方法执行，暂停游戏时间
                }
                return true; // 继续执行原方法，时间正常流动
            }

            // 基地外：首先检查 GameManager.Paused（暂停菜单是否打开）
            bool isPaused = IsGamePaused();
            if (isPaused)
            {
                // 暂停菜单打开，暂停游戏时间
                UpdateRealTimeOnly(__instance, instance.realTimeField);
                return false; // 阻止原方法执行，配合Time.timeScale实现完全暂停
            }

            // 检查是否有地图、背包或战利品界面View打开
            View? activeView = View.ActiveView;
            
            if (activeView != null)
            {
                string viewTypeName = activeView.GetType().Name;
                
                // 特殊处理 LootView：检查加载状态
                bool shouldPauseForView = false;
                if (viewTypeName == "LootView")
                {
                    // 如果是 LootView，检查是否正在加载
                    bool isLoading = IsLootViewLoading(activeView);
                    
                    // 检查是否有物品正在等待检查或音效播放中
                    bool hasPendingInspection = instance.HasItemsPendingInspection();
                    
                    // 如果正在加载或有物品等待检查/音效播放，不暂停时间；加载完成后才暂停
                    shouldPauseForView = !isLoading && !hasPendingInspection;
                }
                else
                {
                    // 其他View：检查是否是地图View（MapSelectionView或MiniMapView）、背包View（InventoryView）、商店界面（StockShopView）、
                    // 玩家属性（PlayerStatsView）、任务（QuestView）、任务给予者（QuestGiverView）、主钥匙（MasterKeysView）、笔记（NoteIndexView）、ATM（ATMView）或物品分解（ItemDecomposeView）
                    shouldPauseForView = (viewTypeName == "MapSelectionView" || viewTypeName == "MiniMapView" || viewTypeName == "InventoryView" || viewTypeName == "StockShopView" || 
                        viewTypeName == "PlayerStatsView" || viewTypeName == "QuestView" || viewTypeName == "QuestGiverView" || viewTypeName == "MasterKeysView" || viewTypeName == "NoteIndexView" || 
                        viewTypeName == "ATMView" || viewTypeName == "ItemDecomposeView");
                }

                if (shouldPauseForView)
                {
                    // 使用Time.timeScale实现全局暂停，这里仍需要更新真实时间（realTimePlayed）
                    // 以确保游戏内的时间统计正确
                    UpdateRealTimeOnly(__instance, instance.realTimeField);
                    return false; // 阻止原方法执行，配合Time.timeScale实现完全暂停
                }
            }

            // 没有相关View打开，时间正常流动
            return true; // 继续执行原方法，时间正常流动
        }

        private static void UpdateRealTimeOnly(object gameClock, FieldInfo? realTimeField)
        {
            if (realTimeField == null || gameClock == null)
            {
                return;
            }
            try
            {
                object? value = realTimeField.GetValue(gameClock);
                float unscaledDeltaTime = Time.unscaledDeltaTime;
                
                if (value is float floatValue)
                {
                    realTimeField.SetValue(gameClock, floatValue + unscaledDeltaTime);
                }
                else if (value is double doubleValue)
                {
                    realTimeField.SetValue(gameClock, doubleValue + unscaledDeltaTime);
                }
                else if (float.TryParse(value?.ToString(), out float parsedValue))
                {
                    realTimeField.SetValue(gameClock, parsedValue + unscaledDeltaTime);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[时间暂停] 更新真实时间失败: {ex.Message}");
            }
        }
    }
}