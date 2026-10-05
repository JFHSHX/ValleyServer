#pragma warning disable SYSLIB0050

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace HeadlessServer
{
    /// <summary>
    /// Headless diagnostics: file-driven command input plus commands that probe which
    /// gameplay systems actually work in the headless host. The server disables console
    /// input when no TTY is attached (service/container/CI), so operator commands can also
    /// be submitted by appending lines to <c>server-commands.txt</c> beside the executable;
    /// the file is consumed and truncated on the next main-loop pass.
    /// </summary>
    partial class Program
    {
        private static readonly string commandFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server-commands.txt");

        private static long commandFilePosition = 0;

        private static void RegisterDiagnosticCommands()
        {
            commandRegistry.Register(new ServerCommand(
                name: "diag",
                usage: "diag",
                description: "Prints a snapshot of world state: date, weather, NPC counts, crops.",
                handler: _ => PrintDiagnostics()));

            commandRegistry.Register(new ServerCommand(
                name: "plant",
                usage: "plant [cropSeedItemId] [count]",
                description: "Plants watered test crops near the farmhouse porch for day-roll probing.",
                handler: a => PlantTestCrops(
                    a.Length > 0 ? a[0] : "472",
                    a.Length > 1 && int.TryParse(a[1], out int n) ? Math.Clamp(n, 1, 20) : 4)));

            commandRegistry.Register(new ServerCommand(
                name: "timeskip",
                usage: "timeskip <inGameMinutes>",
                description: "Advances the in-game clock directly (for schedule probing without clients).",
                handler: a =>
                {
                    int mins = a.Length > 0 && int.TryParse(a[0], out int m) ? m : 600;
                    long msPerTen = Math.Max(1, ServerConfig.Current.Simulation.MillisecondsPerTenMinutes);
                    for (int i = 0; i < mins / 10; i++)
                        AdvanceHeadlessClock(msPerTen);
                    Console.WriteLine($"[Diag] time now {Game1.timeOfDay}; npc spot-check: {NpcSpotCheck()}");
                }));

            commandRegistry.Register(new ServerCommand(
                name: "probe",
                usage: "probe <npcName>",
                description: "Steps one NPC's location updates manually and reports position deltas.",
                handler: a => ProbeNpc(a.Length > 0 ? a[0] : "Haley")));

            commandRegistry.Register(new ServerCommand(
                name: "roll",
                usage: "roll",
                description: "Forces an overnight NewDay roll (host sleeps immediately, no clients needed).",
                handler: _ => ForceDayRoll()));
        }

        private static void PumpCommandFile()
        {
            try
            {
                if (!File.Exists(commandFilePath))
                    return;
                var info = new FileInfo(commandFilePath);
                if (info.Length <= commandFilePosition)
                {
                    if (info.Length < commandFilePosition)
                        commandFilePosition = 0;
                    return;
                }
                using var stream = new FileStream(commandFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(commandFilePosition, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                        continue;
                    Console.WriteLine($"[Commands][file] {trimmed}");
                    string[] tokens = ServerCommandRegistry.Tokenize(trimmed);
                    if (tokens.Length == 0)
                        continue;
                    ServerCommand? command = commandRegistry.Find(tokens[0]);
                    if (command == null)
                    {
                        Console.WriteLine($"[Commands] Unknown command '{tokens[0]}'. Type 'help' for the list.");
                        continue;
                    }
                    try
                    {
                        command.Handler(tokens.Length > 1 ? tokens[1..] : Array.Empty<string>());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Commands] '{command.Name}' failed: {ex}");
                    }
                }
                commandFilePosition = stream.Position;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Commands] Command file pump error: {ex.Message}");
            }
        }

        private static void PrintDiagnostics()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[Diag] {Game1.season} Y{Game1.year} D{Game1.dayOfMonth} time={Game1.timeOfDay} newDay={Game1.newDay} daySyncActive={headlessNewDayActive}");
                try
                {
                    sb.AppendLine($"[Diag] weather: raining={Game1.isRaining} snowing={Game1.isSnowing} lightning={Game1.isLightning}");
                sb.AppendLine($"[Diag] clock: shouldTimePass={Game1.shouldTimePass()} timePaused={Game1.netWorldState?.Value?.IsTimePaused} festival={Game1.isFestival()} farmEvent={(Game1.farmEvent != null)} pausedField={Game1.paused} currentLoc={Game1.currentLocation?.Name}");
                }
                catch (Exception wex) { sb.AppendLine($"[Diag] weather query failed: {wex.Message}"); }

                int totalNpcs = 0;
                var npcCounts = new List<string>();
                foreach (GameLocation loc in Game1.locations)
                {
                    try
                    {
                        int c = loc.characters.Count;
                        if (c > 0)
                        {
                            totalNpcs += c;
                            npcCounts.Add($"{loc.NameOrUniqueName}:{c}");
                        }
                    }
                    catch { }
                }
                sb.AppendLine($"[Diag] NPCs total={totalNpcs} by-location=[{string.Join(" ", npcCounts)}]");

                Farm? farm = Game1.getFarm();
                if (farm != null)
                {
                    int hoed = 0, crops = 0;
                    var cropDetails = new List<string>();
                    foreach (var kv in farm.terrainFeatures.Pairs)
                    {
                        if (kv.Value is HoeDirt dirt)
                        {
                            hoed++;
                            if (dirt.crop != null)
                            {
                                crops++;
                                cropDetails.Add($"({kv.Key.X},{kv.Key.Y}) {dirt.crop.netSeedIndex.Value}/phase{dirt.crop.currentPhase.Value}/day{dirt.crop.dayOfCurrentPhase.Value}/watered={dirt.state.Value}");
                            }
                        }
                    }
                    sb.AppendLine($"[Diag] farm hoedirt={hoed} crops={crops}");
                    foreach (string d in cropDetails.Take(8))
                        sb.AppendLine($"[Diag]   crop {d}");
                }
                sb.AppendLine($"[Diag] locations={Game1.locations.Count()} onlineFarmers={(Game1.otherFarmers?.Roots.Count.ToString() ?? "?")} herds={Game1.getFarm()?.animals?.Count().ToString() ?? "?"}");
                Console.Write(sb.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Diag] failed: {ex}");
            }
        }

        private static string NpcSpotCheck()
        {
            var parts = new List<string>();
            foreach (string name in new[] { "Pierre", "Lewis", "Abigail", "George" })
            {
                foreach (GameLocation loc in Game1.locations)
                {
                    var npc = loc.characters.FirstOrDefault(c => c.Name == name);
                    if (npc != null)
                    {
                        parts.Add($"{name}@{loc.Name}({npc.Tile.X:0},{npc.Tile.Y:0}) sched={(npc.Schedule == null ? "null" : npc.Schedule.Count.ToString())} ctrl={(npc.controller == null ? "-" : npc.controller.pathToEndPoint?.Count.ToString() ?? "?")} ignoreSched={npc.ignoreScheduleToday}");
                        break;
                    }
                }
            }
            return string.Join(" ", parts);
        }

        private static void PlantTestCrops(string seedItemId, int count)
        {
            Farm? farm = Game1.getFarm();
            if (farm == null)
            {
                Console.WriteLine("[Plant] Farm unavailable.");
                return;
            }
            int planted = 0;
            // Porch area around x=64,y=15, scan outward for free tappable tiles.
            for (int x = 60; x < 70 && planted < count; x++)
            {
                for (int y = 12; y < 22 && planted < count; y++)
                {
                    var tile = new Vector2(x, y);
                    if (farm.terrainFeatures.ContainsKey(tile) || farm.objects.ContainsKey(tile))
                        continue;
                    try
                    {
                        // Follow the vanilla planting path exactly (HoeDirt.plant), which
                        // resolves seed -> crop data through the loaded crop catalog; a
                        // hand-rolled Crop can silently miss its data and never grow.
                        if (!farm.terrainFeatures.TryGetValue(tile, out var feature) || feature is not HoeDirt)
                        {
                            feature = new HoeDirt(0, farm);
                            farm.terrainFeatures.Add(tile, (HoeDirt)feature);
                        }
                        var dirt = (HoeDirt)feature;
                        if (!dirt.plant(seedItemId, Game1.player, isFertilizer: false))
                            continue;
                        dirt.state.Value = 1; // watered so dayUpdate() advances growth
                        planted++;
                        Console.WriteLine($"[Plant] Planted {seedItemId} at ({x},{y}): seed={dirt.crop?.netSeedIndex.Value} phase={dirt.crop?.currentPhase.Value} days={dirt.crop?.dayOfCurrentPhase.Value}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Plant] Failed at ({x},{y}): {ex.Message}");
                    }
                }
            }
            Console.WriteLine($"[Plant] Done: planted {planted}/{count} test crops.");
        }

        private static void ProbeNpc(string name)
        {
            NPC? npc = null;
            GameLocation? loc = null;
            foreach (GameLocation l in Game1.locations)
            {
                npc = l.characters.FirstOrDefault(c => c.Name == name);
                if (npc != null) { loc = l; break; }
            }
            if (npc == null || loc == null)
            {
                Console.WriteLine($"[Probe] NPC {name} not found.");
                return;
            }
            Console.WriteLine($"[Probe] {name}@{loc.Name} pos={npc.Position} dir2loc={(npc.DirectionsToNewLocation == null ? "null" : "set")} square={npc.IsWalkingInSquare} ctrl={(npc.controller == null ? "null" : npc.controller.pathToEndPoint?.Count.ToString())} timeNow={Game1.timeOfDay} shouldPass={Game1.shouldTimePass()} master={Game1.IsMasterGame}");
            var time = Game1.currentGameTime ?? new GameTime();
            for (int i = 1; i <= 240; i++)
            {
                time = new GameTime(time.TotalGameTime + TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));
                Game1.currentGameTime = time;
                try { loc.updateEvenIfFarmerIsntHere(time, false); }
                catch (Exception ex) { Console.WriteLine($"[Probe] tick{i} error: {ex.Message}"); break; }
                if (i % 60 == 0)
                {
                    Console.WriteLine($"[Probe]   tick{i}: pos={npc.Position} ctrl={(npc.controller == null ? "null" : npc.controller.pathToEndPoint?.Count.ToString())} loc={npc.currentLocation?.Name}");
                }
            }
            Console.WriteLine($"[Probe] done: {name}@{loc.Name} pos={npc.Position}");
        }

        private static void ForceDayRoll()
        {
            if (Game1.newDay || headlessNewDayActive)
            {
                Console.WriteLine("[Roll] A day roll is already in progress; refusing.");
                return;
            }
            Console.WriteLine($"[Roll] Forcing overnight NewDay from day {Game1.dayOfMonth} {Game1.timeOfDay}...");
            try
            {
                Farmer host = Game1.player;
                host.isInBed.Value = true;
                host.timeWentToBed.Value = (int)Game1.timeOfDay;
                Game1.netReady?.SetLocalReady("sleep", true);
                Game1.netReady?.SetLocalReady("ready_for_save", true);
                Game1.netReady?.SetLocalReady("wakeup", true);
                Game1.NewDay(0f);
                Console.WriteLine("[Roll] Game1.NewDay invoked; PumpHeadlessNewDayProcess will drive the overnight coroutine.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Roll] NewDay invocation failed: {ex}");
            }
        }
    }
}
