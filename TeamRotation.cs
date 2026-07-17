using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("TeamRotation", "emma_smith", "2.4.0")]
    [Description("Allows team leaders to rotate offline players out to make room for online players")]
    public class TeamRotation : RustPlugin
    {
        #region Fields

        private PluginConfig _config;
        private const string PermissionUse = "teamrotation.use";
        private const string PermissionAdmin = "teamrotation.admin";
        private const string UIMainName = "TeamRotationUI";
        private const float MaxRotateDistance = 10f;
        private readonly HashSet<ulong> _processingPlayers = new HashSet<ulong>();
        private readonly List<BuildingPrivlidge> _cupboardsCache = new List<BuildingPrivlidge>();
        private readonly List<BaseEntity> _entitiesCache = new List<BaseEntity>();
        private readonly List<AutoTurret> _turretsCache = new List<AutoTurret>();
        private readonly List<SleepingBag> _bagsCache = new List<SleepingBag>();
        private static FieldInfo _codeLockWhitelistField;
        private static FieldInfo _codeLockGuestlistField;
        private RotatedPlayersData _rotatedData;
        private readonly Dictionary<string, DateTime> _lastWebhookTime = new Dictionary<string, DateTime>();

        #endregion

        #region Data Storage

        private class RotatedPlayersData
        {
            public Dictionary<ulong, HashSet<ulong>> TeamLeaderBans = new Dictionary<ulong, HashSet<ulong>>();
        }

        private void LoadData()
        {
            try
            {
                _rotatedData = Interface.Oxide.DataFileSystem.ReadObject<RotatedPlayersData>("TeamRotation");
            }
            catch
            {
                _rotatedData = new RotatedPlayersData();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("TeamRotation", _rotatedData);
        }

        private bool IsPlayerBanned(ulong teamLeaderId, ulong playerId)
        {
            if (_rotatedData.TeamLeaderBans.TryGetValue(teamLeaderId, out HashSet<ulong> bannedPlayers))
            {
                return bannedPlayers.Contains(playerId);
            }
            return false;
        }

        private void BanPlayer(ulong teamLeaderId, ulong playerId)
        {
            if (!_rotatedData.TeamLeaderBans.ContainsKey(teamLeaderId))
            {
                _rotatedData.TeamLeaderBans[teamLeaderId] = new HashSet<ulong>();
            }
            _rotatedData.TeamLeaderBans[teamLeaderId].Add(playerId);
            SaveData();
        }

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Bag/Bed Deletion Radius (from TC)")]
            public float BagDeletionRadius { get; set; } = 50f;

            [JsonProperty("Enable Debug Logging")]
            public bool DebugLogging { get; set; } = false;

            [JsonProperty("Require Permission")]
            public bool RequirePermission { get; set; } = true;

            [JsonProperty("Feature Toggles")]
            public FeatureSettings Features { get; set; } = new FeatureSettings();

            [JsonProperty("Discord Webhook")]
            public DiscordSettings Discord { get; set; } = new DiscordSettings();

            [JsonProperty("UI Settings")]
            public UISettings UI { get; set; } = new UISettings();

            [JsonProperty("Messages")]
            public Dictionary<string, string> Messages { get; set; } = new Dictionary<string, string>();

            public class DiscordSettings
            {
                [JsonProperty("Webhook URL")]
                public string WebhookUrl { get; set; } = "";

                [JsonProperty("Enable Rotation Alerts")]
                public bool EnableRotationAlerts { get; set; } = true;

                [JsonProperty("Enable Admin Command Alerts")]
                public bool EnableAdminAlerts { get; set; } = true;

                [JsonProperty("Enable Banned Player Attempt Alerts")]
                public bool EnableBannedAttempts { get; set; } = true;

                [JsonProperty("Webhook Cooldown (Seconds)")]
                public int WebhookCooldown { get; set; } = 60;

                [JsonProperty("Webhook Color (Decimal)")]
                public int WebhookColor { get; set; } = 15158332;
            }

            public class FeatureSettings
            {
                [JsonProperty("De-authorize from Tool Cupboards")]
                public bool DeAuthTCs { get; set; } = true;

                [JsonProperty("De-authorize from Code Locks")]
                public bool DeAuthCodeLocks { get; set; } = true;

                [JsonProperty("Transfer Key Lock Ownership")]
                public bool TransferKeyLocks { get; set; } = true;

                [JsonProperty("De-authorize from Turrets")]
                public bool DeAuthTurrets { get; set; } = true;

                [JsonProperty("Delete Bags and Beds")]
                public bool DeleteBagsBeds { get; set; } = true;

                [JsonProperty("Kick from Team")]
                public bool KickFromTeam { get; set; } = true;

                [JsonProperty("Ban System (Prevent Re-authorization)")]
                public bool BanSystem { get; set; } = true;

                [JsonProperty("Block Team Rejoin")]
                public bool BlockTeamRejoin { get; set; } = true;
            }

            public class UISettings
            {
                [JsonProperty("Button Color (RGBA)")]
                public string ButtonColor { get; set; } = "0.7 0.3 0.3 0.9";

                [JsonProperty("Button Text Color (RGBA)")]
                public string TextColor { get; set; } = "1 1 1 1";

                [JsonProperty("Button Position - Offset Min")]
                public string OffsetMin { get; set; } = "280 290";

                [JsonProperty("Button Position - Offset Max")]
                public string OffsetMax { get; set; } = "410 325";
            }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    LoadDefaultConfig();
                }
                else
                {
                    if (_config.Messages == null)
                        _config.Messages = new Dictionary<string, string>();

                    foreach (var message in GetDefaultMessages())
                    {
                        if (!_config.Messages.ContainsKey(message.Key))
                            _config.Messages[message.Key] = message.Value;
                    }
                }
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintWarning($"Configuration file is invalid, loading defaults: {ex.Message}");
                LoadDefaultConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            _config.Messages = GetDefaultMessages();
        }

        private static Dictionary<string, string> GetDefaultMessages()
        {
            return new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this feature.",
                ["NotTeamLeader"] = "Only the team leader can rotate players.",
                ["NoTeam"] = "You are not in a team.",
                ["NoOfflinePlayers"] = "There are no offline players in your team to rotate.",
                ["RotationStarted"] = "Rotating {0} offline player(s) from your team...",
                ["RotationComplete"] = "Rotation complete! Kicked {5} player(s) from team. De-authorized from {1} TC(s), {2} lock(s), {3} turret(s). Deleted {4} bag(s)/bed(s).",
                ["ProcessingError"] = "An error occurred while processing rotation. Please try again.",
                ["AlreadyProcessing"] = "A rotation is already in progress. Please wait.",
                ["ButtonText"] = "Rotate Players",
                ["BannedFromTeam"] = "You have been rotated from this team and cannot authorize to their entities until wipe.",
                ["BannedFromTeamRejoin"] = "You have been rotated from this team and cannot rejoin until server wipe."
            };
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Discord Webhook

        private void SendDiscordWebhook(string title, string description, List<Dictionary<string, object>> fields, int color)
        {
            if (string.IsNullOrEmpty(_config.Discord.WebhookUrl)) return;

            var payload = new Dictionary<string, object>
            {
                ["embeds"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["title"] = title,
                        ["description"] = description,
                        ["color"] = color,
                        ["fields"] = fields,
                        ["footer"] = new Dictionary<string, string>
                        {
                            ["text"] = $"TeamRotation v{this.Version} by emma_smith"
                        },
                        ["timestamp"] = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            string json = JsonConvert.SerializeObject(payload);
            webrequest.Enqueue(_config.Discord.WebhookUrl, json, (code, response) =>
            {
                if (code != 200 && code != 204)
                {
                    if (_config.DebugLogging)
                        PrintWarning($"Discord webhook failed: {code} - {response}");
                }
            }, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            });
        }

        private void SendRotationWebhook(BasePlayer teamLeader, HashSet<ulong> rotatedPlayers, RelationshipManager.PlayerTeam team, int tcCount, int lockCount, int turretCount, int bagCount)
        {
            if (!_config.Discord.EnableRotationAlerts || string.IsNullOrEmpty(_config.Discord.WebhookUrl)) return;

            string cooldownKey = $"{teamLeader.userID}:Rotation";

            if (_lastWebhookTime.TryGetValue(cooldownKey, out DateTime lastTime))
            {
                var timeSinceLastWebhook = DateTime.UtcNow - lastTime;
                if (timeSinceLastWebhook.TotalSeconds < _config.Discord.WebhookCooldown)
                {
                    if (_config.DebugLogging)
                        Puts($"Webhook cooldown active for {teamLeader.displayName} rotation - {_config.Discord.WebhookCooldown - timeSinceLastWebhook.TotalSeconds:F1}s remaining");
                    return;
                }
            }

            _lastWebhookTime[cooldownKey] = DateTime.UtcNow;

            var fields = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Team Leader",
                    ["value"] = $"{teamLeader.displayName} ({teamLeader.UserIDString})",
                    ["inline"] = false
                }
            };

            string rotatedPlayersText = "";
            foreach (var playerId in rotatedPlayers)
            {
                var playerName = covalence.Players.FindPlayerById(playerId.ToString())?.Name ?? playerId.ToString();
                rotatedPlayersText += $"• {playerName} ({playerId})\n";
            }

            fields.Add(new Dictionary<string, object>
            {
                ["name"] = $"Rotated Players ({rotatedPlayers.Count})",
                ["value"] = string.IsNullOrEmpty(rotatedPlayersText) ? "None" : rotatedPlayersText,
                ["inline"] = false
            });

            string remainingPlayersText = "";
            int remainingCount = 0;
            foreach (var memberID in team.members)
            {
                if (!rotatedPlayers.Contains(memberID))
                {
                    remainingCount++;
                    var memberName = covalence.Players.FindPlayerById(memberID.ToString())?.Name ?? memberID.ToString();
                    remainingPlayersText += $"• {memberName} ({memberID})\n";
                }
            }

            fields.Add(new Dictionary<string, object>
            {
                ["name"] = $"Remaining Team Members ({remainingCount})",
                ["value"] = string.IsNullOrEmpty(remainingPlayersText) ? "None" : remainingPlayersText,
                ["inline"] = false
            });

            fields.Add(new Dictionary<string, object>
            {
                ["name"] = "De-authorization Summary",
                ["value"] = $"**TCs:** {tcCount}\n**Locks:** {lockCount}\n**Turrets:** {turretCount}\n**Bags/Beds:** {bagCount}",
                ["inline"] = false
            });

            SendDiscordWebhook(
                "🔄 Team Rotation Executed",
                $"Team leader **{teamLeader.displayName}** has rotated {rotatedPlayers.Count} offline player(s) from their team.",
                fields,
                _config.Discord.WebhookColor
            );
        }

        private void SendAdminCommandWebhook(BasePlayer admin, string command, string targetPlayer = null, ulong targetId = 0)
        {
            if (!_config.Discord.EnableAdminAlerts || string.IsNullOrEmpty(_config.Discord.WebhookUrl)) return;

            string cooldownKey = $"{admin.userID}:{command.Replace("/", "").Replace(".", "")}";

            if (_lastWebhookTime.TryGetValue(cooldownKey, out DateTime lastTime))
            {
                var timeSinceLastWebhook = DateTime.UtcNow - lastTime;
                if (timeSinceLastWebhook.TotalSeconds < _config.Discord.WebhookCooldown)
                {
                    if (_config.DebugLogging)
                        Puts($"Webhook cooldown active for {admin.displayName} on {command} - {_config.Discord.WebhookCooldown - timeSinceLastWebhook.TotalSeconds:F1}s remaining");
                    return;
                }
            }

            _lastWebhookTime[cooldownKey] = DateTime.UtcNow;

            var fields = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Admin",
                    ["value"] = $"{admin.displayName} ({admin.UserIDString})",
                    ["inline"] = true
                },
                new Dictionary<string, object>
                {
                    ["name"] = "Command",
                    ["value"] = $"`{command}`",
                    ["inline"] = true
                }
            };

            if (!string.IsNullOrEmpty(targetPlayer))
            {
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = "Target Player",
                    ["value"] = $"{targetPlayer} ({targetId})",
                    ["inline"] = false
                });
            }

            SendDiscordWebhook(
                "⚙️ Admin Command Executed",
                $"Admin **{admin.displayName}** executed a TeamRotation command.",
                fields,
                3447003
            );
        }

        private void SendBannedAttemptWebhook(BasePlayer player, ulong teamLeaderId, string attemptType)
        {
            if (!_config.Discord.EnableBannedAttempts || string.IsNullOrEmpty(_config.Discord.WebhookUrl)) return;

            string cooldownKey = $"{player.userID}:{attemptType.Replace(" ", "")}";

            if (_lastWebhookTime.TryGetValue(cooldownKey, out DateTime lastTime))
            {
                var timeSinceLastWebhook = DateTime.UtcNow - lastTime;
                if (timeSinceLastWebhook.TotalSeconds < _config.Discord.WebhookCooldown)
                {
                    if (_config.DebugLogging)
                        Puts($"Webhook cooldown active for {player.displayName} on {attemptType} - {_config.Discord.WebhookCooldown - timeSinceLastWebhook.TotalSeconds:F1}s remaining");
                    return;
                }
            }

            _lastWebhookTime[cooldownKey] = DateTime.UtcNow;

            var teamLeaderName = covalence.Players.FindPlayerById(teamLeaderId.ToString())?.Name ?? teamLeaderId.ToString();

            var fields = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["name"] = "Banned Player",
                    ["value"] = $"{player.displayName} ({player.UserIDString})",
                    ["inline"] = false
                },
                new Dictionary<string, object>
                {
                    ["name"] = "Team Leader",
                    ["value"] = $"{teamLeaderName} ({teamLeaderId})",
                    ["inline"] = false
                },
                new Dictionary<string, object>
                {
                    ["name"] = "Attempt Type",
                    ["value"] = attemptType,
                    ["inline"] = false
                }
            };

            SendDiscordWebhook(
                "⚠️ Banned Player Attempt",
                $"Rotated player **{player.displayName}** attempted to {attemptType.ToLower()}.",
                fields,
                15158332
            );
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            LoadData();

            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionAdmin, this);
            lang.RegisterMessages(_config.Messages, this);

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            _codeLockWhitelistField = typeof(CodeLock).GetField("whitelistPlayers", bindingFlags);
            _codeLockGuestlistField = typeof(CodeLock).GetField("guestPlayers", bindingFlags);

            if (_config.DebugLogging)
            {
                Puts($"CodeLock reflection - whitelistPlayers: {(_codeLockWhitelistField != null ? "Found" : "NOT FOUND")}");
                Puts($"CodeLock reflection - guestPlayers: {(_codeLockGuestlistField != null ? "Found" : "NOT FOUND")}");
            }

            timer.Every(300f, () => CleanupWebhookCooldowns());
        }

        private void CleanupWebhookCooldowns()
        {
            var expiredEntries = _lastWebhookTime
                .Where(x => (DateTime.UtcNow - x.Value).TotalSeconds > _config.Discord.WebhookCooldown * 2)
                .Select(x => x.Key)
                .ToList();

            foreach (var key in expiredEntries)
            {
                _lastWebhookTime.Remove(key);
            }

            if (_config.DebugLogging && expiredEntries.Count > 0)
                Puts($"Cleaned up {expiredEntries.Count} expired webhook cooldown entries");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UIMainName);
            }

            _processingPlayers.Clear();
            _cupboardsCache.Clear();
            _entitiesCache.Clear();
            _turretsCache.Clear();
            _bagsCache.Clear();
            _lastWebhookTime.Clear();
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;

            if (!entity.ShortPrefabName.ToLower().Contains("cupboard.tool")) return;

            if (_config.DebugLogging)
                Puts($"Player {player.displayName} opened TC");

            var team = RelationshipManager.ServerInstance.FindPlayersTeam(player.userID);
            bool isLeader = team != null && team.teamLeader == player.userID;

            if (_config.DebugLogging)
                Puts($"  - In team: {team != null}, Team leader: {isLeader}");

            if (!isLeader)
            {
                if (_config.DebugLogging)
                    Puts($"  - Not team leader, no button shown");
                return;
            }

            if (_config.RequirePermission)
            {
                bool hasPerm = permission.UserHasPermission(player.UserIDString, PermissionUse);
                if (_config.DebugLogging)
                    Puts($"  - Has permission: {hasPerm}");

                if (!hasPerm)
                {
                    if (_config.DebugLogging)
                        Puts($"  - No permission, no button shown");
                    return;
                }
            }

            if (_config.DebugLogging)
                Puts($"  - Creating UI button for {player.displayName}");

            var tc = entity as BuildingPrivlidge;
            if (tc == null) return;
            CreateRotationUI(player, tc);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            if (entity.ShortPrefabName.ToLower().Contains("cupboard.tool"))
            {
                CuiHelper.DestroyUi(player, UIMainName);
            }
        }

        private object OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (!_config.Features.BanSystem) return null;
            if (privilege == null || player == null) return null;

            ulong ownerId = privilege.OwnerID;
            if (IsPlayerBanned(ownerId, player.userID))
            {
                player.ChatMessage(lang.GetMessage("BannedFromTeam", this, player.UserIDString));
                SendBannedAttemptWebhook(player, ownerId, "Authorize to Tool Cupboard");
                return false;
            }

            return null;
        }

        private object OnTurretAuthorize(AutoTurret turret, BasePlayer player)
        {
            if (!_config.Features.BanSystem) return null;
            if (turret == null || player == null) return null;

            ulong ownerId = turret.OwnerID;
            if (IsPlayerBanned(ownerId, player.userID))
            {
                player.ChatMessage(lang.GetMessage("BannedFromTeam", this, player.UserIDString));
                SendBannedAttemptWebhook(player, ownerId, "Authorize to Auto Turret");
                return false;
            }

            return null;
        }

        private object OnCodeEntered(CodeLock codeLock, BasePlayer player, string code)
        {
            if (!_config.Features.BanSystem) return null;
            if (codeLock == null || player == null) return null;

            var parentEntity = codeLock.GetParentEntity();
            ulong ownerId = codeLock.OwnerID != 0 ? codeLock.OwnerID : (parentEntity != null ? parentEntity.OwnerID : 0);

            if (ownerId != 0 && IsPlayerBanned(ownerId, player.userID))
            {
                player.ChatMessage(lang.GetMessage("BannedFromTeam", this, player.UserIDString));
                SendBannedAttemptWebhook(player, ownerId, "Authorize to Code Lock");
                return false;
            }

            return null;
        }

        private object OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (!_config.Features.BlockTeamRejoin || !_config.Features.BanSystem) return null;
            if (team == null || player == null) return null;

            ulong teamLeaderId = team.teamLeader;
            if (IsPlayerBanned(teamLeaderId, player.userID))
            {
                player.ChatMessage(lang.GetMessage("BannedFromTeamRejoin", this, player.UserIDString));
                SendBannedAttemptWebhook(player, teamLeaderId, "Rejoin Team");
                return false;
            }

            return null;
        }

        #endregion

        #region UI

        private void CreateRotationUI(BasePlayer player, BuildingPrivlidge tc)
        {
            CuiHelper.DestroyUi(player, UIMainName);

            var elements = new CuiElementContainer();

            elements.Add(new CuiButton
            {
                Button = { Color = _config.UI.ButtonColor, Command = $"teamrotation.rotate {tc.net.ID.Value}" },
                Text = { Text = lang.GetMessage("ButtonText", this, player.UserIDString), Font = "robotocondensed-bold.ttf", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = _config.UI.TextColor },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = _config.UI.OffsetMin, OffsetMax = _config.UI.OffsetMax }
            }, "Overlay", UIMainName);

            CuiHelper.AddUi(player, elements);

            if (_config.DebugLogging)
                Puts($"  - UI created and sent to player");
        }

        #endregion

        #region Commands

        [ConsoleCommand("teamrotation.rotate")]
        private void CmdRotatePlayers(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (_config.RequirePermission && !permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse(arg.Args[0], out ulong netID)) return;

            var tc = BaseNetworkable.serverEntities.Find(new NetworkableId(netID)) as BuildingPrivlidge;
            if (tc == null || tc.IsDestroyed)
            {
                player.ChatMessage(lang.GetMessage("ProcessingError", this, player.UserIDString));
                return;
            }

            if (Vector3.Distance(player.transform.position, tc.transform.position) > MaxRotateDistance)
            {
                player.ChatMessage(lang.GetMessage("ProcessingError", this, player.UserIDString));
                return;
            }

            PerformRotation(player, tc);
        }

        [ChatCommand("rotateoffline")]
        private void ChatCmdRotate(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            _cupboardsCache.Clear();
            Vis.Entities(player.transform.position, 3f, _cupboardsCache);
            var tc = _cupboardsCache.Count > 0 ? _cupboardsCache[0] : null;

            if (tc == null)
            {
                player.ChatMessage("No Tool Cupboard found nearby.");
                return;
            }

            SendAdminCommandWebhook(player, "/rotateoffline");
            PerformRotation(player, tc);
        }

        [ChatCommand("rotation.bans")]
        private void ChatCmdViewBans(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            if (_rotatedData.TeamLeaderBans.Count == 0)
            {
                player.ChatMessage("No banned players found.");
                return;
            }

            SendAdminCommandWebhook(player, "/rotation.bans");
            player.ChatMessage("=== Rotation Ban List ===");
            foreach (var kvp in _rotatedData.TeamLeaderBans)
            {
                var leaderName = covalence.Players.FindPlayerById(kvp.Key.ToString())?.Name ?? kvp.Key.ToString();
                player.ChatMessage($"Team Leader: {leaderName}");
                foreach (var bannedId in kvp.Value)
                {
                    var bannedName = covalence.Players.FindPlayerById(bannedId.ToString())?.Name ?? bannedId.ToString();
                    player.ChatMessage($"  - Banned: {bannedName} ({bannedId})");
                }
            }
        }

        [ChatCommand("rotation.clearbans")]
        private void ChatCmdClearBans(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            _rotatedData.TeamLeaderBans.Clear();
            SaveData();
            SendAdminCommandWebhook(player, "/rotation.clearbans");
            player.ChatMessage("All rotation bans have been cleared (server wipe).");
        }

        [ChatCommand("rotation.unban")]
        private void ChatCmdUnbanPlayer(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                player.ChatMessage(lang.GetMessage("NoPermission", this, player.UserIDString));
                return;
            }

            if (args.Length < 1)
            {
                player.ChatMessage("Usage: /rotation.unban <name or steamID>");
                return;
            }

            string searchTerm = string.Join(" ", args);
            ulong targetId = 0;

            if (!ulong.TryParse(searchTerm, out targetId))
            {
                var foundPlayer = covalence.Players.FindPlayer(searchTerm);
                if (foundPlayer == null)
                {
                    player.ChatMessage($"Player '{searchTerm}' not found.");
                    return;
                }
                targetId = ulong.Parse(foundPlayer.Id);
            }

            int removedCount = 0;
            var leadersToRemove = new List<ulong>();

            foreach (var kvp in _rotatedData.TeamLeaderBans)
            {
                if (kvp.Value.Remove(targetId))
                {
                    removedCount++;

                    if (kvp.Value.Count == 0)
                    {
                        leadersToRemove.Add(kvp.Key);
                    }
                }
            }

            foreach (var leaderId in leadersToRemove)
            {
                _rotatedData.TeamLeaderBans.Remove(leaderId);
            }

            var targetName = covalence.Players.FindPlayerById(targetId.ToString())?.Name ?? targetId.ToString();

            if (removedCount > 0)
            {
                SaveData();
                SendAdminCommandWebhook(player, "/rotation.unban", targetName, targetId);
                player.ChatMessage($"Unbanned '{targetName}' from {removedCount} team(s).");
            }
            else
            {
                player.ChatMessage($"'{targetName}' was not found in any ban lists.");
            }
        }

        #endregion

        #region Core Logic

        private void PerformRotation(BasePlayer player, BuildingPrivlidge tc)
        {
            if (_processingPlayers.Contains(player.userID))
            {
                player.ChatMessage(lang.GetMessage("AlreadyProcessing", this, player.UserIDString));
                return;
            }

            _processingPlayers.Add(player.userID);

            try
            {
                if (!IsTeamLeader(player))
                {
                    player.ChatMessage(lang.GetMessage("NotTeamLeader", this, player.UserIDString));
                    return;
                }

                var team = RelationshipManager.ServerInstance.FindPlayersTeam(player.userID);
                if (team == null)
                {
                    player.ChatMessage(lang.GetMessage("NoTeam", this, player.UserIDString));
                    return;
                }

                var offlinePlayers = new HashSet<ulong>();
                foreach (var memberID in team.members)
                {
                    if (memberID == player.userID) continue;

                    var member = BasePlayer.FindByID(memberID);
                    if (member == null || !member.IsConnected)
                    {
                        offlinePlayers.Add(memberID);
                    }
                }

                if (offlinePlayers.Count == 0)
                {
                    player.ChatMessage(lang.GetMessage("NoOfflinePlayers", this, player.UserIDString));
                    return;
                }

                player.ChatMessage(string.Format(lang.GetMessage("RotationStarted", this, player.UserIDString), offlinePlayers.Count));

                int tcCount = 0, lockCount = 0, turretCount = 0, bagCount = 0;
                Vector3 tcPosition = tc.transform.position;
                float radius = _config.BagDeletionRadius;

                if (_config.Features.DeAuthTCs)
                {
                    _cupboardsCache.Clear();
                    Vis.Entities(tcPosition, radius, _cupboardsCache);

                    if (_config.DebugLogging)
                        Puts($"  - Found {_cupboardsCache.Count} TC(s) in radius");

                    foreach (var cupboard in _cupboardsCache)
                    {
                        if (DeAuthFromTC(cupboard, offlinePlayers)) tcCount++;
                    }
                }

                if (_config.Features.DeAuthCodeLocks || _config.Features.TransferKeyLocks)
                {
                    _entitiesCache.Clear();
                    Vis.Entities(tcPosition, radius, _entitiesCache);

                    int codeLockCount = 0;
                    int keyLockCount = 0;
                    foreach (var entity in _entitiesCache)
                    {
                        if (entity == null || entity.IsDestroyed) continue;

                        var lockSlot = entity.GetSlot(BaseEntity.Slot.Lock);
                        if (lockSlot == null) continue;

                        if (_config.Features.DeAuthCodeLocks)
                        {
                            var codeLock = lockSlot as CodeLock;
                            if (codeLock != null)
                            {
                                codeLockCount++;
                                if (DeAuthFromCodeLock(codeLock, offlinePlayers)) lockCount++;
                                continue;
                            }
                        }

                        if (_config.Features.TransferKeyLocks)
                        {
                            var keyLock = lockSlot as KeyLock;
                            if (keyLock != null)
                            {
                                keyLockCount++;
                                if (DeAuthFromKeyLock(keyLock, offlinePlayers, player.userID)) lockCount++;
                            }
                        }
                    }

                    if (_config.DebugLogging)
                        Puts($"  - Found {codeLockCount} code lock(s) and {keyLockCount} key lock(s) in radius");
                }

                if (_config.Features.DeAuthTurrets)
                {
                    _turretsCache.Clear();
                    Vis.Entities(tcPosition, radius, _turretsCache);

                    if (_config.DebugLogging)
                        Puts($"  - Found {_turretsCache.Count} turret(s) in radius");

                    foreach (var turret in _turretsCache)
                    {
                        if (DeAuthFromTurret(turret, offlinePlayers)) turretCount++;
                    }
                }

                if (_config.Features.DeleteBagsBeds)
                {
                    _bagsCache.Clear();
                    Vis.Entities(tcPosition, radius, _bagsCache);

                    if (_config.DebugLogging)
                        Puts($"  - Found {_bagsCache.Count} bag(s)/bed(s) in radius");

                    foreach (var bag in _bagsCache)
                    {
                        if (bag != null && !bag.IsDestroyed && offlinePlayers.Contains(bag.deployerUserID))
                        {
                            bag.Kill();
                            bagCount++;
                        }
                    }
                }

                int kickedCount = 0;
                foreach (var offlinePlayerID in offlinePlayers)
                {
                    if (_config.Features.KickFromTeam)
                    {
                        team.RemovePlayer(offlinePlayerID);
                        kickedCount++;
                    }

                    if (_config.Features.BanSystem)
                    {
                        BanPlayer(player.userID, offlinePlayerID);

                        if (_config.DebugLogging)
                            Puts($"  - Kicked player {offlinePlayerID} from team and added to ban list");
                    }
                    else if (_config.DebugLogging && _config.Features.KickFromTeam)
                    {
                        Puts($"  - Kicked player {offlinePlayerID} from team");
                    }
                }

                SendRotationWebhook(player, offlinePlayers, team, tcCount, lockCount, turretCount, bagCount);

                player.ChatMessage(string.Format(lang.GetMessage("RotationComplete", this, player.UserIDString),
                    offlinePlayers.Count, tcCount, lockCount, turretCount, bagCount, kickedCount));
            }
            catch (Exception ex)
            {
                PrintError($"Error in PerformRotation: {ex}");
                player.ChatMessage(lang.GetMessage("ProcessingError", this, player.UserIDString));
            }
            finally
            {
                _processingPlayers.Remove(player.userID);
            }
        }

        private bool DeAuthFromTC(BuildingPrivlidge cupboard, HashSet<ulong> playerIDs)
        {
            if (cupboard == null || cupboard.IsDestroyed || cupboard.authorizedPlayers.Count == 0) return false;

            int removedCount = cupboard.authorizedPlayers.RemoveWhere(playerIDs.Contains);

            if (removedCount > 0)
            {
                cupboard.SendNetworkUpdate();
                return true;
            }

            return false;
        }

        private bool DeAuthFromCodeLock(CodeLock codeLock, HashSet<ulong> playerIDs)
        {
            if (codeLock == null || codeLock.IsDestroyed) return false;

            bool modified = false;
            int totalRemoved = 0;

            if (_codeLockWhitelistField != null)
            {
                var whitelist = _codeLockWhitelistField.GetValue(codeLock) as List<ulong>;
                if (whitelist != null && whitelist.Count > 0)
                {
                    int removedCount = 0;
                    for (int i = whitelist.Count - 1; i >= 0; i--)
                    {
                        if (playerIDs.Contains(whitelist[i]))
                        {
                            whitelist.RemoveAt(i);
                            modified = true;
                            removedCount++;
                        }
                    }
                    totalRemoved += removedCount;

                    if (_config.DebugLogging && removedCount > 0)
                        Puts($"  - CodeLock whitelistPlayers: Removed {removedCount} player(s)");
                }
            }

            if (_codeLockGuestlistField != null)
            {
                var guestlist = _codeLockGuestlistField.GetValue(codeLock) as List<ulong>;
                if (guestlist != null && guestlist.Count > 0)
                {
                    int removedCount = 0;
                    for (int i = guestlist.Count - 1; i >= 0; i--)
                    {
                        if (playerIDs.Contains(guestlist[i]))
                        {
                            guestlist.RemoveAt(i);
                            modified = true;
                            removedCount++;
                        }
                    }
                    totalRemoved += removedCount;

                    if (_config.DebugLogging && removedCount > 0)
                        Puts($"  - CodeLock guestPlayers: Removed {removedCount} player(s)");
                }
            }

            if (modified)
            {
                codeLock.SendNetworkUpdate();
                if (_config.DebugLogging && totalRemoved > 0)
                    Puts($"  - CodeLock total: Removed {totalRemoved} player(s)");
            }

            return modified;
        }

        private bool DeAuthFromKeyLock(KeyLock keyLock, HashSet<ulong> playerIDs, ulong newOwnerId)
        {
            if (keyLock == null || keyLock.IsDestroyed) return false;

            if (playerIDs.Contains(keyLock.OwnerID))
            {
                try
                {
                    ulong previousOwnerId = keyLock.OwnerID;
                    keyLock.OwnerID = newOwnerId;
                    keyLock.SendNetworkUpdate();

                    if (_config.DebugLogging)
                        Puts($"  - KeyLock: Changed owner from {previousOwnerId} to {newOwnerId} (team leader)");

                    return true;
                }
                catch (Exception ex)
                {
                    if (_config.DebugLogging)
                        Puts($"  - KeyLock: Error changing owner: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        private bool DeAuthFromTurret(AutoTurret turret, HashSet<ulong> playerIDs)
        {
            if (turret == null || turret.IsDestroyed || turret.authorizedPlayers.Count == 0) return false;

            int removedCount = turret.authorizedPlayers.RemoveWhere(playerIDs.Contains);

            if (removedCount > 0)
            {
                turret.SendNetworkUpdate();
                return true;
            }

            return false;
        }

        #endregion

        #region Helpers

        private bool IsTeamLeader(BasePlayer player)
        {
            var team = RelationshipManager.ServerInstance.FindPlayersTeam(player.userID);
            return team != null && team.teamLeader == player.userID;
        }

        #endregion
    }
}
