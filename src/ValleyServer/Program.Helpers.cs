#pragma warning disable SYSLIB0050

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using xTile.Dimensions;
using xTile.Display;
using xTile.Tiles;
using Lidgren.Network;
using Netcode;
using StardewValley;
using StardewValley.Network;
using StardewValley.Network.NetReady;
using StardewValley.Network.Dedicated;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Buffs;
using StardewValley.SaveSerialization;
using StardewValley.GameData.LocationContexts;
using StardewValley.GameData.Locations;
using StardewValley.Events;
namespace HeadlessServer
{
    partial class Program
    {
        private static readonly Dictionary<string, string> loggedLocationErrors = new Dictionary<string, string>();
        private static long lastDebrisDiagnosticsMs = -10000;
        private static readonly Dictionary<int, long> receivedMessageTypeCounts = new Dictionary<int, long>();
        private static readonly Dictionary<string, int> lastDebrisCountByLocation = new Dictionary<string, int>();
        private static bool updateLateErrorLogged = false;
        private static IEnumerator<int>? headlessNewDayProcess;
        // Written by the overnight worker when it finishes, read by the main loop every tick,
        // so it must not be cached across threads.
        private static volatile Thread? headlessNewDayThread;
        private static volatile bool headlessNewDayActive;
        private static long lastNewDayMonitorMs;
        // Day-roll watchdog: vanilla's barrier loops wait for every farmer still in
        // Game1.otherFarmers and only abort on a *client* timeout, so one wedged client can
        // freeze the overnight roll (and with it the day-end world save) forever.
        private static string lastNewDayProgressSignature = string.Empty;
        private static long lastNewDayProgressMs;
        private static long lastNewDayWatchdogReportMs;
        private static bool newDayWatchdogDroppedFarmers;
        private const int NewDayStallWarnSeconds = 15;
        private const int NewDayStallDropSeconds = 120;
        // Vanilla drives the overnight network pump from the overnight worker. Keep the
        // headless main loop from concurrently entering the same game/network state.

        /// <summary>
        /// Hands a farmhand the configured starter seeds, unless it already carries them.
        /// A configured count of 0 disables the hand-out entirely.
        /// </summary>
        private static void GiveStarterParsnipSeeds(Farmer farmhand)
        {
            int count = ServerConfig.Current.World.StarterParsnipSeeds;
            if (count <= 0)
            {
                return;
            }
            if (farmhand.Items.Any(item => item != null && item.QualifiedItemId == "(O)472"))
            {
                return;
            }

            farmhand.Items.Add(ItemRegistry.Create("(O)472", count));
            Console.WriteLine($"Added {count} starter parsnip seeds to farmhand {farmhand.Name} ({farmhand.UniqueMultiplayerID}).");
        }

        private static void EnsureFarmhandHomesAndBeds(Farm farm)
        {
            foreach (var building in farm.buildings)
            {
                try { building.load(); } catch (Exception ex) { Console.WriteLine($"[FarmInit] Failed to load {building.buildingType.Value}: {ex.Message}"); }
                if (building.GetIndoors() is FarmHouse house)
                {
                    if (house.GetPlayerBed() == null)
                    {
                        house.furniture.Add(new BedFurniture(BedFurniture.DEFAULT_BED_INDEX, new Vector2(9f, 8f)));
                        Console.WriteLine($"[FarmInit] Added fallback bed to {house.NameOrUniqueName}.");
                    }
                    // Cabin interiors end up nearly empty in the headless path, leaving
                    // players a bare room. Furnish the vanilla cabin basics.
                    if (house.furniture.Count <= 2)
                    {
                        try
                        {
                            // Vanilla cabin interior is minimal: a bed plus a fireplace at its
                            // map-designated point. Skip extras to avoid wrong textures.
                            var f = StardewValley.Objects.Furniture.GetFurnitureInstance("(F)1792", house.getFireplacePoint().ToVector2());
                            house.furniture.Add(f);
                            // Budget TV by the bed, matching the vanilla cabin starter set.
                            var tv = StardewValley.Objects.Furniture.GetFurnitureInstance("(F)1468", new Vector2(3f, 3f));
                            house.furniture.Add(tv);
                            Console.WriteLine($"[FarmInit] Furnished cabin interior {house.NameOrUniqueName}.");
                        }
                        catch (Exception ex) { Console.WriteLine($"[FarmInit] Furnishing failed for {house.NameOrUniqueName}: {ex.Message}"); }
                    }
            }
            }
            foreach (var farmer in Game1.otherFarmers.Values)
            {
                try
                {
                    // Saved farmhands may carry a homeLocation GUID from a previous session's
                    // world (interior GUIDs regenerate each boot). A stale name makes clients
                    // crash in BedFurniture.ApplyWakeUpPosition -> RequireLocation on join.
                    string? homeName = farmer.homeLocation.Value;
                    bool stale = !string.IsNullOrEmpty(homeName) && Game1.getLocationFromName(homeName) == null;
                    if (stale)
                    {
                        Console.WriteLine($"[FarmhandHome] {farmer.UniqueMultiplayerID} home {homeName} no longer exists; clearing for reassignment.");
                        farmer.homeLocation.Value = null;
                    }
                    bool assigned = Game1.netWorldState.Value.TryAssignFarmhandHome(farmer);
                    Console.WriteLine($"[FarmhandHome] {farmer.UniqueMultiplayerID} home={farmer.homeLocation.Value}, assigned={assigned}.");
                    if (Game1.getLocationFromName(farmer.homeLocation.Value) is FarmHouse home && home.GetPlayerBed() == null)
                        home.furniture.Add(new BedFurniture(BedFurniture.DEFAULT_BED_INDEX, new Vector2(9f, 8f)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FarmhandHome] Initialization failed for {farmer.UniqueMultiplayerID}: {ex.Message}");
                }
            }
        }

        // Spawns a temporary farmhand plus a wood drop within magnetic range, runs the same
        // location update the tick loop uses, and verifies the debris target assignment.
        // Server-side state is fully restored afterwards, so real clients see a pristine world.
        private static void RunDebrisSelfTest(Farm farm)
        {
            long testFarmerId = SelfTestFarmerId;
            Console.WriteLine("[SelfTest] Running debris target assignment self-test...");
            List<Debris>? realDebris = null;
            try
            {
                var testFarmer = new Farmer(new FarmerSprite(null), Vector2.Zero, 1, "SelfTest", Farmer.initialTools(), isMale: true)
                {
                    UniqueMultiplayerID = testFarmerId
                };
                testFarmer.currentLocation = farm;
                testFarmer.Position = new Vector2(64f * 64f, 15f * 64f); // farmhouse porch area
                var testRoot = new NetFarmerRoot(testFarmer);
                Game1.otherFarmers.Roots[testFarmerId] = testRoot;
                // NetFarmerRef resolves its target through Game1.getAllFarmers(), which in
                // 1.6.15 enumerates the world-state farmhandData directory, not otherFarmers.
                Game1.netWorldState.Value.farmhandData[testFarmerId] = testFarmer;

                // The self-test must never destroy real dropped items: the farm may hold debris
                // restored from the world save. Snapshot the list and put it back afterwards
                // instead of clearing it.
                realDebris = new List<Debris>(farm.debris);

                Vector2 debrisOrigin = testFarmer.Position + new Vector2(64f, 0f); // within the 128px magnetic radius
                farm.debris.Add(new Debris("(O)388", 4, debrisOrigin, testFarmer.Position));

                var time = new GameTime(TimeSpan.Zero, TimeSpan.FromMilliseconds(16));
                for (int i = 0; i < 80; i++)
                {
                    time = new GameTime(time.TotalGameTime + TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));
                    Game1.currentGameTime = time;
                    foreach (GameLocation location in Game1.locations)
                    {
                        if (location.farmers.Any())
                        {
                            location.UpdateWhenCurrentLocation(time);
                        }
                        location.updateEvenIfFarmerIsntHere(time);
                    }
                }

                Debris? tracked = farm.debris.FirstOrDefault();
                Farmer? assigned = tracked?.player.Value;
                if (assigned != null && assigned.UniqueMultiplayerID == testFarmerId)
                {
                    Console.WriteLine($"[SelfTest] Debris target assignment PASS: debris assigned to farmer {assigned.UniqueMultiplayerID} at {assigned.Position}.");
                }
                else
                {
                    Console.WriteLine($"[SelfTest] Debris target assignment FAIL: debrisCount={farm.debris.Count} assigned={(assigned == null ? "null" : assigned.UniqueMultiplayerID.ToString())}");
                    if (tracked != null)
                    {
                        Vector2 approx = Vector2.Zero;
                        foreach (Chunk chunk in tracked.Chunks)
                        {
                            approx += chunk.position.Value;
                        }
                        if (tracked.Chunks.Count > 0)
                        {
                            approx /= tracked.Chunks.Count;
                        }
                        Console.WriteLine($"[SelfTest]   debris: type={tracked.debrisType.Value} itemId={tracked.itemId.Value} item={(tracked.item == null ? "null" : tracked.item.QualifiedItemId)} chunks={tracked.Chunks.Count} attract={tracked.chunksMoveTowardPlayer} bounceTimer={tracked.timeSinceDoneBouncing} droppedBy={tracked.DroppedByPlayerID.Value} approxPos={approx}");
                        foreach (Farmer farmer in farm.farmers)
                        {
                            Point pixel = farmer.StandingPixel;
                            int radius = farmer.GetAppliedMagneticRadius();
                            bool inRange = Math.Abs(approx.X + 32f - pixel.X) <= radius && Math.Abs(approx.Y + 32f - pixel.Y) <= radius;
                            Console.WriteLine($"[SelfTest]   farmer {farmer.UniqueMultiplayerID}: pos={farmer.Position} standing={pixel} magnetRadius={radius} inRange={inRange} acceptsItem={farmer.couldInventoryAcceptThisItem(tracked.itemId.Value, 1, tracked.itemQuality)}");
                        }
                    }
                }

                // Restore pristine state before real clients connect. The test debris and the test
                // farmer never survive, and the farm's real debris is put back untouched.
                farm.debris.Clear();
                foreach (Debris debris in realDebris)
                {
                    farm.debris.Add(debris);
                }
                Game1.otherFarmers.Roots.Remove(testFarmerId);
                // farmhandData is the persistent farmhand directory the vanilla save writes, so the
                // test farmer has to leave it too or it reappears in every later session.
                Game1.netWorldState.Value.farmhandData.Remove(testFarmerId);
                // The removal above queues an outgoing net change; leaving it there makes vanilla's
                // next forced world state write (the first thing a day roll does) die with
                // "Collection was modified" -- see DropQueuedNetDictionaryChanges.
                int droppedChanges = DropQueuedNetDictionaryChanges(Game1.netWorldState.Value.farmhandData);
                testFarmer.currentLocation = null;
                Console.WriteLine($"[SelfTest] Cleaned up test farmer and debris ({realDebris.Count} real debris item(s) restored, phantom farmhand {testFarmerId} removed, {droppedChanges} queued net change(s) dropped).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SelfTest] Debris self-test crashed: {ex}");
                // Same restore as the success path: never let the self-test delete real debris.
                farm.debris.Clear();
                if (realDebris != null)
                {
                    foreach (Debris debris in realDebris)
                    {
                        farm.debris.Add(debris);
                    }
                }
                Game1.otherFarmers.Roots.Remove(testFarmerId);
                Game1.netWorldState.Value.farmhandData.Remove(testFarmerId);
                int droppedChanges = DropQueuedNetDictionaryChanges(Game1.netWorldState.Value.farmhandData);
                Console.WriteLine($"[SelfTest] Restored {realDebris?.Count ?? 0} real debris item(s) after the crash and removed phantom farmhand {testFarmerId} ({droppedChanges} queued net change(s) dropped).");
            }
        }

        // Mirrors vanilla Game1.UpdateLocations/_UpdateLocation for the host: every location
        // updates each tick, and inhabited locations additionally run UpdateWhenCurrentLocation.
        // The debris target assignment happens there (master game), while pickup runs on the
        // owning client 鈥?see the call-site comment for the full vanilla flow.
        private static void UpdateHeadlessLocations(GameTime simulationTime)
        {
            foreach (GameLocation location in Game1.locations)
            {
                if (location == null)
                {
                    continue;
                }
                try
                {
                    bool shouldUpdate = location.farmers.Any();
                    if (shouldUpdate)
                    {
                        location.UpdateWhenCurrentLocation(simulationTime);
                    }
                    location.updateEvenIfFarmerIsntHere(simulationTime);
                }
                catch (Exception ex)
                {
                    string key = location.NameOrUniqueName;
                    if (!loggedLocationErrors.TryGetValue(key, out string? previous) || previous != ex.Message)
                    {
                        loggedLocationErrors[key] = ex.Message;
                        Console.WriteLine($"[LocationUpdate] {key}: {ex}");
                    }
                }
            }
            LogDebrisDiagnostics();
        }

        private static void LogDebrisDiagnostics()
        {
            long now = Environment.TickCount64;
            if (now - lastDebrisDiagnosticsMs < 5000)
            {
                return;
            }
            lastDebrisDiagnosticsMs = now;

            if (clientConnections.Count > 0)
            {
                var parts = receivedMessageTypeCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}");
                Console.WriteLine($"[MsgStats] received types: {string.Join(" ", parts)}");
            }

            int worldDebris = 0;
            foreach (GameLocation location in Game1.locations)
            {
                if (location == null)
                {
                    continue;
                }
                string key = location.NameOrUniqueName;
                int count = location.debris.Count;
                worldDebris += count;
                if (count == 0)
                {
                    lastDebrisCountByLocation[key] = 0;
                    continue;
                }

                int assigned = 0;
                var owners = new List<long>();
                foreach (Debris debris in location.debris)
                {
                    Farmer? target = debris.player.Value;
                    if (target != null)
                    {
                        assigned++;
                        owners.Add(target.UniqueMultiplayerID);
                    }
                }

                int last = lastDebrisCountByLocation.TryGetValue(key, out int lastValue) ? lastValue : 0;
                if (count > last)
                {
                    // Newly spawned debris appeared on the server since the last sample.
                    Debris? fresh = location.debris[last];
                    string where = fresh != null && fresh.Chunks.Count > 0 ? fresh.Chunks[0].position.Value.ToString() : "?";
                    Console.WriteLine($"[DebrisSpawn] {key}: +{count - last} now {count}; sample itemId={(fresh == null ? "?" : fresh.itemId.Value)} type={(fresh == null ? "?" : fresh.debrisType.Value.ToString())} chunk0={where} assigned={assigned}");
                }
                else if (count < last)
                {
                    Console.WriteLine($"[DebrisSpawn] {key}: -{last - count} (collected/expired) now {count}");
                }
                lastDebrisCountByLocation[key] = count;
                Console.WriteLine($"[DebrisDiag] {key}: debris={count} assigned={assigned} targets=[{string.Join(",", owners)}] farmers={location.farmers.Count()}");
            }

            if (clientConnections.Count > 0)
            {
                Console.WriteLine($"[DebrisDiag] world debris total={worldDebris}");
            }
        }

        private static void AdvanceHeadlessClock(long elapsedMilliseconds)
        {
            // Configured pacing: real milliseconds that advance the in-game clock by ten
            // minutes. Read per call so a single source of truth drives the whole loop.
            long millisecondsPerTenMinutes = ServerConfig.Current.Simulation.MillisecondsPerTenMinutes;
            headlessClockAccumulatorMs += elapsedMilliseconds;
            while (headlessClockAccumulatorMs >= millisecondsPerTenMinutes)
            {
                headlessClockAccumulatorMs -= millisecondsPerTenMinutes;
                // Vanilla schedule engine lives in Game1.addMinute() (called from
                // UpdateGameClock), but that method also runs presentation code
                // (keyboard state, music) that a headless host cannot support. Replicate
                // its gameplay core here: time advance, per-location checkSchedule for
                // every NPC, ten-minute location updates, and lightning.
                int timeOfDay = Game1.timeOfDay + 10;
                if (timeOfDay % 100 == 60) timeOfDay += 40;
                if (timeOfDay % 100 == 90) timeOfDay -= 40;
                Game1.timeOfDay = Math.Min(timeOfDay, 2600);

                try
                {
                    Game1.currentLocation?.performTenMinuteUpdate(Game1.timeOfDay);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HeadlessClock] performTenMinuteUpdate failed at {Game1.timeOfDay}: {ex.Message}");
                }

                foreach (GameLocation location in Game1.locations)
                {
                    if (location == null) continue;
                    try
                    {
                        for (int i = location.characters.Count - 1; i >= 0; i--)
                        {
                            location.characters[i].checkSchedule(Game1.timeOfDay);
                        }
                    }
                    catch (Exception ex)
                    {
                        string key = location.NameOrUniqueName;
                        if (!loggedLocationErrors.TryGetValue(key, out string? prev2) || prev2 != ex.Message)
                        {
                            loggedLocationErrors[key] = ex.Message;
                            Console.WriteLine($"[HeadlessClock] checkSchedule {key}: {ex.Message}");
                        }
                    }
                }

                if (Game1.timeOfDay is 1900 or 2000)
                {
                    try
                    {
                        Game1.currentLocation?.switchOutNightTiles();
                    }
                    catch { /* night-tile visuals need content; ambience only */ }
                }
                if (Game1.isLightning && Game1.IsMasterGame)
                {
                    try { Utility.performLightningUpdate(Game1.timeOfDay); }
                    catch (Exception ex) { Console.WriteLine($"[HeadlessClock] lightning update failed: {ex.Message}"); }
                }
            }
        }

        private static bool hostSleepTriggered = false;
        private static int lastLogReady = -1, lastLogRequired = -1;

        private static void EnsureHeadlessDedicatedHostFlag()
        {
            try
            {
                FarmerTeam? team = Game1.player?.team;
                FieldInfo? flagField = typeof(FarmerTeam).GetField("hasDedicatedHost", BindingFlags.Instance | BindingFlags.NonPublic);
                if (team != null && flagField?.GetValue(team) is NetBool flag && !flag.Value)
                {
                    flag.Value = true;
                    Console.WriteLine("[HeadlessNewDay] Restored dedicated-host flag.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessNewDay] Failed to restore dedicated-host flag: {ex.Message}");
            }
        }

        private static void PrepareHeadlessHostForSleep()
        {
            if (Game1.dedicatedServer == null || Game1.netReady == null)
                return;
            if (!Game1.HasDedicatedHost)
                return;
            if (Game1.newDay)
                return;

            SyncDisconnectingFarmers();

            // Festivals also gate on a ready check; the invisible host must say yes
            // or single-player clients hang on "waiting for players" forever.
            int festReady = Game1.netReady.GetNumberReady("festivalStart");
            int festRequired = Game1.netReady.GetNumberRequired("festivalStart");
            if (festRequired > 0 && festReady >= clientConnections.Count)
            {
                Game1.netReady.SetLocalReady("festivalStart", true);
            }

            int ready = Game1.netReady.GetNumberReady("sleep");
            int required = Game1.netReady.GetNumberRequired("sleep");
            int activeClients = clientConnections.Count;
            if (ready <= 0)
            {
                hostSleepTriggered = false;
            }
            if (ready != lastLogReady || required != lastLogRequired)
            {
                lastLogReady = ready; lastLogRequired = required;
                Console.WriteLine($"[HeadlessNewDay][dbg] sleep ready={ready} required={required} activeClients={activeClients} hostTriggered={hostSleepTriggered} hostFarmInBed={Game1.player.isInBed.Value} hostLoc={Game1.currentLocation?.Name}");
            }

            // Headless server never runs the render-loop warp that a real dedicated host
            // relies on to move itself home and join the night sleep check. Reproduce the
            // vanilla dedicated-server steps once all real farmhands are in bed: place the
            // invisible host in its own farmhouse bed and answer the sleep prompt so the
            // host becomes sleep-ready.
            bool allActiveClientsReady = activeClients > 0 && ready >= activeClients;
            if (!Game1.newDay && allActiveClientsReady && !hostSleepTriggered)
            {
                Farmer host = Game1.player;
                host.isInBed.Value = true;
                host.timeWentToBed.Value = (int)Game1.timeOfDay;
                host.Halt();
                Console.WriteLine($"[HeadlessNewDay] All {activeClients} active client(s) ready (ready={ready}/{required}). Setting host sleep ready...");
                try
                {
                    Game1.netReady.SetLocalReady("sleep", true);
                    Game1.netReady.SetLocalReady("ready_for_save", true);
                    Game1.netReady.SetLocalReady("wakeup", true);
                    hostSleepTriggered = true;
                    Console.WriteLine($"[HeadlessNewDay] Host marked ready (ready={Game1.netReady.GetNumberReady("sleep")}/{Game1.netReady.GetNumberRequired("sleep")}, isReady={Game1.netReady.IsReady("sleep")}).");
                }
                catch (Exception ex)
                {
                    hostSleepTriggered = false;
                    Console.WriteLine($"[HeadlessNewDay] Host sleep setup failed: {ex}");
                }
            }

            // Drive the vanilla NewDay once readycheck finishes or when all active clients + host are in bed
            if (hostSleepTriggered && !Game1.newDay)
            {
                hostSleepTriggered = false;
                Console.WriteLine($"[HeadlessNewDay] Sleep check satisfied; starting NewDay (time {Game1.timeOfDay}, day {Game1.dayOfMonth})...");
                try
                {
                    Game1.NewDay(0f);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HeadlessNewDay] Game1.NewDay failed: {ex}");
                }
            }
        }

        private static void SyncDisconnectingFarmers()
        {
            try
            {
                var mp = typeof(Game1).GetField("multiplayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as Multiplayer;
                if (mp == null) return;
                var field = typeof(Multiplayer).GetField("disconnectingFarmers", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(mp) is List<long> list)
                {
                    list.Clear();
                    foreach (var id in Game1.otherFarmers.Roots.Keys)
                    {
                        if (!clientConnections.ContainsKey(id))
                        {
                            list.Add(id);
                        }
                    }
                }
            }
            catch { }
        }

        private static void PrepareHeadlessEndOfNightUi()
        {
            // Game1._newDayAfterFade eventually calls showEndOfNightStuff(). A real
            // dedicated host has a UI/content environment, but this host intentionally
            // does not load fonts. Pre-seed the menu stack with an uninitialized
            // SaveGameMenu so showEndOfNightStuff() pops a harmless placeholder instead
            // of invoking SaveGameMenu() -> SparklingText -> dialogueFont.MeasureString.
            try
            {
                Game1.endOfNightMenus ??= new Stack<StardewValley.Menus.IClickableMenu>();
                if (Game1.endOfNightMenus.Count == 0)
                {
                    var placeholder = (StardewValley.Menus.SaveGameMenu)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.SaveGameMenu));
                    Game1.endOfNightMenus.Push(placeholder);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessNewDay] Failed to prepare headless end-of-night UI: {ex}");
            }
        }

        private static void PumpHeadlessNewDayProcess()
        {
            // In the retail game the overnight coroutine (_newDayAfterFade) runs on a
            // background Task (_newDayTask) while the main thread keeps pumping network
            // messages; the coroutine's barrier() calls block on ITS OWN thread waiting for
            // farmhand replies that only the main loop can receive. Mirror that model: if we
            // stepped the coroutine synchronously on the main thread, barrier() would block
            // the main loop and the server would deadlock with clients stuck on a black
            // screen waiting for the day roll to finish.
            if (!Game1.newDay || headlessNewDayThread != null)
            {
                return;
            }
            EnsureHeadlessDedicatedHostFlag();
            PrepareHeadlessEndOfNightUi();
            IEnumerator<int>? enumerator = null;
            try
            {
                MethodInfo? overnight = typeof(Game1).GetMethod(
                    "_newDayAfterFade", BindingFlags.Static | BindingFlags.NonPublic);
                if (overnight == null)
                    throw new MissingMethodException(typeof(Game1).FullName, "_newDayAfterFade");
                enumerator = (IEnumerator<int>?)overnight.Invoke(null, null);
                headlessNewDayProcess = enumerator;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessNewDay] Failed to start overnight coroutine: {ex}");
                return;
            }
            IEnumerator<int>? iterator = enumerator;
            Game1.newDay = true;
            headlessNewDayActive = true;
            Console.WriteLine(DescribeNewDayState("worker-starting"));
            headlessNewDayThread = new Thread(() =>
            {
                try
                {
                    // Keep the dedicated-host identity intact throughout the overnight
                    // coroutine. showEndOfNightStuff() must skip client UI on the server.
                    EnsureHeadlessDedicatedHostFlag();
                    Console.WriteLine(DescribeNewDayState("worker-enter"));
                    // The iterator blocks internally at each barrier() while it waits for
                    // the farmhand replies the main loop feeds into newDaySync; MoveNext
                    // therefore naturally paces the overnight sequence.
                    while (iterator != null && iterator.MoveNext())
                    {
                    }

                    // On dedicated/headless server, SaveGameMenu UI is bypassed, so ensure
                    // save synchronization flags and finish signals are sent to client farmhands.
                    Console.WriteLine(DescribeNewDayState("coroutine-returned"));
                    // Vanilla persists the whole world during the SaveGameMenu that this host
                    // bypasses; pump the native SaveGame.Save() enumerator here instead so
                    // every day roll writes a full world save to disk (the same
                    // SaveSerialization pipeline the farmhand XML files already use).
                    bool worldSaved = TrySaveWorldToDisk();
                    if (!worldSaved)
                    {
                        // Clients are still released below so nobody hangs on the black screen,
                        // but the day must not look persisted when it is not.
                        Console.WriteLine("[HeadlessNewDay] !! The world save failed, so this day's progress exists only in memory. " +
                            "Fix the cause logged above and run the 'save' command before restarting the server.");
                    }
                    try
                    {
                        if (Game1.newDaySync != null && Game1.newDaySync.hasInstance())
                        {
                            if (!Game1.newDaySync.hasSaved())
                            {
                                Console.WriteLine(DescribeNewDayState("before-flagSaved"));
                                // The headless server skips SaveGameMenu.update(), so it never
                                // marks itself ready for the save/wakeup ReadyChecks the client
                                // runs after the save menu. Mirror the master menu branch here:
                                // mark ready_for_save, then broadcast the saved variable.
                                try { Game1.newDaySync.readyForSave(); } catch (Exception rEx) { Console.WriteLine($"[HeadlessNewDay] readyForSave failed: {rEx.Message}"); }
                                Game1.newDaySync.flagSaved();
                                Console.WriteLine("[HeadlessNewDay] Flagged newDaySync.flagSaved() for clients.");
                                Console.WriteLine(DescribeNewDayState("after-flagSaved"));
                            }
                            if (!Game1.newDaySync.hasFinished())
                            {
                                Console.WriteLine(DescribeNewDayState("before-finish"));
                                // Mark the host ready for the "wakeup" ReadyCheck, then send the
                                // finished variable. Without this the client's
                                // PollForEndOfNewDaySync() never sees wakeup ready and stays
                                // stuck on the black screen showing "waiting for players".
                                try { Game1.newDaySync.readyForFinish(); } catch (Exception rEx) { Console.WriteLine($"[HeadlessNewDay] readyForFinish failed: {rEx.Message}"); }
                                Game1.newDaySync.finish();
                                Console.WriteLine("[HeadlessNewDay] Flagged newDaySync.finish() for clients.");
                                Console.WriteLine(DescribeNewDayState("after-finish"));
                            }
                            Console.WriteLine(DescribeNewDayState("before-destroy"));
                            Game1.newDaySync.destroy();
                            Console.WriteLine(DescribeNewDayState("after-destroy"));
                        }
                    }
                    catch (Exception syncEx)
                    {
                        Console.WriteLine($"[HeadlessNewDay] Exception signaling newDaySync finish: {syncEx.Message}");
                    }

                    Console.WriteLine($"[HeadlessNewDay] Overnight coroutine finished at day {Game1.dayOfMonth}, time {Game1.timeOfDay}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HeadlessNewDay] Overnight coroutine failed: {ex}");
                }
                finally
                {
                    Game1.newDay = false;
                    headlessNewDayProcess = null;
                    headlessNewDayActive = false;
                    headlessNewDayThread = null;
                    try { Game1.timeOfDay = 600; Game1.netWorldState?.Value?.UpdateFromGame1(); } catch { }
                    // The headless night end never runs the SaveGameMenu that would normally
                    // pop itself and wake the host. Clear the end-of-night state manually or
                    // Game1.shouldTimePass() stays false forever (menus freeze time), blocking
                    // the NPC schedule clock and every time-gated update system.
                    try
                    {
                        Game1.showingEndOfNightStuff = false;
                        Game1.endOfNightMenus?.Clear();
                        if (Game1.activeClickableMenu is StardewValley.Menus.SaveGameMenu)
                            Game1.activeClickableMenu = null;
                        if (Game1.player != null)
                        {
                            Game1.player.isInBed.Value = false;
                            Game1.player.timeWentToBed.Value = 0;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[HeadlessNewDay] wake-up cleanup failed: {ex.Message}"); }
                    Console.WriteLine($"[HeadlessNewDay] newDay cleared (time reset to {Game1.timeOfDay}).");
                    Console.WriteLine(DescribeNewDayState("worker-finally"));
                }
            })
            {
                IsBackground = true,
                Name = "HeadlessNewDay"
            };
            headlessNewDayThread.Start();
            Console.WriteLine("[HeadlessNewDay] Started vanilla overnight coroutine on background thread.");
        }

        /// <summary>
        /// Watches an in-progress overnight roll for a farmhand that never answers its barrier.
        /// Vanilla's NetSynchronizer.isBarrierReady waits for every farmer still present in
        /// Game1.otherFarmers and only gives up when the *client* times out, so on a dedicated
        /// server one crashed or wedged client freezes the roll — and the day-end world save with
        /// it — for everybody else. Reports the blocking barrier and, after a long silence, lets
        /// vanilla's own removal path drop the farmers that never replied. Runs on the main loop.
        /// </summary>
        private static void PumpHeadlessNewDayWatchdog()
        {
            if (!headlessNewDayActive)
            {
                lastNewDayProgressSignature = string.Empty;
                newDayWatchdogDroppedFarmers = false;
                return;
            }

            string signature = DescribeNewDayState("watchdog");
            long now = Environment.TickCount64;
            if (!string.Equals(signature, lastNewDayProgressSignature, StringComparison.Ordinal))
            {
                // Any change means the roll moved on: restart the silence timer.
                lastNewDayProgressSignature = signature;
                lastNewDayProgressMs = now;
                lastNewDayWatchdogReportMs = 0;
                newDayWatchdogDroppedFarmers = false;
                return;
            }

            long stalledMs = now - lastNewDayProgressMs;
            if (stalledMs < NewDayStallWarnSeconds * 1000L)
                return;

            List<long> waiting = DescribeNewDayBlocker(out string blocker);
            if (waiting.Count == 0)
                return;

            if (now - lastNewDayWatchdogReportMs >= 15000)
            {
                lastNewDayWatchdogReportMs = now;
                Console.WriteLine($"[HeadlessNewDay] !! Day roll has made no progress for {stalledMs / 1000}s; missing replies from " +
                    $"{string.Join(", ", waiting.Select(id => $"{id} ({(clientConnections.ContainsKey(id) ? "connected" : "disconnected")})"))} at {blocker}");
            }

            if (stalledMs < NewDayStallDropSeconds * 1000L || newDayWatchdogDroppedFarmers)
                return;

            newDayWatchdogDroppedFarmers = true;
            List<long> disconnected = waiting.Where(id => !clientConnections.ContainsKey(id)).ToList();
            List<long> unresponsive = waiting.Where(id => clientConnections.ContainsKey(id)).ToList();
            Console.WriteLine($"[HeadlessNewDay] !! Dropping {disconnected.Count} disconnected and {unresponsive.Count} unresponsive farmhand(s) " +
                $"after {stalledMs / 1000}s without progress at {blocker}, so the day roll and its world save can finish.");
            DropFarmersForSleepBarrier(disconnected.Concat(unresponsive));
        }

        /// <summary>
        /// Names the barrier the roll is stuck on and the farmers that have not answered it.
        /// Unreached barriers have no entry in vanilla's barrier map and answered ones contain
        /// every online farmer, so any entry with a missing farmer is a blocker.
        /// </summary>
        private static List<long> DescribeNewDayBlocker(out string blocker)
        {
            blocker = "<unknown>";
            var waiting = new List<long>();
            try
            {
                NewDaySynchronizer? sync = Game1.newDaySync;
                FieldInfo? barriersField = typeof(NetSynchronizer).GetField("barriers", BindingFlags.Instance | BindingFlags.NonPublic);
                if (sync == null || barriersField?.GetValue(sync) is not Dictionary<string, HashSet<long>> barriers)
                {
                    blocker = "no barrier context";
                    return waiting;
                }

                var parts = new List<string>();
                foreach (var entry in barriers)
                {
                    List<long> missing = Game1.otherFarmers.Keys.Where(id => !entry.Value.Contains(id)).ToList();
                    if (missing.Count == 0)
                        continue;
                    parts.Add($"{entry.Key} (missing {string.Join("|", missing)})");
                    foreach (long id in missing)
                    {
                        if (!waiting.Contains(id))
                            waiting.Add(id);
                    }
                }
                blocker = parts.Count == 0 ? "every barrier satisfied" : string.Join("; ", parts);
            }
            catch (Exception ex)
            {
                blocker = $"blocker inspection failed: {ex.Message}";
            }
            return waiting;
        }

        /// <summary>
        /// Adds a farmhand to Multiplayer.disconnectingFarmers, vanilla's own path for taking a
        /// farmhand out of the online set: the next Multiplayer.UpdateEarly() (run by the day roll's
        /// message pump, or by the main loop when no roll is running) calls
        /// removeDisconnectedFarmers(), which removes it from Game1.otherFarmers on the thread that
        /// owns the update loop. Mutating Game1.otherFarmers directly from another thread would race
        /// the roll worker, which iterates that dictionary inside NetSynchronizer.barrierReady.
        /// Returns true when the farmhand is (now) marked for removal.
        /// </summary>
        internal static bool MarkFarmhandAsDisconnecting(long farmerId)
        {
            try
            {
                var mp = typeof(Game1).GetField("multiplayer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as Multiplayer;
                FieldInfo? field = typeof(Multiplayer).GetField("disconnectingFarmers", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mp == null || field?.GetValue(mp) is not List<long> disconnecting)
                {
                    Console.WriteLine("[HeadlessNewDay] !! Could not reach multiplayer.disconnectingFarmers; the farmhand cannot be removed safely.");
                    return false;
                }
                if (!disconnecting.Contains(farmerId))
                {
                    disconnecting.Add(farmerId);
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessNewDay] !! Marking farmhand {farmerId} as disconnecting failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Drops farmhands that are blocking the overnight barrier through vanilla's own removal
        /// path (see MarkFarmhandAsDisconnecting): the roll's message pump applies it within ~16ms,
        /// between its barrier checks, so it cannot race the worker's iteration of otherFarmers.
        /// </summary>
        private static void DropFarmersForSleepBarrier(IEnumerable<long> farmerIds)
        {
            int marked = 0;
            foreach (long id in farmerIds)
            {
                if (MarkFarmhandAsDisconnecting(id))
                {
                    marked++;
                }
            }
            Console.WriteLine($"[HeadlessNewDay] Marked {marked} unreachable farmhand(s) as disconnecting; the roll's own message pump " +
                "applies the removal, so the next barrier check should pass.");
        }

        /// <summary>
        /// Drops the outbound netcode changes queued on a NetDictionary (for example
        /// NetWorldState.farmhandData). Adding or removing an entry queues a change in the dictionary's
        /// private outgoingChanges list, and vanilla's write path enumerates that list while a
        /// re-entrant clean can clear it: the day roll then dies with "Collection was modified;
        /// enumeration operation may not execute" while writing NetWorldState field 'farmhandData'.
        /// Only call this at startup, before any client can have received the queued entries: the
        /// dictionary contents are untouched, so a client still gets the full state when it joins.
        /// </summary>
        internal static int DropQueuedNetDictionaryChanges(object netDictionary)
        {
            // GetField() only sees members declared on the type itself, so the private
            // outgoingChanges field of a generic base (NetDictionary`5) has to be looked up
            // by walking the base type chain by hand.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            for (Type? type = netDictionary.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo? field = type.GetField("outgoingChanges", flags);
                if (field?.GetValue(netDictionary) is System.Collections.IList changes)
                {
                    int dropped = changes.Count;
                    changes.Clear();
                    return dropped;
                }
            }
            return -1;
        }

        /// <summary>
        /// True when a client may activate this farmhand id. Reserved ids, the host's own id and ids
        /// that another live connection already owns are rejected: accepting them let a client evict
        /// the earlier player's mapping (whose disconnect then cleaned up nothing) while both
        /// connections kept writing deltas for the same farmhand. A stale mapping (connection no
        /// longer Connected) is allowed to be taken over.
        /// </summary>
        internal static bool IsClientFarmhandIdAcceptable(long farmhandId, NetConnection sender)
        {
            if (farmhandId == 0L || farmhandId == 99999999L || farmhandId == SelfTestFarmerId || farmhandId == Game1.player?.UniqueMultiplayerID)
            {
                return false;
            }
            if (clientConnections.TryGetValue(farmhandId, out NetConnection? existing)
                && existing != sender
                && existing.Status == NetConnectionStatus.Connected)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Registers farmhands whose handshake completed while a day roll was running. The roll's
        /// worker thread iterates Game1.otherFarmers inside NetSynchronizer.barrierReady, so the main
        /// loop applies the deferred roots only once no roll is active.
        /// </summary>
        private static void PumpPendingFarmhandRegistrations()
        {
            if (pendingFarmhandRegistrations.Count == 0)
            {
                return;
            }

            foreach (long id in pendingFarmhandRegistrations)
            {
                if (!pendingFarmhandRoots.TryGetValue(id, out NetFarmerRoot? root))
                {
                    continue;
                }
                if (!clientConnections.ContainsKey(id))
                {
                    Console.WriteLine($"[Protocol] Dropping the deferred online registration of farmhand {id}: its connection is gone.");
                    continue;
                }
                Game1.otherFarmers.Roots[id] = root;
                Console.WriteLine($"[Protocol] Deferred online registration of farmhand {id} applied after the day roll finished.");
            }

            pendingFarmhandRegistrations.Clear();
            pendingFarmhandRoots.Clear();
        }

        /// <summary>
        /// Discards overnight sync messages that arrived after the roll's last pump. Their
        /// generation is over: replaying them into the next roll would mix a finished day's
        /// ready/finish replies into the next day's barriers.
        /// </summary>
        private static void DropStaleDeferredOvernightMessages()
        {
            int dropped = 0;
            while (deferredOvernightMessages.TryDequeue(out _))
            {
                dropped++;
            }
            if (dropped > 0)
            {
                Console.WriteLine($"[Protocol] Dropped {dropped} deferred overnight sync message(s) left over from the finished day roll.");
            }
        }

        /// <summary>
        /// Creates the inert GameRunner singleton the headless host needs: an uninitialized
        /// instance holding an empty gameInstances list (LocalMultiplayer.IsLocalMultiplayer
        /// dereferences it) and a window shell (Options' ctor detaches its resize handler).
        /// Idempotent; both the startup mock block and the load pipeline call it.
        /// </summary>
        internal static void EnsureHeadlessGameRunner()
        {
            if (GameRunner.instance != null)
                return;
            var runnerMock = (GameRunner)FormatterServices.GetUninitializedObject(typeof(GameRunner));
            typeof(GameRunner).GetField("gameInstances", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(runnerMock, new List<Game1>());
            // GamePlatform/GameWindow are abstract (and internal) in MonoGame; the DesktopGL
            // build implements them as SdlGamePlatform/SdlGameWindow. Only inert shells are
            // needed: OnWindowSizeChange's base.Window.ClientSizeChanged -= handler is a
            // no-op on an uninitialized window whose event delegate is still null.
            Type platformType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.GamePlatform")!;
            Type windowType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.GameWindow")!;
            object platformMock = FormatterServices.GetUninitializedObject(FindConcreteShell(platformType));
            platformType.GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(platformMock, FormatterServices.GetUninitializedObject(FindConcreteShell(windowType)));
            typeof(Game).GetField("Platform", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.SetValue(runnerMock, platformMock);
            GameRunner.instance = runnerMock;
            Console.WriteLine("Mocked GameRunner.instance (empty gameInstances + window shell) for headless mode.");
        }

        /// <summary>
        /// Installs the inert statics that the vanilla Game1 constructor would have created.
        /// Headless mode bypasses that constructor, and both the save and the load pipeline
        /// run vanilla code that dereferences them, so every entry point has to install them
        /// before pumping vanilla enumerators. Idempotent: existing values are left alone.
        /// </summary>
        internal static void EnsureHeadlessCommonStatics()
        {
            EnsureHeadlessGameRunner();

            // Game1.nonWarpFade / fadeToBlack / globalFade all forward to this private
            // ScreenFade instance; a null one made Game1.loadForNewGame throw an NRE on its
            // very first statements (and Game1.NewDay threw later, after newDay was set).
            // Its state is plain fields and its callbacks are never invoked because headless
            // never runs UpdateFade, so a real instance with inert callbacks is safe.
            FieldInfo? screenFadeField = typeof(Game1).GetField("screenFade", BindingFlags.Static | BindingFlags.NonPublic);
            if (screenFadeField != null && screenFadeField.GetValue(null) == null)
            {
                screenFadeField.SetValue(null, new StardewValley.BellsAndWhistles.ScreenFade(() => false, () => { }));
                Console.WriteLine("Mocked Game1.screenFade for headless fade writes.");
            }

            // Game1.loadForNewGame constructs the ChatBox, whose constructor assigns
            // KeyboardDispatcher.Subscriber. The dispatcher lives in
            // game1.instanceKeyboardDispatcher (null here), so that assignment threw an NRE
            // and aborted the whole load. On Windows the real constructor only subscribes to
            // window.TextInput, which is inert for the headless window shell because nothing
            // ever pumps input events.
            if (Game1.keyboardDispatcher == null)
            {
                GameWindow? windowShell = GameRunner.instance?.Window;
                if (windowShell == null)
                {
                    Console.WriteLine("Cannot mock Game1.keyboardDispatcher: no window shell available.");
                }
                else
                {
                    Game1.keyboardDispatcher = new KeyboardDispatcher(windowShell);
                    Console.WriteLine("Mocked Game1.keyboardDispatcher for headless UI construction.");
                }
            }

            // Game1 now derives from InstanceGame, so every GraphicsDevice/Content/Components
            // lookup forwards to GameRunner.instance. The uninitialized runner holds no
            // graphics device service, which made Game.get_GraphicsDevice throw inside the
            // load pipeline. GraphicsDeviceManager implements IGraphicsDeviceService, so the
            // display chain mocked for Options also satisfies runner device queries.
            if (Game1.graphics != null)
            {
                FieldInfo? serviceField = typeof(Game)
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(field => typeof(IGraphicsDeviceService).IsAssignableFrom(field.FieldType));
                if (serviceField != null && serviceField.GetValue(GameRunner.instance) == null)
                {
                    serviceField.SetValue(GameRunner.instance, Game1.graphics);
                    Console.WriteLine("Wired GameRunner graphics device service for headless GraphicsDevice queries.");
                }
            }

            // BuffManager.GetValues marks the HUD buff display dirty for the locally
            // controlled farmer. Debris target selection reads each farmer's magnetic
            // radius through that path, so the host farmer's recalculation must not NRE
            // on the missing UI component. Provide an inert instance (no constructors run,
            // so nothing subscribes to game events).
            if (Game1.buffsDisplay == null)
            {
                Game1.buffsDisplay = (StardewValley.Menus.BuffsDisplay)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.BuffsDisplay));
                Console.WriteLine("Mocked Game1.buffsDisplay for headless buff recalculation.");
            }

            // getLoadEnumerator dereferences Game1.dayTimeMoneyBox at its very end; the
            // real Game1 constructor creates it, this headless instance does not.
            // Provide an inert instance (no constructors run) so the pipeline completes.
            FieldInfo? dayTimeMoneyBoxField = typeof(Game1).GetField("dayTimeMoneyBox",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (dayTimeMoneyBoxField != null && dayTimeMoneyBoxField.GetValue(null) == null)
            {
                dayTimeMoneyBoxField.SetValue(null, FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.DayTimeMoneyBox)));
                Console.WriteLine("Mocked Game1.dayTimeMoneyBox for headless load pipeline.");
            }

            // The headless server runs no audio device. Vanilla's Game1.Initialize falls
            // back to the game's own no-op audio stack when XACT cannot start, and gameplay
            // code calls these unconditionally (initializeVolumeLevels during the load
            // pipeline, playSound during festivals and tool use). Mirror that fallback so
            // every audio call is inert instead of null-dereferencing.
            if (Game1.audioEngine == null)
            {
                Type dummyAudioEngineType = typeof(Game1).Assembly.GetType("StardewValley.Audio.DummyAudioEngine")!;
                Game1.audioEngine = (StardewValley.Audio.IAudioEngine)Activator.CreateInstance(dummyAudioEngineType)!;
                Game1.soundBank = new DummySoundBank();
                Game1.audioEngine.Update();
                Game1.musicCategory = Game1.audioEngine.GetCategory("Music");
                Game1.soundCategory = Game1.audioEngine.GetCategory("Sound");
                Game1.ambientCategory = Game1.audioEngine.GetCategory("Ambient");
                Game1.footstepCategory = Game1.audioEngine.GetCategory("Footsteps");
                Game1.wind = Game1.soundBank.GetCue("wind");
                Game1.chargeUpSound = Game1.soundBank.GetCue("toolCharge");
                Console.WriteLine("Installed the no-op audio stack (DummyAudioEngine/DummySoundBank) for headless sound calls.");
            }

            // FarmerRenderer's textures normally come from Game1.LoadContent. A null
            // hairStylesTexture NREs while the load pipeline restores the host farmer
            // (Farmer.changeHairStyle -> GetLastHairStyle -> GetAllHairstyleIndices reads
            // hairStylesTexture.Height). The headless content manager hands out inert
            // 1280x1280 textures for any Texture2D request, which satisfies that math and
            // every other dimension lookup without touching the GPU.
            if (FarmerRenderer.hairStylesTexture == null)
            {
                FarmerRenderer.hairStylesTexture = Game1.content.Load<Texture2D>("Characters\\Farmer\\hairstyles");
                FarmerRenderer.shirtsTexture = Game1.content.Load<Texture2D>("Characters\\Farmer\\shirts");
                FarmerRenderer.hatsTexture = Game1.content.Load<Texture2D>("Characters\\Farmer\\hats");
                FarmerRenderer.accessoriesTexture = Game1.content.Load<Texture2D>("Characters\\Farmer\\accessories");
                FarmerRenderer.pantsTexture = Game1.content.Load<Texture2D>("Characters\\Farmer\\pants");
                Console.WriteLine("Loaded inert FarmerRenderer textures for headless hairstyle and clothing lookups.");
            }

            // Vanilla's Game1 constructor creates several collections that its own code
            // dereferences unconditionally (loadForNewGame ends with
            // newGameSetupOptions.Clear(), the debris pass walks _farmerShadows). The
            // headless instance runs no constructor, so every null collection field would
            // throw. Seed empty instances to match vanilla's post-constructor state.
            if (Game1.game1 != null)
                SeedEmptyCollections(Game1.game1);

            // Game1.Initialize creates the online-player dictionary, and Game1.GetPlayer
            // dereferences it unconditionally. The load pipeline reaches GetPlayer while it
            // replays queued team events (FarmerTeam.OnBuildingConstructedEvent ->
            // Building.performActionOnConstruction -> Cabin.CreateFarmhand), so a null
            // dictionary aborted the whole load. Restoring a world only needs it to exist:
            // farmhands live in netWorldState.farmhandData, and vanilla replaces this
            // instance as clients connect.
            if (Game1.otherFarmers == null)
            {
                Game1.otherFarmers = new NetRootDictionary<long, Farmer>();
                Game1.otherFarmers.Serializer = SaveSerializer.GetSerializer(typeof(Farmer));
                Console.WriteLine("Created Game1.otherFarmers for headless load pipeline.");
            }
        }

        /// <summary>
        /// Replaces every null array field of an inert mock with an empty array. Framework
        /// types (MonoGame's GraphicsDevice) copy their backing arrays into result arrays
        /// even when empty, which throws ArgumentNullException on uninitialized instances.
        /// </summary>
        private static void SeedEmptyArrays(object instance)
        {
            foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!field.FieldType.IsArray || field.GetValue(instance) != null)
                    continue;
                Type? elementType = field.FieldType.GetElementType();
                if (elementType == null)
                    continue;
                try
                {
                    field.SetValue(instance, Array.CreateInstance(elementType, 0));
                }
                catch
                {
                    // Best effort: fields that reject the value are left as they were.
                }
            }
        }

        /// <summary>
        /// Replaces every null List/Dictionary/HashSet field of a headless mock with an empty
        /// instance, matching the state vanilla reaches by running the real constructors
        /// (which those code paths then dereference without a null check). Best effort.
        /// </summary>
        private static void SeedEmptyCollections(object instance)
        {
            foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.GetValue(instance) != null)
                    continue;
                Type type = field.FieldType;
                if (!type.IsClass || type.IsAbstract || !type.IsGenericType)
                    continue;
                Type definition = type.GetGenericTypeDefinition();
                if (definition != typeof(List<>) && definition != typeof(Dictionary<,>) && definition != typeof(HashSet<>))
                    continue;
                try
                {
                    field.SetValue(instance, Activator.CreateInstance(type));
                }
                catch
                {
                    // Best effort: fields that reject the value are left as they were.
                }
            }
        }

        /// <summary>
        /// Returns <paramref name="type"/> itself when it can be instantiated without a
        /// constructor, otherwise the first concrete subclass. MonoGame's abstract
        /// GamePlatform/GameWindow are implemented by the internal Sdl* types, which
        /// <see cref="FormatterServices.GetUninitializedObject(Type)"/> accepts (it
        /// refuses abstract types).
        /// </summary>
        private static Type FindConcreteShell(Type type)
        {
            if (!type.IsAbstract)
                return type;
            try
            {
                return type.Assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && type.IsAssignableFrom(t)) ?? type;
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).FirstOrDefault(t => !t!.IsAbstract && type.IsAssignableFrom(t!)) ?? type;
            }
        }

        /// <summary>
        /// Installs the inert MonoGame display chain that headless Options construction and
        /// every GraphicsDevice query forwards to: platform/window shells, a one-entry
        /// display-mode list, a graphics device and its device manager. No native resources
        /// are created. Idempotent, so callers may install it before any vanilla code that
        /// reads options or graphics state.
        /// </summary>
        internal static void EnsureHeadlessGraphics()
        {
            EnsureHeadlessGameRunner();
            if (Game1.graphics != null)
                return;

            // MonoGame's GraphicsAdapter.SupportedDisplayModes unconditionally asks
            // SDL for the window's display index (SdlGameWindow.Instance.Handle) and
            // re-enumerates modes unless the cached _displayIndex matches that value.
            // Install an inert window shell and seed a one-entry mode list with the
            // index SDL reports, so the getter returns the cached list.
            Type sdlWindowType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.SdlGameWindow")!;
            FieldInfo? windowInstanceField = sdlWindowType.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (windowInstanceField?.GetValue(null) == null)
            {
                object windowShell = FormatterServices.GetUninitializedObject(FindConcreteShell(sdlWindowType));
                sdlWindowType.GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(windowShell, IntPtr.Zero);
                windowInstanceField?.SetValue(null, windowShell);
                Console.WriteLine("Mocked SdlGameWindow.Instance for headless display queries.");
            }
            int displayIndex = -1;
            try
            {
                // MonoGame's SDL wrapper lives in the global namespace as the
                // nested type Sdl+Display, so resolve it by name instead of by
                // an assumed Microsoft.Xna.Framework prefix.
                MethodInfo? getDisplayIndex = typeof(Game).Assembly.GetType("Sdl+Display")
                    ?.GetMethod("GetWindowDisplayIndex", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDisplayIndex != null)
                {
                    displayIndex = (int)getDisplayIndex.Invoke(null, new object[] { IntPtr.Zero })!;
                    Console.WriteLine($"Headless SDL display index probe returned {displayIndex}.");
                }
                else
                {
                    Console.WriteLine("Headless SDL display index probe unavailable; using sentinel index.");
                }
            }
            catch (Exception probeEx)
            {
                Console.WriteLine($"Headless SDL display index probe failed; using sentinel index: {probeEx.Message}");
            }
            var modeCtor = typeof(DisplayMode).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(int), typeof(SurfaceFormat) }, null);
            var mode = (DisplayMode)modeCtor!.Invoke(new object[] { 1920, 1080, SurfaceFormat.Color });
            // Both DisplayMode's and DisplayModeCollection's constructors are
            // internal, so reflection flags are required to reach them.
            var modes = (DisplayModeCollection)Activator.CreateInstance(typeof(DisplayModeCollection),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new object[] { new List<DisplayMode> { mode } }, null)!;
            var adapterMock = (GraphicsAdapter)FormatterServices.GetUninitializedObject(typeof(GraphicsAdapter));
            typeof(GraphicsAdapter).GetField("_supportedDisplayModes", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(adapterMock, modes);
            typeof(GraphicsAdapter).GetField("_displayIndex", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(adapterMock, displayIndex);
            var deviceMock = (GraphicsDevice)FormatterServices.GetUninitializedObject(typeof(GraphicsDevice));
            typeof(GraphicsDevice).GetField("<Adapter>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(deviceMock, adapterMock);
            // MonoGame's GraphicsDevice.GetRenderTargets() copies its backing array
            // into a result array; on an uninitialized device that field is null and
            // Array.Copy throws ArgumentNullException. Utility.getSafeArea() calls it
            // whenever a menu computes its position, so seed empty arrays.
            SeedEmptyArrays(deviceMock);
            var gdmMock = (GraphicsDeviceManager)FormatterServices.GetUninitializedObject(typeof(GraphicsDeviceManager));
            typeof(GraphicsDeviceManager).GetField("_graphicsDevice", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(gdmMock, deviceMock);
            Game1.graphics = gdmMock;
            Console.WriteLine("Mocked Game1.graphics display chain for headless display queries.");
        }

        /// <summary>
        /// Restores the persisted world from a vanilla save slot on startup: validates the
        /// slot with <see cref="SaveGame.TryReadSaveFileWithFallback"/>, then pumps the
        /// native <see cref="SaveGame.getLoadEnumerator(string)"/> pipeline, which rebuilds
        /// the host farmer, farmhands, every location, world state, and weather exactly as
        /// the retail game does. Returns false (caller falls back to a fresh world) when no
        /// slot is configured/detected or the pipeline fails.
        /// </summary>
        /// <summary>
        /// Set when a save slot existed but could not be read and the operator has not opted in
        /// to starting fresh. Main turns this into a refusal to start: see HandleUnreadableSlot.
        /// </summary>
        internal static bool WorldLoadBlockedByUnreadableSlot { get; private set; }

        /// <summary>The slot that blocked startup, for the operator-facing message.</summary>
        internal static string BlockedSlotName { get; private set; } = string.Empty;

        /// <summary>Size of a slot's main data file, for the startup slot listing (0 when absent).</summary>
        private static long SlotDataFileSize(DirectoryInfo slot)
        {
            try
            {
                FileInfo dataFile = new FileInfo(Path.Combine(slot.FullName, slot.Name));
                return dataFile.Exists ? dataFile.Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        internal static bool TryLoadWorldFromDisk()
        {
            string savesFolder = StardewValley.Program.GetSavesFolder();
            string slotFolderName = string.Empty;
            bool loadPumpStarted = false;
            try
            {
                if (!Directory.Exists(savesFolder))
                    return false;

                slotFolderName = ServerConfig.Current.Paths.SaveSlotName;
                if (string.IsNullOrWhiteSpace(slotFolderName))
                {
                    // Auto-detect: the newest slot folder for the configured farm name,
                    // mirroring the layout vanilla creates (FarmName_<uniqueId>). Several slots
                    // can match (every fresh world mints a new unique id), so report all of them:
                    // silently restoring the newest one is how a test world can shadow the
                    // player's real progress.
                    string prefix = SaveGame.FilterFileName(ServerConfig.Current.World.FarmName) + "_";
                    DirectoryInfo[] candidates = new DirectoryInfo(savesFolder)
                        .GetDirectories(prefix + "*")
                        .Where(d => !d.Name.Contains("_unloadable_", StringComparison.OrdinalIgnoreCase))
                        .Where(d => File.Exists(Path.Combine(d.FullName, d.Name)))
                        .OrderByDescending(d => d.LastWriteTime)
                        .ToArray();
                    slotFolderName = candidates.FirstOrDefault()?.Name;
                    if (candidates.Length > 1)
                    {
                        Console.WriteLine($"[HeadlessSave] {candidates.Length} save slots match farm name '{ServerConfig.Current.World.FarmName}'; restoring the newest:");
                        foreach (DirectoryInfo candidate in candidates)
                        {
                            Console.WriteLine($"[HeadlessSave]   {(candidate.Name == slotFolderName ? "->" : "  ")} {candidate.Name} " +
                                $"({candidate.LastWriteTime:yyyy-MM-dd HH:mm}, {SlotDataFileSize(candidate):N0} bytes)");
                        }
                        Console.WriteLine("[HeadlessSave]   Set Paths.SaveSlotName to a specific slot name to restore that one instead.");
                    }
                }
                if (string.IsNullOrWhiteSpace(slotFolderName) || !Directory.Exists(Path.Combine(savesFolder, slotFolderName)))
                    return false;

                // Options construction (during save deserialization) and every
                // GraphicsDevice query forward to the mocked display chain.
                EnsureHeadlessGraphics();

                // Validate before pumping: getLoadEnumerator flips gameMode to 9 on failure,
                // which would leave the half-initialized host in an error state.
                SaveGame probe = SaveGame.TryReadSaveFileWithFallback(slotFolderName, out string error, out bool recovered);
                if (probe == null)
                {
                    Console.WriteLine($"[HeadlessSave] Slot {slotFolderName} failed to load: {error}");
                    // TryReadSaveFileWithFallback only reports the outer message, which
                    // hides the element that actually failed. Re-run the raw deserialize
                    // here so the full exception chain (with inner messages) is logged.
                    try
                    {
                        string dataFile = Path.Combine(savesFolder, slotFolderName, slotFolderName);
                        byte[] raw = File.ReadAllBytes(dataFile);
                        using MemoryStream ms = new MemoryStream(raw, writable: false);
                        if (ms.ReadByte() == 120) // zlib header; day-end saves are uncompressed
                            throw new InvalidDataException("Compressed save not supported by probe");
                        ms.Position = 0;
                        SaveSerializer.Deserialize<SaveGame>(ms);
                        Console.WriteLine("[HeadlessSave] Manual re-read succeeded (inconsistent failure).");
                    }
                    catch (Exception probeEx)
                    {
                        Console.WriteLine($"[HeadlessSave] Full failure detail: {probeEx}");
                    }
                    HandleUnreadableSlot(savesFolder, slotFolderName, loadPumpStarted: false);
                    return false;
                }
                Console.WriteLine($"[HeadlessSave] Restoring world from slot {slotFolderName} (day={probe.dayOfMonth} year={probe.year})...");
                if (recovered)
                    Console.WriteLine("[HeadlessSave] Save file was corrupted; auto-recovered it from the backup.");

                // getLoadEnumerator dereferences several statics that only the real Game1
                // constructor creates (screenFade, dayTimeMoneyBox, ...). Install them
                // before any vanilla code runs.
                EnsureHeadlessCommonStatics();

                // getLoadEnumerator applies several stages on background Tasks whenever
                // LocalMultiplayer reports remote multiplayer, and rethrows task failures
                // after the original stack trace has been reset. Record first-chance
                // exceptions (with IL offsets) so a headless failure still reports its
                // real origin.
                var firstChance = new List<string>();
                int firstChanceCount = 0;
                EventHandler<FirstChanceExceptionEventArgs> capture = (_, args) =>
                {
                    firstChanceCount++;
                    // Keep the most recent exceptions: vanilla retries throw-and-catch many
                    // benign exceptions early, and the interesting one is the last.
                    if (firstChance.Count >= 40)
                        firstChance.RemoveAt(0);
                    int ilOffset = -1;
                    string origin = "<no stack trace>";
                    try
                    {
                        StackTrace trace = new StackTrace(args.Exception);
                        ilOffset = trace.GetFrame(0)?.GetILOffset() ?? -1;
                        // Keep a few frames: the failing frame is often a framework method
                        // whose caller is the only place that identifies the missing object.
                        string[] frames = (args.Exception.StackTrace ?? string.Empty)
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim())
                            .Take(4)
                            .ToArray();
                        if (frames.Length > 0)
                            origin = string.Join(" <- ", frames);
                    }
                    catch
                    {
                        // Stack inspection is best effort only.
                    }
                    firstChance.Add($"{args.Exception.GetType().Name} IL_{ilOffset:X4} {origin}");
                };
                AppDomain.CurrentDomain.FirstChanceException += capture;
                try
                {
                    // From here on the pipeline mutates Game1's statics (farm name, unique id,
                    // date, locations), so a failure stops being recoverable by building a fresh
                    // world in this process — see HandleUnreadableSlot.
                    loadPumpStarted = true;
                    IEnumerator<int> loader = SaveGame.getLoadEnumerator(slotFolderName);
                    int steps = 0;
                    while (loader != null && loader.MoveNext())
                    {
                        steps++;
                    }
                    Console.WriteLine($"[HeadlessSave] World load finished ({steps} steps): day={Game1.dayOfMonth} year={Game1.year} season={Game1.season} " +
                        $"locations={Game1.locations.Count} farmhands={Game1.netWorldState.Value.farmhandData.Count()} host={Game1.player?.Name}");
                }
                catch (Exception loadEx)
                {
                    Console.WriteLine($"[HeadlessSave] Load pipeline threw: {loadEx}");
                    DumpLoadFailureState();
                    Console.WriteLine($"[HeadlessSave] First-chance exceptions ({firstChanceCount} total):");
                    foreach (string origin in firstChance)
                        Console.WriteLine($"[HeadlessSave]   {origin}");
                    throw;
                }
                finally
                {
                    AppDomain.CurrentDomain.FirstChanceException -= capture;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessSave] World load failed: {ex}");
                HandleUnreadableSlot(savesFolder, slotFolderName, loadPumpStarted);
                return false;
            }
        }

        /// <summary>
        /// An existing slot could not be read, so the world in memory is not the player's world: a
        /// partially applied load may already have taken over its farm name, unique id and date.
        /// Building a fresh world on top of that is exactly how the original save got overwritten.
        /// Default: refuse to start and leave every file untouched so the slot can be inspected.
        /// Opt-in (World.StartFreshWhenSaveUnreadable): rename the unreadable slot aside, then let
        /// the fallback world start — but only when the failure happened before the load pipeline
        /// ran, because a half-applied load would make the fresh world itself corrupt.
        /// </summary>
        private static void HandleUnreadableSlot(string savesFolder, string slotFolderName, bool loadPumpStarted)
        {
            string slotPath = Path.Combine(savesFolder, slotFolderName);
            if (ServerConfig.Current.World.StartFreshWhenSaveUnreadable && !loadPumpStarted)
            {
                PreserveUnloadableSlot(savesFolder, slotFolderName);
                Console.WriteLine("[HeadlessSave] World.StartFreshWhenSaveUnreadable is true, so a fresh world starts in this " +
                    "session; the slot above was renamed, never deleted, and can be restored by hand.");
                return;
            }

            WorldLoadBlockedByUnreadableSlot = true;
            BlockedSlotName = slotFolderName;
            if (loadPumpStarted)
            {
                Console.WriteLine($"[HeadlessSave] !! Refusing to start: reading slot '{slotPath}' failed part-way through the vanilla " +
                    "load pipeline, so this process already holds a half-applied mixture of the saved world and a new one. A fresh world " +
                    "built on it would be corrupt and would save under this slot's farm name and date.");
                Console.WriteLine("[HeadlessSave] !! The slot itself is untouched. To play a clean fresh world without touching it, set " +
                    "Paths.SaveSlotName to a new, unused slot name and restart; otherwise repair or inspect the slot first.");
                return;
            }

            Console.WriteLine($"[HeadlessSave] !! Refusing to start: the save slot '{slotPath}' exists but could not be read, and a " +
                "fresh world would keep its farm name and unique id, so the next day-end save would overwrite the player's progress.");
            Console.WriteLine("[HeadlessSave] !! Nothing on disk was modified. Inspect or repair that slot, point Paths.SaveSlotName at a " +
                "different slot, or set World.StartFreshWhenSaveUnreadable to true to start fresh anyway (the slot is then renamed aside first).");
        }

        /// <summary>
        /// Moves a save slot the vanilla load pipeline cannot read out of the way before the
        /// caller falls back to a brand-new world. Without this the fallback world's
        /// end-of-day save would overwrite the player's real progress in that slot, which is
        /// the exact data loss this server must never cause. The folder is renamed, never
        /// deleted, so it can be restored by hand; the new name is reported in the log.
        /// </summary>
        private static void PreserveUnloadableSlot(string savesFolder, string slotFolderName)
        {
            if (string.IsNullOrWhiteSpace(slotFolderName))
                return;
            try
            {
                string source = Path.Combine(savesFolder, slotFolderName);
                if (!Directory.Exists(source))
                    return;
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string target = Path.Combine(savesFolder, $"{slotFolderName}_unloadable_{stamp}");
                int suffix = 1;
                while (Directory.Exists(target))
                    target = Path.Combine(savesFolder, $"{slotFolderName}_unloadable_{stamp}_{suffix++}");
                Directory.Move(source, target);
                Console.WriteLine($"[HeadlessSave] !! The slot '{slotFolderName}' could not be read, so it was renamed to " +
                    $"'{Path.GetFileName(target)}' instead of being overwritten by the fallback world. " +
                    $"Rename it back to '{slotFolderName}' after fixing or inspecting the save.");
            }
            catch (Exception moveEx)
            {
                Console.WriteLine($"[HeadlessSave] !! Could not preserve unreadable slot '{slotFolderName}': {moveEx.Message}. " +
                    "Move that folder aside by hand before the fallback world saves over it.");
            }
        }

        /// <summary>
        /// Probes the world state that the native load pipeline depends on, so a failure
        /// inside the opaque iterator can be traced to the exact missing object without
        /// guessing which vanilla source line threw.
        /// </summary>
        private static void DumpLoadFailureState()
        {
            void Report(string label, Func<string> probe)
            {
                try
                {
                    Console.WriteLine($"[HeadlessSave]   {label}: {probe()}");
                }
                catch (Exception probeException)
                {
                    Console.WriteLine($"[HeadlessSave]   {label}: probe threw {probeException.GetType().Name}: {probeException.Message}");
                }
            }

            Console.WriteLine("[HeadlessSave] Load failure state dump:");
            Report("null Game1 instance fields", () =>
            {
                object? game = typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                if (game == null)
                    return "<no Game1.game1 instance>";
                var nullFields = new List<string>();
                foreach (FieldInfo field in typeof(Game1).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (!field.FieldType.IsValueType && field.GetValue(game) == null)
                            nullFields.Add(field.Name);
                    }
                    catch
                    {
                        // Fields that cannot be read are not interesting here.
                    }
                }
                return $"{nullFields.Count} null fields: {string.Join(", ", nullFields)}";
            });
            Report("stage (Game1.loadingMessage)", () => Game1.loadingMessage ?? "<null>");
            Report("Game1.year/uniqueID/gameMode", () => $"year={Game1.year} uniqueID={Game1.uniqueIDForThisGame} gameMode={Game1.gameMode} saveName={Game1.GetSaveGameName() ?? "<null>"}");
            Report("save-fix flags", () =>
            {
                object? flag13 = typeof(Game1).GetField("hasApplied1_3_UpdateChanges", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                object? flag14 = typeof(Game1).GetField("hasApplied1_4_UpdateChanges", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
                return $"hasApplied1_3={flag13} hasApplied1_4={flag14} lastFix={Game1.lastAppliedSaveFix}";
            });
            Report("Game1.locations", () => Game1.locations == null
                ? "<null>"
                : $"{Game1.locations.Count} entries [{string.Join(", ", Game1.locations.Take(4).Select(location => location?.NameOrUniqueName ?? "<null>"))}]");
            Report("Game1.getFarm()", () => Game1.getFarm()?.NameOrUniqueName ?? "<null>");
            Report("Game1.player", () => Game1.player == null
                ? "<null>"
                : $"{Game1.player.Name} uid={Game1.player.UniqueMultiplayerID} location={Game1.player.currentLocation?.NameOrUniqueName ?? "<null>"}");
            Report("Game1.player.team", () => Game1.player?.team == null ? "<null>" : "ok");
            Report("Game1.options", () => Game1.options == null ? "<null>" : "ok");
            Report("Game1.netWorldState", () => Game1.netWorldState?.Value == null ? "<null>" : $"day={Game1.netWorldState.Value.Date.DayOfMonth} farmhands={Game1.netWorldState.Value.farmhandData.Count()}");
            Report("farm.getShippingBin(player)", () =>
            {
                Farm? farm = Game1.getFarm();
                if (farm == null)
                    return "skipped (no farm)";
                IList<Item> shippingBin = farm.getShippingBin(Game1.player);
                return shippingBin == null ? "<null>" : $"{shippingBin.Count} items";
            });
            Report("Game1.getLocationFromName(Railroad)", () => Game1.getLocationFromName("Railroad")?.NameOrUniqueName ?? "<null>");
        }

        /// <summary>
        /// Drives the native <see cref="SaveGame.Save()"/> coroutine to completion, writing
        /// a full world save to the vanilla save directory. Vanilla runs this inside the
        /// SaveGameMenu the host bypasses; pumping it after the overnight coroutine makes
        /// world progress (day, farm objects, crops, world state) survive a restart.
        /// </summary>
        /// <returns>True when the enumerator completed, false when it threw (already logged loudly).</returns>
        private static bool TrySaveWorldToDisk()
        {
            try
            {
                // Vanilla computes the save slot from the host farmer's slotName; the
                // new-game flow sets it via Game1.SetSaveName, which this host bypasses.
                // SetSaveName expects the bare farm name (getSaveEnumerator appends the
                // unique id itself; getLoadEnumerator strips everything after "_").
                if (Game1.player != null && string.IsNullOrEmpty(Game1.player.slotName))
                    Game1.SetSaveName(ServerConfig.Current.World.FarmName);
                Console.WriteLine(DescribeNewDayState("before-worldsave"));
                IEnumerator<int>? saver = SaveGame.Save();
                int steps = 0;
                while (saver != null && saver.MoveNext())
                {
                    steps++;
                }
                // Game1.player.slotName stays empty on the load path (the enumerator derives the
                // folder from the farm name and unique id itself), so report the slot that was
                // actually written instead of the field.
                Console.WriteLine($"[HeadlessSave] World save enumerator finished ({steps} steps): day={Game1.dayOfMonth} " +
                    $"time={Game1.timeOfDay} slot={NewestSlotDescription()}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HeadlessSave] !! World save FAILED: day {Game1.dayOfMonth} at {Game1.timeOfDay} is still " +
                    $"only in memory and will be lost on restart: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Operator-facing world save behind the <c>save</c> command: refuses while a day roll is
        /// half-applied, because what is in memory then is not a consistent world.
        /// </summary>
        internal static void ForceWorldSave()
        {
            if (headlessNewDayActive || Game1.newDay)
            {
                Console.WriteLine("[HeadlessSave] A day roll is in progress, so the world is half-applied; try again once it finishes.");
                return;
            }
            Console.WriteLine("[HeadlessSave] Saving the world on request...");
            if (TrySaveWorldToDisk())
                Console.WriteLine("[HeadlessSave] World saved on request.");
        }

        /// <summary>
        /// Names the slot folder written most recently, for save log lines. The enumerator
        /// derives that folder from the farm name plus unique id and never fills in
        /// Game1.player.slotName on the load path, so reading the field back is misleading.
        /// </summary>
        private static string NewestSlotDescription()
        {
            try
            {
                DirectoryInfo? newest = new DirectoryInfo(StardewValley.Program.GetSavesFolder())
                    .GetDirectories()
                    .OrderByDescending(d => d.LastWriteTime)
                    .FirstOrDefault();
                return newest == null ? "<none>" : $"{newest.Name} ({newest.LastWriteTime:yyyy-MM-dd HH:mm:ss})";
            }
            catch (Exception ex)
            {
                return $"<unavailable: {ex.Message}>";
            }
        }

        /// <summary>
        /// Directory holding the saved farmhand XML files. A relative configured path
        /// resolves against the executable directory.
        /// </summary>
        private static string savedFarmhandsPath
        {
            get
            {
                string configured = ServerConfig.Current.Paths.SaveDirectory;
                return Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configured);
            }
        }
        private static HashSet<long> savedFarmerIds = new HashSet<long>();
        // The authoritative catalog of every farmhand that has ever been created/saved,
        // used to populate the character-selection list. It is intentionally kept separate
        // from Game1.otherFarmers, which only ever holds currently-online players and is
        // pruned by Multiplayer.removeDisconnectedFarmers during the day roll.
        private static readonly Dictionary<long, Farmer> savedFarmhandCatalog = new();

        static List<Farmer> LoadSavedFarmhands()
        {
            var list = new List<Farmer>();
            if (!Directory.Exists(savedFarmhandsPath))
            {
                Directory.CreateDirectory(savedFarmhandsPath);
            }

            foreach (var file in Directory.GetFiles(savedFarmhandsPath, "*.xml"))
            {
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read))
                    {
                        var serializer = SaveSerializer.GetSerializer(typeof(Farmer));
                        var farmer = serializer.Deserialize(stream) as Farmer;
                        if (farmer != null && farmer.UniqueMultiplayerID != 99999999L && farmer.UniqueMultiplayerID != 0)
                        {
                            // Keep every saved farmhand in the selection catalog. Only live
                            // connections are registered into Game1.otherFarmers later (Message 2).
                            savedFarmhandCatalog[farmer.UniqueMultiplayerID] = farmer;
                            list.Add(farmer);
                            savedFarmerIds.Add(farmer.UniqueMultiplayerID);

                            // The world-state directory is a persistent directory of all farmhands
                            // in the world (like vanilla SaveGame does on load); it is not purged the
                            // way otherFarmers is, so it is safe to keep offline farmhands here for
                            // home/cabin resolution.
                            if (!Game1.netWorldState.Value.farmhandData.ContainsKey(farmer.UniqueMultiplayerID))
                            {
                                Game1.netWorldState.Value.farmhandData[farmer.UniqueMultiplayerID] = farmer;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading saved farmhand from {file}: {ex}");
                }
            }
            return list;
        }

        static void SaveFarmhand(Farmer farmer)
        {
            if (!Directory.Exists(savedFarmhandsPath))
            {
                Directory.CreateDirectory(savedFarmhandsPath);
            }

            string filePath = Path.Combine(savedFarmhandsPath, $"{farmer.UniqueMultiplayerID}.xml");
            string tempPath = filePath + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var serializer = SaveSerializer.GetSerializer(typeof(Farmer));
                    serializer.Serialize(stream, farmer);
                    stream.Flush(true);
                }
                File.Move(tempPath, filePath, true);
                Console.WriteLine($"Successfully saved farmhand {farmer.Name} ({farmer.UniqueMultiplayerID}) to {filePath}");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                Console.WriteLine($"Error saving farmhand {farmer.Name}: {ex.Message}");
            }
        }

        static void SaveAllActiveFarmhands()
        {
            foreach (var rootKvp in Game1.otherFarmers.Roots)
            {
                var farmer = rootKvp.Value.Value;
                if (farmer != null && farmer.UniqueMultiplayerID != 99999999L && farmer.UniqueMultiplayerID != 0 && farmer.isCustomized.Value)
                {
                    SaveFarmhand(farmer);
                }
            }
        }

        static void BroadcastMessage(OutgoingMessage outMsg, NetServer server, NetConnection? excludeConnection = null)
        {
            if (server.Connections.Count == 0) return;
            
            var msg = server.CreateMessage();
            MockLidgrenMessageUtils.WriteMessage(outMsg, msg);
            
            List<NetConnection> targets = new List<NetConnection>();
            foreach (var conn in server.Connections)
            {
                if (conn != excludeConnection && conn.Status == NetConnectionStatus.Connected)
                {
                    targets.Add(conn);
                }
            }

            if (targets.Count > 0)
            {
                server.SendMessage(msg, targets, NetDeliveryMethod.ReliableOrdered, 0);
            }
        }

        static byte[] WriteObjectFullBytes<T>(NetRoot<T> root, long peer) where T : class, INetObject<INetSerializable>
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    root.CreateConnectionPacket(writer, peer);
                    return stream.ToArray();
                }
            }
        }
    }
}
