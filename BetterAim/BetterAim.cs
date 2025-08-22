using System;
using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.IO;
using Terraria.Utilities;
using com.tiberiumfusion.ttplugins.HarmonyPlugins;

namespace BetterAim.Plugins
{
    public enum TargetType { None, NPC, Player }

    public struct LockedTargetInfo
    {
        public TargetType Type;
        public int Index;
        public static LockedTargetInfo None => new LockedTargetInfo { Type = TargetType.None, Index = -1 };
    }

    public enum TargetingMode { Persistent, ClosestToPlayer, LowestHealth }

    public class BetterAim : HPlugin
    {
        // Functional State
        private static bool _aimlockActive = false;
        private static LockedTargetInfo _lockedTarget = LockedTargetInfo.None;
        private static bool _isUserAimKeyHeld = false;
        private static bool _isTriggerbotKeyHeld = false;
        private static bool _triggerbotCanUse = false;
        private static int _celebMk2RocketTypeCounter = 0;

        // Configuration
        private static string _customAimKey = "MouseRight";
        private static string _customTriggerbotKey = "MouseRight";
        private static float _aimlockRadiusTiles = 10f;
        private static bool _pvpModeActive = false;
        private static bool _aimlockFunctionEnabled = true;
        private static TargetingMode _currentTargetingMode = TargetingMode.Persistent;
        private static bool _triggerbotActive = false;
        private static bool _debugActive = false;
        private static bool _debugTargetDummyMode = false;

        // UI Message Control
        private static bool _aimlockActivatedMessageShown = false;
        private static bool _noTargetFoundMessageShown = false;

        // Data-Driven Logic
        private static Dictionary<int, Func<Vector2, Vector2, Vector2, float, int, int, bool, Vector2>> _predictionMap;

        public override void Initialize()
        {
            Identity.PluginName = "BetterAim";
            Identity.PluginDescription = "Provides an aimlock feature with multiple targeting modes, prediction for every weapon, keybinds, and a triggerbot";
            Identity.PluginAuthor = "X";
            Identity.PluginVersion = new Version("2.1.2");
            HasPersistentSavedata = false;

            InitializePredictionLogic();
        }

        public override void ConfigurationLoaded(bool successfulConfigLoadFromDisk) { }

        public override void PrePatch()
        {
            CreateHPatchOperation("Terraria.GameInput.PlayerInput", "MouseInput", "MouseInput_Postfix", HPatchLocation.Postfix);
            CreateHPatchOperation("Terraria.Main", "Update", "MainUpdate_Postfix", HPatchLocation.Postfix);
            CreateHPatchOperation("Terraria.Player", "ItemCheck", "ItemCheck_Prefix", HPatchLocation.Prefix);
            CreateHPatchOperation("Terraria.Projectile", "NewProjectile", "NewProjectile_Postfix", HPatchLocation.Postfix);
        }

        public static void MouseInput_Postfix()
        {
            if (!_aimlockFunctionEnabled || !_aimlockActive || _lockedTarget.Type == TargetType.None) return;

            Vector2 targetCenter, targetVelocity;
            if (_lockedTarget.Type == TargetType.NPC && Main.npc.IndexInRange(_lockedTarget.Index) && Main.npc[_lockedTarget.Index].active)
            {
                targetCenter = Main.npc[_lockedTarget.Index].Center;
                targetVelocity = Main.npc[_lockedTarget.Index].velocity;
            }
            else if (_lockedTarget.Type == TargetType.Player && Main.player.IndexInRange(_lockedTarget.Index) && Main.player[_lockedTarget.Index].active)
            {
                targetCenter = Main.player[_lockedTarget.Index].Center;
                targetVelocity = Main.player[_lockedTarget.Index].velocity;
            }
            else return;

            Vector2 finalAimPosition = targetCenter;
            Vector2 shooterPosition = Main.LocalPlayer.RotatedRelativePoint(Main.LocalPlayer.MountedCenter, reverseRotation: true);
            Player player = Main.LocalPlayer;
            Item heldItem = Main.LocalPlayer.HeldItem;

            if (heldItem != null && (heldItem.damage > 0 || heldItem.type == ItemID.CoinGun) && heldItem.shootSpeed > 0f)
            {
                int projToShoot = heldItem.shoot;
                float shootSpeed = heldItem.shootSpeed;
                bool canShoot = true;
                int damage = heldItem.damage;
                float knockBack = heldItem.knockBack;
                if (heldItem.useAmmo != AmmoID.None) Main.LocalPlayer.PickAmmo(heldItem, ref projToShoot, ref shootSpeed, ref canShoot, ref damage, ref knockBack, out _, true);

                projToShoot = GetFinalProjectileID(heldItem.type, projToShoot);

                if (heldItem.type == ItemID.DD2BetsyBow)
                {
                    shootSpeed *= 0.8f;
                }
                if (heldItem.type == ItemID.ApprenticeStaffT3)
                {
                    shootSpeed = (shootSpeed * 0.8f) + 1f;
                    Vector2 aimDirection = (Main.MouseWorld - shooterPosition).SafeNormalize(Vector2.UnitY);

                    Vector2 spawnOffset = aimDirection * 40f * heldItem.scale;

                    if (Collision.CanHit(shooterPosition, 0, 0, shooterPosition + spawnOffset, 0, 0))
                    {
                        shooterPosition += spawnOffset;
                    }
                }
                if (heldItem.melee)
                {
                    shootSpeed /= player.meleeSpeed;
                }
                Projectile dummyProjectile = new Projectile();
                dummyProjectile.SetDefaults(projToShoot);
                bool projectileCollides = dummyProjectile.tileCollide;

                if (_predictionMap.TryGetValue(heldItem.type, out var predictionFunc))
                {
                    finalAimPosition = predictionFunc(shooterPosition, targetCenter, targetVelocity, shootSpeed, projToShoot, dummyProjectile.extraUpdates, projectileCollides);
                }
                else // Default logic for unmapped items
                {
                    if (dummyProjectile.aiStyle == 2)
                    {
                        finalAimPosition = CalculateThrownPredictedTargetPosition(shooterPosition, targetCenter, targetVelocity, shootSpeed, projToShoot, dummyProjectile.extraUpdates, projectileCollides);
                    }
                    else if (dummyProjectile.aiStyle == 9 || heldItem.summon || dummyProjectile.aiStyle == 99)
                    {
                        finalAimPosition = targetCenter;
                    }
                    else if (heldItem.useAmmo == AmmoID.Bullet || heldItem.useAmmo == AmmoID.Stake || heldItem.useAmmo == AmmoID.Gel || heldItem.useAmmo == AmmoID.FallenStar || heldItem.useAmmo == AmmoID.Coin || heldItem.useAmmo == AmmoID.Sand)
                    {
                        finalAimPosition = CalculatePrecisePredictedTargetPosition(shooterPosition, targetCenter, targetVelocity, shootSpeed * (1f + dummyProjectile.extraUpdates));
                    }
                    else if (heldItem.useAmmo == AmmoID.Arrow || heldItem.useAmmo == AmmoID.CandyCorn || heldItem.useAmmo == AmmoID.Dart || heldItem.useAmmo == AmmoID.StyngerBolt || heldItem.useAmmo == AmmoID.JackOLantern || heldItem.useAmmo == AmmoID.NailFriendly || heldItem.useAmmo == AmmoID.Flare || dummyProjectile.aiStyle == 1)
                    {
                        finalAimPosition = CalculateParabolicPredictedTargetPosition(shooterPosition, targetCenter, targetVelocity, shootSpeed, projToShoot, dummyProjectile.extraUpdates, projectileCollides);
                    }
                    else if (heldItem.useAmmo == AmmoID.Rocket)
                    {
                        finalAimPosition = CalculateRocketPredictedTargetPosition(shooterPosition, targetCenter, targetVelocity, shootSpeed, projToShoot, dummyProjectile.extraUpdates, projectileCollides);
                    }
                    else
                    {
                        finalAimPosition = CalculatePrecisePredictedTargetPosition(shooterPosition, targetCenter, targetVelocity, shootSpeed * (1f + dummyProjectile.extraUpdates));
                    }
                }
            }

            if (heldItem.melee && heldItem.shootSpeed > 0f)
            {
                Vector2 aimDirection = finalAimPosition - player.RotatedRelativePoint(player.MountedCenter, reverseRotation: true);
                player.direction = (aimDirection.X > 0) ? 1 : -1;
                player.itemRotation = aimDirection.ToRotation();
                if (player.direction < 0)
                {
                    player.itemRotation += (float)Math.PI;
                }
            }

            Vector2 screenPosition = finalAimPosition - Main.screenPosition;

            if (Main.LocalPlayer.gravDir == -1f)
            {
                Vector2 playerScreenPos = Main.LocalPlayer.Center - Main.screenPosition;
                Vector2 aimOffset = screenPosition - playerScreenPos;
                aimOffset.Y *= Main.LocalPlayer.gravDir;
                screenPosition = playerScreenPos + aimOffset;
            }

            Terraria.GameInput.PlayerInput.MouseX = (int)Math.Round(screenPosition.X);
            Terraria.GameInput.PlayerInput.MouseY = (int)Math.Round(screenPosition.Y);
        }

        public static void MainUpdate_Postfix(GameTime gameTime)
        {
            HandleKeybinds();
            if (!_aimlockFunctionEnabled) return;

            HandleAimKeyInput();

            bool anyUIOpen = Main.playerInventory || Main.gameMenu || Main.ingameOptionsWindow || Main.mapFullscreen || Main.LocalPlayer.talkNPC != -1 || Main.LocalPlayer.sign != -1 || Main.LocalPlayer.chest != -1;
            if (anyUIOpen)
            {
                if (_aimlockActive) ResetAimlockState(true, "Aimlock Deactivated (UI Open)");
                return;
            }

            if (_isUserAimKeyHeld)
            {
                if (_currentTargetingMode != TargetingMode.Persistent || !IsTargetValid(_lockedTarget)) FindAndSetTarget();
                if (_lockedTarget.Type != TargetType.None)
                {
                    _aimlockActive = true;
                    if (!_aimlockActivatedMessageShown)
                    {
                        Main.NewText($"Aimlock Activated on {GetTargetName(_lockedTarget)}", 50, 205, 50);
                        _aimlockActivatedMessageShown = true;
                    }
                    _noTargetFoundMessageShown = false;
                }
                else
                {
                    _aimlockActive = false;
                    if (!_noTargetFoundMessageShown)
                    {
                        Main.NewText("No target found within radius.", 255, 255, 0);
                        _noTargetFoundMessageShown = true;
                    }
                }
            }
            else if (_aimlockActive)
            {
                ResetAimlockState(true, "Aimlock Deactivated");
            }

            HandleTriggerbot();
        }

        #region Initialization
        private static void InitializePredictionLogic()
        {
            Func<Vector2, Vector2, Vector2, float, int, int, bool, Vector2> predictParabolic =
                (s, tc, tv, ss, id, upd, collides) => CalculateParabolicPredictedTargetPosition(s, tc, tv, ss, id, upd, collides);

            _predictionMap = new Dictionary<int, Func<Vector2, Vector2, Vector2, float, int, int, bool, Vector2>>
            {
                { ItemID.BloodRainBow, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss * 0.675f, id, upd) },
                { ItemID.PiranhaGun, (s, tc, tv, ss, id, upd, collides) => PredictPiranhaGun(s, tc, tv, ss, id, upd) },
                { ItemID.Toxikarp, (s, tc, tv, ss, id, upd, collides) => CalculateParabolicPredictedTargetPosition_Toxikarp(s, tc, tv, ss, collides) },
                { ItemID.Harpoon, (s, tc, tv, ss, id, upd, collides) => CalculateHarpoonPredictedTargetPosition(s, tc, tv, ss, collides) },
                { ItemID.SnowmanCannon, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, upd) },
                { ItemID.ElectrosphereLauncher, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, upd) },
                { ItemID.FireworksLauncher, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, upd) }, // Celebration
                { ItemID.Celeb2, (s, tc, tv, ss, id, upd, collides) => PredictCelebrationMk2(s, tc, tv, ss, id, upd, collides) },
                { ItemID.PaperAirplaneA, (s, tc, tv, ss, id, upd, collides) => CalculatePaperAirplanePredictedTargetPosition(s, tc, tv, ss, Main.WindForVisuals, collides) },
                { ItemID.PaperAirplaneB, (s, tc, tv, ss, id, upd, collides) => CalculatePaperAirplanePredictedTargetPosition(s, tc, tv, ss, Main.WindForVisuals, collides) },
                { ItemID.PainterPaintballGun, predictParabolic },
                { ItemID.AleThrowingGlove, predictParabolic },
                { ItemID.Beenade, predictParabolic },
                { ItemID.Grenade, predictParabolic },
                { ItemID.StickyGrenade, predictParabolic },
                { ItemID.BouncyGrenade, predictParabolic },
                { ItemID.PartyGirlGrenade, predictParabolic },
                { ItemID.SpikyBall, predictParabolic },
                { ItemID.Javelin, predictParabolic },
                { ItemID.BoneJavelin, predictParabolic },
                { ItemID.MolotovCocktail, predictParabolic },
                { ItemID.DaedalusStormbow, (s, tc, tv, ss, id, upd, collides) => PredictSkyfall(s, Main.LocalPlayer.velocity, tc, tv, ss, 800f, 3, id, upd) },
                { ItemID.IceSickle, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 0.95f, extraUpdates, float.MaxValue, collides) },
                { ItemID.DeathSickle, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 0.96f, extraUpdates, float.MaxValue, collides) },
                { ItemID.RocketLauncher, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 1.1f, extraUpdates, 15f, collides) },
                { ItemID.ChainKnife, predictParabolic },
                { ItemID.TrueNightsEdge, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.Seedler, predictParabolic},
                { ItemID.TerraBlade, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.Meowmere, predictParabolic},
                { ItemID.StarWrath, (s, tc, tv, ss, id, upd, collides) => PredictSkyfall_Linear(s, Main.LocalPlayer.velocity, tc, tv, ss, 700f, 200f, upd) },
                { ItemID.Zenith, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.SolarEruption, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.VampireKnives, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, upd) },
                { ItemID.FlowerofFire, predictParabolic},
                { ItemID.FlowerofFrost, predictParabolic},
                { ItemID.DemonScythe, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 1.06f, extraUpdates, float.MaxValue, collides, 30, 70) },
                { ItemID.AquaScepter, predictParabolic},
                { ItemID.SoulDrain, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.MeteorStaff, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, upd) },
                { ItemID.CrystalVileShard, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, 14.5f * 16f / 45f, id, upd) },
                { ItemID.NettleBurst, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, 384f / 121f, id, upd) },
                { ItemID.Razorpine, predictParabolic},
                { ItemID.BlizzardStaff, (s, tc, tv, ss, id, upd, collides) => PredictSkyfall(s, Main.LocalPlayer.velocity, tc, tv, ss, 700f, 3, id, upd) },
                { ItemID.WaspGun, (s, tc, tv, ss, id, upd, collides) => PredictStraightLineWeapon(s, tc, tv, ss, id, 0) },
                { ItemID.ApprenticeStaffT3, PredictBetsysWrath},
                { ItemID.RainbowGun, PredictRainbowGun},
                { ItemID.LaserMachinegun, (s, tc, tv, ss, id, upd, collides) => CalculateParabolicPredictedTargetPosition(s, tc, tv, 14f, id, 2, collides) },
                { ItemID.ChargedBlasterCannon, PredictChargedBlasterCannon},
                { ItemID.BubbleGun, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 0.96f, extraUpdates, float.MaxValue, collides) },
                { ItemID.LeafBlower, PredictLeafBlower },
                { ItemID.CursedFlames, predictParabolic},
                { ItemID.GoldenShower, predictParabolic},
                { ItemID.CrystalStorm, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 0.985f, extraUpdates, float.MaxValue, collides) },
                { ItemID.MagnetSphere, PredictMagnetSphere },
                { ItemID.RazorbladeTyphoon, PredictRazorbladeTyphoon },
                { ItemID.LunarFlareBook, (s, tc, tv, ss, id, upd, collides) => PredictSkyfall(s, Main.LocalPlayer.velocity, tc, tv, ss / 2f, 700f, 2, id, upd) },
                { ItemID.MedusaHead, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.SpiritFlame, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.SharpTears, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos }, // Blood Thorn
                { ItemID.FairyQueenMagicItem, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos }, // Nightglow
                { ItemID.SparkleGuitar, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos }, // Stellar Tune
                { ItemID.LastPrism, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => targetPos },
                { ItemID.NebulaArcanum, (playerPos, targetPos, targetVel, speed, projToShoot, extraUpdates, collides) => CalculateVariableSpeedPredictedTargetPosition(playerPos, targetPos, targetVel, speed, 0.98f, extraUpdates, float.MaxValue, collides, 30, int.MaxValue, 4f) },
            };
        }
        #endregion

        #region State & Input Handling
        private static void HandleKeybinds()
        {
            if (HHelpers.InputReading.IsKeyPressed(Keys.NumPad7))
            {
                _currentTargetingMode = (TargetingMode)(((int)_currentTargetingMode + 1) % Enum.GetNames(typeof(TargetingMode)).Length);
                Main.NewText($"Targeting Mode: {_currentTargetingMode}", 255, 255, 0);
                ResetAimlockState();
            }
            if (HHelpers.InputReading.IsKeyPressed(Keys.NumPad8))
            {
                _aimlockFunctionEnabled = !_aimlockFunctionEnabled;
                Main.NewText($"Aimlock Function: {(_aimlockFunctionEnabled ? "Enabled" : "Disabled")}", _aimlockFunctionEnabled ? (byte)50 : (byte)255, _aimlockFunctionEnabled ? (byte)205 : (byte)0, _aimlockFunctionEnabled ? (byte)50 : (byte)0);
                if (!_aimlockFunctionEnabled) ResetAimlockState(true);
            }
            if (HHelpers.InputReading.IsKeyPressed(Keys.NumPad9))
            {
                _pvpModeActive = !_pvpModeActive;
                Main.NewText($"PvP Target Preference: {(_pvpModeActive ? "Enabled" : "Disabled")}", 255, 255, 0);
                ResetAimlockState();
            }
            if (HHelpers.InputReading.IsKeyPressed(Keys.NumPad0))
            {
                _triggerbotActive = !_triggerbotActive;
                Main.NewText($"Triggerbot: {(_triggerbotActive ? "Enabled" : "Disabled")}", 255, 165, 0);
            }
            if (HHelpers.InputReading.IsKeyPressed(Keys.NumPad4) && _debugActive)
            {
                _debugTargetDummyMode = !_debugTargetDummyMode;
                Main.NewText($"Debug Target Dummy Mode: {(_debugTargetDummyMode ? "Enabled" : "Disabled")}", 100, 149, 237);
                ResetAimlockState();
            }
        }

        private static void ResetAimlockState(bool deactivate = false, string message = "")
        {
            if (deactivate) _aimlockActive = false;
            _lockedTarget = LockedTargetInfo.None;
            _aimlockActivatedMessageShown = false;
            _noTargetFoundMessageShown = false;
            if (!string.IsNullOrEmpty(message) && deactivate)
            {
                Main.NewText(message, 255, 0, 0);
            }
        }

        private static void HandleAimKeyInput()
        {
            MouseState currentMouseState = Mouse.GetState();
            KeyboardState currentKeyboardState = Keyboard.GetState();
            bool keyHeld = false;

            if (_customAimKey.Equals("MouseRight", StringComparison.OrdinalIgnoreCase)) keyHeld = currentMouseState.RightButton == ButtonState.Pressed;
            else if (_customAimKey.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase)) keyHeld = currentMouseState.LeftButton == ButtonState.Pressed;
            else if (Enum.TryParse(_customAimKey, true, out Keys keyboardKey)) keyHeld = currentKeyboardState.IsKeyDown(keyboardKey);

            if (keyHeld && !_isUserAimKeyHeld)
            {
                _isUserAimKeyHeld = true;
                ResetAimlockState();
            }
            else if (!keyHeld && _isUserAimKeyHeld)
            {
                _isUserAimKeyHeld = false;
                if (_aimlockActive) ResetAimlockState(true, "Aimlock Deactivated");
            }
        }

        private static void HandleTriggerbot()
        {
            MouseState currentMouseState = Mouse.GetState();
            KeyboardState currentKeyboardState = Keyboard.GetState();
            _isTriggerbotKeyHeld = false;

            if (_customTriggerbotKey.Equals("MouseRight", StringComparison.OrdinalIgnoreCase)) _isTriggerbotKeyHeld = currentMouseState.RightButton == ButtonState.Pressed;
            else if (_customTriggerbotKey.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase)) _isTriggerbotKeyHeld = currentMouseState.LeftButton == ButtonState.Pressed;
            else if (Enum.TryParse(_customTriggerbotKey, true, out Keys keyboardKey)) _isTriggerbotKeyHeld = currentKeyboardState.IsKeyDown(keyboardKey);

            if (_triggerbotActive && _isTriggerbotKeyHeld && _lockedTarget.Type != TargetType.None)
            {
                Vector2 targetCenter = (_lockedTarget.Type == TargetType.NPC) ? Main.npc[_lockedTarget.Index].Center : Main.player[_lockedTarget.Index].Center;
                var player = Main.LocalPlayer;
                Item heldItem = player.HeldItem;
                if (Collision.CanHitLine(player.Center, 1, 1, targetCenter, 1, 1) &&
                    heldItem != null && heldItem.damage > 0 && heldItem.useTime > 0 &&
                    heldItem.pick == 0 && heldItem.axe == 0 && heldItem.hammer == 0 &&
                    player.CheckMana(heldItem.mana, false) && ((player.ItemTimeIsZero &&
                    player.reuseDelay == 0) || heldItem.channel))
                {
                    _triggerbotCanUse = true;
                }
                else
                {
                    _triggerbotCanUse = false;
                }
            }
        }
        #endregion

        #region Targeting
        private static void FindAndSetTarget()
        {
            _lockedTarget = FindClosestTarget(_aimlockRadiusTiles, _currentTargetingMode, _pvpModeActive);
        }

        private static bool IsTargetValid(LockedTargetInfo target)
        {
            if (target.Type == TargetType.NPC)
            {
                if (!Main.npc.IndexInRange(target.Index)) return false;
                NPC npc = Main.npc[target.Index];
                if (_debugTargetDummyMode && npc.type == NPCID.TargetDummy) return npc.active;
                return npc.active && !npc.friendly && npc.CanBeChasedBy();
            }
            if (target.Type == TargetType.Player)
            {
                if (!Main.player.IndexInRange(target.Index)) return false;
                Player player = Main.player[target.Index];
                return player.active && !player.dead && player.hostile && player.whoAmI != Main.myPlayer && (player.team == 0 || Main.LocalPlayer.team == 0 || player.team != Main.LocalPlayer.team);
            }
            return false;
        }

        private static string GetTargetName(LockedTargetInfo target)
        {
            if (target.Type == TargetType.NPC && Main.npc.IndexInRange(target.Index)) return Main.npc[target.Index].GivenOrTypeName;
            if (target.Type == TargetType.Player && Main.player.IndexInRange(target.Index)) return Main.player[target.Index].name;
            return "Unknown Target";
        }

        private static LockedTargetInfo FindClosestTarget(float radiusTiles, TargetingMode mode, bool preferPlayers)
        {
            Vector2 originPoint = (mode == TargetingMode.ClosestToPlayer) ? Main.LocalPlayer.Center : Main.MouseWorld;
            float searchRadiusPixelsSq = (mode == TargetingMode.ClosestToPlayer) ? float.MaxValue : (radiusTiles * 16f) * (radiusTiles * 16f);

            LockedTargetInfo bestPlayerLoS = LockedTargetInfo.None, bestNpcLoS = LockedTargetInfo.None;
            LockedTargetInfo bestPlayerNoLoS = LockedTargetInfo.None, bestNpcNoLoS = LockedTargetInfo.None;
            float bestPlayerMetricLoS = float.MaxValue, bestNpcMetricLoS = float.MaxValue;
            float bestPlayerMetricNoLoS = float.MaxValue, bestNpcMetricNoLoS = float.MaxValue;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                if (IsTargetValid(new LockedTargetInfo { Type = TargetType.NPC, Index = i }))
                {
                    NPC npc = Main.npc[i];
                    float distSq = Vector2.DistanceSquared(originPoint, npc.Center);
                    if (distSq < searchRadiusPixelsSq)
                    {
                        float metric = (mode == TargetingMode.LowestHealth) ? npc.life : distSq;
                        if (Collision.CanHitLine(originPoint, 1, 1, npc.Center, 1, 1))
                        {
                            if (metric < bestNpcMetricLoS) { bestNpcMetricLoS = metric; bestNpcLoS = new LockedTargetInfo { Type = TargetType.NPC, Index = i }; }
                        }
                        else
                        {
                            if (metric < bestNpcMetricNoLoS) { bestNpcMetricNoLoS = metric; bestNpcNoLoS = new LockedTargetInfo { Type = TargetType.NPC, Index = i }; }
                        }
                    }
                }
            }

            if (Main.LocalPlayer.hostile)
            {
                for (int i = 0; i < Main.maxPlayers; i++)
                {
                    if (IsTargetValid(new LockedTargetInfo { Type = TargetType.Player, Index = i }))
                    {
                        Player player = Main.player[i];
                        float distSq = Vector2.DistanceSquared(originPoint, player.Center);
                        if (distSq < searchRadiusPixelsSq)
                        {
                            float metric = (mode == TargetingMode.LowestHealth) ? player.statLife : distSq;
                            if (Collision.CanHitLine(originPoint, 1, 1, player.Center, 1, 1))
                            {
                                if (metric < bestPlayerMetricLoS) { bestPlayerMetricLoS = metric; bestPlayerLoS = new LockedTargetInfo { Type = TargetType.Player, Index = i }; }
                            }
                            else
                            {
                                if (metric < bestPlayerMetricNoLoS) { bestPlayerMetricNoLoS = metric; bestPlayerNoLoS = new LockedTargetInfo { Type = TargetType.Player, Index = i }; }
                            }
                        }
                    }
                }
            }

            bool playerLoSAvailable = bestPlayerLoS.Type != TargetType.None;
            bool npcLoSAvailable = bestNpcLoS.Type != TargetType.None;
            if (playerLoSAvailable && (!npcLoSAvailable || (preferPlayers && bestPlayerMetricLoS <= bestNpcMetricLoS))) return bestPlayerLoS;
            if (npcLoSAvailable) return bestNpcLoS;

            bool playerNoLoSAvailable = bestPlayerNoLoS.Type != TargetType.None;
            bool npcNoLoSAvailable = bestNpcNoLoS.Type != TargetType.None;
            if (playerNoLoSAvailable && (!npcNoLoSAvailable || (preferPlayers && bestPlayerMetricNoLoS <= bestNpcMetricNoLoS))) return bestPlayerNoLoS;
            if (npcNoLoSAvailable) return bestNpcNoLoS;

            return LockedTargetInfo.None;
        }
        #endregion

        #region Harmony Patches
        private static void ItemCheck_Prefix()
        {
            if (_triggerbotActive && _isTriggerbotKeyHeld && _lockedTarget.Type != TargetType.None && _triggerbotCanUse)
                Main.LocalPlayer.controlUseItem = true;
        }
        private static void NewProjectile_Postfix(ref int __result, IEntitySource spawnSource, int Owner)
        {
            if (spawnSource is EntitySource_ItemUse_WithAmmo itemSource && itemSource.Item.type == ItemID.Celeb2 && Owner == Main.myPlayer)
            {
                if (Main.projectile.IndexInRange(__result))
                {
                    _celebMk2RocketTypeCounter = (int)Main.projectile[__result].ai[0];
                }
            }
        }
        #endregion

        #region Prediction Calculations
        // Special Prediction Wrappers for Dictionary
        private static Vector2 PredictPiranhaGun(Vector2 shooterPos, Vector2 targetCenter, Vector2 targetVel, float shootSpeed, int _, int __)
        {
            for (int i = 0; i < 1000; i++)
            {
                Projectile p = Main.projectile[i];
                if (p.active && p.owner == Main.myPlayer && p.type == ProjectileID.MechanicalPiranha && p.ai[1] > 0f)
                {
                    int npcIndex = (int)p.ai[1] - 1;
                    if (Main.npc.IndexInRange(npcIndex) && Main.npc[npcIndex].active) return Main.npc[npcIndex].Center + Main.npc[npcIndex].velocity * 10f;
                }
            }
            float speedToUse = (Vector2.Distance(shooterPos, targetCenter) < 1000f && Collision.CanHitLine(shooterPos, 1, 1, targetCenter, 1, 1)) ? 16f : shootSpeed;
            return CalculatePrecisePredictedTargetPosition(shooterPos, targetCenter, targetVel, speedToUse);
        }

        private static Vector2 PredictStraightLineWeapon(Vector2 shooterPos, Vector2 targetCenter, Vector2 targetVel, float shootSpeed, int projToShoot, int extraUpdates)
        {
            return CalculatePrecisePredictedTargetPosition(shooterPos, targetCenter, targetVel, shootSpeed * (1f + extraUpdates));
        }

        private static Vector2 PredictCelebrationMk2(Vector2 shooterPos, Vector2 targetCenter, Vector2 targetVel, float shootSpeed, int _, int __, bool checkCollision)
        {
            int nextRocketType = Main.LocalPlayer.channel ? (_celebMk2RocketTypeCounter + 1) % 7 : 0;
            if (nextRocketType == 3 || nextRocketType == 4) return CalculatePrecisePredictedTargetPosition(shooterPos, targetCenter, targetVel, shootSpeed * 2f);
            return CalculateCelebMk2PredictedTargetPosition(shooterPos, targetCenter, targetVel, shootSpeed, nextRocketType, checkCollision);
        }
        private static Vector2 PredictSkyfall(Vector2 playerPosition, Vector2 playerVelocity, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, float launchHeight, int horizontalSpawnType, int projToShoot, int extraUpdates)
        {
            float gravity = 0.1f;
            int gravityDelay = 15;
            float terminalVelocity = 16f;

            switch (projToShoot)
            {
                case ProjectileID.HolyArrow: gravity = 0.07f; gravityDelay = 20; break;
                case ProjectileID.FrostburnArrow: gravity = 0.085f; gravityDelay = 17; break;
                case ProjectileID.JestersArrow: gravity = 0f; break;
                case ProjectileID.ShimmerArrow: gravity = -0.1f; break;
                case ProjectileID.MoonlordArrow: gravityDelay = 14; break;
                case ProjectileID.StarWrath: gravity = 0.2f; gravityDelay = 20; break;
            }

            float effectiveSpeed = shootSpeed * (1f + extraUpdates);
            float timeToIntercept = Vector2.Distance(playerPosition, targetPosition) / effectiveSpeed;
            if (float.IsNaN(timeToIntercept) || timeToIntercept <= 0) timeToIntercept = 0.1f;

            for (int i = 0; i < 3; i++)
            {
                Vector2 futureTargetPos = targetPosition + targetVelocity * timeToIntercept;
                Vector2 futurePlayerPos = playerPosition + new Vector2(playerVelocity.X, 0) * timeToIntercept;

                Vector2 futureSkyLaunchPoint = new Vector2(0, playerPosition.Y - launchHeight);
                switch (horizontalSpawnType)
                {
                    case 0: futureSkyLaunchPoint.X = futurePlayerPos.X; break;
                    case 1: futureSkyLaunchPoint.X = futurePlayerPos.X + (200f * Main.player[Main.myPlayer].direction); break;
                    case 2: futureSkyLaunchPoint.X = (futurePlayerPos.X + futureTargetPos.X) / 2f; break;
                    case 3: futureSkyLaunchPoint.X = futureTargetPos.X; break;
                }

                float distance = Vector2.Distance(futureSkyLaunchPoint, futureTargetPos);
                float time = 0;
                float distTraveled = 0;
                Vector2 currentVel = Vector2.Normalize(futureTargetPos - futureSkyLaunchPoint) * shootSpeed;
                if (float.IsNaN(currentVel.X)) currentVel = new Vector2(0, -1) * shootSpeed;

                while (distTraveled < distance && time < 1800)
                {
                    for (int k = 0; k < 1 + extraUpdates; k++)
                    {
                        if (time >= gravityDelay)
                        {
                            currentVel.Y += gravity;
                        }
                        if (currentVel.Y > terminalVelocity) currentVel.Y = terminalVelocity;

                        distTraveled += currentVel.Length();
                    }
                    time++;
                }

                timeToIntercept = time;
            }

            return targetPosition + targetVelocity * timeToIntercept;
        }
        private static Vector2 PredictSkyfall_Linear(Vector2 playerPosition, Vector2 playerVelocity, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, float launchHeight, float horizontalOffset, int extraUpdates)
        {
            float effectiveSpeed = shootSpeed * (1f + extraUpdates);
            if (effectiveSpeed <= 0) return targetPosition;

            Vector2 timeAdjustedTargetPos = targetPosition;

            for (int i = 0; i < 5; i++)
            {
                // Estimate time to target from player as a baseline for predicting future positions
                float timeToTargetGuess = Vector2.Distance(playerPosition, timeAdjustedTargetPos) / effectiveSpeed;
                if (float.IsNaN(timeToTargetGuess) || timeToTargetGuess <= 0) timeToTargetGuess = 0.1f;

                // Predict future positions of player and target based on our time guess
                Vector2 futurePlayerPos = playerPosition + playerVelocity * timeToTargetGuess;
                Vector2 futureTargetPosWithVel = targetPosition + targetVelocity * timeToTargetGuess;

                // Calculate the projectile's spawn point based on the future player position
                Vector2 spawnPoint = new Vector2(
                    futurePlayerPos.X + (horizontalOffset * Main.player[Main.myPlayer].direction),
                    futurePlayerPos.Y - launchHeight
                );

                timeAdjustedTargetPos = CalculatePrecisePredictedTargetPosition(spawnPoint, targetPosition, targetVelocity, effectiveSpeed);

                // Fallback if prediction returns an invalid result
                if (float.IsNaN(timeAdjustedTargetPos.X) || float.IsInfinity(timeAdjustedTargetPos.X))
                {
                    return targetPosition + targetVelocity * timeToTargetGuess; // Return a reasonable guess
                }
            }

            return timeAdjustedTargetPos;
        }
        private static Vector2 PredictBetsysWrath(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            Func<Vector2, bool, Tuple<float, bool>> simulation = (initialVelocity, shouldCheck) =>
                SimulateShot_BetsysWrath(playerPosition, targetPosition, initialVelocity, extraUpdates, shouldCheck);

            Vector2 idealAimPoint = SolveArc(playerPosition, targetPosition, targetVelocity, shootSpeed, extraUpdates, simulation, checkCollision);
            Vector2 idealLaunchDirection = (idealAimPoint - playerPosition).SafeNormalize(Vector2.UnitY);
            float builtInRotation = 1f * MathHelper.ToRadians(1f) * -Main.LocalPlayer.direction;
            Vector2 correctedAimDirection = idealLaunchDirection.RotatedBy(-builtInRotation);
            return playerPosition + correctedAimDirection * 5000f;
        }
        private static Vector2 PredictRainbowGun(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            float trueProjectileSpeed = 19f + (75f / 80f);

            Vector2 interceptPoint = targetPosition;
            for (int i = 0; i < 3; i++)
            {
                float timeToIntercept = Vector2.Distance(playerPosition, interceptPoint) / trueProjectileSpeed;
                interceptPoint = targetPosition + targetVelocity * timeToIntercept;
            }

            Vector2 bestAimDirection = Vector2.Zero;
            float closestMissDistance = float.MaxValue;

            for (float angleDegrees = -90f; angleDegrees <= 90f; angleDegrees += 2.0f)
            {
                float angleRadians = MathHelper.ToRadians(angleDegrees);
                Vector2 launchVelocity = new Vector2((float)Math.Cos(angleRadians), -(float)Math.Sin(angleRadians)) * trueProjectileSpeed;
                if (targetPosition.X < playerPosition.X) launchVelocity.X = -Math.Abs(launchVelocity.X);

                Vector2 finalPosition = playerPosition;
                Vector2 currentVelocity = launchVelocity;
                float aiTimer = 0;
                bool isObstructed = false;

                float initialDistToTargetSq = Vector2.DistanceSquared(playerPosition, interceptPoint);

                for (int time = 0; time < 1800; time++)
                {
                    float timerIncrement = 1f;
                    if (currentVelocity.Y < 0f) timerIncrement -= currentVelocity.Y / 3f;
                    aiTimer += timerIncrement;

                    if (aiTimer > 30f)
                    {
                        currentVelocity.Y += 0.5f;
                        if (currentVelocity.Y > 0f) currentVelocity.X *= 0.95f;
                        else currentVelocity.X *= 1.05f;
                    }

                    if (currentVelocity.Length() > 0)
                    {
                        currentVelocity.Normalize();
                        currentVelocity *= trueProjectileSpeed;
                    }

                    finalPosition += currentVelocity;

                    if (checkCollision && !isObstructed && Collision.SolidCollision(finalPosition, 1, 1))
                    {
                        isObstructed = true;
                        break;
                    }

                    if (Vector2.DistanceSquared(playerPosition, finalPosition) > initialDistToTargetSq && Vector2.Dot(currentVelocity, interceptPoint - finalPosition) < 0)
                    {
                        break;
                    }
                }

                float missDistance = Vector2.Distance(finalPosition, interceptPoint);

                if (!isObstructed && missDistance < closestMissDistance)
                {
                    closestMissDistance = missDistance;
                    bestAimDirection = launchVelocity.SafeNormalize(Vector2.UnitY);
                }
                else if (isObstructed && bestAimDirection == Vector2.Zero && missDistance < closestMissDistance)
                {
                    closestMissDistance = missDistance;
                    bestAimDirection = launchVelocity.SafeNormalize(Vector2.UnitY);
                }
            }

            if (bestAimDirection != Vector2.Zero)
            {
                return playerPosition + bestAimDirection * 5000f;
            }

            return interceptPoint;
        }
        private static Vector2 PredictChargedBlasterCannon(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            Player player = Main.player[Main.myPlayer];

            bool isInBeamPhase = false;
            for (int i = 0; i < 1000; i++)
            {
                Projectile p = Main.projectile[i];
                if (p.active && p.owner == player.whoAmI && p.type == 460 && p.ai[0] > 180f)
                {
                    isInBeamPhase = true;
                    break;
                }
            }

            if (player.channel && isInBeamPhase)
            {
                return targetPosition;
            }
            else
            {
                shootSpeed = 10f;
                extraUpdates = 1;

                int orbProjectileId = 459;

                return CalculateParabolicPredictedTargetPosition(playerPosition, targetPosition, targetVelocity, shootSpeed, orbProjectileId, extraUpdates, checkCollision);
            }
        }
        private static Vector2 PredictLeafBlower(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            Vector2 interceptPoint = targetPosition;

            for (int i = 0; i < 5; i++)
            {
                float distanceToIntercept = Vector2.Distance(playerPosition, interceptPoint);

                float time = 0;
                float distanceTraveled = 0;
                Vector2 currentVelocity = Vector2.Normalize(interceptPoint - playerPosition) * shootSpeed;
                Vector2 randomDrift = Vector2.Zero;

                while (distanceTraveled < distanceToIntercept && time < 1800)
                {
                    if (time == 50f)
                    {
                        UnifiedRandom random = new UnifiedRandom(projToShoot);
                        randomDrift.X = (float)random.Next(-100, 101) * 6E-05f;
                        randomDrift.Y = (float)random.Next(-100, 101) * 6E-05f;
                    }

                    if (time >= 50f)
                    {
                        currentVelocity += randomDrift;
                    }

                    if (Math.Abs(currentVelocity.X) + Math.Abs(currentVelocity.Y) > 16f)
                    {
                        currentVelocity *= 0.95f;
                    }
                    if (Math.Abs(currentVelocity.X) + Math.Abs(currentVelocity.Y) < 12f)
                    {
                        currentVelocity *= 1.05f;
                    }

                    distanceTraveled += currentVelocity.Length();
                    time++;
                }

                interceptPoint = targetPosition + targetVelocity * time;
            }

            return interceptPoint;
        }
        private static Vector2 PredictMagnetSphere(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            return SolveArc(playerPosition, targetPosition, targetVelocity, shootSpeed, extraUpdates, (initialVelocity, shouldCheck) => SimulateShot_MagnetSphere(playerPosition, targetPosition, initialVelocity, extraUpdates, shouldCheck), checkCollision);
        }
        private static Vector2 PredictRazorbladeTyphoon(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float shootSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            Vector2 interceptPoint = targetPosition;
            Main.NewText(extraUpdates.ToString());
            for (int i = 0; i < 5; i++)
            {
                float distanceToIntercept = Vector2.Distance(playerPosition, interceptPoint);

                float time = 0;
                float distanceTraveled = 0;
                Vector2 currentVelocity = Vector2.Normalize(interceptPoint - playerPosition) * shootSpeed;

                while (distanceTraveled < distanceToIntercept && time < 1800)
                {
                    for (int update = 0; update < extraUpdates; update++)
                    {
                        Vector2 vectorToTarget = interceptPoint - (playerPosition + (currentVelocity * time));
                        float targetRotation = vectorToTarget.ToRotation();
                        float currentRotation = currentVelocity.ToRotation();

                        double angleDifference = targetRotation - currentRotation;
                        if (angleDifference > Math.PI) angleDifference -= Math.PI * 2.0;
                        if (angleDifference < -Math.PI) angleDifference += Math.PI * 2.0;

                        currentVelocity = currentVelocity.RotatedBy(angleDifference * 0.1);

                        float currentSpeed = currentVelocity.Length();
                        currentVelocity.Normalize();
                        currentVelocity *= currentSpeed + 0.0025f;
                    }

                    distanceTraveled += currentVelocity.Length();
                    time++;
                }

                interceptPoint = targetPosition + targetVelocity * time;
            }

            return interceptPoint;
        }
        // Core Prediction Logic
        private static Vector2 CalculatePrecisePredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float effectiveProjectileSpeed)
        {
            Vector2 D = targetPosition - shooterPosition;
            float A = targetVelocity.LengthSquared() - (effectiveProjectileSpeed * effectiveProjectileSpeed);
            float B = 2 * Vector2.Dot(D, targetVelocity);
            float C = D.LengthSquared();
            float timeToIntercept = -1f;

            if (Math.Abs(A) < 1e-6)
            {
                if (Math.Abs(B) > 1e-6) timeToIntercept = -C / B;
            }
            else
            {
                float discriminant = B * B - 4 * A * C;
                if (discriminant >= 0)
                {
                    float sqrtDiscriminant = (float)Math.Sqrt(discriminant);
                    float t1 = (-B + sqrtDiscriminant) / (2 * A);
                    float t2 = (-B - sqrtDiscriminant) / (2 * A);
                    if (t1 > 0 && t2 > 0) timeToIntercept = Math.Min(t1, t2);
                    else if (t1 > 0) timeToIntercept = t1;
                    else if (t2 > 0) timeToIntercept = t2;
                }
            }
            return timeToIntercept > 0 ? targetPosition + targetVelocity * timeToIntercept : targetPosition;
        }

        private static Vector2 CalculateParabolicPredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, int projToShoot, int projectileExtraUpdates, bool checkCollision)
        {
            float currentGravity = 0.1f, drag = 1.0f, terminalVelocity = 16f;
            int gravityDelay = 15;
            bool useUnconditionalDrag = false;

            switch (projToShoot)
            {
                case ProjectileID.MoonlordArrow: gravityDelay = 14; break;
                case ProjectileID.HolyArrow: currentGravity = 0.07f; gravityDelay = 20; break;
                case ProjectileID.FrostburnArrow: currentGravity = 0.085f; gravityDelay = 17; break;
                case ProjectileID.JestersArrow: currentGravity = 0f; break;
                case ProjectileID.ShimmerArrow: currentGravity = -0.1f; break;
                case ProjectileID.Hellwing: currentGravity = 0f; break;
                case ProjectileID.BoneArrow: currentGravity = 0.06f; gravityDelay = 35; break;
                case ProjectileID.FrostArrow: currentGravity = 0.05f; gravityDelay = 30; break;
                case ProjectileID.PulseBolt: currentGravity = 0f; break;
                case ProjectileID.BeeArrow: currentGravity = 0f; break;
                case ProjectileID.ShadowFlameArrow: currentGravity = 0.04f; gravityDelay = 30; break;
                case ProjectileID.FairyQueenRangedItemShot: projectileSpeed *= 2; currentGravity = 0f; break; // Twilight Lance (From Eventide)
                case ProjectileID.DD2BetsyArrow: gravityDelay = 20; currentGravity = 0.2f; terminalVelocity = 12f; drag = 0.98f; break; // Aerial Bane
                case ProjectileID.CandyCorn: gravityDelay = 30; currentGravity = 0.5f; break;
                case ProjectileID.PoisonDartBlowgun: case ProjectileID.IchorDart: case ProjectileID.CrystalDart: gravityDelay = 20; currentGravity = 0.075f; break;
                case ProjectileID.CursedDart: currentGravity = 0f; break;
                case ProjectileID.JackOLantern: gravityDelay = 20; currentGravity = 0.5f; break;
                case ProjectileID.Stynger: gravityDelay = 60; currentGravity = 0.15f; break;
                case ProjectileID.Nail: gravityDelay = 20; currentGravity = 0.1f; drag = 0.992f; terminalVelocity = 18f; useUnconditionalDrag = true; break;
                case ProjectileID.Ale: gravityDelay = 15; currentGravity = 0.2f; drag = 0.99f; useUnconditionalDrag = true; break;
                case ProjectileID.Flare: case ProjectileID.BlueFlare: case ProjectileID.SpelunkerFlare: case ProjectileID.CursedFlare: case ProjectileID.RainbowFlare: case ProjectileID.ShimmerFlare: gravityDelay = 30; currentGravity = 0.09f; terminalVelocity = float.MaxValue; break;
                case ProjectileID.Beenade: gravityDelay = 5; currentGravity = 0.2f; break;
                case ProjectileID.Grenade: case ProjectileID.StickyGrenade: case ProjectileID.BouncyGrenade: case ProjectileID.PartyGirlGrenade: gravityDelay = 10; currentGravity = 0.2f; terminalVelocity = float.MaxValue; break;
                case ProjectileID.SpikyBall: gravityDelay = 5; currentGravity = 0.2f; break;
                case ProjectileID.JavelinFriendly: gravityDelay = 60; currentGravity = 0.3f; drag = 0.98f; useUnconditionalDrag = true; break;
                case ProjectileID.MolotovCocktail: gravityDelay = 15; currentGravity = 0.2f; drag = 0.99f; useUnconditionalDrag = true; break;
                case ProjectileID.BoneJavelin: gravityDelay = 45; currentGravity = 0.35f; drag = 0.98f; useUnconditionalDrag = true; break;
                case ProjectileID.ChainKnife: gravityDelay = 10; currentGravity = 0.3f; terminalVelocity = float.MaxValue;  break;
                case ProjectileID.SeedlerNut: gravityDelay = 5; currentGravity = 0.3f; drag = 0.99f; useUnconditionalDrag = true; break;
                case ProjectileID.Meowmere: gravityDelay = 20; currentGravity = 0.2f; break;
                case ProjectileID.Daybreak: gravityDelay = 45; currentGravity = 0.15f; drag = 0.995f; terminalVelocity = float.MaxValue; useUnconditionalDrag = true; break;
                case ProjectileID.BallofFire: case ProjectileID.BallofFrost: gravityDelay = 20; currentGravity = 0.2f; break; // From Flower of Fire and Flower of Frost
                case ProjectileID.WaterStream: gravityDelay = 3; currentGravity = 0.15f; terminalVelocity = float.MaxValue; break; // From Aqua Scepter
                case ProjectileID.PineNeedleFriendly: gravityDelay = 50; currentGravity = 0.5f; break; // From Razorpine
                case ProjectileID.BoulderStaffOfEarth: gravityDelay = 15; currentGravity = 0.2f; break;
                case ProjectileID.CursedFlameFriendly: gravityDelay = 20; currentGravity = 0.2f; break;
                case ProjectileID.GoldenShowerFriendly: gravityDelay = 3; currentGravity = 0.075f; terminalVelocity = float.MaxValue; break;
            }
            if (projToShoot == 507 || projToShoot == 598) shooterPosition.Y -= Main.LocalPlayer.height / 6f;

            if (currentGravity == 0) return CalculatePrecisePredictedTargetPosition(shooterPosition, targetPosition, targetVelocity, projectileSpeed * (1f + projectileExtraUpdates));
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, projectileExtraUpdates, (v, check) => SimulateShot(shooterPosition, targetPosition, v, currentGravity, gravityDelay, projectileExtraUpdates, terminalVelocity, drag, useUnconditionalDrag, check), checkCollision);
        }

        private static Vector2 CalculateThrownPredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            float gravity = 0.4f, drag = 0.97f, terminalVelocity = 16f;
            int gravityDelay = 20;
            switch (projToShoot)
            {
                case ProjectileID.SnowBallFriendly: gravity = 0.3f; drag = 0.98f; break;
                case ProjectileID.MagicDagger: gravityDelay = 31; break;
            }
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, extraUpdates, (v, check) => SimulateShot_Thrown(shooterPosition, targetPosition, v, gravity, gravityDelay, drag, terminalVelocity, check), checkCollision);
        }

        private static Vector2 CalculateVariableSpeedPredictedTargetPosition(Vector2 playerPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, float accelerationFactor, int extraUpdates, float terminalVelocity, bool checkCollision, int accelerationDelay = 0, int accelerationDuration = int.MaxValue, float minSpeedBeforeHoming = 0f) // New optional parameter
        {
            Vector2 interceptPoint = targetPosition;
            bool simulationSuccess = true;
            if (accelerationFactor < 1.0f)
            {
                for (int i = 0; i < 5; i++)
                {
                    float distanceToIntercept = Vector2.Distance(playerPosition, interceptPoint);
                    float time = 0;
                    Vector2 direction = Vector2.Normalize(interceptPoint - playerPosition);
                    Vector2 currentVelocity = direction * projectileSpeed;
                    float distanceTraveled = 0;

                    simulationSuccess = false;
                    while (distanceTraveled < distanceToIntercept && time < 1800)
                    {
                        if (time >= accelerationDelay && time < accelerationDelay + accelerationDuration)
                        {
                            currentVelocity *= accelerationFactor;
                        }

                        if (minSpeedBeforeHoming > 0f && currentVelocity.LengthSquared() < minSpeedBeforeHoming * minSpeedBeforeHoming)
                        {
                            break;
                        }

                        if (currentVelocity.LengthSquared() < 1f)
                        {
                            break;
                        }

                        distanceTraveled += currentVelocity.Length();
                        time++;
                    }

                    if (distanceTraveled >= distanceToIntercept)
                    {
                        simulationSuccess = true;
                    }
                    else
                    {
                        simulationSuccess = false;
                        break;
                    }
                    interceptPoint = targetPosition + targetVelocity * time;
                }
            }
            if (!simulationSuccess)
            {
                return CalculatePrecisePredictedTargetPosition(playerPosition, targetPosition, targetVelocity, projectileSpeed);
            }
            for (int i = 0; i < 5; i++)
            {
                float distanceToIntercept = Vector2.Distance(playerPosition, interceptPoint);
                float time = 0;
                Vector2 direction = Vector2.Normalize(interceptPoint - playerPosition);
                Vector2 currentVelocity = direction * projectileSpeed;
                float distanceTraveled = 0;

                while (distanceTraveled < distanceToIntercept && time < 1800)
                {
                    if (time >= accelerationDelay && time < accelerationDelay + accelerationDuration)
                    {
                        if (accelerationFactor > 1.0f && Math.Abs(currentVelocity.X) < terminalVelocity && Math.Abs(currentVelocity.Y) < terminalVelocity)
                        {
                            currentVelocity *= accelerationFactor;
                        }
                        else if (accelerationFactor < 1.0f)
                        {
                            currentVelocity *= accelerationFactor;
                        }
                    }
                    distanceTraveled += currentVelocity.Length();
                    time++;
                }
                interceptPoint = targetPosition + targetVelocity * time;
            }

            return SolveArc(playerPosition, interceptPoint, targetVelocity, projectileSpeed, extraUpdates, (initialVelocity, shouldCheck) => SimulateShot_VariableSpeed(playerPosition, interceptPoint, initialVelocity, accelerationFactor, extraUpdates, terminalVelocity, shouldCheck, accelerationDelay, accelerationDuration, minSpeedBeforeHoming), checkCollision);
        }
        private static Vector2 CalculateParabolicPredictedTargetPosition_Toxikarp(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, bool checkCollision)
        {
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, 0, (v, check) => SimulateShot_Toxikarp(shooterPosition, targetPosition, v, check), checkCollision);
        }

        private static Vector2 CalculateRocketPredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, int projToShoot, int extraUpdates, bool checkCollision)
        {
            var standardRocketIDs = new HashSet<int> { 134, 137, 140, 143, 776, 780, 784, 787, 790, 793, 796, 799 };
            bool isStandardRocket = standardRocketIDs.Contains(projToShoot);
            Vector2 interceptPoint;

            // Iterative prediction only for standard (accelerating) rockets
            if (isStandardRocket)
            {
                interceptPoint = targetPosition;
                for (int i = 0; i < 3; i++)
                {
                    float dist = Vector2.Distance(shooterPosition, interceptPoint);
                    float simSpeed = projectileSpeed, simDist = 0, time = 0;
                    while (simDist < dist && time < 1800)
                    {
                        if (simSpeed < 15f) simSpeed *= 1.1f;
                        simDist += simSpeed;
                        time++;
                    }
                    interceptPoint = targetPosition + targetVelocity * time;
                }
            }
            else // For non-accelerating rockets (Grenades, Mines), use simple prediction.
            {
                interceptPoint = CalculatePrecisePredictedTargetPosition(shooterPosition, targetPosition, targetVelocity, projectileSpeed);
            }

            return SolveArc(shooterPosition, interceptPoint, targetVelocity, projectileSpeed, extraUpdates, (v, check) => SimulateShot_Rocket(shooterPosition, interceptPoint, v, projToShoot, check), checkCollision);
        }

        private static Vector2 CalculateHarpoonPredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, bool checkCollision)
        {
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, 0, (v, check) => SimulateShot_Harpoon(shooterPosition, targetPosition, v, check), checkCollision);
        }

        private static Vector2 CalculateCelebMk2PredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, int rocketType, bool checkCollision)
        {
            float currentGravity = 0.1f;
            int gravityDelay = 15;
            switch (rocketType)
            {
                case 0: gravityDelay = 20; currentGravity = 0.12f; break;
                case 1: gravityDelay = 10; currentGravity = 0.25f; break;
                case 2: gravityDelay = 60; break;
                case 5: gravityDelay = 40; currentGravity = 0.08f; break;
                case 6: gravityDelay = 30; break;
            }
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, 1, (v, check) => SimulateShot(shooterPosition, targetPosition, v, currentGravity, gravityDelay, 1, 16f, 1.0f, false, check), checkCollision);
        }

        private static Vector2 CalculatePaperAirplanePredictedTargetPosition(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, float windSpeed, bool checkCollision)
        {
            return SolveArc(shooterPosition, targetPosition, targetVelocity, projectileSpeed, 0, (v, check) => SimulateShot_PaperAirplane(shooterPosition, targetPosition, v, windSpeed, check), checkCollision);
        }
        public static int GetFinalProjectileID(int weaponItemID, int ammoProjID)
        {
            int finalProjID = ammoProjID;
            switch (weaponItemID)
            {
                case ItemID.MoltenFury: if (finalProjID == ProjectileID.WoodenArrowFriendly) finalProjID = ProjectileID.HellfireArrow; break;
                case ItemID.Marrow: finalProjID = ProjectileID.BoneArrow; break;
                case ItemID.IceBow: finalProjID = ProjectileID.FrostArrow; break;
                case ItemID.SniperRifle: case ItemID.VenusMagnum: case ItemID.Uzi: if (finalProjID == ProjectileID.Bullet) finalProjID = ProjectileID.BulletHighVelocity; break;
                case ItemID.PulseBow: finalProjID = ProjectileID.PulseBolt; break;
                case ItemID.BeesKnees: if (finalProjID == ProjectileID.WoodenArrowFriendly) finalProjID = ProjectileID.BeeArrow; break;
                case ItemID.HellwingBow: if (finalProjID == ProjectileID.WoodenArrowFriendly) finalProjID = ProjectileID.Hellwing; break;
                case ItemID.ShadowFlameBow: finalProjID = ProjectileID.ShadowFlameArrow; break;
                case ItemID.DD2BetsyBow: finalProjID = ProjectileID.DD2BetsyArrow; break; // Aerial Bane
                case ItemID.BloodRainBow: finalProjID = ProjectileID.BloodArrow; break;
                case ItemID.FairyQueenRangedItem: if (finalProjID == ProjectileID.WoodenArrowFriendly) finalProjID = ProjectileID.FairyQueenRangedItemShot; break; // Twighlight Lance (From Eventide)
                case ItemID.ElectrosphereLauncher: finalProjID = ProjectileID.ElectrosphereMissile; break;
                case ItemID.PewMaticHorn: finalProjID = ProjectileID.PewMaticHornShot; break;
            }
            return finalProjID;
        }
        #endregion

        #region Simulation Physics
        private static Tuple<float, bool> SimulateShot(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, float gravity, int gravityDelay, int extraUpdates, float terminalVelocity, float drag, bool unconditionalDrag, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                for (int k = 0; k < 1 + extraUpdates; k++)
                {
                    position += velocity;
                    if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                    if (update_counter >= gravityDelay)
                    {
                        velocity.Y += gravity;
                        if (unconditionalDrag && drag < 1.0f) velocity.X *= drag;
                        else if (!unconditionalDrag && velocity.Y > 0f && drag < 1.0f) velocity.X *= drag;
                    }
                    if (gravity == -0.1f && velocity.Y < -16f) velocity.Y = -16f;
                    if (velocity.Y > terminalVelocity) velocity.Y = terminalVelocity;
                    update_counter++;
                }
                if (update_counter > 3600) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }

        private static Tuple<float, bool> SimulateShot_Thrown(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, float gravity, int gravityDelay, float drag, float terminalVelocity, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            while ((velocity.X > 0 && position.X < targetPosition.X) || (velocity.X < 0 && position.X > targetPosition.X))
            {
                if (Main.windPhysics) velocity.X += Main.windSpeedCurrent * Main.windPhysicsStrength;

                if (update_counter >= gravityDelay)
                {
                    velocity.Y += gravity;
                    velocity.X *= drag;
                }
                if (velocity.Y > terminalVelocity) velocity.Y = terminalVelocity;
                position += velocity;
                if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                update_counter++;
                if (update_counter > 1800) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }
        private static Tuple<float, bool> SimulateShot_VariableSpeed(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, float accelerationFactor, int extraUpdates, float terminalVelocity, bool checkCollision, int accelerationDelay = 0, int accelerationDuration = int.MaxValue, float minSpeedBeforeHoming = 0f) // New optional parameter
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            int physicsStartTime = accelerationDelay;
            int physicsEndTime = accelerationDelay + accelerationDuration;

            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                for (int k = 0; k < 1 + extraUpdates; k++)
                {
                    if (update_counter >= physicsStartTime && update_counter < physicsEndTime)
                    {
                        if (accelerationFactor > 1.0f && Math.Abs(velocity.X) < terminalVelocity && Math.Abs(velocity.Y) < terminalVelocity)
                        {
                            velocity *= accelerationFactor;
                        }
                        else if (accelerationFactor < 1.0f)
                        {
                            velocity *= accelerationFactor;
                        }
                    }

                    if (minSpeedBeforeHoming > 0f && velocity.LengthSquared() < minSpeedBeforeHoming * minSpeedBeforeHoming)
                    {
                        goto SimulationEnd;
                    }

                    position += velocity;

                    if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1))
                    {
                        isObstructed = true;
                    }
                    update_counter++;
                }

                if (velocity.LengthSquared() < 0.01f) break;
                if (update_counter > 3600) break;
            }

        SimulationEnd:;
            return new Tuple<float, bool>(position.Y, isObstructed);
        }
        private static Tuple<float, bool> SimulateShot_Toxikarp(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                if (update_counter > 30)
                {
                    if (velocity.Y > -8f) velocity.Y -= 0.05f;
                    velocity.X *= 0.98f;
                }
                position += velocity;
                if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                update_counter++;
                if (update_counter > 3600) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }

        private static Tuple<float, bool> SimulateShot_Rocket(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, int projToShoot, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            bool isGrenadeType = (projToShoot >= 133 && projToShoot <= 136) || projToShoot == 139 || projToShoot == 142 || projToShoot == 777 || projToShoot == 781 || projToShoot == 785 || projToShoot == 788 || projToShoot == 791 || projToShoot == 794 || projToShoot == 797 || projToShoot == 800;
            bool isMineType = (projToShoot >= 135 && projToShoot <= 138) || projToShoot == 141 || projToShoot == 144 || projToShoot == 778 || projToShoot == 782 || projToShoot == 786 || projToShoot == 789 || projToShoot == 792 || projToShoot == 795 || projToShoot == 798 || projToShoot == 801;
            var standardRocketIDs = new HashSet<int> { 134, 137, 140, 143, 776, 780, 784, 787, 790, 793, 796, 799 };
            bool isStandardRocket = standardRocketIDs.Contains(projToShoot);


            int gravityDelay = 15, update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                update_counter++;
                if (isMineType) { velocity.Y += 0.2f; velocity *= 0.97f; }
                else if (isGrenadeType && update_counter > gravityDelay) { if (velocity.Y == 0f) velocity.X *= 0.95f; velocity.Y += 0.2f; }
                else if (isStandardRocket && Math.Abs(velocity.X) < 15f && Math.Abs(velocity.Y) < 15f) velocity *= 1.1f;

                position += velocity;
                if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                if (update_counter > 1800) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }

        private static Tuple<float, bool> SimulateShot_Harpoon(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            float internalAITimer = 0f;
            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                if (internalAITimer >= 10f) { internalAITimer = 15f; velocity.Y += 0.3f; }
                internalAITimer += 1f;
                position += velocity;
                if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                update_counter++;
                if (update_counter > 3600) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }

        private static Tuple<float, bool> SimulateShot_PaperAirplane(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, float windSpeed, bool checkCollision)
        {
            Vector2 position = shooterPosition, velocity = initialVelocity;
            bool isObstructed = false;
            float direction = Math.Sign(velocity.X), rotation = velocity.ToRotation(), internalAITimer = -55f;

            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                internalAITimer += 1f;
                Vector2 baseVector = rotation.ToRotationVector2() * 8f;
                Vector2 windVector = baseVector + new Vector2(windSpeed, (float)Math.Sin(Math.PI * 2f * (update_counter % 90.0 / 90.0)) * direction * windSpeed);

                if (internalAITimer >= 20f && internalAITimer <= 69f && direction == Math.Sign(windSpeed) && velocity.Length() > 3f)
                {
                    windVector = baseVector.RotatedBy(-direction * Math.PI * 2f * 0.02f * Math.Min(1f, (internalAITimer - 20f) / 50f));
                }
                else
                {
                    if (internalAITimer == 70f) internalAITimer = -180f;
                    Vector2 oldVelocity = velocity;
                    velocity.Y = MathHelper.Clamp(velocity.Y + ((update_counter % 40 < 20) ? -0.15f : 0.15f), -2f, 2f);
                    velocity.X = MathHelper.Clamp(velocity.X + windSpeed * 0.006f, -6f, 6f);
                    if (velocity.X * oldVelocity.X < 0f) { direction *= -1; internalAITimer = -210f; }
                }

                velocity = windVector.SafeNormalize(Vector2.UnitY) * velocity.Length();
                rotation = velocity.ToRotation();
                position += velocity;
                if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1)) isObstructed = true;
                update_counter++;
                if (update_counter > 1800) break;
            }
            return new Tuple<float, bool>(position.Y, isObstructed);
        }
        private static Tuple<float, bool> SimulateShot_BetsysWrath(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, int extraUpdates, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                for (int k = 0; k < 1 + extraUpdates; k++)
                {
                    float currentGravity = 0f;
                    if (update_counter >= 10f)
                    {
                        currentGravity += 0.1f;
                    }
                    if (update_counter >= 20f)
                    {
                        currentGravity += 0.1f;
                    }
                    velocity.Y += currentGravity;

                    velocity.X *= 0.99f;

                    if (velocity.Y > 32f)
                    {
                        velocity.Y = 32f;
                    }

                    position += velocity;

                    if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1))
                    {
                        isObstructed = true;
                    }
                    update_counter++;
                }

                if (update_counter > 3600) break;
            }

            return new Tuple<float, bool>(position.Y, isObstructed);
        }
        private static Tuple<float, bool> SimulateShot_MagnetSphere(Vector2 shooterPosition, Vector2 targetPosition, Vector2 initialVelocity, int extraUpdates, bool checkCollision)
        {
            Vector2 position = shooterPosition;
            Vector2 velocity = initialVelocity;
            bool isObstructed = false;
            if (targetPosition.X < shooterPosition.X) velocity.X = -Math.Abs(velocity.X); else velocity.X = Math.Abs(velocity.X);

            int update_counter = 0;
            while ((velocity.X > 0f && position.X < targetPosition.X) || (velocity.X < 0f && position.X > targetPosition.X))
            {
                for (int k = 0; k < 1 + extraUpdates; k++)
                {
                    if (velocity.Length() > 2f)
                    {
                        velocity *= 0.98f;
                    }

                    position += velocity;

                    if (checkCollision && !isObstructed && Collision.SolidCollision(position, 1, 1))
                    {
                        isObstructed = true;
                    }
                    update_counter++;
                }

                if (update_counter > 3600) break;
            }

            return new Tuple<float, bool>(position.Y, isObstructed);
        }
        #endregion

        #region Arc Solver
        private static Vector2 SolveArc(Vector2 shooterPosition, Vector2 targetPosition, Vector2 targetVelocity, float projectileSpeed, int projectileExtraUpdates, Func<Vector2, bool, Tuple<float, bool>> simulationFunction, bool checkCollision)
        {
            float effectiveSpeed = projectileSpeed * (1f + projectileExtraUpdates);
            Vector2 interceptPoint = CalculatePrecisePredictedTargetPosition(shooterPosition, targetPosition, targetVelocity, effectiveSpeed);

            Func<float, float> simulateNoCollision = (angle) => simulationFunction(new Vector2((float)Math.Cos(angle), -(float)Math.Sin(angle)) * projectileSpeed, false).Item1;

            float low = -(float)Math.PI / 2f, high = (float)Math.PI / 2f, lowAngle, highAngle;
            for (int j = 0; j < 30; j++) { float mid = (low + high) / 2f; if (simulateNoCollision(mid) < interceptPoint.Y) high = mid; else low = mid; }
            lowAngle = (low + high) / 2f;
            low = lowAngle; high = (float)Math.PI / 2f;
            for (int j = 0; j < 30; j++) { float mid = (low + high) / 2f; if (simulateNoCollision(mid) > interceptPoint.Y) high = mid; else low = mid; }
            highAngle = (low + high) / 2f;

            var lowArcResult = simulationFunction(new Vector2((float)Math.Cos(lowAngle), -(float)Math.Sin(lowAngle)) * projectileSpeed, checkCollision);
            var highArcResult = simulationFunction(new Vector2((float)Math.Cos(highAngle), -(float)Math.Sin(highAngle)) * projectileSpeed, checkCollision);

            bool lowArcHits = Math.Abs(lowArcResult.Item1 - interceptPoint.Y) <= 16f;
            bool highArcHits = Math.Abs(highArcResult.Item1 - interceptPoint.Y) <= 16f;
            float finalAngle = float.NaN;

            if (lowArcHits && !lowArcResult.Item2) finalAngle = lowAngle;
            else if (highArcHits && !highArcResult.Item2) finalAngle = highAngle;
            else if (lowArcHits) finalAngle = lowAngle;
            else if (highArcHits) finalAngle = highAngle;

            if (float.IsNaN(finalAngle))
            {
                var allSimulatedArcs = new List<(float angle, bool obstructed, float yDiff)>();
                for (float degrees = -90f; degrees <= 90f; degrees += 2.0f)
                {
                    float currentAngle = MathHelper.ToRadians(degrees);
                    var sweepResult = simulationFunction(new Vector2((float)Math.Cos(currentAngle), -(float)Math.Sin(currentAngle)) * projectileSpeed, checkCollision);
                    allSimulatedArcs.Add((currentAngle, sweepResult.Item2, Math.Abs(sweepResult.Item1 - interceptPoint.Y)));
                }

                var perfectHits = allSimulatedArcs.Where(a => a.yDiff <= 16f).ToList();
                if (perfectHits.Any())
                {
                    finalAngle = perfectHits.OrderBy(a => a.obstructed).ThenBy(a => a.yDiff).First().angle;
                }
                else if (allSimulatedArcs.Any())
                {
                    var unobstructedOptions = allSimulatedArcs.Where(a => !a.obstructed).ToList();
                    if (unobstructedOptions.Any()) finalAngle = unobstructedOptions.OrderBy(a => a.yDiff).First().angle;
                    else finalAngle = allSimulatedArcs.OrderBy(a => Math.Abs(a.angle)).First().angle;
                }
            }

            if (!float.IsNaN(finalAngle))
            {
                Vector2 aimDirection = new Vector2((float)Math.Cos(finalAngle), -(float)Math.Sin(finalAngle));
                if (interceptPoint.X < shooterPosition.X) { aimDirection.X = -Math.Abs(aimDirection.X); } else { aimDirection.X = Math.Abs(aimDirection.X); }
                return shooterPosition + aimDirection * 5000f;
            }

            return shooterPosition + (interceptPoint - shooterPosition).SafeNormalize(Vector2.UnitY) * 5000f;
        }
        #endregion
    }
}