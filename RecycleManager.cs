// #define ENABLE_TESTS

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;

#if ENABLE_TESTS
using System.Collections;
using System.Reflection;
using Oxide.Core.Plugins;
#endif

namespace Oxide.Plugins
{
    [Info("Recycle Manager", "WhiteThunder", "2.0.0")]
    [Description("Allows customizing recycler speed, input, and output")]
    internal class RecycleManager : CovalencePlugin
    {
        #region Fields

        private Configuration _config;

        private const string PermissionAdmin = "recyclemanager.admin";

        private const int ScrapItemId = -932201673;
        private const float RecycleEfficiency = 0.5f;

        private readonly object True = true;
        private readonly object False = false;

        private readonly RecycleComponentManager _recycleComponentManager = new RecycleComponentManager();
        private readonly float[] _recycleTime = new float[1];

        #if ENABLE_TESTS
        private readonly RecycleManagerTests _testRunner;
        #endif

        public RecycleManager()
        {
            #if ENABLE_TESTS
            _testRunner = new RecycleManagerTests(this);
            #endif
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init(this);
            _recycleComponentManager.Init(this);

            permission.RegisterPermission(PermissionAdmin, this);

            if (!_config.RecycleSpeed.Enabled)
            {
                Unsubscribe(nameof(OnRecyclerToggle));
            }
        }

        private void OnServerInitialized()
        {
            #if ENABLE_TESTS
            _testRunner.Run();
            #endif
        }

        private void Unload()
        {
            #if ENABLE_TESTS
            _testRunner.Interrupt();
            #endif

            _recycleComponentManager.Unload();
        }

        // This hook is primarily used to determine whether an item can be placed into the recycler input,
        // but it's also called when processing each item.
        private object CanBeRecycled(Item item, Recycler recycler)
        {
            if (item == null)
                return null;

            if (_config.RestrictedInputItems.IsDisallowed(item))
            {
                // Defensively return null if vanilla would *disallow* recycling, to avoid hook conflicts.
                return IsVanillaRecyclable(item) && !RecyclableWasBlocked(item, recycler)
                    ? False
                    : null;
            }

            if (_config.OverrideOutput.GetBestOverride(item) != null)
            {
                // Defensively return null if vanilla would *allow* recycling, to avoid hook conflicts.
                return IsVanillaRecyclable(item) || RecyclableWasBlocked(item, recycler)
                    ? null
                    : True;
            }

            return null;
        }

        private object CanRecycle(Recycler recycler, Item item)
        {
            return CanBeRecycled(item, recycler);
        }

        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            _recycleComponentManager.HandleRecyclerToggle(recycler, player);
        }

        private object OnItemRecycle(Item item, Recycler recycler)
        {
            if (RecycleItemWasBlocked(item, recycler))
                return null;

            var amountToConsume = DetermineConsumptionAmount(recycler, item);
            if (amountToConsume <= 0)
                return False;

            var recycleEfficiency = DetermineRecycleEfficiency(recycler, item);

            var customIngredientList = _config.OverrideOutput.GetBestOverride(item);
            if (customIngredientList != null)
            {
                item.UseItem(amountToConsume);

                if (PopulateOutputWithOverride(recycler, customIngredientList, amountToConsume, recycleEfficiency))
                {
                    recycler.StopRecycling();
                }

                return False;
            }

            // If the item is not vanilla recyclable, and this plugin doesn't have an override,
            // that probably means another plugin is going to handle recycling it.
            if (!IsVanillaRecyclable(item))
                return null;

            item.UseItem(amountToConsume);

            var outputIsFull = PopulateOutputVanilla(_config, recycler, item, amountToConsume, recycleEfficiency);
            if (outputIsFull || !recycler.HasRecyclable())
            {
                recycler.StopRecycling();
            }

            return False;
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object OnRecycleManagerItemRecyclable(Item item, Recycler recycler)
            {
                return Interface.CallHook("OnRecycleManagerItemRecyclable", item, recycler);
            }

            public static object OnRecycleManagerSpeed(Recycler recycler, BasePlayer player, float[] recycleTime)
            {
                return Interface.CallHook("OnRecycleManagerSpeed", recycler, player, recycleTime);
            }

            public static object OnRecycleManagerRecycle(Item item, Recycler recycler)
            {
                return Interface.CallHook("OnRecycleManagerRecycle", item, recycler);
            }
        }

        #endregion

        #region Commands

        [Command("recyclemanager.add", "recman.add")]
        private void CommandAddItem(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyHasPermission(player, PermissionAdmin)
                || !VerifyConfigLoaded(player))
                return;

            ItemDefinition itemDefinition;
            if (!VerifyValidItemIdOrShortName(player, args.ElementAtOrDefault(0), out itemDefinition, cmd))
                return;

            if (!_config.OverrideOutput.AddOverride(this, itemDefinition))
            {
                ReplyToPlayer(player, LangEntry.AddExists, itemDefinition.shortname);
                return;
            }

            SaveConfig();
            ReplyToPlayer(player, LangEntry.AddSuccess, itemDefinition.shortname);
        }

        [Command("recyclemanager.reset", "recman.reset")]
        private void CommandResetItem(IPlayer player, string cmd, string[] args)
        {
            if (!VerifyHasPermission(player, PermissionAdmin)
                || !VerifyConfigLoaded(player))
                return;

            ItemDefinition itemDefinition;
            if (!VerifyValidItemIdOrShortName(player, args.ElementAtOrDefault(0), out itemDefinition, cmd))
                return;

            _config.OverrideOutput.ResetOverride(this, itemDefinition);
            SaveConfig();
            ReplyToPlayer(player, LangEntry.ResetSuccess, itemDefinition.shortname);
        }

        #endregion

        #region Helper Methods - Instance

        private bool VerifyHasPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
                return true;

            ReplyToPlayer(player, LangEntry.ErrorNoPermission);
            return false;
        }

        private bool VerifyConfigLoaded(IPlayer player)
        {
            if (!_config.UsingDefaults)
                return true;

            ReplyToPlayer(player, LangEntry.ErrorConfig);
            return false;
        }

        private bool VerifyValidItemIdOrShortName(IPlayer player, string itemArg, out ItemDefinition itemDefinition, string command)
        {
            if (itemArg == null)
            {
                itemDefinition = (player.Object as BasePlayer)?.GetActiveItem()?.info;
                if (itemDefinition != null)
                    return true;

                ReplyToPlayer(player, LangEntry.ItemSyntax, command);
                return false;
            }

            int itemId;
            if (int.TryParse(itemArg, out itemId))
            {
                itemDefinition = ItemManager.FindItemDefinition(itemId);
                if (itemDefinition != null)
                    return true;
            }

            itemDefinition = ItemManager.FindItemDefinition(itemArg);
            if (itemDefinition != null)
                return true;

            ReplyToPlayer(player, LangEntry.ErrorInvalidItem, itemArg);
            return false;
        }

        private bool TryDetermineRecycleTime(Recycler recycler, BasePlayer player, out float recycleTime)
        {
            _recycleTime[0] = _config.RecycleSpeed.DefaultRecycleTime
                              * _config.RecycleSpeed.GetTimeMultiplierForPlayer(player);

            var hookResult = ExposedHooks.OnRecycleManagerSpeed(recycler, player, _recycleTime);
            if (hookResult is bool && !(bool)hookResult)
            {
                recycleTime = 0;
                return false;
            }

            recycleTime = Math.Max(0, _recycleTime[0]);
            return true;
        }

        private float DetermineRecycleEfficiency(Recycler recycler, Item item)
        {
            return item.hasCondition
                ? Mathf.Clamp01(recycler.recycleEfficiency * Mathf.Clamp(item.conditionNormalized * item.maxConditionNormalized, 0.1f, 1f))
                : recycler.recycleEfficiency;
        }

        private int DetermineConsumptionAmount(Recycler recycler, Item item)
        {
            var amountToConsume = 1;

            if (item.amount > 1)
            {
                var consumeMultiplier = _config.MaxItemsPerRecycle.GetPercent(item) / 100f;
                amountToConsume = Mathf.CeilToInt(Mathf.Min(item.amount, (float)item.info.stackable * consumeMultiplier));

                // In case the configured multiplier is 0, ensure at least 1 item is recycled.
                amountToConsume = Math.Max(amountToConsume, 1);
            }

            // Call standard Oxide hook for compatibility.
            var amountOverride = Interface.CallHook("OnItemRecycleAmount", item, amountToConsume, recycler);
            if (amountOverride is int)
            {
                return (int)amountOverride;
            }

            return amountToConsume;
        }

        private bool PopulateOutputWithOverride(Recycler recycler, IngredientInfo[] customIngredientList, int amountToConsume, float recycleEfficiency = RecycleEfficiency, bool alwaysRoundUp = false)
        {
            var outputIsFull = false;

            foreach (var ingredientInfo in customIngredientList)
            {
                if (ingredientInfo.ItemDefinition == null)
                    continue;

                var amountToCreatePerConsumedItem = ingredientInfo.Amount * recycleEfficiency;
                if (amountToCreatePerConsumedItem <= 0)
                    continue;

                var amountToCreate = CalculateOutputAmount(amountToConsume, amountToCreatePerConsumedItem, alwaysRoundUp: alwaysRoundUp);
                if (amountToCreate <= 0)
                    continue;

                if (AddItemToRecyclerOutput(recycler, ingredientInfo.ItemDefinition, amountToCreate, ingredientInfo.SkinId, ingredientInfo.DisplayName))
                {
                    outputIsFull = true;
                }
            }

            return outputIsFull;
        }

        private static bool PopulateOutputVanilla(Configuration config, Recycler recycler, Item item, int amountToConsume, float recycleEfficiency = RecycleEfficiency, bool alwaysRoundUp = false)
        {
            var outputIsFull = false;

            if (item.info.Blueprint.scrapFromRecycle > 0)
            {
                var scrapOutputMultiplier = config.OutputMultipliers.GetOutputMultiplier(ScrapItemId);

                var scrapAmount = Mathf.CeilToInt(item.info.Blueprint.scrapFromRecycle * amountToConsume);
                scrapAmount = Mathf.CeilToInt(scrapAmount * scrapOutputMultiplier);

                if (!alwaysRoundUp && item.info.stackable == 1 && item.hasCondition)
                {
                    scrapAmount = Mathf.CeilToInt(scrapAmount * item.conditionNormalized);
                }

                if (scrapAmount >= 1)
                {
                    var scrapItem = ItemManager.CreateByItemID(ScrapItemId, scrapAmount);
                    recycler.MoveItemToOutput(scrapItem);
                }
            }

            foreach (var ingredient in item.info.Blueprint.ingredients)
            {
                // Skip scrap since it's handled separately.
                if (ingredient.itemDef.itemid == ScrapItemId)
                    continue;

                var amountToCreatePerConsumedItem = ingredient.amount
                    * recycleEfficiency
                    / item.info.Blueprint.amountToCreate;

                if (amountToCreatePerConsumedItem <= 0)
                    continue;

                var amountToCreate = CalculateOutputAmount(
                    amountToConsume,
                    amountToCreatePerConsumedItem,
                    config.OutputMultipliers.GetOutputMultiplier(ingredient.itemid),
                    alwaysRoundUp: alwaysRoundUp
                );

                if (amountToCreate <= 0)
                    continue;

                if (AddItemToRecyclerOutput(recycler, ingredient.itemDef, amountToCreate))
                {
                    outputIsFull = true;
                }
            }

            return outputIsFull;
        }

        #endregion

        #region Helper Methods - Static

        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Recycle Manager] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Recycle Manager] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Recycle Manager] {message}");

        private static bool IsVanillaRecyclable(Item item)
        {
            return item.info.Blueprint != null;
        }

        private static bool RecyclableWasBlocked(Item item, Recycler recycler)
        {
            var hookResult = ExposedHooks.OnRecycleManagerItemRecyclable(item, recycler);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool RecycleItemWasBlocked(Item item, Recycler recycler)
        {
            var hookResult = ExposedHooks.OnRecycleManagerRecycle(item, recycler);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static Item CreateItem(ItemDefinition itemDefinition, int amount, ulong skinId, string displayName = null)
        {
            var item = ItemManager.Create(itemDefinition, amount, skinId);

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                item.name = displayName;
            }

            return item;
        }

        private static int CalculateOutputAmountVanillaRandom(int amountToConsume, float amountToCreatePerConsumedItem)
        {
            var amountToCreate = 0;

            // Use vanilla behavior for up to 100 items (arbitrary number).
            // Roll a random number for every item to consume.
            for (var i = 0; i < amountToConsume; i++)
            {
                if (UnityEngine.Random.Range(0f, 1f) <= amountToCreatePerConsumedItem)
                {
                    amountToCreate++;
                }
            }

            return amountToCreate;
        }

        private static int CalculateOutputAmountFast(int amountToConsume, float amountToCreatePerConsumedItem)
        {
            // To save on performance, don't generate hundreds/thousands/millions of random numbers.
            var fractionalAmountToCreate = amountToCreatePerConsumedItem * amountToConsume;

            var amountToCreate = (int)fractionalAmountToCreate;

            // Roll a random number to see if the the remainder should be given.
            var remainderFraction = fractionalAmountToCreate - amountToCreate;
            if (remainderFraction > 0 && UnityEngine.Random.Range(0f, 1f) <= remainderFraction)
            {
                amountToCreate++;
            }

            return amountToCreate;
        }

        private static int CalculateOutputAmount(int amountToConsume, float amountToCreatePerConsumedItem, float outputMultiplier = 1, bool alwaysRoundUp = false)
        {
            if (amountToCreatePerConsumedItem > 1 && outputMultiplier > 1)
            {
                // Round up to make sure vanilla fractional amounts are multiplied accurately.
                amountToCreatePerConsumedItem = Mathf.CeilToInt(amountToCreatePerConsumedItem) * outputMultiplier;
            }

            if (!alwaysRoundUp && amountToCreatePerConsumedItem < 1f)
            {
                if (amountToConsume <= 100)
                {
                    return CalculateOutputAmountVanillaRandom(amountToConsume, amountToCreatePerConsumedItem);
                }
                else
                {
                    return CalculateOutputAmountFast(amountToConsume, amountToCreatePerConsumedItem);
                }
            }

            return amountToConsume * Mathf.CeilToInt(amountToCreatePerConsumedItem);
        }

        private static bool AddItemToRecyclerOutput(Recycler recycler, ItemDefinition itemDefinition, int amountToCreate, ulong skinId = 0, string displayName = null)
        {
            var outputIsFull = false;
            var numStacks = Mathf.CeilToInt(amountToCreate / (float)itemDefinition.stackable);

            for (var i = 0; i < numStacks; i++)
            {
                var amountForStack = Math.Min(amountToCreate, itemDefinition.stackable);
                var outputItem = CreateItem(itemDefinition, amountForStack, skinId, displayName);

                if (!recycler.MoveItemToOutput(outputItem))
                {
                    outputIsFull = true;
                }

                amountToCreate -= amountForStack;

                if (amountToCreate <= 0)
                    break;
            }

            return outputIsFull;
        }

        #endregion

        #region Recycler Component

        private class RecyclerComponent : FacepunchBehaviour
        {
            public static RecyclerComponent AddToRecycler(RecycleManager plugin, RecycleComponentManager recycleComponentManager, Recycler recycler)
            {
                var component = recycler.gameObject.AddComponent<RecyclerComponent>();
                component._plugin = plugin;
                component._recycleComponentManager = recycleComponentManager;
                component._recycler = recycler;
                component._vanillaRecycleThink = recycler.RecycleThink;
                return component;
            }

            private RecycleManager _plugin;
            private RecycleComponentManager _recycleComponentManager;
            private Recycler _recycler;
            private Action _vanillaRecycleThink;
            private Action _customRecycleThink;
            private float _recycleTime;

            private Configuration _config => _plugin._config;
            private bool _enableIncrementalRecycling => _config.RecycleSpeed.EnableIncrementalRecycling;

            private RecyclerComponent()
            {
                _customRecycleThink = CustomRecycleThink;
            }

            public void DestroyImmediate()
            {
                DestroyImmediate(this);
            }

            public void HandleRecyclerToggle(BasePlayer player)
            {
                // Delay for the following reasons.
                //   1. Allow other plugins to block toggle the recycler.
                //   2. Override other plugins after they start the recycler with custom speed.
                // If another plugin wants to alter the speed, they should use the hook that will be called on a delay.
                Invoke(() => HandleRecyclerToggleDelayed(player), 0);
            }

            private void HandleRecyclerToggleDelayed(BasePlayer player)
            {
                if (_recycler.IsOn())
                {
                    // Allow other plugins to block this, or to modify recycle time.
                    if (!_plugin.TryDetermineRecycleTime(_recycler, player, out _recycleTime))
                        return;

                    // Cancel the vanilla recycle invoke, in case another plugin started it.
                    // The custom recycle think method will also cancel it, since it may be started on a delay.
                    _recycler.CancelInvoke(_vanillaRecycleThink);

                    if (_enableIncrementalRecycling)
                    {
                        Invoke(_customRecycleThink, GetItemRecycleTime(GetNextItem()));
                    }
                    else
                    {
                        InvokeRepeating(_customRecycleThink, _recycleTime, _recycleTime);
                    }
                }
                else
                {
                    // The recycler is off, but vanilla doesn't know how to turn off custom recycling.
                    CancelInvoke(_customRecycleThink);
                }
            }

            private void CustomRecycleThink()
            {
                if (!_recycler.IsOn())
                {
                    // Vanilla or another plugin turned off the recycler, so don't process the next item.
                    if (!_enableIncrementalRecycling)
                    {
                        // Incremental recycling is disabled, so we must cancel the repeating custom invoke.
                        CancelInvoke(_customRecycleThink);
                    }
                    return;
                }

                // Stop the vanilla invokes if another plugin started them.
                // Necessary because some plugins will start the vanilla invokes on a delay to change recycler speed.
                // If another plugin wants to change recycler speed, they should use the hook designed for that.
                if (_recycler.IsInvoking(_vanillaRecycleThink))
                {
                    _recycler.CancelInvoke(_vanillaRecycleThink);
                }

                _recycler.RecycleThink();

                if (_enableIncrementalRecycling)
                {
                    var nextItem = GetNextItem();
                    if (nextItem != null)
                    {
                        Invoke(_customRecycleThink, GetItemRecycleTime(nextItem));
                    }
                }
            }

            private float GetItemRecycleTime(Item item)
            {
                if (_recycleTime == 0)
                    return _recycleTime;

                return _recycleTime * _config.RecycleSpeed.GetTimeMultiplierForItem(item);
            }

            private Item GetNextItem()
            {
                for (var i = 0; i < 6; i++)
                {
                    var item = _recycler.inventory.GetSlot(i);
                    if (item != null)
                        return item;
                }

                return null;
            }

            private void OnDestroy()
            {
                _recycleComponentManager.HandleRecyclerComponentDestroyed(_recycler);
            }
        }

        private class RecycleComponentManager
        {
            private RecycleManager _plugin;
            private readonly Dictionary<Recycler, RecyclerComponent> _recyclerComponents = new Dictionary<Recycler, RecyclerComponent>();

            public void Init(RecycleManager plugin)
            {
                _plugin = plugin;
            }

            public void Unload()
            {
                foreach (var recyclerComponent in _recyclerComponents.Values.ToArray())
                {
                    recyclerComponent.DestroyImmediate();
                }
            }

            public void HandleRecyclerToggle(Recycler recycler, BasePlayer player)
            {
                EnsureRecyclerComponent(recycler).HandleRecyclerToggle(player);
            }

            public void HandleRecyclerComponentDestroyed(Recycler recycler)
            {
                _recyclerComponents.Remove(recycler);
            }

            private RecyclerComponent EnsureRecyclerComponent(Recycler recycler)
            {
                RecyclerComponent recyclerComponent;
                if (!_recyclerComponents.TryGetValue(recycler, out recyclerComponent))
                {
                    recyclerComponent = RecyclerComponent.AddToRecycler(_plugin, this, recycler);
                    _recyclerComponents[recycler] = recyclerComponent;
                }
                return recyclerComponent;
            }
        }

        #endregion

        #region Configuration

        private class CaseInsensitiveDictionary<TValue> : Dictionary<string, TValue>
        {
            public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase) {}
        }

        private class CaseInsensitiveHashSet : HashSet<string>
        {
            public CaseInsensitiveHashSet() : base(StringComparer.OrdinalIgnoreCase) {}
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class PermissionSpeedProfile
        {
            [JsonProperty("Permission suffix")]
            public string PermissionSuffix;

            [JsonProperty("Recycle time (seconds)")]
            private float RecycleTime { set { TimeMultiplier = value / 5f; } }

            [JsonProperty("Recycle time multiplier")]
            public float TimeMultiplier = 1;

            [JsonIgnore]
            public string Permission { get; private set; }

            public void Init(RecycleManager plugin)
            {
                if (!string.IsNullOrWhiteSpace(PermissionSuffix))
                {
                    Permission = $"{nameof(RecycleManager)}.speed.{PermissionSuffix}".ToLower();
                    plugin.permission.RegisterPermission(Permission, plugin);
                }
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RecycleSpeed
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Default recycle time (seconds)")]
            public float DefaultRecycleTime = 5;

            [JsonProperty("Recycle time (seconds)")]
            private float DeprecatedRecycleTime { set { DefaultRecycleTime = value; } }

            [JsonProperty("Recycle time multiplier by item short name (item: multiplier)")]
            public Dictionary<string, float> TimeMultiplierByShortName = new Dictionary<string, float>();

            [JsonProperty("Recycle time multiplier by permission")]
            public PermissionSpeedProfile[] PermissionSpeedProfiles = new PermissionSpeedProfile[]
            {
                new PermissionSpeedProfile
                {
                    PermissionSuffix = "fast",
                    TimeMultiplier = 0.2f,
                },
                new PermissionSpeedProfile
                {
                    PermissionSuffix = "instant",
                    TimeMultiplier = 0,
                },
            };

            [JsonProperty("Speeds requiring permission")]
            private PermissionSpeedProfile[] DeprecatedSpeedsRequiringPermisison
            { set { PermissionSpeedProfiles = value; } }

            [JsonIgnore]
            private Permission _permission;

            [JsonIgnore]
            private Dictionary<int, float> _timeMultiplierByItemId = new Dictionary<int, float>();

            [JsonIgnore]
            public bool EnableIncrementalRecycling;

            public void Init(RecycleManager plugin)
            {
                _permission = plugin.permission;

                foreach (var speedProfile in PermissionSpeedProfiles)
                {
                    speedProfile.Init(plugin);
                }

                foreach (var entry in TimeMultiplierByShortName)
                {
                    var itemShortName = entry.Key;
                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {itemShortName}");
                        continue;
                    }

                    if (entry.Value == 1)
                        continue;

                    _timeMultiplierByItemId[itemDefinition.itemid] = entry.Value;
                    EnableIncrementalRecycling = true;
                }
            }

            public float GetTimeMultiplierForPlayer(BasePlayer player)
            {
                for (var i = PermissionSpeedProfiles.Length - 1; i >= 0; i--)
                {
                    var speedProfile = PermissionSpeedProfiles[i];
                    if (speedProfile.Permission != null
                        && _permission.UserHasPermission(player.UserIDString, speedProfile.Permission))
                    {
                        return speedProfile.TimeMultiplier;
                    }
                }

                return 1;
            }

            public float GetTimeMultiplierForItem(Item item)
            {
                float time;
                if (_timeMultiplierByItemId.TryGetValue(item.info.itemid, out time))
                    return time;

                return 1;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class MaxItemsPerRecycle
        {
            [JsonProperty("Default percent")]
            public float DefaultPercent = 10f;

            [JsonProperty("Percent by input item short name")]
            public Dictionary<string, float> PercentByShortName = new Dictionary<string, float>();

            [JsonProperty("Percent by input item skin ID")]
            public Dictionary<ulong, float> PercentBySkinId = new Dictionary<ulong, float>();

            [JsonProperty("Percent by input item display name (custom items)")]
            public CaseInsensitiveDictionary<float> PercentByDisplayName = new CaseInsensitiveDictionary<float>();

            [JsonIgnore]
            private Dictionary<int, float> PercentByItemId = new Dictionary<int, float>();

            public void Init(RecycleManager plugin)
            {
                foreach (var entry in PercentByShortName)
                {
                    var shortName = entry.Key;
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    PercentByItemId[itemDefinition.itemid] = entry.Value;
                }
            }

            public float GetPercent(Item item)
            {
                float multiplier;
                if (!string.IsNullOrWhiteSpace(item.name) && PercentByDisplayName.TryGetValue(item.name, out multiplier))
                    return multiplier;

                if (item.skin != 0 && PercentBySkinId.TryGetValue(item.skin, out multiplier))
                    return multiplier;

                if (PercentByItemId.TryGetValue(item.info.itemid, out multiplier))
                    return multiplier;

                return DefaultPercent;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class OutputMultiplierSettings
        {
            [JsonProperty("Default multiplier")]
            public float DefaultMultiplier = 1f;

            [JsonProperty("Multiplier by output item short name")]
            public Dictionary<string, float> MultiplierByOutputShortName = new Dictionary<string, float>();

            [JsonIgnore]
            private Dictionary<int, float> MultiplierByOutputItemId = new Dictionary<int, float>();

            public void Init(RecycleManager plugin)
            {
                foreach (var entry in MultiplierByOutputShortName)
                {
                    var shortName = entry.Key;
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    MultiplierByOutputItemId[itemDefinition.itemid] = entry.Value;
                }
            }

            public float GetOutputMultiplier(int itemId)
            {
                float multiplier;
                if (MultiplierByOutputItemId.TryGetValue(itemId, out multiplier))
                    return multiplier;

                return DefaultMultiplier;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class IngredientInfo
        {
            [JsonProperty("Item short name")]
            public string ShortName;

            [JsonProperty("Item skin ID", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong SkinId;

            [JsonProperty("Display name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DisplayName;

            [JsonProperty("Amount")]
            public float Amount;

            [JsonIgnore]
            public ItemDefinition ItemDefinition;

            public void Init(RecycleManager plugin)
            {
                ItemDefinition = ItemManager.FindItemDefinition(ShortName);
                if (ItemDefinition == null)
                {
                    LogWarning($"Invalid item short name in config: {ShortName}");
                }

                if (Amount < 0)
                {
                    Amount = 0;
                }
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class OverrideOutput
        {
            [JsonProperty("Override output by input item short name")]
            public Dictionary<string, IngredientInfo[]> OverrideOutputByShortName = new Dictionary<string, IngredientInfo[]>();

            [JsonProperty("Override output by input item skin ID")]
            public Dictionary<ulong, IngredientInfo[]> OverrideOutputBySkinId = new Dictionary<ulong, IngredientInfo[]>();

            [JsonProperty("Override output by input item display name (custom items)")]
            public CaseInsensitiveDictionary<IngredientInfo[]> OverrideOutputByDisplayName = new CaseInsensitiveDictionary<IngredientInfo[]>();

            [JsonIgnore]
            private Dictionary<int, IngredientInfo[]> OverrideOutputByItemId = new Dictionary<int, IngredientInfo[]>();

            public void Init(RecycleManager plugin)
            {
                foreach (var entry in OverrideOutputByShortName)
                {
                    var shortName = entry.Key;
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    var ingredientInfoList = entry.Value;
                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init(plugin);
                    }

                    OverrideOutputByItemId[itemDefinition.itemid] = ingredientInfoList;
                }

                foreach (var ingredientInfoList in OverrideOutputBySkinId.Values)
                {
                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init(plugin);
                    }
                }

                foreach (var ingredientInfoList in OverrideOutputByDisplayName.Values)
                {
                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init(plugin);
                    }
                }
            }

            public IngredientInfo[] GetBestOverride(Item item)
            {
                IngredientInfo[] ingredientInfoList;
                if (!string.IsNullOrWhiteSpace(item.name) && OverrideOutputByDisplayName.TryGetValue(item.name, out ingredientInfoList))
                    return ingredientInfoList;

                if (item.skin != 0 && OverrideOutputBySkinId.TryGetValue(item.skin, out ingredientInfoList))
                    return ingredientInfoList;

                if (OverrideOutputByItemId.TryGetValue(item.info.itemid, out ingredientInfoList))
                    return ingredientInfoList;

                return null;
            }

            public bool AddOverride(RecycleManager plugin,ItemDefinition itemDefinition)
            {
                if (OverrideOutputByShortName.ContainsKey(itemDefinition.shortname))
                    return false;

                ResetOverride(plugin, itemDefinition);
                return true;
            }

            public void ResetOverride(RecycleManager plugin,ItemDefinition itemDefinition)
            {
                var ingredients = GetVanillaOutput(plugin, itemDefinition);

                OverrideOutputByShortName[itemDefinition.shortname] = ingredients;
                OverrideOutputByItemId[itemDefinition.itemid] = ingredients;
            }

            private IngredientInfo[] GetVanillaOutput(RecycleManager plugin, ItemDefinition itemDefinition)
            {
                if (itemDefinition.Blueprint?.ingredients == null)
                    return new IngredientInfo[0];

                var ingredientList = new List<IngredientInfo>();

                if (itemDefinition.Blueprint.scrapFromRecycle > 0)
                {
                    var ingredientInfo = new IngredientInfo
                    {
                        ShortName = "scrap",
                        Amount = itemDefinition.Blueprint.scrapFromRecycle,
                    };
                    ingredientInfo.Init(plugin);
                    ingredientList.Add(ingredientInfo);
                }

                foreach (var blueprintIngredient in itemDefinition.Blueprint.ingredients)
                {
                    if (blueprintIngredient.itemid == ScrapItemId)
                        continue;

                    var ingredientInfo = new IngredientInfo
                    {
                        ShortName = blueprintIngredient.itemDef.shortname,
                        Amount = blueprintIngredient.amount / itemDefinition.Blueprint.amountToCreate,
                    };

                    ingredientInfo.Init(plugin);
                    ingredientList.Add(ingredientInfo);
                }

                return ingredientList.ToArray();
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RestrictedInputItems
        {
            [JsonProperty("Item short names")]
            public CaseInsensitiveHashSet DisallowedInputShortNames = new CaseInsensitiveHashSet();

            [JsonProperty("Item skin IDs")]
            public HashSet<ulong> DisallowedInputSkinIds = new HashSet<ulong>();

            [JsonProperty("Item display names (custom items)")]
            public CaseInsensitiveHashSet DisallowedInputDisplayNames = new CaseInsensitiveHashSet();

            [JsonIgnore]
            private HashSet<int> DisallowedInputItemIds = new HashSet<int>();

            public void Init(RecycleManager plugin)
            {
                foreach (var shortName in DisallowedInputShortNames)
                {
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    DisallowedInputItemIds.Add(itemDefinition.itemid);
                }
            }

            public bool IsDisallowed(Item item)
            {
                if (!string.IsNullOrEmpty(item.name) && DisallowedInputDisplayNames.Contains(item.name, StringComparer.OrdinalIgnoreCase))
                    return true;

                if (item.skin != 0 && DisallowedInputSkinIds.Contains(item.skin))
                    return true;

                if (DisallowedInputItemIds.Contains(item.info.itemid))
                    return true;

                return false;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Custom recycle speed")]
            public RecycleSpeed RecycleSpeed = new RecycleSpeed();

            [JsonProperty("Restricted input items")]
            public RestrictedInputItems RestrictedInputItems = new RestrictedInputItems();

            [JsonProperty("Max items in stack per recycle (% of max stack size)")]
            public MaxItemsPerRecycle MaxItemsPerRecycle = new MaxItemsPerRecycle();

            [JsonProperty("Output multipliers")]
            public OutputMultiplierSettings OutputMultipliers = new OutputMultiplierSettings();

            [JsonProperty("Override output (before efficiency factor)")]
            public OverrideOutput OverrideOutput = new OverrideOutput();

            public void Init(RecycleManager plugin)
            {
                RecycleSpeed.Init(plugin);
                RestrictedInputItems.Init(plugin);
                MaxItemsPerRecycle.Init(plugin);
                OutputMultipliers.Init(plugin);
                OverrideOutput.Init(plugin);
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            [JsonIgnore]
            public bool UsingDefaults = false;

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigSection(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigSection(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigSection(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
                _config.UsingDefaults = true;
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        #region Localization

        private class LangEntry
        {
            public static List<LangEntry> AllLangEntries = new List<LangEntry>();

            public static readonly LangEntry ErrorNoPermission = new LangEntry("Error.NoPermission", "You don't have permission to do that.");
            public static readonly LangEntry ErrorConfig = new LangEntry("Error.Config", "Error: The config did not load correctly. Please fix the config and reload the plugin before running this command.");
            public static readonly LangEntry ErrorInvalidItem = new LangEntry("Error.InvalidItem", "Error: Invalid item: <color=#fe0>{0}</color>");

            public static readonly LangEntry ItemSyntax = new LangEntry("Item.Syntax", "Syntax: <color=#fe0>{0} <item id or short name></color>");

            public static readonly LangEntry AddExists = new LangEntry("Add.Exists", "Error: Item <color=#fe0>{0}</color> is already in the config. To reset that item to vanilla output, use <color=#fe0>recyclemanager.reset {0}</color>.");
            public static readonly LangEntry AddSuccess = new LangEntry("Add.Success", "Successfully added item <color=#fe0>{0}</color> to the config.");

            public static readonly LangEntry ResetSuccess = new LangEntry("Reset.Success", "Successfully reset item <color=#fe0>{0}</color> in the config.");

            public string Name;
            public string English;

            public LangEntry(string name, string english)
            {
                Name = name;
                English = english;

                AllLangEntries.Add(this);
            }
        }


        private string GetMessage(string playerId, LangEntry langEntry) =>
            lang.GetMessage(langEntry.Name, this, playerId);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1) =>
            string.Format(GetMessage(playerId, langEntry), arg1);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2) =>
            string.Format(GetMessage(playerId, langEntry), arg1, arg2);

        private string GetMessage(string playerId, LangEntry langEntry, object arg1, object arg2, string arg3) =>
            string.Format(GetMessage(playerId, langEntry), arg1, arg2, arg3);

        private string GetMessage(string playerId, LangEntry langEntry, params object[] args) =>
            string.Format(GetMessage(playerId, langEntry), args);


        private void ReplyToPlayer(IPlayer player, LangEntry langEntry) =>
            player.Reply(GetMessage(player.Id, langEntry));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, object arg1, object arg2, object arg3) =>
            player.Reply(GetMessage(player.Id, langEntry, arg1, arg2, arg3));

        private void ReplyToPlayer(IPlayer player, LangEntry langEntry, params object[] args) =>
            player.Reply(GetMessage(player.Id, langEntry, args));


        protected override void LoadDefaultMessages()
        {
            var englishLangKeys = new Dictionary<string, string>();

            foreach (var langEntry in LangEntry.AllLangEntries)
            {
                englishLangKeys[langEntry.Name] = langEntry.English;
            }

            lang.RegisterMessages(englishLangKeys, this, "en");
        }

        #endregion

        #region Tests

        #if ENABLE_TESTS

        private class RecycleManagerTests : BaseTestSuite
        {
            private static void Assert(bool value, string message = null)
            {
                if (!value)
                    throw new Exception($"Assertion failed: {message}");
            }

            private static void AssertItemInSlot(ItemContainer container, int slot, out Item item)
            {
                item = container.GetSlot(slot);
                Assert(item != null, $"Expected item in slot {slot}, but found none.");
            }

            private static void AssertItemShortName(Item item, string shortName)
            {
                Assert(item.info.shortname == shortName, $"Expected item '{shortName}', but found '{item.info.shortname}'.");
            }

            private static void AssertItemSkin(Item item, ulong skin)
            {
                Assert(item.skin == skin, $"Expected item {item.info.shortname} to have skin '{skin}', but found '{item.skin}'.");
            }

            private static void AssertItemDisplayName(Item item, string displayName)
            {
                Assert(item.name == displayName, $"Expected item {item.info.shortname} to have display name '{displayName}', but found '{item.name}'.");
            }

            private static void AssertItemAmount(Item item, int expectedAmount)
            {
                Assert(item.amount == expectedAmount, $"Expected item '{item.info.shortname}' to have amount {expectedAmount}, but found {item.amount}.");
            }

            private static void AssertItemInContainer(ItemContainer container, int slot, string shortName, int amount)
            {
                Item item;
                AssertItemInSlot(container, slot, out item);
                AssertItemShortName(item, shortName);
                AssertItemAmount(item, amount);
            }

            private static Item CreateItem(string shortName, int amount = 1, ulong skin = 0)
            {
                var item = ItemManager.CreateByName(shortName, amount, skin);
                Assert(item != null, $"Failed to create item with short name '{shortName}' and amount '{amount}' and skin '{skin}'.");
                return item;
            }

            private static Item AddItemToContainer(ItemContainer container, string shortName, int amount = 1, ulong skin = 0, int slot = -1)
            {
                var item = CreateItem(shortName, amount, skin);
                if (!item.MoveToContainer(container, slot))
                    throw new Exception($"Failed to move item '{shortName}' to container.");

                return item;
            }

            private static void AssertSubscribed(PluginManager pluginManager, Plugin plugin, string hookName, bool subscribed = true)
            {
                var hookSubscribers = GetHookSubscribers(pluginManager, hookName);
                if (hookSubscribers == null)
                    return;

                Assert(hookSubscribers.Contains(plugin) == subscribed);
            }

            private static Action SetMaxStackSize(string shortName, int amount)
            {
                var itemDefinition = ItemManager.FindItemDefinition(shortName);
                Assert(itemDefinition != null, $"Failed to find item definition for short name '{shortName}'.");
                var originalMaxStackSize = itemDefinition.stackable;
                itemDefinition.stackable = amount;
                return () => itemDefinition.stackable = originalMaxStackSize;
            }

            private RecycleManager _plugin;
            private Configuration _originalConfig;
            private Recycler _recycler;
            private BasePlayer _player;

            public RecycleManagerTests(RecycleManager plugin)
            {
                _plugin = plugin;
            }

            protected override void BeforeAll()
            {
                _originalConfig = _plugin._config;

                _recycler = (Recycler)GameManager.server.CreateEntity("assets/bundled/prefabs/static/recycler_static.prefab", new Vector3(0, -1000, 0));
                _recycler.limitNetworking = true;
                _recycler.Spawn();

                _player = (BasePlayer)GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", new Vector3(0, -1000, 0));
                _player.limitNetworking = true;
                _player.modelState.flying = true;
                _player.Spawn();
            }

            protected override void BeforeEach()
            {
                _recycler.inventory.Clear();
                ItemManager.DoRemoves();
                SetupPlayer(123);
            }

            private HashSet<string> GetPluginPermissions()
            {
                var registeredPermissionsField = typeof(Permission).GetField("registeredPermissions", BindingFlags.Instance | BindingFlags.NonPublic);
                var permissionsMap = (Dictionary<Plugin, HashSet<string>>)registeredPermissionsField.GetValue(_plugin.permission);
                HashSet<string> permissionList;
                return permissionsMap.TryGetValue(_plugin, out permissionList)
                    ? permissionList
                    : null;
            }

            private void InitializePlugin(Configuration config)
            {
                _plugin._config = config;
                GetPluginPermissions()?.Clear();

                _recycler.StopRecycling();

                // Reset recycle components since they cache the config.
                _plugin._recycleComponentManager.Unload();

                _plugin.Init();
                _plugin.OnServerInitialized();
            }

            private void SetupPlayer(ulong userId)
            {
                _player.userID = userId;
                _player.UserIDString = userId.ToString();

                foreach (var perm in _plugin.permission.GetUserPermissions(_player.UserIDString).ToArray())
                {
                    _plugin.permission.RevokeUserPermission(_player.UserIDString, perm);
                }
            }

            private static IList<Plugin> GetHookSubscribers(PluginManager pluginManager, string hookName)
            {
                var hookSubscriptionsField = typeof(PluginManager).GetField("hookSubscriptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (hookSubscriptionsField == null)
                    return null;

                var hookSubscriptions = hookSubscriptionsField.GetValue(pluginManager) as IDictionary<string, IList<Plugin>>;
                if (hookSubscriptions == null)
                    return null;

                IList<Plugin> pluginList;
                return hookSubscriptions.TryGetValue(hookName, out pluginList)
                    ? pluginList
                    : null;
            }

            [TestMethod("Given rope is restricted, it should not be allowed in recyclers")]
            public void Test_ItemRestrictions_ItemShortNames()
            {
                InitializePlugin(new Configuration
                {
                    RestrictedInputItems = new RestrictedInputItems
                    {
                        DisallowedInputShortNames = new CaseInsensitiveHashSet
                        {
                            "rope",
                        },
                    },
                });

                // Tarp is not restricted, should be allowed.
                var tarp = CreateItem("tarp");
                if (_recycler.inventory.CanAcceptItem(tarp, 0) != ItemContainer.CanAcceptResult.CanAccept)
                    throw new Exception($"Expected {tarp.info.shortname} to be allowed in recycler, but it was disallowed.");

                var rope = CreateItem("rope");
                if (_recycler.inventory.CanAcceptItem(rope, 0) != ItemContainer.CanAcceptResult.CannotAccept)
                    throw new Exception($"Expected {rope.info.shortname} to be disallowed in recycler, but it was allowed.");
            }

            [TestMethod("Given recycle speed disabled, items should recycle after 5 seconds")]
            public IEnumerator Test_RecycleSpeed_Disabled()
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = false,
                        DefaultRecycleTime = 2,
                    },
                });

                AssertSubscribed(_plugin.plugins.PluginManager, _plugin, nameof(OnRecyclerToggle), subscribed: false);
                var gears = AddItemToContainer(_recycler.inventory, "gears");
                _recycler.StartRecycling();

                yield return new WaitForSeconds(4.9f);
                AssertItemAmount(gears, 1);
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 0);
            }

            [TestMethod("Given default recycle speed 0.1 seconds, items should recycle after 0.1 seconds")]
            public IEnumerator Test_RecycleSpeed_Enabled()
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                });

                var gears = AddItemToContainer(_recycler.inventory, "gears");
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                AssertItemAmount(gears, 1);
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 0);
            }

            [TestMethod("Given default recycle speed 3 seconds, player permission 0.1 multiplier, items should recycle after 0.3 seconds")]
            public IEnumerator Test_RecycleSpeed_Permission()
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 3,
                        PermissionSpeedProfiles = new PermissionSpeedProfile[]
                        {
                            new PermissionSpeedProfile { PermissionSuffix = "fast", TimeMultiplier = 0.1f },
                        },
                    },
                });

                _plugin.permission.GrantUserPermission(_player.UserIDString, "recyclemanager.speed.fast", _plugin);

                var gears = AddItemToContainer(_recycler.inventory, "gears");
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                AssertItemAmount(gears, 1);
                yield return new WaitForSeconds(0.31f);
                AssertItemAmount(gears, 0);
            }

            [TestMethod("Given default recycle time 0.2 seconds, gears 0.5 multiplier, gears should recycle after 0.1 seconds, metalpipes after 0.2 seconds")]
            public IEnumerator Test_RecycleSpeed()
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.2f,
                        TimeMultiplierByShortName = new Dictionary<string, float>
                        {
                            ["gears"] = 0.5f,
                        },
                    },
                });

                var gears1 = AddItemToContainer(_recycler.inventory, "gears", slot: 0);
                var pipe1 = AddItemToContainer(_recycler.inventory, "metalpipe", slot: 1);
                var gears2 = AddItemToContainer(_recycler.inventory, "gears", slot: 2);
                var pipe2 = AddItemToContainer(_recycler.inventory, "metalpipe", slot: 3);

                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears1, 0);
                AssertItemAmount(pipe1, 1);
                AssertItemAmount(gears2, 1);
                AssertItemAmount(pipe2, 1);

                yield return new WaitForSeconds(0.2f);
                AssertItemAmount(pipe1, 0);
                AssertItemAmount(gears2, 1);
                AssertItemAmount(pipe2, 1);

                yield return new WaitForSeconds(0.1f);
                AssertItemAmount(gears2, 0);
                AssertItemAmount(pipe2, 1);

                yield return new WaitForSeconds(0.2f);
                AssertItemAmount(pipe2, 0);
            }

            [TestMethod("Given gears max stack size 100, stack of 3 gears, with no override configured, should output 30 scrap and 39 metal fragments")]
            public IEnumerator Test_RecycleStacks_NoOverride(List<Action> cleanupActions)
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 3);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 30);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 39);
            }

            [TestMethod("Given gears max stack size 100, stack of 75 gears, default stack percent 50%, should output 500 scrap & 975 metal fragments, then should output 750 scrap & 975 metal fragments")]
            public IEnumerator Test_RecycleStacks_DefaultPercent(List<Action> cleanupActions)
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                    MaxItemsPerRecycle = new MaxItemsPerRecycle
                    {
                        DefaultPercent = 50f,
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 75);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 25);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 500);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 650);

                yield return new WaitForSeconds(0.1f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 750);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 975);
            }

            [TestMethod("Given gears max stack size 100, stack of 75 gears, gears stack percent 50%, should output 500 scrap & 975 metal fragments, then should output 750 scrap & 975 metal fragments")]
            public IEnumerator Test_RecycleStacks_ShortNamePercent(List<Action> cleanupActions)
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                    MaxItemsPerRecycle = new MaxItemsPerRecycle
                    {
                        DefaultPercent = 10f,
                        PercentByShortName = new Dictionary<string, float>
                        {
                            ["gears"] = 50,
                        }
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 75);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 25);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 500);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 650);

                yield return new WaitForSeconds(0.1f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 750);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 975);
            }

            [TestMethod("Given 2.0 default multiplier, 3.0 scrap output multiplier, stack of 3 gears, should output 90 scrap & 78 metal fragments")]
            public IEnumerator Test_OutputMultipliers(List<Action> cleanupActions)
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                    OutputMultipliers = new OutputMultiplierSettings
                    {
                        DefaultMultiplier = 2,
                        MultiplierByOutputShortName = new Dictionary<string, float>
                        {
                            ["scrap"] = 3f
                        },
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 3);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 90);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 78);
            }

            [TestMethod("Given override for gears, stack of 3 gears, should output custom items")]
            public IEnumerator Test_OverrideOutput_ByItemShortName(List<Action> cleanupActions)
            {
                InitializePlugin(new Configuration
                {
                    RecycleSpeed = new RecycleSpeed
                    {
                        Enabled = true,
                        DefaultRecycleTime = 0.1f,
                    },
                    OutputMultipliers = new OutputMultiplierSettings
                    {
                        // Output multipliers should have no effect.
                        DefaultMultiplier = 2,
                    },
                    OverrideOutput = new OverrideOutput
                    {
                        OverrideOutputByShortName = new CaseInsensitiveDictionary<IngredientInfo[]>
                        {
                            ["gears"] = new IngredientInfo[]
                            {
                                new IngredientInfo
                                {
                                    ShortName = "wood",
                                    Amount = 100,
                                    SkinId = 123456,
                                    DisplayName = "Vood",
                                },
                            },
                        },
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 3);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 0);

                Item outputItem;
                AssertItemInSlot(_recycler.inventory, 6, out outputItem);
                AssertItemShortName(outputItem, "wood");
                AssertItemSkin(outputItem, 123456);
                AssertItemDisplayName(outputItem, "Vood");
                AssertItemAmount(outputItem, 150);
            }

            protected override void AfterAll(bool interrupted)
            {
                if (_recycler != null && !_recycler.IsDestroyed)
                {
                    _recycler.Kill();
                }

                if (_player != null && !_player.IsDestroyed)
                {
                    _player.Die();
                }

                if (!interrupted)
                {
                    InitializePlugin(_originalConfig);
                }
            }
        }

        #endif

        #endregion

        #region Test Runner

        #if ENABLE_TESTS

        [AttributeUsage(AttributeTargets.Method)]
        public class TestMethodAttribute : Attribute
        {
            public readonly string Name;
            public bool Skip;
            public bool Only;

            public TestMethodAttribute(string name = null)
            {
                Name = name;
            }
        }

        public abstract class BaseTestSuite
        {
            private enum TestStatus
            {
                Skipped,
                Running,
                Success,
                Error,
            }

            private class TestInfo
            {
                public string Name;
                public bool Async;
                public MethodInfo MethodInfo;
                public TestMethodAttribute Attribute;
                public TestStatus Status = TestStatus.Skipped;
                public bool ShouldSkip;
                public Exception Exception;
            }

            public bool IsRunning { get; private set; }

            private List<TestInfo> _testInfoList = new List<TestInfo>();
            private Coroutine _coroutine;

            protected virtual void BeforeAll() {}
            protected virtual void BeforeEach() {}
            protected virtual void AfterEach() {}
            protected virtual void AfterAll(bool interrupted) {}

            public void Run()
            {
                if (IsRunning)
                    return;

                if (!TryRunBeforeAll())
                    return;

                var hasOnly = false;

                foreach (var methodInfo in GetType().GetMethods())
                {
                    var testMethodAttribute = methodInfo.GetCustomAttributes(typeof(TestMethodAttribute), true).FirstOrDefault() as TestMethodAttribute;
                    if (testMethodAttribute == null)
                        continue;

                    var isAsync = methodInfo.ReturnType == typeof(IEnumerator);
                    if (!isAsync && methodInfo.ReturnType != typeof(void))
                    {
                        LogError($"Disallowed return type '{methodInfo.ReturnType.FullName}' for test '{testMethodAttribute.Name}'");
                        continue;
                    }

                    hasOnly |= testMethodAttribute.Only;

                    _testInfoList.Add(new TestInfo
                    {
                        Name = testMethodAttribute.Name,
                        Async = isAsync,
                        MethodInfo = methodInfo,
                        Attribute = testMethodAttribute,
                        ShouldSkip = testMethodAttribute.Skip,
                    });
                }

                if (hasOnly)
                {
                    foreach (var testInfo in _testInfoList)
                    {
                        testInfo.ShouldSkip = !testInfo.Attribute.Only;
                    }
                }

                var syncTestList = _testInfoList.Where(testInfo => !testInfo.Async).ToArray();
                var asyncTestList = _testInfoList.Where(testInfo => testInfo.Async).ToArray();

                var canKeepRunning = RunSyncTests(syncTestList);
                if (!canKeepRunning && asyncTestList.Length == 0)
                {
                    RunAfterAll();
                    return;
                }

                _coroutine = ServerMgr.Instance.StartCoroutine(RunAsyncTests(asyncTestList));
            }

            public void Interrupt()
            {
                if (!IsRunning)
                    return;

                if (_coroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(_coroutine);
                    LogWarning("Interrupted tests.");
                }

                RunAfterAll(true);
            }

            private bool TryRunBeforeAll()
            {
                IsRunning = true;

                try
                {
                    BeforeAll();
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to run BeforeAll() for test suite {GetType().FullName}:\n{ex}");
                    IsRunning = false;
                    return false;
                }
            }

            private bool TryRunBeforeEach()
            {
                try
                {
                    BeforeEach();
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to run BeforeEach() for test suite {GetType().FullName}:\n{ex}");
                    return false;
                }
            }

            private bool TryRunAfterEach()
            {
                try
                {
                    AfterEach();
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"Failed to run AfterEach() for test suite {GetType().FullName}:\n{ex}");
                    return false;
                }
            }

            private void RunAfterAll(bool interrupted = false)
            {
                try
                {
                    AfterAll(interrupted);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to run AfterAll() method for test suite {GetType().FullName}:\n{ex}");
                }

                IsRunning = false;

                if (!interrupted)
                {
                    PrintResults();
                }
            }

            private bool HasParameter<T>(MethodInfo methodInfo, int parameterIndex = 0)
            {
                return methodInfo.GetParameters().ElementAtOrDefault(parameterIndex)?.ParameterType == typeof(T);
            }

            private object[] CreateTestArguments(MethodInfo methodInfo, out List<Action> cleanupActions)
            {
                if (HasParameter<List<Action>>(methodInfo))
                {
                    cleanupActions = new List<Action>();
                    return new object[] { cleanupActions };
                }

                cleanupActions = null;
                return null;
            }

            private void RunCleanupActions(List<Action> cleanupActions)
            {
                if (cleanupActions == null)
                    return;

                foreach (var action in cleanupActions)
                {
                    try
                    {
                        action.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to run cleanup action:\n{ex}");
                    }
                }
            }

            private bool RunSyncTests(IEnumerable<TestInfo> syncTestList)
            {
                foreach (var testInfo in syncTestList)
                {
                    if (testInfo.ShouldSkip)
                        continue;

                    if (!TryRunBeforeEach())
                        return false;

                    List<Action> cleanupActions;
                    var args = CreateTestArguments(testInfo.MethodInfo, out cleanupActions);

                    try
                    {
                        testInfo.MethodInfo.Invoke(this, args);
                    }
                    catch (Exception ex)
                    {
                        testInfo.Exception = ex;
                        testInfo.Status = TestStatus.Error;
                    }

                    testInfo.Status = TestStatus.Success;

                    RunCleanupActions(cleanupActions);

                    if (!TryRunAfterEach())
                        return false;
                }

                return true;
            }

            private IEnumerator RunAsyncTests(IEnumerable<TestInfo> asyncTestList)
            {
                foreach (var testInfo in asyncTestList)
                {
                    if (testInfo.ShouldSkip)
                        continue;

                    if (!TryRunBeforeEach())
                        break;

                    List<Action> cleanupActions;
                    var args = CreateTestArguments(testInfo.MethodInfo, out cleanupActions);

                    IEnumerator enumerator;

                    try
                    {
                        enumerator = (IEnumerator)testInfo.MethodInfo.Invoke(this, args);
                    }
                    catch (Exception ex)
                    {
                        RunCleanupActions(cleanupActions);
                        testInfo.Exception = ex;
                        testInfo.Status = TestStatus.Error;
                        continue;
                    }

                    testInfo.Status = TestStatus.Running;

                    while (testInfo.Status == TestStatus.Running)
                    {
                        // This assumes Current is null or an instance of YieldInstruction.
                        yield return enumerator.Current;

                        try
                        {
                            if (!enumerator.MoveNext())
                            {
                                testInfo.Status = TestStatus.Success;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            RunCleanupActions(cleanupActions);
                            testInfo.Exception = ex;
                            testInfo.Status = TestStatus.Error;
                            break;
                        }
                    }

                    RunCleanupActions(cleanupActions);

                    if (!TryRunAfterEach())
                        break;
                }

                RunAfterAll();
            }

            private void PrintResults()
            {
                foreach (var testInfo in _testInfoList)
                {
                    switch (testInfo.Status)
                    {
                        case TestStatus.Success:
                            LogWarning($"[PASSED]  {testInfo.Name}");
                            break;

                        case TestStatus.Skipped:
                            LogWarning($"[SKIPPED] {testInfo.Name}");
                            break;

                        case TestStatus.Error:
                            LogError($"[FAILED]  {testInfo.Name}:\n{testInfo.Exception}");
                            break;

                        case TestStatus.Running:
                            LogError($"[RUNNING] {testInfo.Name}");
                            break;

                        default:
                            LogError($"[{testInfo.Status}] {testInfo.Name}");
                            break;
                    }
                }
            }
        }

        #endif

        #endregion
    }
}