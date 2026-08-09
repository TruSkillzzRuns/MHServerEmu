using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.Games.Entities
{
    // OmegaDev2 Gear Picker bridge. HTTP handlers run on ThreadPool threads;
    // item creation touches Game.Current-dependent code and entity state, so
    // the actual gives are marshalled onto this player's game thread via a
    // zero-delay scheduled event. The HTTP handler awaits the
    // TaskCompletionSource to return real per-item results.
    public partial class Player
    {
        private static readonly Logger WebItemGiveLogger = LogManager.CreateLogger();

        public sealed class WebItemGiveEntry
        {
            public ulong ItemProtoRef { get; set; }
            public int Count { get; set; }
            public int Level { get; set; }
            public ulong RarityProtoRef { get; set; }
        }

        public sealed class WebItemGiveResult
        {
            public string ItemProtoRef { get; set; }
            public int StatusCode { get; set; }
            public int GivenCount { get; set; }
            public string Message { get; set; }
        }

        public sealed class WebItemGiveState
        {
            public List<WebItemGiveEntry> Entries { get; }
            public TaskCompletionSource<List<WebItemGiveResult>> Tcs { get; }
            public WebItemGiveState(List<WebItemGiveEntry> entries, TaskCompletionSource<List<WebItemGiveResult>> tcs)
            {
                Entries = entries;
                Tcs = tcs;
            }
        }

        private readonly EventGroup _webItemGiveEvents = new();
        private readonly EventPointer<WebItemGiveEvent> _webItemGiveEvent = new();

        /// <summary>
        /// Thread-safe entry point for the items/give web endpoint. Schedules
        /// the actual gives on this player's game thread and completes
        /// <paramref name="tcs"/> with per-entry results.
        /// </summary>
        public void GiveItemsFromWeb(List<WebItemGiveEntry> entries, TaskCompletionSource<List<WebItemGiveResult>> tcs)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null)
            {
                tcs.TrySetResult(new List<WebItemGiveResult>
                {
                    new() { ItemProtoRef = "", StatusCode = 500, GivenCount = 0, Message = "no game scheduler" },
                });
                return;
            }

            if (_webItemGiveEvent.IsValid) scheduler.CancelEvent(_webItemGiveEvent);
            scheduler.ScheduleEvent(_webItemGiveEvent, TimeSpan.Zero, _webItemGiveEvents);
            _webItemGiveEvent.Get().Initialize(this, new WebItemGiveState(entries, tcs));
        }

        private void DoWebItemGive(WebItemGiveState state)
        {
            var results = new List<WebItemGiveResult>(state.Entries.Count);

            try
            {
                var lootManager = Game?.LootManager;
                foreach (WebItemGiveEntry entry in state.Entries)
                {
                    var result = new WebItemGiveResult
                    {
                        ItemProtoRef = $"0x{entry.ItemProtoRef:X16}",
                        StatusCode = 200,
                        GivenCount = 0,
                        Message = "ok",
                    };
                    results.Add(result);

                    PrototypeId itemProtoRef = (PrototypeId)entry.ItemProtoRef;
                    if (itemProtoRef == PrototypeId.Invalid || itemProtoRef.As<ItemPrototype>() == null)
                    {
                        result.StatusCode = 400;
                        result.Message = "not an item prototype";
                        continue;
                    }

                    if (lootManager == null)
                    {
                        result.StatusCode = 500;
                        result.Message = "no loot manager";
                        continue;
                    }

                    int count = Math.Clamp(entry.Count, 1, 100);
                    int level = entry.Level > 0 ? Math.Clamp(entry.Level, 1, 100) : (CurrentAvatar?.CharacterLevel ?? 60);
                    PrototypeId rarityRef = (PrototypeId)entry.RarityProtoRef;

                    // Costumes are authored as non-droppable: they fail the rarity restriction
                    // for every rarity under LootContext.Drop, so ItemResolver.ResolveRarity()
                    // returns Invalid, the ItemSpec fails IsValid, and UpdateAffixes() errors out
                    // before anything is created. They do pass under CashShop/Crafting/Vendor,
                    // which is how they are actually obtained. Roll them as CashShop items so the
                    // Gear Picker can hand them over. Verified against 1.53 data 2026-08-02.
                    LootContext lootContext = itemProtoRef.As<CostumePrototype>() != null
                        ? LootContext.CashShop
                        : LootContext.Drop;

                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            ItemSpec itemSpec = lootManager.CreateItemSpec(itemProtoRef, lootContext, this, level, rarityRef);
                            if (itemSpec == null)
                            {
                                result.StatusCode = 422;
                                result.Message = "spec creation failed (level/rarity restrictions?)";
                                break;
                            }

                            using var summaryHandle = LootResultSummaryPool.Get(out LootResultSummary summary);
                            summary.Add(new LootResult(itemSpec));
                            if (lootManager.GiveLootFromSummary(summary, this) == false)
                            {
                                result.StatusCode = 422;
                                result.Message = "give failed (inventory full?)";
                                break;
                            }

                            result.GivenCount++;
                        }
                        catch (Exception ex)
                        {
                            result.StatusCode = 500;
                            result.Message = ex.Message;
                            break;
                        }
                    }
                }
            }
            finally
            {
                state.Tcs.TrySetResult(results);
            }
        }

        private sealed class WebItemGiveEvent : CallMethodEventParam1<Player, WebItemGiveState>
        {
            protected override CallbackDelegate GetCallback() => static (player, state) => player.DoWebItemGive(state);
        }
    }
}
