using pkNX.Game;
using pkNX.Structures.FlatBuffers;
using pkNX.Structures.FlatBuffers.ZA;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using pkNX.Structures;

namespace pkNX.WinForms;

public sealed class SpawnRipper9a
{
    private readonly FlatBufferSource Game;
    private readonly string _outDir;

    // Spawner Information
    public readonly World9a AllMaps;
    public readonly LumioseMap Overworld;
    public const byte LocationLabs = 234;
    public const byte LocationSewer = 235;
    public const ushort LocationHyperspace = 273;

    public readonly SpawnNameResolver Resolver;
    public readonly SpawnGlobalInfo Shared;
    public readonly MapSpawnerSet RipInfo;
    private readonly SpawnExporter Exporter;

    public List<SpawnSceneSimulation> RippedMaps = [];

    public SpawnRipper9a(GameManager9a rom, string language, string outDir, World9a world)
    {
        AllMaps = world;
        _outDir = outDir;
        if (!Directory.Exists(_outDir) && Path.IsPathRooted(_outDir))
            Directory.CreateDirectory(_outDir);

        Game = new(rom);
        Resolver = new(rom, language);
        Shared = new(rom);

        Overworld = AllMaps.Overworld;
        Exporter = new(_outDir)
        {
            GetLocationName = z => Resolver.LocationNames[z],
        };
        RipInfo = new MapSpawnerSet
        {
            SpawnerInfo = GetLinkedSpawnerNames(Shared.SpawnerDatabase.Table),
        };

        FindAllPointsOnMaps();
    }

    public SpawnRipper9a(GameManager9a rom, string language, string outDir) : this(rom, language, outDir, new(rom)) { }

    private void FindAllPointsOnMaps()
    {
        RippedMaps.Add(SimulateScene(SceneInfo9a.ScenePathsT1, "t1", ManualPositionRip.GetLocation));
        RippedMaps.Add(SimulateScene(SceneInfo9a.ScenePathsT2, "t2", _ => [LocationLabs]));
        RippedMaps.Add(SimulateScene(SceneInfo9a.ScenePathsT3, "t3", _ => [LocationSewer]));
    }

    public void DumpSceneToPickle(List<SimulatedAreaSet> rip)
    {
        var sets = rip.SelectMany(z => z.ToAreas()).ToArray();
        foreach (var set in sets)
            set.CondenseList();

        var path = Path.Combine(_outDir, "encounter_za.pkl");
        EncounterSlot9a.WritePickle(sets, path);

        // Write a concatenated array of slots for visual pattern inspection.
        EncounterSlot9a.WritePickleWithoutLocations(sets.SelectMany(z => z.Slots), path);
    }

    public void ExportRippedMaps()
    {
        List<SimulatedAreaSet> rip = [];
        foreach (var map in RippedMaps)
        {
            // Iterate all points and populate our fake spawners with every result encounter.
            var spawnIn = new SimulatedAreaSet(this);
            spawnIn.Populate(map);
            rip.Add(spawnIn);

            Exporter.ExportJson(spawnIn.Detailed, map.Name);
            Exporter.ExportJson(spawnIn.ToAreas(), map.Name);

            var sp = map.SpawnerPositions;
            var pt = sp.Select(z => PointDump.FromSpawner(z.Value, map.LocationNameFetch)).ToList();
            var text = string.Join(Environment.NewLine, ManualPositionRip.Export(pt));
            var path = Path.Combine(_outDir, $"{map.Name}_pt.txt");
            File.WriteAllText(path, text);
        }
        DumpSceneToPickle(rip);
        Exporter.ExportJson(RippedMaps);
        Exporter.ExportAll(this);
    }

    private static Dictionary<string, PokemonSpawnerData> GetLinkedSpawnerNames(IEnumerable<PokemonSpawnerDataDB> spawnerI)
    {
        var result = new Dictionary<string, PokemonSpawnerData>();
        foreach (var spawn in spawnerI)
        {
            foreach (var obj in spawn.Table)
            {
                foreach (var sub in obj.AppearanceSpawnerObjectInfoList)
                    result.TryAdd(sub.ObjectName, obj);
            }
        }
        return result;
    }

    private int GetLocationFromOverworldMap(PackedVec3f arg)
    {
        var placeName = Overworld.GetPlaceName(arg.X, arg.Y, arg.Z, out _);
        if (placeName is "")
            return 0;
        return Resolver.GetLocationIndex(placeName);
    }

    private SpawnSceneSimulation SimulateScene(string[] scenePaths, string map, Func<PackedVec3f, List<ushort>> location)
    {
        var result = new SpawnSceneSimulation
        {
            Name = map,
            Paths = scenePaths,
            LocationNameFetch = location,
            Game = Game,
        };
        result.ScrapePoints();
        return result;
    }

    public string GetLocationName(int locationIndex) => Resolver.LocationNames[locationIndex];
    public bool TryGetSpawner(string name, [NotNullWhen(true)] out PokemonSpawnerData? spawnerData) => RipInfo.SpawnerInfo.TryGetValue(name, out spawnerData);
    public bool TryGetEncounter(string id, [NotNullWhen(true)] out EncountData? slot) => Shared.Encounters.TryGetValue(id, out slot);

    public void ExportHyperspacePickle()
    {
        var list = new List<EncounterSlot9a>();
        var locationTable = new EncounterArea9a[] { new() { Location = LocationHyperspace, Slots = list } };

        // Random set of spawns can appear at any spawner. All we care about is the encounters within that set.
        AddRandomSpawns(list);

        // Special Distortion boss spawners come with a pack of encounters to act as nuisance. We need them.
        // ect_zdm40X_sp0Y_Z
        // bosses are the special spawn with fixed IVs. Don't need them.
        var band = Shared.Encounters.Select(z => z.Value)
            .Where(z => IsHyperspaceBandSp(z.Id));

        foreach (var enc in band)
        {
            // Need to get the spawner that has this encounter, for the relevant level boost.
            if (!RipInfo.TryGetSpawnerFromEncounter(enc.Id, out var spawner))
            {
                System.Diagnostics.Debug.WriteLine($"Could not find spawner for hyperspace band encounter {enc.Id} - {(Species)enc.DevNo}");
                continue;
            }
            AddSpawner(list, spawner);
        }

        foreach (var set in locationTable)
            set.CondenseList();

        var path = Path.Combine(_outDir, "encounter_hyperspace_za.pkl");
        EncounterSlot9a.WritePickle(locationTable, path);

        // Write a concatenated array of slots for visual pattern inspection.
        EncounterSlot9a.WritePickleWithoutLocations(locationTable.SelectMany(z => z.Slots), path);
    }

    private static bool IsHyperspaceBandSp(string name) => name.StartsWith("ect_zdm40") && !name.EndsWith("_sp");

    private void AddRandomSpawns(List<EncounterSlot9a> list)
    {
        foreach (var set in Shared.HyperspaceSpawnSets)
        {
            foreach (var info in RipInfo.SpawnerInfo)
            {
                if (info.Value.Id != set)
                    continue;
                AddSpawner(list, info.Value);
                break;
            }
        }
    }

    private void AddSpawner(List<EncounterSlot9a> list, PokemonSpawnerData spawner)
    {
        foreach (var info in spawner.AppearanceSpawnerObjectInfoList)
        {
            var appearInfo = info.Appearance;
            if (appearInfo.MinCount == 0)
                continue;

            var encounters = spawner.EncountDataInfoList;
            // don't care about randomness of the list of encounters. just add them.

            foreach (var enc in encounters)
            {
                if (enc.EncountDataId is not { } id)
                    continue;
                if (!TryGetEncounter(id, out var slot))
                    continue;
                EncounterSlot9a.AddSlots(list, slot, enc, hyperspaceBoost: true);
            }
        }
    }
}

public record PointDump
{
    public static PointDump FromSpawner(SceneSpawner sp, Func<PackedVec3f, List<ushort>> resolver) => new()
    {
        X = sp.Position.X,
        Y = sp.Position.Y,
        Z = sp.Position.Z,
        Guess = resolver(sp.Position),
        Name = sp.Name,
        Type = sp.Type,
    };

    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Z { get; init; }
    public required List<ushort> Guess { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }

    public float DistanceTo(PackedVec3f other) => new PackedVec3f(X, Y, Z).DistanceTo(other);
    public float DistanceTo(PointDump other) => new PackedVec3f(X, Y, Z).DistanceTo(new PackedVec3f(other.X, other.Y, other.Z));
}
