// OmegaDev2 cross-version account migration endpoints.
//
//   GET  /webapi/account/migration/export?player=*   — snapshot the
//        player's avatars/team-ups/items into a version-agnostic JSON
//        document (PrototypeGuid/AssetGuid + property-name based, safe to
//        move between 1.48/1.52/1.53 servers)
//   POST /webapi/account/migration/import             — { playerName,
//        snapshot } apply a previously-exported snapshot to a player on
//        THIS server (whichever client version this server is built for)
//   GET  /webapi/account/migration/credentials/export?email=*  — read the
//        account row itself (email/player name/password hash+salt) so the
//        exact same login works on another version without retyping a
//        password
//   POST /webapi/account/migration/credentials/import — clone that account
//        row onto THIS server's Account.db (no-op if the email already
//        exists here)
//
// The entity handlers run on the game thread — entity/inventory state is
// game state, and only ever operate on an account the caller is actually
// logged into on the target server, never touching another player's data.
// The credentials handlers talk to the account database directly (no live
// game session required) since a brand new account has no session yet.

using System;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Persistence;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.PlayerManagement.Players;

namespace MHServerEmu.WebFrontend.Handlers.WebApi
{
    public class AccountMigrationExportWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            Player player = PhantomsWebUtil.FindTargetPlayer(PhantomsWebUtil.QueryParam(context, "player"), null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                var snapshot = new AccountMigrationSnapshot
                {
                    SourceHeroName = p.CurrentAvatar != null ? GameDatabase.GetPrototypeName(p.CurrentAvatar.PrototypeDataRef) : null,
                    Player = AccountMigrationUtility.ExportEntity(p),
                    AvatarSharedProperties = AccountMigrationUtility.ExportProperties(p.AvatarProperties),
                    Achievements = AccountMigrationUtility.ExportAchievements(p.AchievementState),
                    Badges = AccountMigrationUtility.ExportBadges(p.GetAllBadges()),
                    StashTabOptions = AccountMigrationUtility.ExportStashTabOptions(p.GetAllStashTabOptions())
                };

                // Stash tabs/general-extra bags only exist as runtime
                // Inventory instances once purchased/unlocked -- capture
                // which ones the source player actually has so import can
                // unlock the same set on the target before placing items,
                // instead of requiring them to be manually re-bought first.
                foreach (Inventory unlockedInv in new InventoryIterator(p,
                    InventoryIterationFlags.PlayerGeneralExtra |
                    InventoryIterationFlags.PlayerStashGeneral |
                    InventoryIterationFlags.PlayerStashAvatarSpecific))
                {
                    string invGuid = AccountMigrationUtility.ExportPrototypeRef(unlockedInv.PrototypeDataRef);
                    if (invGuid != null)
                        snapshot.UnlockedStashInventoryGuids.Add(invGuid);
                }

                foreach (Avatar avatar in new AvatarIterator(p))
                    snapshot.Avatars.Add(AccountMigrationUtility.ExportEntity(avatar));

                EntityManager mgr = p.Game.EntityManager;
                foreach (var entry in p.GetInventory(InventoryConvenienceLabel.TeamUpLibrary))
                {
                    Agent teamUp = mgr.GetEntity<Agent>(entry.Id);
                    if (teamUp != null)
                        snapshot.TeamUps.Add(AccountMigrationUtility.ExportEntity(teamUp));
                }

                const InventoryIterationFlags itemFlags =
                    InventoryIterationFlags.PlayerGeneral |
                    InventoryIterationFlags.PlayerGeneralExtra |
                    InventoryIterationFlags.PlayerStashGeneral |
                    InventoryIterationFlags.PlayerStashAvatarSpecific |
                    InventoryIterationFlags.Equipment;

                foreach (Inventory inventory in new InventoryIterator(p, itemFlags))
                {
                    foreach (var entry in inventory)
                    {
                        Item item = mgr.GetEntity<Item>(entry.Id);
                        if (item == null) continue;

                        EntitySnapshot itemSnapshot = AccountMigrationUtility.ExportEntity(item);
                        // Tag which specific player-owned inventory (base
                        // General, General-extra, or a numbered stash tab)
                        // this item sat in, so import can put it back in
                        // the same tab instead of flattening everything
                        // into the base General inventory.
                        itemSnapshot.StashInventoryGuid = AccountMigrationUtility.ExportPrototypeRef(inventory.PrototypeDataRef);
                        snapshot.Items.Add(itemSnapshot);
                    }
                }

                foreach (Avatar avatar in new AvatarIterator(p))
                {
                    foreach (Inventory inventory in new InventoryIterator(avatar, InventoryIterationFlags.Equipment))
                    {
                        foreach (var entry in inventory)
                        {
                            Item item = mgr.GetEntity<Item>(entry.Id);
                            if (item == null) continue;

                            EntitySnapshot itemSnapshot = AccountMigrationUtility.ExportEntity(item);
                            // Tag which avatar/equip-slot this item actually sat
                            // in so import can put it back on the hero instead
                            // of dropping it in the stash. See EntitySnapshot.
                            itemSnapshot.EquippedOnAvatarGuid = AccountMigrationUtility.ExportPrototypeRef(avatar.PrototypeDataRef);
                            itemSnapshot.EquipInventoryGuid = AccountMigrationUtility.ExportPrototypeRef(inventory.PrototypeDataRef);
                            snapshot.Items.Add(itemSnapshot);
                        }
                    }
                }

                Logger.Info($"[AccountMigration] {p.GetName()}: exported {snapshot.Avatars.Count} avatar(s), {snapshot.TeamUps.Count} team-up(s), {snapshot.Items.Count} item(s)");

                return new { Ok = true, Snapshot = snapshot };
            });

            await context.SendJsonAsync(result);
        }
    }

    public class AccountMigrationImportWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public sealed class ImportRequest
        {
            public string PlayerName { get; set; }
            public AccountMigrationSnapshot Snapshot { get; set; }
        }

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            ImportRequest request = await context.ReadJsonAsync<ImportRequest>();
            if (request?.Snapshot == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing snapshot" });
                return;
            }

            Player player = PhantomsWebUtil.FindTargetPlayer(request.PlayerName, null, out string error);
            if (player == null)
            {
                await context.SendJsonAsync(new { Ok = false, Error = error ?? "player not found" });
                return;
            }

            object result = await PhantomsWebUtil.RunOnGameThread(player, p =>
            {
                int avatarsImported = 0, teamUpsImported = 0, itemsImported = 0, itemsEquipped = 0, stashTabsUnlocked = 0;
                var skipped = new List<string>();

                // Currency and any other Player-level Properties -- these
                // live on the player, not any individual avatar/item, so
                // they need their own explicit import pass.
                if (request.Snapshot.Player != null)
                    AccountMigrationUtility.ImportEntity(p, request.Snapshot.Player);

                // AvatarProperties is a SEPARATE PropertyCollection from
                // Properties above (e.g. PropertyEnum.AllianceOverride) --
                // easy to miss since it's not part of the Player EntitySnapshot.
                AccountMigrationUtility.ImportProperties(p.AvatarProperties, request.Snapshot.AvatarSharedProperties);

                // Achievement ids are raw uints, verified safe to copy
                // as-is -- see ExportAchievements for why.
                AccountMigrationUtility.ImportAchievements(p.AchievementState, request.Snapshot.Achievements);

                // MHServerEmu-internal admin/CSR flags, not client content --
                // raw ints, safe across builds (see ExportBadges).
                AccountMigrationUtility.ImportBadges(p, request.Snapshot.Badges);

                // Unlock the same stash tabs/general-extra bags the source
                // player had purchased, BEFORE placing any items -- a tab
                // that isn't unlocked yet doesn't exist as a runtime
                // Inventory, so items tagged for it would otherwise fall
                // back to base General until manually re-bought.
                foreach (string stashGuid in request.Snapshot.UnlockedStashInventoryGuids)
                {
                    if (AccountMigrationUtility.TryImportPrototypeRef(stashGuid, out PrototypeId stashRef) == false)
                        continue;

                    if (p.IsInventoryUnlocked(stashRef))
                        continue;

                    if (p.UnlockInventory(stashRef))
                        stashTabsUnlocked++;
                }

                // Cosmetic tab name/color/sort -- only meaningful for tabs
                // that resolve on this version, silently no-ops otherwise.
                AccountMigrationUtility.ImportStashTabOptions(p, request.Snapshot.StashTabOptions);

                foreach (EntitySnapshot avatarSnapshot in request.Snapshot.Avatars)
                {
                    PrototypeId avatarRef = AccountMigrationUtility.ResolveEntityType(avatarSnapshot);
                    if (avatarRef == PrototypeId.Invalid)
                    {
                        skipped.Add($"avatar '{avatarSnapshot.EntityTypeName}' — not available on this client version");
                        continue;
                    }

                    if (p.HasAvatarFullyUnlocked(avatarRef) == false)
                        p.UnlockAvatar(avatarRef, AvatarUnlockType.FreeUnlock, false);

                    Avatar avatar = null;
                    foreach (Avatar candidate in new AvatarIterator(p))
                    {
                        if (candidate.PrototypeDataRef == avatarRef) { avatar = candidate; break; }
                    }

                    if (avatar == null || AccountMigrationUtility.ImportEntity(avatar, avatarSnapshot) == false)
                    {
                        skipped.Add($"avatar '{avatarSnapshot.EntityTypeName}' — failed to apply properties");
                        continue;
                    }

#if GAME_VERSION_1_48
                    // PowerSpec/PowerSpecPending are NOT version-gated in
                    // PropertyEnum, so ImportEntity's generic by-name property
                    // copy above happily carries over whatever value the
                    // source (1.52/1.53) avatar had, even though those builds
                    // have no power-point-allocation UI/commit path that
                    // meaningfully sets it. 1.48's new power point system
                    // reads PowerSpec directly as "points already invested" --
                    // confirmed live 2026-07-29: a migrated avatar showed
                    // ValidatePendingPowerPointAllocation() computing
                    // TotalAfterAllocation=21 against Max=20 while trying to
                    // spend the correct 19th/20th point on a fresh power the
                    // player insisted they had never touched. Every migrated
                    // 1.48 avatar must start with a clean power-point slate,
                    // so strip whatever PowerSpec/PowerSpecPending values
                    // ImportEntity just copied in before the player ever
                    // opens the Powers screen.
                    using (var removeListHandle = MHServerEmu.Core.Memory.ListPool<MHServerEmu.Games.Properties.PropertyId>.Instance.Get(out var removeList))
                    {
                        foreach (var kvp in avatar.Properties.IteratePropertyRange(MHServerEmu.Games.Properties.PropertyEnum.PowerSpec))
                            removeList.Add(kvp.Key);
                        foreach (var kvp in avatar.Properties.IteratePropertyRange(MHServerEmu.Games.Properties.PropertyEnum.PowerSpecPending))
                            removeList.Add(kvp.Key);

                        foreach (var propId in removeList)
                            avatar.Properties.RemoveProperty(propId);
                    }
#endif

                    // The roster shows PropertyEnum.AvatarLibraryLevel, which
                    // lives on the PLAYER (not the avatar) and is normally
                    // only kept in sync by the avatar's own level-up code
                    // path -- writing CharacterLevel directly onto the
                    // avatar's properties above never runs that path, so
                    // without this call the character screen (reading the
                    // avatar directly) and the roster (reading this cached
                    // Player property) disagree.
                    p.OnAvatarCharacterLevelChanged(avatar);

                    avatarsImported++;
                }

                EntityManager mgr = p.Game.EntityManager;

                foreach (EntitySnapshot teamUpSnapshot in request.Snapshot.TeamUps)
                {
                    PrototypeId teamUpRef = AccountMigrationUtility.ResolveEntityType(teamUpSnapshot);
                    if (teamUpRef == PrototypeId.Invalid)
                    {
                        skipped.Add($"team-up '{teamUpSnapshot.EntityTypeName}' — not available on this client version");
                        continue;
                    }

                    Inventory teamUpLibrary = p.GetInventory(InventoryConvenienceLabel.TeamUpLibrary);
                    bool alreadyOwned = false;
                    if (teamUpLibrary != null)
                    {
                        foreach (var entry in teamUpLibrary)
                        {
                            Agent existing = mgr.GetEntity<Agent>(entry.Id);
                            if (existing != null && existing.PrototypeDataRef == teamUpRef) { alreadyOwned = true; break; }
                        }
                    }

                    if (alreadyOwned == false)
                        p.UnlockTeamUpAgent(teamUpRef, false);

                    Agent teamUp = null;
                    if (teamUpLibrary != null)
                    {
                        foreach (var entry in teamUpLibrary)
                        {
                            Agent candidate = mgr.GetEntity<Agent>(entry.Id);
                            if (candidate != null && candidate.PrototypeDataRef == teamUpRef) { teamUp = candidate; break; }
                        }
                    }

                    if (teamUp == null || AccountMigrationUtility.ImportEntity(teamUp, teamUpSnapshot) == false)
                    {
                        skipped.Add($"team-up '{teamUpSnapshot.EntityTypeName}' — failed to apply properties");
                        continue;
                    }

                    teamUpsImported++;
                }

                // Recomputes the other roster-cached fields (costume,
                // assigned team-up, aggregate stats) from current avatar
                // and team-up state -- same call the normal login path
                // makes. Must run after both loops above, since it reads
                // the team-ups that were just imported.
                p.SetAvatarLibraryProperties();
#if GAME_VERSION_1_52 || GAME_VERSION_1_53
                p.SetTeamUpLibraryProperties();
#endif

                Inventory generalInventory = p.GetInventory(InventoryConvenienceLabel.General);
                if (generalInventory == null)
                {
                    skipped.Add("items — player has no general inventory to place them in");
                }
                else
                {
                    foreach (EntitySnapshot itemSnapshot in request.Snapshot.Items)
                    {
                        PrototypeId itemRef = AccountMigrationUtility.ResolveEntityType(itemSnapshot);
                        if (itemRef == PrototypeId.Invalid || itemSnapshot.ItemSpec == null)
                        {
                            skipped.Add($"item '{itemSnapshot.EntityTypeName}' — not available on this client version");
                            continue;
                        }

                        PrototypeId rarityRef = ResolveGuidOrInvalid(itemSnapshot.ItemSpec.RarityProtoGuid);
                        if (rarityRef == PrototypeId.Invalid)
                        {
                            skipped.Add($"item '{itemSnapshot.EntityTypeName}' — rarity not available on this client version");
                            continue;
                        }

                        // ItemSpec has to be fully built and handed to
                        // EntitySettings BEFORE creation -- most of its
                        // fields (ItemProtoRef, Seed, EquippableBy, etc.)
                        // are get-only afterward, so there's no way to
                        // apply them to an already-created entity.
                        ItemSpec itemSpec = AccountMigrationUtility.BuildItemSpec(itemRef, itemSnapshot.ItemSpec);
                        if (itemSpec == null || itemSpec.IsValid == false)
                        {
                            skipped.Add($"item '{itemSnapshot.EntityTypeName}' — failed to build a valid ItemSpec on this version");
                            continue;
                        }

                        // Try to put this item back on the hero it was
                        // equipped on, in the same equip-slot inventory
                        // (Weapon, Costume, etc.) -- falls back to the
                        // player's general inventory (still transferred,
                        // just unequipped) if that avatar/inventory doesn't
                        // exist on this version or its slot is already
                        // occupied.
                        ulong containerId = p.Id;
                        PrototypeId containerInvRef = generalInventory.PrototypeDataRef;
                        uint targetSlot = generalInventory.GetFreeSlot(null, true);
                        bool equipped = false;

                        if (string.IsNullOrEmpty(itemSnapshot.EquippedOnAvatarGuid) == false &&
                            AccountMigrationUtility.TryImportPrototypeRef(itemSnapshot.EquippedOnAvatarGuid, out PrototypeId equipAvatarRef) &&
                            AccountMigrationUtility.TryImportPrototypeRef(itemSnapshot.EquipInventoryGuid, out PrototypeId equipInvRef))
                        {
                            Avatar equipAvatar = null;
                            foreach (Avatar candidate in new AvatarIterator(p))
                            {
                                if (candidate.PrototypeDataRef == equipAvatarRef) { equipAvatar = candidate; break; }
                            }

                            Inventory equipInventory = equipAvatar?.GetInventoryByRef(equipInvRef);
                            if (equipInventory != null)
                            {
                                uint freeEquipSlot = equipInventory.GetFreeSlot(null, true);
                                if (freeEquipSlot == Inventory.InvalidSlot && equipInventory.Count > 0)
                                {
                                    // UnlockAvatar() already auto-granted a
                                    // default occupant here (e.g. the
                                    // starting costume) before this loop
                                    // ever ran, so the real slot the source
                                    // account had equipped shows up as
                                    // "full". That default has no history
                                    // worth keeping on a brand-new hero --
                                    // evict it so the actual migrated item
                                    // can take its place.
                                    foreach (var occupantEntry in equipInventory)
                                    {
                                        mgr.GetEntity<Entity>(occupantEntry.Id)?.Destroy();
                                        break;
                                    }
                                    freeEquipSlot = equipInventory.GetFreeSlot(null, true);
                                }

                                if (freeEquipSlot != Inventory.InvalidSlot)
                                {
                                    containerId = equipAvatar.Id;
                                    containerInvRef = equipInventory.PrototypeDataRef;
                                    targetSlot = freeEquipSlot;
                                    equipped = true;
                                }
                            }
                        }

                        // Not equipped (or couldn't be) -- try to put it
                        // back in the specific stash tab / general-extra
                        // bag it came from instead of always defaulting to
                        // base General.
                        if (equipped == false &&
                            string.IsNullOrEmpty(itemSnapshot.StashInventoryGuid) == false &&
                            AccountMigrationUtility.TryImportPrototypeRef(itemSnapshot.StashInventoryGuid, out PrototypeId stashInvRef))
                        {
                            Inventory stashInventory = p.GetInventoryByRef(stashInvRef);
                            if (stashInventory != null)
                            {
                                uint freeStashSlot = stashInventory.GetFreeSlot(null, true);
                                if (freeStashSlot != Inventory.InvalidSlot)
                                {
                                    containerId = p.Id;
                                    containerInvRef = stashInventory.PrototypeDataRef;
                                    targetSlot = freeStashSlot;
                                }
                            }
                        }

                        if (targetSlot == Inventory.InvalidSlot)
                        {
                            skipped.Add($"item '{itemSnapshot.EntityTypeName}' — no free slot to place it in");
                            continue;
                        }

                        using EntitySettings settings = MHServerEmu.Core.Memory.ObjectPoolManager.Instance.Get<EntitySettings>();
                        settings.EntityRef = itemRef;
                        settings.ItemSpec = itemSpec;
                        settings.InventoryLocation = new(containerId, containerInvRef, targetSlot);

                        Item item = mgr.CreateEntity(settings) as Item;
                        if (item == null || AccountMigrationUtility.ImportEntity(item, itemSnapshot) == false)
                        {
                            skipped.Add($"item '{itemSnapshot.EntityTypeName}' — failed to create/apply");
                            continue;
                        }

                        itemsImported++;
                        if (equipped) itemsEquipped++;
                    }
                }

                Logger.Info($"[AccountMigration] {p.GetName()}: imported {avatarsImported} avatar(s), {teamUpsImported} team-up(s), {itemsImported} item(s) ({itemsEquipped} re-equipped), {stashTabsUnlocked} stash tab(s) unlocked, {skipped.Count} skipped");

                return new
                {
                    Ok = true,
                    AvatarsImported = avatarsImported,
                    TeamUpsImported = teamUpsImported,
                    ItemsImported = itemsImported,
                    ItemsEquipped = itemsEquipped,
                    StashTabsUnlocked = stashTabsUnlocked,
                    Skipped = skipped
                };
            });

            await context.SendJsonAsync(result);
        }

        private static PrototypeId ResolveGuidOrInvalid(string guidString)
        {
            if (string.IsNullOrEmpty(guidString)) return PrototypeId.Invalid;
            if (ulong.TryParse(guidString, System.Globalization.NumberStyles.HexNumber, null, out ulong raw) == false)
                return PrototypeId.Invalid;
            return GameDatabase.GetDataRefByPrototypeGuid((PrototypeGuid)raw);
        }
    }

    /// <summary>
    /// The account row itself: email, player name, and the password hash/salt
    /// pair as actually stored (PBKDF2-HMAC-SHA512, see CryptographyHelper).
    /// Copying these two blobs verbatim onto another server's Account.db
    /// lets the exact same plaintext password keep working there, since the
    /// hashing algorithm/iteration count is fixed in code, not per-row.
    /// </summary>
    public sealed class AccountCredentialsSnapshot
    {
        public string Email { get; set; }
        public string PlayerName { get; set; }
        public string PasswordHash { get; set; } // base64
        public string Salt { get; set; } // base64
        public AccountUserLevel UserLevel { get; set; }
        public AccountFlags Flags { get; set; }
    }

    public class AccountMigrationCredentialsExportWebHandler : WebHandler
    {
        protected override async Task Get(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            string email = PhantomsWebUtil.QueryParam(context, "email");
            if (string.IsNullOrWhiteSpace(email))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing email" });
                return;
            }

            if (AccountManager.TryGetAccountByEmail(email, out DBAccount account) == false)
            {
                await context.SendJsonAsync(new { Ok = false, Error = $"no account found for {email}" });
                return;
            }

            var credentials = new AccountCredentialsSnapshot
            {
                Email = account.Email,
                PlayerName = account.PlayerName,
                PasswordHash = Convert.ToBase64String(account.PasswordHash),
                Salt = Convert.ToBase64String(account.Salt),
                UserLevel = account.UserLevel,
                Flags = account.Flags
            };

            await context.SendJsonAsync(new { Ok = true, Credentials = credentials });
        }
    }

    public class AccountMigrationCredentialsImportWebHandler : WebHandler
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        protected override async Task Post(WebRequestContext context)
        {
            if (!await LocalOnlyGuard.CheckAsync(context)) return;

            AccountCredentialsSnapshot credentials = await context.ReadJsonAsync<AccountCredentialsSnapshot>();
            if (credentials == null || string.IsNullOrWhiteSpace(credentials.Email) || string.IsNullOrWhiteSpace(credentials.PlayerName))
            {
                await context.SendJsonAsync(new { Ok = false, Error = "missing credentials" });
                return;
            }

            if (AccountManager.TryGetAccountByEmail(credentials.Email, out _))
            {
                await context.SendJsonAsync(new
                {
                    Ok = true,
                    Created = false,
                    Message = $"Account {credentials.Email} already exists on this server — left as-is."
                });
                return;
            }

            byte[] passwordHash, salt;
            try
            {
                passwordHash = Convert.FromBase64String(credentials.PasswordHash);
                salt = Convert.FromBase64String(credentials.Salt);
            }
            catch (FormatException)
            {
                await context.SendJsonAsync(new { Ok = false, Error = "malformed credential data" });
                return;
            }

            // The (email, playerName, password) constructor is reused only to
            // get a fresh, collision-safe Id out of its internal IdGenerator.
            // The placeholder password it hashes here is immediately replaced
            // with the real hash/salt copied from the source account below.
            DBAccount account = new(credentials.Email, credentials.PlayerName, Guid.NewGuid().ToString("N"))
            {
                PasswordHash = passwordHash,
                Salt = salt,
                UserLevel = credentials.UserLevel,
                Flags = credentials.Flags
            };

            if (IDBManager.Instance.InsertAccount(account) == false)
            {
                await context.SendJsonAsync(new
                {
                    Ok = false,
                    Error = "database rejected the new account (player name may already be taken by a different account on this server)"
                });
                return;
            }

            Logger.Info($"[AccountMigration] cloned account credentials for {credentials.Email} ({credentials.PlayerName})");
            await context.SendJsonAsync(new
            {
                Ok = true,
                Created = true,
                Message = $"Account {credentials.Email} created on this server with your existing password — log in normally, no new password needed."
            });
        }
    }
}
