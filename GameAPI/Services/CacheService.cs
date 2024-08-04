﻿using GameAPI.Options;
using GameDomain.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GameAPI.Services;

public class CacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly PlayerService _playerService; // Для доступа к PlayerService
    private const string LeaderboardKey = "players:leaderboard";

    public CacheService(IDistributedCache cache, PlayerService playerService)
    {
        _cache = cache;
        _playerService = playerService;
    }

    public async Task<List<KeyValuePair<string, double>>> GetTopPlayersAsync(int count = 100)
    {
        // Пытаемся получить данные из кэша
        var cachedData = await _cache.GetStringAsync(LeaderboardKey);
        if (!string.IsNullOrEmpty(cachedData))
        {
            return JsonSerializer.Deserialize<List<KeyValuePair<string, double>>>(cachedData);
        }
        
        // Получение данных из базы данных, если кэш пуст
        var playersFromDb = await _playerService.GetTopPlayers(count);
        var players = playersFromDb.Select(p => new KeyValuePair<string, double>(p.Id, p.Rating)).ToList();
        
        // Сериализация и кэширование данных
        var serializedData = JsonSerializer.Serialize(players);
        await SetAsync(LeaderboardKey, serializedData, TimeSpan.FromMinutes(5));
        
        return players;
    }
    
    public async Task<string?> GetAsync(string key)
    {
        return await _cache.GetStringAsync(key);
    }

    public async Task SetAsync(string key, string value, TimeSpan? expiration = null)
    {
        var options = new DistributedCacheEntryOptions()
            .SetSlidingExpiration(expiration ?? TimeSpan.FromMinutes(5));
        await _cache.SetStringAsync(key, value, options);
    }

    public async Task RemoveAsync(string key)
    {
        await _cache.RemoveAsync(key);
    }

    public async Task UpdateLeaderboardAsync(string playerId, int rating)
    {
        // Эта операция будет требовать считывания, обновления и записи обратно в кэш
        var leaders = await GetTopPlayersAsync(); // Считывание текущего списка
        var updatedLeaders = leaders.Select(p => 
            new KeyValuePair<string, double>(p.Key, p.Key == playerId ? rating : p.Value)).ToList();

        var serializedData = JsonSerializer.Serialize(updatedLeaders);
        await SetAsync(LeaderboardKey, serializedData, TimeSpan.FromMinutes(5)); // Перезапись обновлённых данных
    }
}
