﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using AllEnum;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Exceptions;
using PokemonGo.RocketAPI.Extensions;
using PokemonGo.RocketAPI.GeneratedCode;
using PokemonGo.RocketAPI.Logic.Utils;
using PokemonGo.RocketAPI.Helpers;

namespace PokemonGo.RocketAPI.Logic
{
    public class Logic
    {
        private readonly Client _client;
        private readonly ISettings _clientSettings;
        private readonly Inventory _inventory;
        private readonly Statistics _stats;

        public Logic(ISettings clientSettings)
        {
            _clientSettings = clientSettings;
            _client = new Client(_clientSettings);
            _inventory = new Inventory(_client);
            _stats = new Statistics();
        }

        public async Task Execute()
        {
            CheckAndDownloadVersion.CheckVersion();

            if (_clientSettings.DefaultLatitude == 0 || _clientSettings.DefaultLongitude == 0)
            {
                Logger.Error($"Please change first Latitude and/or Longitude because currently your using default values!");
                Logger.Error($"Window will be auto closed in 15 seconds!");
                await Task.Delay(15000);
                System.Environment.Exit(1);
            }

            Logger.Normal(ConsoleColor.DarkGreen, $"Starting Execute on login server: {_clientSettings.AuthType}");
            while (true)
            {
                try
                {
                    if (_clientSettings.AuthType == AuthType.Ptc)
                        await _client.DoPtcLogin(_clientSettings.PtcUsername, _clientSettings.PtcPassword);
                    else if (_clientSettings.AuthType == AuthType.Google)
                        await _client.DoGoogleLogin();

                    await PostLoginExecute();
                }
                catch (AccessTokenExpiredException)
                {
                    Logger.Error($"Access token expired");
                }
                await Task.Delay(10000);
            }
        }

        public async Task PostLoginExecute()
        { 
            Logger.Normal(ConsoleColor.DarkGreen, $"Client logged in");

            while (true)
            {
                try
                {
                    await _client.SetServer();

                    //var inventory = await _client.GetInventory();
                    //var playerStats = inventory.InventoryDelta.InventoryItems.Select(i => i.InventoryItemData).FirstOrDefault(i => i.PlayerStats != null);

                    var profile = await _client.GetProfile();
                    var _currentLevelInfos = await Statistics._getcurrentLevelInfos(_inventory);

                    Logger.Normal(ConsoleColor.Yellow, "----------------------------");
                    if (_clientSettings.AuthType == AuthType.Ptc)
                        Logger.Normal(ConsoleColor.Cyan, $"PTC Account: {_clientSettings.PtcUsername}\n");
                    //Logger.Normal(ConsoleColor.Cyan, "Password: " + _clientSettings.PtcPassword + "\n");
                    Logger.Normal(ConsoleColor.DarkGray, $"Latitude: {_clientSettings.DefaultLatitude}");
                    Logger.Normal(ConsoleColor.DarkGray, $"Longitude: {_clientSettings.DefaultLongitude}");
                    Logger.Normal(ConsoleColor.Yellow, "----------------------------");
                    Logger.Normal(ConsoleColor.DarkGray, "Your Account:\n");
                    Logger.Normal(ConsoleColor.DarkGray, $"Name: {profile.Profile.Username}");
                    Logger.Normal(ConsoleColor.DarkGray, $"Team: {profile.Profile.Team}");
                    Logger.Normal(ConsoleColor.DarkGray, $"Level: {_currentLevelInfos}");
                    Logger.Normal(ConsoleColor.DarkGray, $"Stardust: {profile.Profile.Currency.ToArray()[1].Amount}");
                    Logger.Normal(ConsoleColor.Yellow, "----------------------------");

                    //await EvolveAllPokemonWithEnoughCandy();
                    await TransferDuplicatePokemon();
                    await RecycleItems();
                    await ExecuteFarmingPokestopsAndPokemons();

                    /*
                * Example calls below
                *
                var profile = await _client.GetProfile();
                var settings = await _client.GetSettings();
                var mapObjects = await _client.GetMapObjects();
                var inventory = await _client.GetInventory();
                var pokemons = inventory.InventoryDelta.InventoryItems.Select(i => i.InventoryItemData?.Pokemon).Where(p => p != null && p?.PokemonId > 0);
                */
                }
                catch (AccessTokenExpiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception: {ex}");
                }

                await Task.Delay(10000);
            }
        }

        public async Task RepeatAction(int repeat, Func<Task> action)
        {
            for (int i = 0; i < repeat; i++)
                await action();
        }

        private async Task ExecuteFarmingPokestopsAndPokemons()
        {
            var mapObjects = await _client.GetMapObjects();
            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts).Where(i => i.Type == FortType.Checkpoint && i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime());
            Logger.Normal(ConsoleColor.Green, $"Found {pokeStops.Count()} pokestops");

            foreach (var pokeStop in pokeStops)
            {
                await ExecuteCatchAllNearbyPokemons();
                await TransferDuplicatePokemon();

                var distance = Navigation.DistanceBetween2Coordinates(_client.CurrentLat, _client.CurrentLng, pokeStop.Latitude, pokeStop.Longitude);
                var update = await _client.UpdatePlayerLocation(pokeStop.Latitude, pokeStop.Longitude, _clientSettings.DefaultAltitude);
                var fortInfo = await _client.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                var fortSearch = await _client.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                _stats.addExperience(fortSearch.ExperienceAwarded);
                _stats.updateConsoleTitle(_inventory);

                Logger.Normal(ConsoleColor.Cyan, $"Using Pokestop: {fortInfo.Name} in {Math.Round(distance)}m distance");
                Logger.Normal(ConsoleColor.Cyan, $"Received XP: {fortSearch.ExperienceAwarded}, Gems: { fortSearch.GemsAwarded}, Eggs: {fortSearch.PokemonDataEgg} Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)}");

                await RandomHelper.RandomDelay(750,1000);
                await RecycleItems();
            }
        }

        private async Task ExecuteCatchAllNearbyPokemons()
        {
            var mapObjects = await _client.GetMapObjects();
            var pokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons);
            if (pokemons != null && pokemons.Any())
                Logger.Normal(ConsoleColor.Green, $"Found {pokemons.Count()} catchable Pokemon");

            foreach (var pokemon in pokemons)
            {
                var distance = Navigation.DistanceBetween2Coordinates(_client.CurrentLat, _client.CurrentLng, pokemon.Latitude, pokemon.Longitude);
                await Task.Delay(distance > 100 ? 15000 : 500);

                await _client.UpdatePlayerLocation(pokemon.Latitude, pokemon.Longitude, _clientSettings.DefaultAltitude);

                var encounter = await _client.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnpointId);
                await CatchEncounter(encounter, pokemon);
            }
            await RandomHelper.RandomDelay(8000, 9000);
        }

        private async Task CatchEncounter(EncounterResponse encounter, MapPokemon pokemon)
        {
            CatchPokemonResponse caughtPokemonResponse;
            do
            {
                var bestBerry = await GetBestBerry(encounter?.WildPokemon);
                if (bestBerry != AllEnum.ItemId.ItemUnknown && encounter?.CaptureProbability.CaptureProbability_.First() < 0.35)
                {
                    var useRaspberry = await _client.UseCaptureItem(pokemon.EncounterId, bestBerry, pokemon.SpawnpointId);
                    Logger.Normal($"Use Rasperry {bestBerry}");
                    await RandomHelper.RandomDelay(500, 1000);
                }

                var bestPokeball = await GetBestBall(encounter?.WildPokemon);
                if (bestPokeball == MiscEnums.Item.ITEM_UNKNOWN)
                {
                    Logger.Normal($"You don't own any Pokeballs :( - We missed a {pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp}");
                    return;
                }

                var distance = Navigation.DistanceBetween2Coordinates(_client.CurrentLat, _client.CurrentLng, pokemon.Latitude, pokemon.Longitude);
                caughtPokemonResponse = await _client.CatchPokemon(pokemon.EncounterId, pokemon.SpawnpointId, pokemon.Latitude, pokemon.Longitude, bestPokeball);

                if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    foreach (int xp in caughtPokemonResponse.Scores.Xp)
                        _stats.addExperience(xp);
                    _stats.increasePokemons();
                    var profile = await _client.GetProfile();
                    _stats.getStardust(profile.Profile.Currency.ToArray()[1].Amount);
                }
                _stats.updateConsoleTitle(_inventory);
                Logger.Normal(ConsoleColor.Yellow,
                    caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess
                    ? $"We caught a {pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp} and CaptureProbability: {encounter?.CaptureProbability.CaptureProbability_.First()}, used {bestPokeball} in {Math.Round(distance)}m distance and received XP {caughtPokemonResponse.Scores.Xp.Sum()}"
                    : $"{pokemon.PokemonId} with CP {encounter?.WildPokemon?.PokemonData?.Cp} CaptureProbability: {encounter?.CaptureProbability.CaptureProbability_.First()} in {Math.Round(distance)}m distance {caughtPokemonResponse.Status} while using a {bestPokeball}.."
                    );
                await RandomHelper.RandomDelay(1750, 2250);
            }
            while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed || caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchEscape);
        }

        private async Task EvolveAllPokemonWithEnoughCandy()
        {
            var pokemonToEvolve = await _inventory.GetPokemonToEvolve();
            foreach (var pokemon in pokemonToEvolve)
            {
                var evolvePokemonOutProto = await _client.EvolvePokemon((ulong)pokemon.Id);

                if (evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.PokemonEvolvedSuccess)
                    Logger.Normal($"Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExpAwarded}xp");
                else
                    Logger.Normal($"Failed to evolve {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}");

                Logger.Normal(
                    evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.PokemonEvolvedSuccess
                        ? $"Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExpAwarded} xp"
                        : $"Failed to evolve {pokemon.PokemonId}. EvolvePokemonOutProto.Result was {evolvePokemonOutProto.Result}, stopping evolving {pokemon.PokemonId}"
                    );

                await Task.Delay(3000);
            }
        }

        private async Task TransferDuplicatePokemon(bool keepPokemonsThatCanEvolve = false)
        {
            var duplicatePokemons = await _inventory.GetDuplicatePokemonToTransfer(keepPokemonsThatCanEvolve);
            if (duplicatePokemons != null && duplicatePokemons.Any())
                Logger.Normal(ConsoleColor.DarkYellow, $"Transfering duplicate Pokemon");

            foreach (var duplicatePokemon in duplicatePokemons)
            {
                var bestPokemonOfType = await _inventory.GetHighestCPofType(duplicatePokemon);
                var transfer = await _client.TransferPokemon(duplicatePokemon.Id);

                _stats.increasePokemonsTransfered();
                _stats.updateConsoleTitle(_inventory);

                Logger.Normal(ConsoleColor.DarkYellow, $"Transfer {duplicatePokemon.PokemonId} with {duplicatePokemon.Cp} CP (Best: {bestPokemonOfType})");
                await Task.Delay(500);
            }
        }

        private async Task RecycleItems()
        {
            var items = await _inventory.GetItemsToRecycle(_clientSettings);

            foreach (var item in items)
            {
                var transfer = await _client.RecycleItem((AllEnum.ItemId)item.Item_, item.Count);
                Logger.Normal(ConsoleColor.DarkCyan, $"Recycled {item.Count}x {(AllEnum.ItemId)item.Item_}");

                _stats.addItemsRemoved(item.Count);
                _stats.updateConsoleTitle(_inventory);

                await Task.Delay(500);
            }
        }

        private async Task<MiscEnums.Item> GetBestBall(WildPokemon pokemon)
        {
            var pokemonCp = pokemon?.PokemonData?.Cp;

            var items = await _inventory.GetItems();
            var balls = items.Where(i => (MiscEnums.Item)i.Item_ == MiscEnums.Item.ITEM_POKE_BALL
                                      || (MiscEnums.Item)i.Item_ == MiscEnums.Item.ITEM_MASTER_BALL
                                      || (MiscEnums.Item)i.Item_ == MiscEnums.Item.ITEM_ULTRA_BALL
                                      || (MiscEnums.Item)i.Item_ == MiscEnums.Item.ITEM_GREAT_BALL).GroupBy(i => ((MiscEnums.Item)i.Item_)).ToList();
            if (balls.Count == 0) return MiscEnums.Item.ITEM_UNKNOWN;

            var pokeBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_POKE_BALL);
            var greatBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_GREAT_BALL);
            var ultraBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_ULTRA_BALL);
            var masterBalls = balls.Any(g => g.Key == MiscEnums.Item.ITEM_MASTER_BALL);

            if (masterBalls && pokemonCp >= 1500)
                return MiscEnums.Item.ITEM_MASTER_BALL;
            else if (ultraBalls && pokemonCp >= 1500)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            else if (greatBalls && pokemonCp >= 1500)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (ultraBalls && pokemonCp >= 1000)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            else if (greatBalls && pokemonCp >= 1000)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (ultraBalls && pokemonCp >= 600)
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            else if (greatBalls && pokemonCp >= 600)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            if (greatBalls && pokemonCp >= 350)
                return MiscEnums.Item.ITEM_GREAT_BALL;

            return balls.OrderBy(g => g.Key).First().Key;
        }

        private async Task<AllEnum.ItemId> GetBestBerry(WildPokemon pokemon)
        {
            var pokemonCp = pokemon?.PokemonData?.Cp;

            var items = await _inventory.GetItems();
            var berries = items.Where(i => (AllEnum.ItemId)i.Item_ == AllEnum.ItemId.ItemRazzBerry
                                        || (AllEnum.ItemId)i.Item_ == AllEnum.ItemId.ItemBlukBerry
                                        || (AllEnum.ItemId)i.Item_ == AllEnum.ItemId.ItemNanabBerry
                                        || (AllEnum.ItemId)i.Item_ == AllEnum.ItemId.ItemWeparBerry
                                        || (AllEnum.ItemId)i.Item_ == AllEnum.ItemId.ItemPinapBerry).GroupBy(i => ((AllEnum.ItemId)i.Item_)).ToList();
            if (berries.Count == 0) return AllEnum.ItemId.ItemUnknown;

            var razzBerryCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_RAZZ_BERRY);
            var blukBerryCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_BLUK_BERRY);
            var nanabBerryCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_NANAB_BERRY);
            var weparBerryCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_WEPAR_BERRY);
            var pinapBerryCount = await _inventory.GetItemAmountByType(MiscEnums.Item.ITEM_PINAP_BERRY);

            if (pinapBerryCount > 0 && pokemonCp >= 1000)
                return AllEnum.ItemId.ItemPinapBerry;
            else if (weparBerryCount > 0 && pokemonCp >= 1000)
                return AllEnum.ItemId.ItemWeparBerry;
            else if (nanabBerryCount > 0 && pokemonCp >= 1000)
                return AllEnum.ItemId.ItemNanabBerry;

            if (weparBerryCount > 0 && pokemonCp >= 600)
                return AllEnum.ItemId.ItemWeparBerry;
            else if (nanabBerryCount > 0 && pokemonCp >= 600)
                return AllEnum.ItemId.ItemNanabBerry;
            else if (blukBerryCount > 0 && pokemonCp >= 600)
                return AllEnum.ItemId.ItemBlukBerry;

            if (blukBerryCount > 0 && pokemonCp >= 350)
                return AllEnum.ItemId.ItemBlukBerry;

            return berries.OrderBy(g => g.Key).First().Key;
        }
    }
}