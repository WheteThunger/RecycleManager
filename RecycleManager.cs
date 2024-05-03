// #define ENABLE_TESTS

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    [Info("Recycle Manager", "WhiteThunder", "2.1.0")]
    [Description("Allows customizing recycler speed, input, and output")]
    internal class RecycleManager : CovalencePlugin
    {
        #region Fields

        private Configuration _config;

        private const string PermissionAdmin = "recyclemanager.admin";

        private const int ScrapItemId = -932201673;
        private const float ClassicRecycleEfficiency = 0.5f;
        private const float VanillaMaxItemsInStackFraction = 0.1f;

        private readonly object True = true;
        private readonly object False = false;

        private const int NumInputSlots = 6;
        private const int NumOutputSlots = 6;

        private readonly RecyclerComponentManager _recyclerComponentManager = new();
        private readonly RecycleEditManager _recycleEditManager;
        private readonly float[] _recycleTime = new float[1];
        private readonly float[] _recycleEfficiency = new float[1];

        private static readonly FieldInfo ScrapRemainderField = typeof(Recycler).GetField("scrapRemainder", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private bool IsEditUIEnabled => !_config.UsingDefaults && _config.EditUISettings.Enabled;

        #if ENABLE_TESTS
        private readonly RecycleManagerTests _testRunner;
        #endif

        public RecycleManager()
        {
            #if ENABLE_TESTS
            _testRunner = new RecycleManagerTests(this);
            #endif

            _recycleEditManager = new RecycleEditManager(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            _config.Init(this);
            _recyclerComponentManager.Init(this);

            permission.RegisterPermission(PermissionAdmin, this);

            if (!_config.Speed.Enabled && !_config.Efficiency.Enabled)
            {
                Unsubscribe(nameof(OnRecyclerToggle));
            }

            if (!IsEditUIEnabled)
            {
                Unsubscribe(nameof(OnLootEntity));
            }
        }

        private void OnServerInitialized()
        {
            #if ENABLE_TESTS
            _testRunner.Run();
            #endif

            if (IsEditUIEnabled)
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    var container = player.inventory.loot.containers.FirstOrDefault();
                    if (container == null)
                        continue;

                    var recycler = container.entityOwner as Recycler;
                    if ((object)recycler == null)
                        continue;

                    OnLootEntity(player, recycler);
                }
            }
        }

        private void Unload()
        {
            #if ENABLE_TESTS
            _testRunner.Interrupt();
            #endif

            _recyclerComponentManager.Unload();
            _recycleEditManager.Unload();
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
            _recyclerComponentManager.HandleRecyclerToggle(recycler, player);
        }

        private object OnItemRecycle(Item item, Recycler recycler)
        {
            if (RecycleItemWasBlocked(item, recycler))
                return null;

            var maxItemsInStackFraction = _config.MaxItemsPerRecycle.GetPercent(item) / 100f;
            var recycleAmount = DetermineConsumptionAmount(recycler, item, maxItemsInStackFraction);
            if (recycleAmount <= 0)
                return False;

            var customIngredientList = _config.OverrideOutput.GetBestOverride(item);
            if (customIngredientList != null)
            {
                item.UseItem(recycleAmount);

                // Overrides already account for standard recycle efficiency, so only calculate based on item condition.
                if (PopulateOutputWithOverride(recycler, customIngredientList, recycleAmount, DetermineItemRecycleEfficiency(item, 1f)))
                {
                    recycler.StopRecycling();
                }

                return False;
            }

            // If the item is not vanilla recyclable, and this plugin doesn't have an override,
            // that probably means another plugin is going to handle recycling it.
            if (!IsVanillaRecyclable(item))
                return null;

            // Issue: Last known player will always be null if speed and efficiency are both disabled, since toggle hook
            // is not subscribed to improve performance.
            var lastKnownPlayer = _recyclerComponentManager.EnsureRecyclerComponent(recycler).Player;
            var recycleEfficiency = DetermineItemRecycleEfficiency(item, GetRecyclerEfficiency(recycler, lastKnownPlayer, out var vanillaEfficiency));

            if (recycleEfficiency == vanillaEfficiency
                && maxItemsInStackFraction == VanillaMaxItemsInStackFraction
                && !IsOutputCustomized(item.info.Blueprint))
                return null;

            item.UseItem(recycleAmount);

            var outputIsFull = PopulateOutputVanilla(_config, recycler, item, recycleAmount, recycleEfficiency);
            if (outputIsFull || !recycler.HasRecyclable())
            {
                recycler.StopRecycling();
            }

            return False;
        }

        private void OnLootEntity(BasePlayer player, Recycler recycler)
        {
            if (!recycler.onlyOneUser)
                return;

            if (permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                var player2 = player;
                var recycler2 = recycler;
                NextTick(() =>
                {
                    var lootingContainer = player2.inventory.loot.containers.FirstOrDefault();
                    if (lootingContainer == null || lootingContainer != recycler2.inventory)
                        return;

                    _recycleEditManager.HandlePlayerStartedLooting(player2, recycler2);
                });
            }
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

            public static void OnRecycleManagerEfficiency(Recycler recycler, BasePlayer player, float[] recyclerEfficiency)
            {
                Interface.CallHook("OnRecycleManagerEfficiency", recycler, player, recyclerEfficiency);
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

            if (!VerifyValidItemIdOrShortName(player, args.ElementAtOrDefault(0), out var itemDefinition, cmd))
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

            if (!VerifyValidItemIdOrShortName(player, args.ElementAtOrDefault(0), out var itemDefinition, cmd))
                return;

            _config.OverrideOutput.ResetOverride(this, itemDefinition);
            SaveConfig();
            ReplyToPlayer(player, LangEntry.ResetSuccess, itemDefinition.shortname);
        }

        [Command("recyclemanager.ui")]
        private void CommandEdit(IPlayer player, string cmd, string[] args)
        {
            if (player.IsServer || !player.HasPermission(PermissionAdmin))
                return;

            var basePlayer = player.Object as BasePlayer;
            _recycleEditManager.GetController(basePlayer)?.HandleUICommand(basePlayer, args);
        }

        #endregion

        #region Utilities

        private readonly struct ReflectionAdapter<T>
        {
            private readonly FieldInfo _fieldInfo;
            private readonly object _object;

            public ReflectionAdapter(object obj, FieldInfo fieldInfo)
            {
                _object = obj;
                _fieldInfo = fieldInfo;
            }

            public T Value
            {
                get => _fieldInfo == null ? default : (T)_fieldInfo.GetValue(_object);
                set => _fieldInfo?.SetValue(_object, value);
            }

            public static implicit operator T(ReflectionAdapter<T> adapter)
            {
                return adapter.Value;
            }
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

            if (int.TryParse(itemArg, out var itemId))
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

        private float GetRecyclerEfficiency(Recycler recycler, BasePlayer player, out float vanillaEfficiency)
        {
            vanillaEfficiency = DetermineVanillaRecycleEfficiency(recycler);

            _recycleEfficiency[0] = _config.Efficiency.Enabled
                ? _config.Efficiency.GetRecyclerEfficiency(recycler)
                : vanillaEfficiency;

            ExposedHooks.OnRecycleManagerEfficiency(recycler, player, _recycleEfficiency);
            return Mathf.Max(0, _recycleEfficiency[0]);
        }

        private bool TryDetermineRecycleTime(Recycler recycler, BasePlayer player, out float recycleTime)
        {
            _recycleTime[0] = _config.Speed.DefaultRecycleTime
                              * _config.Speed.GetTimeMultiplierForPlayer(player);

            if (_config.Speed.SafeZoneTimeMultiplier != 1 && player.InSafeZone())
            {
                _recycleTime[0] *= _config.Speed.SafeZoneTimeMultiplier;
            }

            if (ExposedHooks.OnRecycleManagerSpeed(recycler, player, _recycleTime) is false)
            {
                recycleTime = 0;
                return false;
            }

            recycleTime = Math.Max(0, _recycleTime[0]);
            return true;
        }

        private object CallCanBeRecycled(Item item, Recycler recycler)
        {
            Unsubscribe(nameof(CanBeRecycled));
            var hookResult = Interface.CallHook(nameof(CanBeRecycled), item, recycler);
            Subscribe(nameof(CanBeRecycled));
            return hookResult;
        }

        private bool IsOutputCustomized(ItemBlueprint blueprint)
        {
            if (blueprint.scrapFromRecycle > 0 && _config.OutputMultipliers.GetOutputMultiplier(ScrapItemId) != 1)
                return true;

            foreach (var ingredient in blueprint.ingredients)
            {
                // Skip scrap since it's handled separately.
                if (ingredient.itemDef.itemid == ScrapItemId)
                    continue;

                if (_config.OutputMultipliers.GetOutputMultiplier(ingredient.itemid) != 1)
                    return true;
            }

            return false;
        }

        #endregion

        #region Helper Methods - Static

        public static void LogError(string message) => Interface.Oxide.LogError($"[Recycle Manager] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Recycle Manager] {message}");

        private static void Swap<T>(ref T a, ref T b)
        {
            (a, b) = (b, a);
        }

        private static bool IsVanillaRecyclable(Item item)
        {
            return item.info.Blueprint != null;
        }

        private static bool RecyclableWasBlocked(Item item, Recycler recycler)
        {
            return ExposedHooks.OnRecycleManagerItemRecyclable(item, recycler) is false;
        }

        private static bool RecycleItemWasBlocked(Item item, Recycler recycler)
        {
            return ExposedHooks.OnRecycleManagerRecycle(item, recycler) is false;
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

        private static int CalculateOutputAmountVanillaRandom(int recycleAmount, float adjustedIngredientChance)
        {
            var outputAmount = 0;

            // Roll a random number for every item to consume.
            for (var i = 0; i < recycleAmount; i++)
            {
                if (UnityEngine.Random.Range(0f, 1f) <= adjustedIngredientChance)
                {
                    outputAmount++;
                }
            }

            return outputAmount;
        }

        private static int CalculateOutputAmountFast(int recycleAmount, float ingredientAmount)
        {
            // To save on performance, don't generate hundreds/thousands/millions of random numbers.
            var outputAmountDecimal = ingredientAmount * recycleAmount;

            var outputAmountInt = (int)outputAmountDecimal;

            // Roll a random number to see if the the remainder should be given.
            var remainderFractionalOutputAmount = outputAmountDecimal - outputAmountInt;
            if (remainderFractionalOutputAmount > 0 && UnityEngine.Random.Range(0f, 1f) <= remainderFractionalOutputAmount)
            {
                outputAmountInt++;
            }

            return outputAmountInt;
        }

        private static int CalculateOutputAmountRandom(int recycleAmount, float ingredientAmount, float recycleEfficiency, float outputMultiplier)
        {
            var adjustedIngredientAmount = ingredientAmount * outputMultiplier;
            if (adjustedIngredientAmount <= 1 && recycleAmount <= 100)
                return CalculateOutputAmountVanillaRandom(recycleAmount, adjustedIngredientAmount * recycleEfficiency);

            return CalculateOutputAmountFast(recycleAmount, adjustedIngredientAmount * recycleEfficiency);
        }

        private static int CalculateOutputAmountNoRandom(int recycleAmount, float ingredientAmount, float recycleEfficiency, float outputMultiplier)
        {
            var adjustedIngredientAmount = Mathf.CeilToInt(ingredientAmount * recycleEfficiency) * outputMultiplier;
            return CalculateOutputAmountFast(recycleAmount, adjustedIngredientAmount);
        }

        private static int CalculateOutputAmount(int recycleAmount, float ingredientAmount, float recycleEfficiency, float outputMultiplier = 1)
        {
            if (ingredientAmount <= 1)
                return CalculateOutputAmountRandom(recycleAmount, ingredientAmount, recycleEfficiency, outputMultiplier);

            return CalculateOutputAmountNoRandom(recycleAmount, ingredientAmount, recycleEfficiency, outputMultiplier);
        }

        private static bool AddItemToRecyclerOutput(Recycler recycler, ItemDefinition itemDefinition, int ingredientAmount, ulong skinId = 0, string displayName = null)
        {
            var outputIsFull = false;
            var numStacks = Mathf.CeilToInt(ingredientAmount / (float)itemDefinition.stackable);

            for (var i = 0; i < numStacks; i++)
            {
                var amountForStack = Math.Min(ingredientAmount, itemDefinition.stackable);
                var outputItem = CreateItem(itemDefinition, amountForStack, skinId, displayName);

                if (!recycler.MoveItemToOutput(outputItem))
                {
                    outputIsFull = true;
                }

                ingredientAmount -= amountForStack;

                if (ingredientAmount <= 0)
                    break;
            }

            return outputIsFull;
        }

        private static float DetermineVanillaRecycleEfficiency(Recycler recycler)
        {
            return recycler.IsSafezoneRecycler()
                ? recycler.safezoneRecycleEfficiency
                : recycler.radtownRecycleEfficiency;
        }

        private static IngredientInfo[] GetVanillaOutput(ItemDefinition itemDefinition)
        {
            if (itemDefinition.Blueprint?.ingredients == null)
                return Array.Empty<IngredientInfo>();

            var ingredientList = new List<IngredientInfo>();

            if (itemDefinition.Blueprint.scrapFromRecycle > 0)
            {
                var ingredientInfo = new IngredientInfo
                {
                    ShortName = "scrap",
                    Amount = itemDefinition.Blueprint.scrapFromRecycle,
                };
                ingredientInfo.Init();
                ingredientList.Add(ingredientInfo);
            }

            foreach (var blueprintIngredient in itemDefinition.Blueprint.ingredients)
            {
                if (blueprintIngredient.itemid == ScrapItemId)
                    continue;

                var amount = blueprintIngredient.amount / itemDefinition.Blueprint.amountToCreate * ClassicRecycleEfficiency;
                if (amount > 1)
                {
                    amount = Mathf.CeilToInt(amount);
                }

                var ingredientInfo = new IngredientInfo
                {
                    ShortName = blueprintIngredient.itemDef.shortname,
                    Amount = amount,
                };

                ingredientInfo.Init();
                ingredientList.Add(ingredientInfo);
            }

            return ingredientList.ToArray();
        }

        private static float DetermineItemRecycleEfficiency(Item item, float recyclerEfficiency)
        {
            return item.hasCondition
                ? Mathf.Clamp01(recyclerEfficiency * Mathf.Clamp(item.conditionNormalized * item.maxConditionNormalized, 0.1f, 1f))
                : recyclerEfficiency;
        }

        private static int DetermineConsumptionAmount(Recycler recycler, Item item, float maxItemsInStackFraction)
        {
            var recycleAmount = 1;

            if (item.amount > 1)
            {
                recycleAmount = Mathf.CeilToInt(Mathf.Min(item.amount, item.MaxStackable() * maxItemsInStackFraction));

                // In case the configured multiplier is 0, ensure at least 1 item is recycled.
                recycleAmount = Math.Max(recycleAmount, 1);
            }

            // Call standard Oxide hook for compatibility.
            if (Interface.CallHook("OnItemRecycleAmount", item, recycleAmount, recycler) is int overrideAmount)
                return overrideAmount;

            return recycleAmount;
        }

        private static bool PopulateOutputWithOverride(Recycler recycler, IngredientInfo[] customIngredientList, int recycleAmount, float recycleEfficiency = 1, bool forEditor = false)
        {
            var outputIsFull = false;

            foreach (var ingredientInfo in customIngredientList)
            {
                if (ingredientInfo.ItemDefinition == null)
                    continue;

                var ingredientAmount = ingredientInfo.Amount;
                if (ingredientAmount <= 0)
                    continue;

                var outputAmount = CalculateOutputAmount(recycleAmount, ingredientAmount, recycleEfficiency);
                if (outputAmount <= 0)
                    continue;

                if (forEditor && outputAmount < 1)
                {
                    outputAmount = 1;
                }

                if (AddItemToRecyclerOutput(recycler, ingredientInfo.ItemDefinition, outputAmount, ingredientInfo.SkinId, ingredientInfo.DisplayName))
                {
                    outputIsFull = true;
                }
            }

            return outputIsFull;
        }

        private static bool PopulateOutputVanilla(Configuration config, Recycler recycler, Item item, int recycleAmount, float recycleEfficiency)
        {
            var outputIsFull = false;

            if (item.info.Blueprint.scrapFromRecycle > 0)
            {
                var scrapOutputMultiplier = config.OutputMultipliers.GetOutputMultiplier(ScrapItemId);
                var scrapAmountDecimal = item.info.Blueprint.scrapFromRecycle * (float)recycleAmount * scrapOutputMultiplier;

                if (item.MaxStackable() == 1 && item.hasCondition)
                {
                    scrapAmountDecimal *= item.conditionNormalized;
                }

                scrapAmountDecimal *= recycleEfficiency / ClassicRecycleEfficiency;
                var scrapAmountInt = Mathf.FloorToInt(scrapAmountDecimal);
                var scrapRemainderForThisCycle = scrapAmountDecimal - scrapAmountInt;

                if (scrapRemainderForThisCycle > 0)
                {
                    var scrapRemainderAdapter = new ReflectionAdapter<float>(recycler, ScrapRemainderField);
                    var recyclerScrapRemainder = scrapRemainderAdapter.Value;
                    recyclerScrapRemainder += scrapRemainderForThisCycle;

                    var scrapRemainderToOutput = Mathf.FloorToInt(recyclerScrapRemainder);
                    if (scrapRemainderToOutput > 0)
                    {
                        recyclerScrapRemainder -= scrapRemainderToOutput;
                        scrapAmountInt += scrapRemainderToOutput;
                    }

                    scrapRemainderAdapter.Value = recyclerScrapRemainder;
                }

                if (scrapAmountInt >= 1)
                {
                    var scrapItem = ItemManager.CreateByItemID(ScrapItemId, scrapAmountInt);
                    recycler.MoveItemToOutput(scrapItem);
                }
            }

            foreach (var ingredient in item.info.Blueprint.ingredients)
            {
                // Skip scrap since it's handled separately.
                if (ingredient.itemDef.itemid == ScrapItemId)
                    continue;

                var ingredientAmount = ingredient.amount / item.info.Blueprint.amountToCreate;
                if (ingredientAmount <= 0)
                    continue;

                var outputAmount = CalculateOutputAmount(
                    recycleAmount,
                    ingredientAmount,
                    recycleEfficiency,
                    config.OutputMultipliers.GetOutputMultiplier(ingredient.itemid)
                );

                if (outputAmount <= 0)
                    continue;

                if (AddItemToRecyclerOutput(recycler, ingredient.itemDef, outputAmount))
                {
                    outputIsFull = true;
                }
            }

            return outputIsFull;
        }

        #endregion

        #region UI

        private enum IdentificationType
        {
            Item,
            Skin,
            DisplayName,
        }

        private enum OutputType
        {
            NotRecyclable,
            Default,
            Custom,
        }

        private enum UICommand
        {
            Edit,
            Reset,
            Save,
            Cancel,
            InputPercentage,
            ChangeIdentificationType,
            ChangeOutputType,
        }

        [Flags]
        private enum LayoutOptions
        {
            AnchorBottom = 1 << 0,
            AnchorRight = 1 << 1,
            Vertical = 1 << 2,
        }

        private class LayoutProvider
        {
            public const string AnchorBottomLeft = "0 0";
            public const string AnchorBottomRight = "1 0";
            public const string AnchorTopLeft = "0 1";
            public const string AnchorTopRight = "1 1";

            public static LayoutProvider Once(float width = 0, float height = 0)
            {
                return _reusable.WithOffset().WithDimensions(width, height).WithOptions(0).WithSpacing(0);
            }

            private static LayoutProvider _reusable = new(0, 0);

            private LayoutOptions _options;
            private string _anchor;
            private float _x, _y;
            private float _xSpacing, _ySpacing;
            private float _width, _height;

            private bool _isVertical => (_options & LayoutOptions.Vertical) != 0;
            private bool _isLeftToRight => (_options & LayoutOptions.AnchorRight) == 0;
            private bool _isTopToBottom => (_options & LayoutOptions.AnchorBottom) == 0;
            private int _xSign => _isLeftToRight ? 1 : -1;
            private int _ySign => _isTopToBottom ? -1 : 1;
            private float XMin, XMax, YMin, YMax;

            public string AnchorMin => _anchor;
            public string AnchorMax => _anchor;
            public string OffsetMin => $"{XMin.ToString()} {YMin.ToString()}";
            public string OffsetMax => $"{XMax.ToString()} {YMax.ToString()}";

            public LayoutProvider(float width, float height)
            {
                WithDimensions(width, height);
                WithOptions(0);
            }

            public LayoutProvider WithDimensions(float width, float height)
            {
                _width = width;
                _height = height;
                return this;
            }

            public LayoutProvider WithOptions(LayoutOptions options)
            {
                _options = options;
                _anchor = DetermineAnchor();
                return this;
            }

            public LayoutProvider WithSpacing(float x, float y = float.MaxValue)
            {
                _xSpacing = x;
                _ySpacing = y != float.MaxValue ? y : x;
                return this;
            }

            public LayoutProvider WithOffset(float x = 0, float y = 0)
            {
                _x = x * _xSign;
                _y = y * _ySign;
                return this;
            }

            public LayoutProvider Next()
            {
                XMin = _x + _xSpacing * _xSign;
                YMin = _y + _ySpacing * _ySign;
                XMax = XMin + _width * _xSign;
                YMax = YMin + _height * _ySign;

                if (_isVertical)
                {
                    _y = YMax;
                }
                else
                {
                    _x = XMax;
                }

                if (YMin > YMax)
                {
                    Swap(ref YMin, ref YMax);
                }

                if (XMin > XMax)
                {
                    Swap(ref XMin, ref XMax);
                }

                return this;
            }

            public CuiRectTransformComponent GetRectTransform()
            {
                return new CuiRectTransformComponent
                {
                    AnchorMin = _anchor,
                    AnchorMax = _anchor,
                    OffsetMin = OffsetMin,
                    OffsetMax = OffsetMax,
                };
            }

            private string DetermineAnchor()
            {
                return _isTopToBottom
                    ? _isLeftToRight ? AnchorTopLeft : AnchorTopRight
                    : _isLeftToRight ? AnchorBottomLeft : AnchorBottomRight;
            }
        }

        private class ButtonColor
        {
            public readonly string Color;
            public readonly string TextColor;

            public ButtonColor(string color, string textColor)
            {
                Color = color;
                TextColor = textColor;
            }
        }

        private class ButtonColorScheme
        {
            public ButtonColor Active;
            public ButtonColor Enabled;
            public ButtonColor Disabled;

            public ButtonColor Get(bool active = false, bool enabled = false)
            {
                return active
                    ? Active
                    : enabled
                        ? Enabled
                        : Disabled;
            }
        }

        private class CuiInputFieldComponentHud : CuiInputFieldComponent
        {
            [JsonProperty("hudMenuInput", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool HudMenuInput { get; set; }
        }

        private class CuiElementRecreate : CuiElement
        {
            [JsonProperty("destroyUi", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string DestroyUi { get; set; }
        }

        private static class EditUI
        {
            private const string UIName = "RecycleManager.UI";
            private const string EditPanelUIName = "RecycleManager.UI.EditPanel";
            private const string PercentagePanelUIName = "RecycleManager.UI.EditPanel.Percentages";

            private const string AnchorMin = "0.5 0";
            private const string AnchorMax = "0.5 0";

            private const float PanelWidth = 380.5f;
            private const float PanelHeight = 93f;
            private const float HeaderHeight = 21;

            private const int ItemPaddingLeft = 6;
            private const int ItemSize = 58;
            private const int ItemSpacing = 4;

            private const string BaseUICommand = "recyclemanager.ui";

            private const string TextColor = "0.8 0.8 0.8 1";
            private const string BackgroundColor = "0.25 0.25 0.25 1";

            private static ButtonColorScheme DefaultButtonColorScheme = new()
            {
                Active = new ButtonColor("0.25 0.5 0.75 1", "0.75 0.85 1 1"),
                Enabled = new ButtonColor("0.4 0.4 0.4 1", "0.71 0.71 0.71 1"),
                Disabled = new ButtonColor("0.4 0.4 0.4 0.5", "0.71 0.71 0.71 0.5"),
            };

            private static ButtonColorScheme SaveButtonColorScheme = new()
            {
                Enabled = new ButtonColor("0.451 0.553 0.271 1", "0.659 0.918 0.2 1"),
                Disabled = new ButtonColor("0.451 0.553 0.271 0.5", "0.659 0.918 0.2 0.5"),
            };

            private static ButtonColorScheme ResetButtonColorScheme = new()
            {
                Enabled = new ButtonColor("0.9 0.5 0.2 1", "1 0.9 0.7 1"),
                Disabled = new ButtonColor("0.9 0.5 0.2 0.25", "1 0.9 0.7 0.25"),
            };

            public static void DestroyUI(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, UIName);
            }

            public static void DrawUI(RecycleManager plugin, BasePlayer player, EditState state)
            {
                if (state == null)
                {
                    DrawDefaultUI(plugin, player);
                }
                else
                {
                    DrawEditUI(plugin, player, state);
                }
            }

            private static void DrawDefaultUI(RecycleManager plugin, BasePlayer player)
            {
                var elements = CreateContainer();
                AddEditButton(elements, plugin, player);

                CuiHelper.AddUi(player, elements);
            }

            private static void DrawEditUI(RecycleManager plugin, BasePlayer player, EditState state)
            {
                var elements = CreateContainer();
                AddEditPanel(elements, plugin, player, state);

                CuiHelper.AddUi(player, elements);
            }

            private static CuiElementContainer CreateContainer()
            {
                var offsetY = 109.5f;
                var offsetX = 192f;

                return new CuiElementContainer
                {
                    new CuiElementRecreate
                    {
                        Parent = "Hud.Menu",
                        Name = UIName,
                        DestroyUi = UIName,
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = AnchorMin,
                                AnchorMax = AnchorMax,
                                OffsetMin = $"{offsetX} {offsetY}",
                                OffsetMax = $"{offsetX} {offsetY}",
                            },
                        },
                    },
                };
            }

            private static void AddEditButton(CuiElementContainer elements, RecycleManager plugin, BasePlayer player)
            {
                var buttonWidth = 80f;
                var offsetX = PanelWidth - buttonWidth;
                var offsetY = 266f;

                elements.Add(new CuiButton
                {
                    Text =
                    {
                        Text = plugin.GetMessage(player.UserIDString, LangEntry.UIButtonAdmin),
                        Color = SaveButtonColorScheme.Enabled.TextColor,
                        Align = TextAnchor.MiddleCenter,
                        FontSize = 15,
                    },
                    Button =
                    {
                        Color = SaveButtonColorScheme.Enabled.Color,
                        Command = $"{BaseUICommand} {UICommand.Edit}",
                        FadeIn = 0.1f,
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0 0",
                        OffsetMin = $"{offsetX} {offsetY}",
                        OffsetMax = $"{offsetX + buttonWidth} {offsetY + HeaderHeight}",
                    },
                }, UIName);
            }

            private static void AddEditHeader(CuiElementContainer elements, RecycleManager plugin, BasePlayer player)
            {
                var offsetY = 266f;

                elements.Add(new CuiElement
                {
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Color = BackgroundColor,
                            Sprite = "assets/content/ui/ui.background.tiletex.psd",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"0 {offsetY}",
                            OffsetMax = $"{PanelWidth} {offsetY + HeaderHeight}",
                        },
                    },
                });

                elements.Add(new CuiElement
                {
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = plugin.GetMessage(player.UserIDString, LangEntry.UIHeader),
                            Align = TextAnchor.MiddleLeft,
                            FontSize = 14,
                            Color = TextColor,
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"5 {offsetY}",
                            OffsetMax = $"{5 + PanelWidth} {offsetY + HeaderHeight}",
                        },
                    },
                });

                var buttonWidth = 80f;
                var offsetX = PanelWidth - buttonWidth;

                elements.Add(new CuiButton
                {
                    Text =
                    {
                        Text = plugin.GetMessage(player.UserIDString, LangEntry.UIButtonClose),
                        Color = DefaultButtonColorScheme.Enabled.TextColor,
                        Align = TextAnchor.MiddleCenter,
                        FontSize = 15,
                    },
                    Button =
                    {
                        Color = DefaultButtonColorScheme.Enabled.Color,
                        Command = $"{BaseUICommand} {UICommand.Cancel}",
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "0 0",
                        OffsetMin = $"{offsetX} {offsetY}",
                        OffsetMax = $"{offsetX + buttonWidth} {offsetY + HeaderHeight}",
                    },
                }, UIName);
            }

            private static void AddEditPanel(CuiElementContainer elements, RecycleManager plugin, BasePlayer player, EditState state)
            {
                elements.Add(new CuiElement
                {
                    Parent = UIName,
                    Name = EditPanelUIName,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Color = BackgroundColor,
                            Sprite = "assets/content/ui/ui.background.tiletex.psd",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = AnchorMin,
                            AnchorMax = AnchorMax,
                            OffsetMin = "0 0",
                            OffsetMax = $"{PanelWidth} {PanelHeight}",
                        },
                    },
                });

                AddEditHeader(elements, plugin, player);

                if (state.BlockedByAnotherPlugin)
                {
                    elements.Add(new CuiElement
                    {
                        Parent = EditPanelUIName,
                        Components =
                        {
                            new CuiTextComponent
                            {
                                Text = plugin.GetMessage(player.UserIDString, LangEntry.UIItemBlocked),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = 12,
                                Color = TextColor,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                            },
                        },
                    });
                    return;
                }

                AddPercentageControllers(elements, state);

                if (state.InputItem == null)
                {
                    elements.Add(new CuiElement
                    {
                        Parent = EditPanelUIName,
                        Components =
                        {
                            new CuiTextComponent
                            {
                                Text = plugin.GetMessage(player.UserIDString, LangEntry.UIEmptyState),
                                Align = TextAnchor.MiddleCenter,
                                FontSize = 12,
                                Color = TextColor,
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "1 1",
                            },
                        },
                    });
                    return;
                }

                AddItemIdentificationControls(elements, plugin, player, state);
                AddItemAllowedControls(elements, plugin, player, state);
                AddPrimaryControls(elements, plugin, player, state);
            }

            private static void AddPercentageControllers(CuiElementContainer elements, EditState state)
            {
                var offsetY = 169;

                elements.Add(new CuiElement
                {
                    Name = PercentagePanelUIName,
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Color = BackgroundColor,
                            Sprite = "assets/content/ui/ui.background.tiletex.psd",
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"0 {offsetY}",
                            OffsetMax = $"{PanelWidth} {offsetY + HeaderHeight}",
                        },
                    },
                });

                if (state.InputItem == null)
                    return;

                for (var i = 0; i < 6; i++)
                {
                    if (state.Chances[i] <= 0)
                        continue;

                    var offsetX = ItemPaddingLeft + i * (ItemSize + ItemSpacing);

                    elements.Add(new CuiElement
                    {
                        Parent = PercentagePanelUIName,
                        Components =
                        {
                            new CuiInputFieldComponentHud
                            {
                                Align = TextAnchor.MiddleCenter,
                                HudMenuInput = true,
                                Text = $"{state.Chances[i]:0.##}%",
                                Color = TextColor,
                                CharsLimit = 6,
                                Command = $"{BaseUICommand} {UICommand.InputPercentage} {i}",
                            },
                            new CuiRectTransformComponent
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetX} 0",
                                OffsetMax = $"{offsetX + ItemSize} {HeaderHeight}",
                            },
                        },
                    });
                }
            }

            private static void AddButton(CuiElementContainer elements,
                LayoutProvider layoutProvider,
                ButtonColorScheme buttonColorScheme,
                string parent,
                string text,
                string command,
                bool active = false,
                bool enabled = true)
            {
                var buttonColor = buttonColorScheme.Get(active, enabled);

                elements.Add(new CuiButton
                {
                    Text =
                    {
                        Text = text,
                        Color = buttonColor.TextColor,
                        Align = TextAnchor.MiddleCenter,
                        FontSize = 12,
                    },
                    Button =
                    {
                        Color = buttonColor.Color,
                        Command = enabled && !active ? command : null,
                    },
                    RectTransform =
                    {
                        AnchorMin = layoutProvider.AnchorMin,
                        AnchorMax = layoutProvider.AnchorMax,
                        OffsetMin = layoutProvider.OffsetMin,
                        OffsetMax = layoutProvider.OffsetMax,
                    },
                }, parent);
            }

            private static void AddItemIdentificationControls(CuiElementContainer elements, RecycleManager plugin, BasePlayer player, EditState state)
            {
                var spacingX = 5;
                var spacingY = 2;
                var paddingY = 5;
                var numElements = 4;

                var columnWidth = PanelWidth / 3f;
                var elementHeight = (PanelHeight - 2 * paddingY - (numElements - 1) * spacingY) / numElements;

                var layoutProvider = LayoutProvider.Once(columnWidth - 2 * spacingX, elementHeight)
                    .WithOffset(0, paddingY - spacingY)
                    .WithSpacing(spacingX, spacingY)
                    .WithOptions(LayoutOptions.Vertical);

                elements.Add(new CuiElement
                {
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = plugin.GetMessage(player.UserIDString, LangEntry.UILabelConfigureBy),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 12,
                            Color = TextColor,
                        },
                        layoutProvider.Next().GetRectTransform(),
                    },
                });

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonItem),
                    $"{BaseUICommand} {UICommand.ChangeIdentificationType} {IdentificationType.Item}",
                    state.IdentificationType == IdentificationType.Item,
                    state.InputItem.skin != 0);

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonSkin),
                    $"{BaseUICommand} {UICommand.ChangeIdentificationType} {IdentificationType.Skin}",
                    state.IdentificationType == IdentificationType.Skin,
                    state.InputItem.skin != 0);

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonDisplayName),
                    $"{BaseUICommand} {UICommand.ChangeIdentificationType} {IdentificationType.DisplayName}",
                    state.IdentificationType == IdentificationType.DisplayName,
                    !string.IsNullOrWhiteSpace(state.InputItem.name));
            }

            private static void AddItemAllowedControls(CuiElementContainer elements, RecycleManager plugin, BasePlayer player, EditState state)
            {
                var spacingX = 5;
                var spacingY = 2;
                var paddingY = 5;
                var numElements = 4;

                var columnWidth = PanelWidth / 3f;
                var elementHeight = (PanelHeight - 2 * paddingY - (numElements - 1) * spacingY) / numElements;

                var layoutProvider = LayoutProvider.Once(columnWidth - 2 * spacingX, elementHeight)
                    .WithOffset(columnWidth, paddingY - spacingY)
                    .WithSpacing(spacingX, spacingY)
                    .WithOptions(LayoutOptions.Vertical);

                elements.Add(new CuiElement
                {
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = plugin.GetMessage(player.UserIDString, LangEntry.UILabelOutput),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 12,
                            Color = TextColor,
                        },
                        layoutProvider.Next().GetRectTransform(),
                    },
                });

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonNotRecyclable),
                    $"{BaseUICommand} {UICommand.ChangeOutputType} {OutputType.NotRecyclable}",
                    state.OutputType == OutputType.NotRecyclable);

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonDefaultOutput),
                    $"{BaseUICommand} {UICommand.ChangeOutputType} {OutputType.Default}",
                    state.OutputType == OutputType.Default,
                    state.IdentificationType != IdentificationType.Item || IsVanillaRecyclable(state.InputItem));

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonCustomOutput),
                    $"{BaseUICommand} {UICommand.ChangeOutputType} {OutputType.Custom}",
                    state.OutputType == OutputType.Custom);
            }

            private static void AddPrimaryControls(CuiElementContainer elements, RecycleManager plugin, BasePlayer player, EditState state)
            {
                var spacingX = 5;
                var spacingY = 2;
                var paddingY = 5;
                var numElements = 4;

                var columnWidth = PanelWidth / 3f;
                var elementHeight = (PanelHeight - 2 * paddingY - (numElements - 1) * spacingY) / numElements;

                var layoutProvider = LayoutProvider.Once(columnWidth - 2 * spacingX, elementHeight)
                    .WithOffset(2 * columnWidth, paddingY - spacingY)
                    .WithSpacing(spacingX, spacingY)
                    .WithOptions(LayoutOptions.Vertical);

                elements.Add(new CuiElement
                {
                    Parent = EditPanelUIName,
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = plugin.GetMessage(player.UserIDString, LangEntry.UILabelActions),
                            Align = TextAnchor.MiddleCenter,
                            FontSize = 12,
                            Color = TextColor,
                        },
                        layoutProvider.Next().GetRectTransform(),
                    },
                });

                AddButton(elements,
                    layoutProvider.Next(),
                    SaveButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonSave),
                    $"{BaseUICommand} {UICommand.Save}",
                    enabled: state.CanSave);

                AddButton(elements,
                    layoutProvider.Next(),
                    ResetButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonReset),
                    $"{BaseUICommand} {UICommand.Reset}",
                    enabled: state.CanReset);

                AddButton(elements,
                    layoutProvider.Next(),
                    DefaultButtonColorScheme,
                    EditPanelUIName,
                    plugin.GetMessage(player.UserIDString, LangEntry.UIButtonCancel),
                    $"{BaseUICommand} {UICommand.Cancel}");
            }
        }

        #endregion

        #region Edit Controller

        private class EditState
        {
            public Item InputItem;
            public IdentificationType IdentificationType;
            public OutputType OutputType;

            public float[] Chances = new float[NumOutputSlots];

            public bool BlockedByAnotherPlugin;
            public bool CanSave;
            public bool CanReset;
        }

        private class EditController : FacepunchBehaviour
        {
            public static EditController AddToRecycler(RecycleManager plugin, RecycleEditManager recycleEditManager, Recycler recycler)
            {
                var component = recycler.gameObject.AddComponent<EditController>();
                component._plugin = plugin;
                component._recycleEditManager = recycleEditManager;
                component._recycler = recycler;
                return component;
            }

            private RecycleManager _plugin;
            private RecycleEditManager _recycleEditManager;
            private Recycler _recycler;
            private BasePlayer _player;
            private EditState _editState;
            private Func<Item, int, bool> _originalCanAcceptItem;
            private Action _onDirtyDelayed;
            private bool _pauseAutoChangeOutput;

            private Configuration _config => _plugin._config;

            private EditController()
            {
                _onDirtyDelayed = OnDirtyDelayed;
            }

            public void StartViewing(BasePlayer player)
            {
                _player = player;
                DrawUI();
            }

            public void DestroyImmediate()
            {
                DestroyImmediate(this);
            }

            private IngredientInfo[] GetSavedIngredients()
            {
                return GetOutput(_editState.IdentificationType, _editState.OutputType);
            }

            private bool CanSave()
            {
                if (_editState.InputItem == null)
                    return false;

                switch (_editState.OutputType)
                {
                    case OutputType.NotRecyclable:
                    {
                        if (IsDisallowed())
                            return false;

                        if (GetOverride() != null)
                            return true;

                        return _editState.IdentificationType == IdentificationType.Item && IsVanillaRecyclable(_editState.InputItem);
                    }

                    case OutputType.Default:
                    {
                        if (IsDisallowed())
                            return true;

                        if (GetOverride() != null)
                            return true;

                        return false;
                    }

                    case OutputType.Custom:
                        return GetOverride() == null
                               || !GetOutputIngredients().SequenceEqual(GetSavedIngredients());

                    default:
                        return true;
                }
            }

            private bool CanReset()
            {
                if (_editState.InputItem == null)
                    return false;

                return IsDisallowed() || GetOverride() != null;
            }

            private void DrawUI()
            {
                if (_editState != null)
                {
                    _editState.CanSave = CanSave();
                    _editState.CanReset = CanReset();

                }

                EditUI.DrawUI(_plugin, _player, _editState);
            }

            private List<IngredientInfo> GetOutputIngredients()
            {
                var customIngredientList = new List<IngredientInfo>();

                for (var i = NumInputSlots; i < NumInputSlots + NumOutputSlots; i++)
                {
                    var item = _recycler.inventory.GetSlot(i);
                    if (item == null)
                        continue;

                    var amount = (float)item.amount;
                    if (amount == 1)
                    {
                        if (_editState.Chances[i - NumInputSlots] == 0)
                        {
                            _editState.Chances[i - NumInputSlots] = 100;
                        }
                        else
                        {
                            amount = _editState.Chances[i - NumInputSlots] / 100f;
                        }
                    }

                    var ingredientInfo = new IngredientInfo
                    {
                        ShortName = item.info.shortname,
                        DisplayName = !string.IsNullOrWhiteSpace(item.name) ? item.name : null,
                        SkinId = item.skin,
                        Amount = amount,
                    };
                    ingredientInfo.Init();
                    customIngredientList.Add(ingredientInfo);
                }

                return customIngredientList;
            }

            public void HandleUICommand(BasePlayer player, string[] args)
            {
                var commandTypeArg = args.FirstOrDefault();
                if (commandTypeArg == null)
                    return;

                if (!Enum.TryParse(commandTypeArg, ignoreCase: true, result: out UICommand uiCommand))
                    return;

                switch (uiCommand)
                {
                    case UICommand.Edit:
                        StartEditing();
                        break;

                    case UICommand.Reset:
                    {
                        _editState.OutputType = OutputType.Default;

                        var changed = false;
                        changed |= _config.RestrictedInputItems.Allow(_editState.InputItem, _editState.IdentificationType);
                        changed |= _config.OverrideOutput.RemoveOverride(_editState.InputItem, _editState.IdentificationType);

                        if (changed)
                        {
                            _plugin.SaveConfig();
                        }

                        _editState.OutputType = DetermineBestOutputType();

                        RemoveOutputItems();
                        PopulateOutputItems();
                        DrawUI();
                        break;
                    }

                    case UICommand.Save:
                    {
                        if (_editState.OutputType == OutputType.Custom)
                        {
                            _config.RestrictedInputItems.Allow(_editState.InputItem, _editState.IdentificationType);
                            _config.OverrideOutput.SetOverride(_editState.InputItem, _editState.IdentificationType, GetOutputIngredients().ToArray());
                            _plugin.SaveConfig();
                        }
                        else
                        {
                            var changed = _config.OverrideOutput.RemoveOverride(_editState.InputItem, _editState.IdentificationType);

                            if (_editState.OutputType == OutputType.NotRecyclable)
                            {
                                if (_editState.IdentificationType != IdentificationType.Item || IsVanillaRecyclable(_editState.InputItem))
                                {
                                    changed |= _config.RestrictedInputItems.Disallow(_editState.InputItem, _editState.IdentificationType);
                                }
                            }
                            else
                            {
                                changed |= _config.RestrictedInputItems.Allow(_editState.InputItem, _editState.IdentificationType);
                            }

                            if (changed)
                            {
                                _plugin.SaveConfig();
                            }
                        }

                        RemoveOutputItems();
                        PopulateOutputItems();
                        DrawUI();
                        break;
                    }

                    case UICommand.Cancel:
                        StopEditing(redraw: true);
                        break;

                    case UICommand.InputPercentage:
                    {
                        var slotArg = args.ElementAtOrDefault(1);
                        var amountArg = args.ElementAtOrDefault(2)?.Replace("%", "");
                        if (slotArg == null || amountArg == null)
                            break;

                        if (!int.TryParse(slotArg, out var slot) || slot < 0 || slot > 5)
                            break;

                        if (!float.TryParse(amountArg, out var chance))
                        {
                            DrawUI();
                            break;
                        }

                        chance = Mathf.Clamp(chance, 0.01f, 100);
                        // Since we allow up to 2 decimal places, allow tolerance of half of 3rd decimal place.
                        // This makes it so if the original value is higher precision like 8.333334, clicking into the
                        // field without changing the input doesn't change the underlying value.
                        if (Math.Abs(chance - _editState.Chances[slot]) >= 0.005f)
                        {
                            _editState.Chances[slot] = chance;
                            _pauseAutoChangeOutput = false;
                            HandleChanges();
                        }

                        DrawUI();
                        break;
                    }

                    case UICommand.ChangeOutputType:
                    {
                        var allowedArg = args.ElementAtOrDefault(1);
                        if (allowedArg == null)
                            break;

                        if (!Enum.TryParse(allowedArg, out OutputType outputType))
                            break;

                        if (outputType == _editState.OutputType)
                            break;

                        _editState.OutputType = outputType;

                        if (outputType == OutputType.NotRecyclable)
                        {
                            RemoveOutputItems();
                        }
                        else
                        {
                            RemoveOutputItems();
                            PopulateOutputItems();
                        }

                        DrawUI();
                        break;
                    }

                    case UICommand.ChangeIdentificationType:
                    {
                        var identifyTypeArg = args.ElementAtOrDefault(1);
                        if (identifyTypeArg == null)
                            break;

                        if (!Enum.TryParse(identifyTypeArg, ignoreCase: true, result: out IdentificationType identificationType))
                            break;

                        if (_editState.IdentificationType == identificationType)
                            break;

                        _editState.IdentificationType = identificationType;
                        _editState.OutputType = DetermineBestOutputType();

                        RemoveOutputItems();
                        PopulateOutputItems();
                        DrawUI();
                        break;
                    }
                }
            }

            private IngredientInfo[] GetOutput(IdentificationType identificationType, OutputType outputType)
            {
                if (outputType == OutputType.NotRecyclable)
                    return null;

                if (outputType == OutputType.Custom)
                {
                    var output = GetOverride(identificationType);
                    if (output != null)
                        return output;
                }

                if (identificationType == IdentificationType.DisplayName)
                    return GetOutput(IdentificationType.Skin, DetermineBestOutputType(IdentificationType.Skin));

                if (identificationType == IdentificationType.Skin)
                    return GetOutput(IdentificationType.Item, DetermineBestOutputType(IdentificationType.Item));

                if (IsVanillaRecyclable(_editState.InputItem))
                    return GetVanillaOutput(_editState.InputItem.info);

                return null;
            }

            private void PopulateOutputItems()
            {
                var output = GetOutput(_editState.IdentificationType, _editState.OutputType);
                if (output == null)
                {
                    for (var i = 0; i < _editState.Chances.Length; i++)
                    {
                        _editState.Chances[i] = 0;
                    }
                    return;
                }

                PopulateOutputWithOverride(_recycler, output, 1, forEditor: true);
                _pauseAutoChangeOutput = true;

                for (var i = 0; i < _editState.Chances.Length; i++)
                {
                    _editState.Chances[i] = 0;

                    var customIngredient = output.ElementAtOrDefault(i);
                    if (customIngredient is { Amount: <= 1 })
                    {
                        _editState.Chances[i] = customIngredient.Amount * 100f;
                    }
                }
            }

            private void RemoveInputItems()
            {
                for (var i = 0; i < NumInputSlots; i++)
                {
                    var item = _recycler.inventory.GetSlot(i);
                    if (item == null)
                        continue;

                    _player.GiveItem(item);
                }
            }

            private void RemoveOutputItems(BasePlayer player = null)
            {
                for (var i = NumInputSlots; i < NumInputSlots + NumOutputSlots; i++)
                {
                    var item = _recycler.inventory.GetSlot(i);
                    if (item == null)
                        continue;

                    if (player != null)
                    {
                        player.GiveItem(item);
                    }
                    else
                    {
                        item.RemoveFromContainer();
                        item.Remove();
                    }
                }

                _pauseAutoChangeOutput = true;
            }

            private void StartEditing()
            {
                _recycler.StopRecycling();

                RemoveOutputItems(_player);

                _originalCanAcceptItem = _recycler.inventory.canAcceptItem;
                _recycler.inventory.canAcceptItem = (item, slot) => _editState?.InputItem != null
                    ? (slot == 0 || slot >= NumInputSlots)
                    : slot < NumInputSlots;

                _recycler.inventory.onDirty += OnDirty;

                _editState = new EditState();
                OnDirtyDelayed();
            }

            private bool CanAcceptItem(Item item)
            {
                if (IsDisallowed())
                    return false;

                if (_plugin.CallCanBeRecycled(item, _recycler) is bool result)
                    return result;

                if (GetOverride() != null)
                    return true;

                return IsVanillaRecyclable(_editState.InputItem);
            }

            private void StopEditing(bool redraw = false)
            {
                if (_editState == null || _recycler == null || _recycler.IsDestroyed)
                    return;

                _recycler.inventory.canAcceptItem = _originalCanAcceptItem;
                _recycler.inventory.onDirty -= OnDirty;

                if (_editState.InputItem != null && !CanAcceptItem(_editState.InputItem))
                {
                    RemoveInputItems();
                }

                RemoveOutputItems();

                _editState = null;

                if (redraw)
                {
                    DrawUI();
                }
            }

            private void StopViewing()
            {
                if (_player == null)
                    return;

                EditUI.DestroyUI(_player);

                _recycleEditManager.HandlePlayerStoppedLooting(_player);
                _player = null;
            }

            private Item TrimInputs()
            {
                Item firstItem = null;

                for (var i = 0; i < NumInputSlots; i++)
                {
                    var item = _recycler.inventory.GetSlot(i);
                    if (item == null)
                        continue;

                    if (firstItem == null)
                    {
                        firstItem = item;
                    }
                    else
                    {
                        item.RemoveFromContainer();
                        _player.GiveItem(item);
                    }
                }

                if (firstItem != null && firstItem.position != 0)
                {
                    firstItem.MoveToContainer(firstItem.parent, 0);
                }

                return firstItem;
            }

            private void RemoveExcessInput(Item item)
            {
                if (item.amount <= 1)
                    return;

                var splitItem = item.SplitItem(item.amount - 1);
                if (splitItem == null)
                    return;

                _player.GiveItem(splitItem);
            }

            private IngredientInfo[] GetOverride(IdentificationType identificationType)
            {
                return _config.OverrideOutput.GetOverride(_editState.InputItem, identificationType);
            }

            private IngredientInfo[] GetOverride()
            {
                return GetOverride(_editState.IdentificationType);
            }

            private bool IsDisallowed(IdentificationType identificationType)
            {
                if (_editState.InputItem == null)
                    return false;

                return _config.RestrictedInputItems.IsDisallowed(_editState.InputItem, identificationType);
            }

            private bool IsDisallowed()
            {
                return IsDisallowed(_editState.IdentificationType);
            }

            private OutputType DetermineBestOutputType(IdentificationType identificationType)
            {
                if (GetOverride(identificationType) != null)
                    return OutputType.Custom;

                if (IsDisallowed(identificationType))
                    return OutputType.NotRecyclable;

                if (identificationType == IdentificationType.Item && !IsVanillaRecyclable(_editState.InputItem))
                    return OutputType.NotRecyclable;

                return OutputType.Default;
            }

            private OutputType DetermineBestOutputType()
            {
                return DetermineBestOutputType(_editState.IdentificationType);
            }

            private IdentificationType DetermineBestIdentificationType()
            {
                if (GetOverride(IdentificationType.DisplayName) != null
                    || IsDisallowed(IdentificationType.DisplayName))
                    return IdentificationType.DisplayName;

                if (GetOverride(IdentificationType.Skin) != null
                    || IsDisallowed(IdentificationType.Skin))
                    return IdentificationType.Skin;

                return IdentificationType.Item;
            }

            private void HandleNewInputItem()
            {
                RemoveExcessInput(_editState.InputItem);
                RemoveOutputItems();

                if (_plugin.CallCanBeRecycled(_editState.InputItem, _recycler) is false)
                {
                    _editState.BlockedByAnotherPlugin = true;
                    DrawUI();
                }
                else
                {
                    _editState.BlockedByAnotherPlugin = false;
                    _editState.IdentificationType = DetermineBestIdentificationType();
                    _editState.OutputType = DetermineBestOutputType();
                    PopulateOutputItems();
                }

                DrawUI();
            }

            private void HandleChanges()
            {
                var output = GetOutputIngredients();
                if (!_pauseAutoChangeOutput)
                {
                    var customOutput = GetOverride();
                    var defaultOutput = GetOutput(_editState.IdentificationType, OutputType.Default);
                    if (output.Count == 0)
                    {
                        _editState.OutputType = OutputType.NotRecyclable;
                    }
                    else if (customOutput != null && output.SequenceEqual(customOutput))
                    {
                        _editState.OutputType = OutputType.Custom;
                    }
                    else if (defaultOutput != null && output.SequenceEqual(defaultOutput))
                    {
                        _editState.OutputType = OutputType.Default;
                    }
                    else
                    {
                        _editState.OutputType = OutputType.Custom;
                    }
                }

                for (var i = 0; i < _editState.Chances.Length; i++)
                {
                    var outputItem = _recycler.inventory.GetSlot(NumInputSlots + i);
                    if (outputItem == null || outputItem.amount > 1)
                    {
                        _editState.Chances[i] = 0;
                    }
                }
            }

            private void OnDirtyDelayed()
            {
                if (_recycler == null)
                {
                    DestroyImmediate();
                    return;
                }

                var inputItem = TrimInputs();
                var previousInputItem = _editState.InputItem;
                _editState.InputItem = inputItem;

                if (inputItem != null && inputItem == previousInputItem)
                {
                    RemoveExcessInput(inputItem);
                    HandleChanges();
                    DrawUI();
                    return;
                }

                if (inputItem == null)
                {
                    _editState.BlockedByAnotherPlugin = false;
                    RemoveOutputItems(previousInputItem == null ? _player : null);
                    DrawUI();
                    return;
                }

                HandleNewInputItem();
            }

            private void OnDirty()
            {
                _pauseAutoChangeOutput = false;
                Invoke(_onDirtyDelayed, 0);
            }

            private void PlayerStoppedLooting(BasePlayer player)
            {
                if (_player == player)
                {
                    StopEditing();
                    StopViewing();
                }
            }

            private void OnDestroy()
            {
                StopEditing();
                StopViewing();
                _recycleEditManager.HandleControllerDestroyed(_recycler);
            }
        }

        #endregion

        #region Edit Manager

        private class RecycleEditManager
        {
            private RecycleManager _plugin;
            private Dictionary<Recycler, EditController> _controllers = new();
            private Dictionary<BasePlayer, EditController> _playerControllers = new();

            public RecycleEditManager(RecycleManager plugin)
            {
                _plugin = plugin;
            }

            public void HandlePlayerStartedLooting(BasePlayer player, Recycler recycler)
            {
                var controller = EnsureController(recycler);
                _playerControllers[player] = controller;
                controller.StartViewing(player);
            }

            public EditController GetController(BasePlayer player)
            {
                return _playerControllers.TryGetValue(player, out var editController)
                    ? editController
                    : null;
            }

            public EditController EnsureController(Recycler recycler)
            {
                var editController = GetController(recycler);
                if (editController == null)
                {
                    editController = EditController.AddToRecycler(_plugin, this, recycler);
                }

                _controllers[recycler] = editController;
                return editController;
            }

            public void HandlePlayerStoppedLooting(BasePlayer player)
            {
                _playerControllers.Remove(player);
            }

            public void HandleControllerDestroyed(Recycler recycler)
            {
                _controllers.Remove(recycler);
            }

            public void Unload()
            {
                foreach (var controller in _controllers.Values.ToArray())
                {
                    controller.DestroyImmediate();
                }
            }

            private EditController GetController(Recycler recycler)
            {
                return _controllers.TryGetValue(recycler, out var editController)
                    ? editController
                    : null;
            }
        }

        #endregion

        #region Recycler Component

        private class RecyclerComponent : FacepunchBehaviour
        {
            public static RecyclerComponent AddToRecycler(RecycleManager plugin, RecyclerComponentManager recyclerComponentManager, Recycler recycler)
            {
                var component = recycler.gameObject.AddComponent<RecyclerComponent>();
                component._plugin = plugin;
                component._recyclerComponentManager = recyclerComponentManager;
                component._recycler = recycler;
                component._vanillaRecycleThink = recycler.RecycleThink;
                return component;
            }

            public BasePlayer Player { get; private set; }
            private RecycleManager _plugin;
            private RecyclerComponentManager _recyclerComponentManager;
            private Recycler _recycler;
            private Action _vanillaRecycleThink;
            private Action _customRecycleThink;
            private float _recycleTime;

            private Configuration _config => _plugin._config;
            private bool _enableIncrementalRecycling => _config.Speed.EnableIncrementalRecycling;

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
                    Player = player;

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
                    Player = null;

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

                return _recycleTime * _config.Speed.GetTimeMultiplierForItem(item);
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
                _recyclerComponentManager.HandleRecyclerComponentDestroyed(_recycler);
            }
        }

        private class RecyclerComponentManager
        {
            private RecycleManager _plugin;
            private readonly Dictionary<Recycler, RecyclerComponent> _recyclerComponents = new();

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

            public RecyclerComponent EnsureRecyclerComponent(Recycler recycler)
            {
                if (!_recyclerComponents.TryGetValue(recycler, out var recyclerComponent))
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
        private class EditUISettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class PermissionSpeedProfile
        {
            [JsonProperty("Permission suffix")]
            public string PermissionSuffix;

            [JsonProperty("Recycle time (seconds)")]
            private float RecycleTime { set => TimeMultiplier = value / 5f; }

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
        private class SpeedSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Default recycle time (seconds)")]
            public float DefaultRecycleTime = 5;

            [JsonProperty("Recycle time (seconds)")]
            private float DeprecatedRecycleTime { set => DefaultRecycleTime = value; }

            [JsonProperty("Recycle time multiplier while in safe zone")]
            public float SafeZoneTimeMultiplier = 1;

            [JsonProperty("Recycle time multiplier by item short name (item: multiplier)")]
            public Dictionary<string, float> TimeMultiplierByShortName = new();

            [JsonProperty("Recycle time multiplier by permission")]
            public PermissionSpeedProfile[] PermissionSpeedProfiles =
            {
                new()
                {
                    PermissionSuffix = "fast",
                    TimeMultiplier = 0.2f,
                },
                new()
                {
                    PermissionSuffix = "instant",
                    TimeMultiplier = 0,
                },
            };

            [JsonProperty("Speeds requiring permission")]
            private PermissionSpeedProfile[] DeprecatedSpeedsRequiringPermission { set => PermissionSpeedProfiles = value; }

            [JsonIgnore]
            private Permission _permission;

            [JsonIgnore]
            private Dictionary<int, float> _timeMultiplierByItemId = new();

            [JsonIgnore]
            public bool EnableIncrementalRecycling;

            public void Init(RecycleManager plugin)
            {
                _permission = plugin.permission;

                foreach (var speedProfile in PermissionSpeedProfiles)
                {
                    speedProfile.Init(plugin);
                }

                foreach (var (itemShortName, timeMultiplier) in TimeMultiplierByShortName)
                {
                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {itemShortName}");
                        continue;
                    }

                    if (timeMultiplier == 1)
                        continue;

                    _timeMultiplierByItemId[itemDefinition.itemid] = timeMultiplier;
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
                if (_timeMultiplierByItemId.TryGetValue(item.info.itemid, out var time))
                    return time;

                return 1;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class EfficiencySettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Default recycle efficiency")]
            public float DefaultRecyclerEfficiency = 0.6f;

            [JsonProperty("Recycle efficiency while in safe zone")]
            public float RecyclerEfficiencyWhileInSafeZone = 0.4f;

            public float GetRecyclerEfficiency(Recycler recycler)
            {
                return recycler.IsSafezoneRecycler() ? RecyclerEfficiencyWhileInSafeZone : DefaultRecyclerEfficiency;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class MaxItemsPerRecycle
        {
            [JsonProperty("Default percent")]
            public float DefaultPercent = 10f;

            [JsonProperty("Percent by input item short name")]
            public Dictionary<string, float> PercentByShortName = new();

            [JsonProperty("Percent by input item skin ID")]
            public Dictionary<ulong, float> PercentBySkinId = new();

            [JsonProperty("Percent by input item display name (custom items)")]
            public CaseInsensitiveDictionary<float> PercentByDisplayName = new();

            [JsonIgnore]
            private Dictionary<int, float> PercentByItemId = new();

            public void Init()
            {
                foreach (var (shortName, percent) in PercentByShortName)
                {
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    PercentByItemId[itemDefinition.itemid] = percent;
                }
            }

            public float GetPercent(Item item)
            {
                if (!string.IsNullOrWhiteSpace(item.name) && PercentByDisplayName.TryGetValue(item.name, out var multiplier))
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
            public Dictionary<string, float> MultiplierByOutputShortName = new();

            [JsonIgnore]
            private Dictionary<int, float> MultiplierByOutputItemId = new();

            public void Init()
            {
                foreach (var (shortName, multiplier) in MultiplierByOutputShortName)
                {
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    MultiplierByOutputItemId[itemDefinition.itemid] = multiplier;
                }
            }

            public float GetOutputMultiplier(int itemId)
            {
                if (MultiplierByOutputItemId.TryGetValue(itemId, out var multiplier))
                    return multiplier;

                return DefaultMultiplier;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class IngredientInfo : IEquatable<IngredientInfo>
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

            public void Init()
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

            public bool Equals(IngredientInfo other)
            {
                if (ReferenceEquals(null, other))
                    return false;

                if (ReferenceEquals(this, other))
                    return true;

                return ItemDefinition == other.ItemDefinition
                       && SkinId == other.SkinId
                       && Amount.Equals(other.Amount)
                       && string.Compare(DisplayName, other.DisplayName, StringComparison.OrdinalIgnoreCase) == 0;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RestrictedInputItems
        {
            [JsonProperty("Item short names")]
            public CaseInsensitiveHashSet DisallowedInputShortNames = new();

            [JsonProperty("Item skin IDs")]
            public HashSet<ulong> DisallowedInputSkinIds = new();

            [JsonProperty("Item display names (custom items)")]
            public CaseInsensitiveHashSet DisallowedInputDisplayNames = new();

            [JsonIgnore]
            private HashSet<int> DisallowedInputItemIds = new();

            public void Init()
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

            public bool IsDisallowed(Item item, IdentificationType identificationType)
            {
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        return DisallowedInputDisplayNames.Contains(item.name);

                    case IdentificationType.Skin:
                        return DisallowedInputSkinIds.Contains(item.skin);

                    case IdentificationType.Item:
                        return DisallowedInputItemIds.Contains(item.info.itemid);
                }

                return true;
            }

            public bool IsDisallowed(Item item)
            {
                if (!string.IsNullOrEmpty(item.name) && IsDisallowed(item, IdentificationType.DisplayName))
                    return true;

                if (item.skin != 0 && IsDisallowed(item, IdentificationType.Skin))
                    return true;

                return IsDisallowed(item, IdentificationType.Item);
            }

            public bool Allow(Item item, IdentificationType identificationType)
            {
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        return DisallowedInputDisplayNames.Remove(item.name);

                    case IdentificationType.Skin:
                        return DisallowedInputSkinIds.Remove(item.skin);

                    case IdentificationType.Item:
                        return DisallowedInputShortNames.Remove(item.info.shortname)
                               | DisallowedInputItemIds.Remove(item.info.itemid);
                }

                return false;
            }

            public bool Disallow(Item item, IdentificationType identificationType)
            {
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        return DisallowedInputDisplayNames.Add(item.name);

                    case IdentificationType.Skin:
                        return DisallowedInputSkinIds.Add(item.skin);

                    case IdentificationType.Item:
                        return DisallowedInputShortNames.Add(item.info.shortname)
                               | DisallowedInputItemIds.Add(item.info.itemid);
                }

                return false;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class OverrideOutput
        {
            [JsonProperty("Override output by input item short name")]
            public Dictionary<string, IngredientInfo[]> OverrideOutputByShortName = new();

            [JsonProperty("Override output by input item skin ID")]
            public Dictionary<ulong, IngredientInfo[]> OverrideOutputBySkinId = new();

            [JsonProperty("Override output by input item display name (custom items)")]
            public CaseInsensitiveDictionary<IngredientInfo[]> OverrideOutputByDisplayName = new();

            [JsonIgnore]
            private Dictionary<int, IngredientInfo[]> OverrideOutputByItemId = new();

            public void Init()
            {
                foreach (var (shortName, ingredientInfoList) in OverrideOutputByShortName)
                {
                    if (string.IsNullOrWhiteSpace(shortName))
                        continue;

                    var itemDefinition = ItemManager.FindItemDefinition(shortName);
                    if (itemDefinition == null)
                    {
                        LogWarning($"Invalid item short name in config: {shortName}");
                        continue;
                    }

                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init();
                    }

                    OverrideOutputByItemId[itemDefinition.itemid] = ingredientInfoList;
                }

                foreach (var ingredientInfoList in OverrideOutputBySkinId.Values)
                {
                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init();
                    }
                }

                foreach (var ingredientInfoList in OverrideOutputByDisplayName.Values)
                {
                    foreach (var ingredientInfo in ingredientInfoList)
                    {
                        ingredientInfo.Init();
                    }
                }
            }

            public IngredientInfo[] GetOverride(Item item, IdentificationType identificationType)
            {
                IngredientInfo[] customIngredientList;
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        return !string.IsNullOrWhiteSpace(item.name) && OverrideOutputByDisplayName.TryGetValue(item.name, out customIngredientList)
                            ? customIngredientList
                            : null;

                    case IdentificationType.Skin:
                        return item.skin != 0 && OverrideOutputBySkinId.TryGetValue(item.skin, out customIngredientList)
                            ? customIngredientList
                            : null;

                    case IdentificationType.Item:
                        return OverrideOutputByItemId.TryGetValue(item.info.itemid, out customIngredientList)
                            ? customIngredientList
                            : null;
                }

                return null;
            }

            public IngredientInfo[] GetBestOverride(Item item)
            {
                if (!string.IsNullOrWhiteSpace(item.name) && OverrideOutputByDisplayName.TryGetValue(item.name, out var ingredientInfoList))
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

            public void SetOverride(Item item, IdentificationType identificationType, IngredientInfo[] customIngredientList)
            {
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        OverrideOutputByDisplayName[item.name] = customIngredientList;
                        break;

                    case IdentificationType.Skin:
                        OverrideOutputBySkinId[item.skin] = customIngredientList;
                        break;

                    case IdentificationType.Item:
                        OverrideOutputByShortName[item.info.shortname] = customIngredientList;
                        OverrideOutputByItemId[item.info.itemid] = customIngredientList;
                        break;
                }
            }

            public bool RemoveOverride(Item item, IdentificationType identificationType)
            {
                switch (identificationType)
                {
                    case IdentificationType.DisplayName:
                        return OverrideOutputByDisplayName.Remove(item.name);

                    case IdentificationType.Skin:
                        return OverrideOutputBySkinId.Remove(item.skin);

                    case IdentificationType.Item:
                        return OverrideOutputByShortName.Remove(item.info.shortname)
                               | OverrideOutputByItemId.Remove(item.info.itemid);
                }

                return false;
            }

            public void ResetOverride(RecycleManager plugin,ItemDefinition itemDefinition)
            {
                var ingredients = GetVanillaOutput(itemDefinition);

                OverrideOutputByShortName[itemDefinition.shortname] = ingredients;
                OverrideOutputByItemId[itemDefinition.itemid] = ingredients;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Edit UI")]
            public EditUISettings EditUISettings = new();

            [JsonProperty("Custom recycle speed")]
            public SpeedSettings Speed = new();

            [JsonProperty("Custom recycle efficiency")]
            public EfficiencySettings Efficiency = new();

            [JsonProperty("Restricted input items")]
            public RestrictedInputItems RestrictedInputItems = new();

            [JsonProperty("Max items in stack per recycle (% of max stack size)")]
            public MaxItemsPerRecycle MaxItemsPerRecycle = new();

            [JsonProperty("Output multipliers")]
            public OutputMultiplierSettings OutputMultipliers = new();

            [JsonProperty("Override output")]
            public OverrideOutput OverrideOutput = new();

            [JsonProperty("Override output (before efficiency factor)")]
            private OverrideOutput DeprecatedOverrideOutput
            {
                set
                {
                    if (value == null)
                        return;

                    foreach (var entry in value.OverrideOutputByShortName)
                    {
                        foreach (var ingredientInfo in entry.Value)
                        {
                            ingredientInfo.Amount *= 0.5f;
                        }

                        OverrideOutput.OverrideOutputByShortName[entry.Key] = entry.Value;
                    }

                    foreach (var entry in value.OverrideOutputBySkinId)
                    {
                        foreach (var ingredientInfo in entry.Value)
                        {
                            ingredientInfo.Amount *= 0.5f;
                        }

                        OverrideOutput.OverrideOutputBySkinId[entry.Key] = entry.Value;
                    }

                    foreach (var entry in value.OverrideOutputByDisplayName)
                    {
                        foreach (var ingredientInfo in entry.Value)
                        {
                            ingredientInfo.Amount *= 0.5f;
                        }

                        OverrideOutput.OverrideOutputByDisplayName[entry.Key] = entry.Value;
                    }
                }
            }

            public void Init(RecycleManager plugin)
            {
                Speed.Init(plugin);
                RestrictedInputItems.Init();
                MaxItemsPerRecycle.Init();
                OutputMultipliers.Init();
                OverrideOutput.Init();
            }
        }

        private Configuration GetDefaultConfig() => new();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            [JsonIgnore]
            public bool UsingDefaults;

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
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var currentDictValue = currentRawValue as Dictionary<string, object>;
                    if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
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
            public static List<LangEntry> AllLangEntries = new();

            public static readonly LangEntry ErrorNoPermission = new("Error.NoPermission", "You don't have permission to do that.");
            public static readonly LangEntry ErrorConfig = new("Error.Config", "Error: The config did not load correctly. Please fix the config and reload the plugin before running this command.");
            public static readonly LangEntry ErrorInvalidItem = new("Error.InvalidItem", "Error: Invalid item: <color=#fe0>{0}</color>");

            public static readonly LangEntry ItemSyntax = new("Item.Syntax", "Syntax: <color=#fe0>{0} <item id or short name></color>");

            public static readonly LangEntry AddExists = new("Add.Exists", "Error: Item <color=#fe0>{0}</color> is already in the config. To reset that item to vanilla output, use <color=#fe0>recyclemanager.reset {0}</color>.");
            public static readonly LangEntry AddSuccess = new("Add.Success", "Successfully added item <color=#fe0>{0}</color> to the config.");

            public static readonly LangEntry ResetSuccess = new("Reset.Success", "Successfully reset item <color=#fe0>{0}</color> in the config.");

            public static readonly LangEntry UIButtonAdmin = new("UI.Button.Admin", "Admin");
            public static readonly LangEntry UIHeader = new("UI.Header", "Recycle Manager");
            public static readonly LangEntry UIButtonClose = new("UI.Button.Close", "Close");
            public static readonly LangEntry UIEmptyState = new("UI.EmptyState", "Place an item into the recycler to preview and edit its output");
            public static readonly LangEntry UIItemBlocked = new("UI.ItemBlocked", "That item is blocked by another plugin");

            public static readonly LangEntry UILabelConfigureBy = new("UI.Label.ConfigureBy", "Configure by:");
            public static readonly LangEntry UIButtonItem = new("UI.Button.Item", "Item");
            public static readonly LangEntry UIButtonSkin = new("UI.Button.Skin", "Skin");
            public static readonly LangEntry UIButtonDisplayName = new("UI.Button.DisplayName", "Display name");

            public static readonly LangEntry UILabelOutput = new("UI.Label.Output", "Output:");
            public static readonly LangEntry UIButtonNotRecyclable = new("UI.Button.NotRecyclable", "Not recyclable");
            public static readonly LangEntry UIButtonDefaultOutput = new("UI.Button.DefaultOutput", "Default output");
            public static readonly LangEntry UIButtonCustomOutput = new("UI.Button.CustomOutput", "Custom output");

            public static readonly LangEntry UILabelActions = new("UI.Label,Actions", "Actions:");
            public static readonly LangEntry UIButtonSave = new("UI.Button.Save", "Save");
            public static readonly LangEntry UIButtonReset = new("UI.Button.Reset", "Reset");
            public static readonly LangEntry UIButtonCancel = new("UI.Button.Cancel", "Cancel");

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
                AssertItemInSlot(container, slot, out var item);
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
                return permissionsMap.TryGetValue(_plugin, out var permissionList)
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
                            new() { PermissionSuffix = "fast", TimeMultiplier = 0.1f },
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

            [TestMethod("Given gears max stack size 100, stack of 3 gears, with no override configured, should output 36 scrap and 48 metal fragments")]
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
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 36);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 48);
            }

            [TestMethod("Given gears max stack size 100, stack of 75 gears, default stack percent 50%, should output 600 scrap & 975 metal fragments, then should output 900 scrap & 1200 metal fragments")]
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
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 600);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 800);

                yield return new WaitForSeconds(0.1f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 900);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 1200);
            }

            [TestMethod("Given gears max stack size 100, stack of 75 gears, gears stack percent 50%, should output 600 scrap & 975 metal fragments, then should output 900 scrap & 1200 metal fragments")]
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
                        },
                    },
                });

                cleanupActions.Add(SetMaxStackSize("gears", 100));

                var gears = AddItemToContainer(_recycler.inventory, "gears", 75);
                _plugin.CallHook(nameof(OnRecyclerToggle), _recycler, _player);
                _recycler.StartRecycling();

                yield return null;
                yield return new WaitForSeconds(0.11f);
                AssertItemAmount(gears, 25);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 600);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 800);

                yield return new WaitForSeconds(0.1f);
                AssertItemAmount(gears, 0);
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 900);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 1200);
            }

            [TestMethod("Given 2.0 default multiplier, 3.0 scrap output multiplier, stack of 3 gears, should output 108 scrap & 96 metal fragments")]
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
                            ["scrap"] = 3f,
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
                AssertItemInContainer(_recycler.inventory, 6, "scrap", 108);
                AssertItemInContainer(_recycler.inventory, 7, "metal.fragments", 96);
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
                                new()
                                {
                                    ShortName = "wood",
                                    Amount = 50,
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

                AssertItemInSlot(_recycler.inventory, 6, out var outputItem);
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

            private List<TestInfo> _testInfoList = new();
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
                    if (methodInfo.GetCustomAttributes(typeof(TestMethodAttribute), true).FirstOrDefault() is not TestMethodAttribute testMethodAttribute)
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

                    var args = CreateTestArguments(testInfo.MethodInfo, out var cleanupActions);

                    try
                    {
                        testInfo.MethodInfo.Invoke(this, args);
                        testInfo.Status = TestStatus.Success;
                    }
                    catch (Exception ex)
                    {
                        testInfo.Exception = ex;
                        testInfo.Status = TestStatus.Error;
                    }

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

                    var args = CreateTestArguments(testInfo.MethodInfo, out var cleanupActions);

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