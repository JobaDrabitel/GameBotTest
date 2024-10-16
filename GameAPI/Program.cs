using System.Text;
using System.Text.Json;
using System.Web;
using GameAPI.Models;
using GameAPI.Options;
using GameAPI.Services;
using GameAPI.Verification.Model;
using GameDomain.Interfaces;
using GameDomain.Models;
using GameDomain.Models.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER"),
            ValidAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE"),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("JWT_KEY")!))
        };
    });
builder.Services.AddSingleton<JwtService>();
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddLogging();

var mongoConnectionString = builder.Configuration["MONGODB_CONNECTIONSTRING"];
var mongoDatabaseName = builder.Configuration["MONGODB_DATABASENAME"];
const string cacheKey = "players";
const string referrerKey = "referrer";
var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

builder.Services.AddSingleton<IMongoClient>(_ =>
{
    var settings = MongoClientSettings.FromConnectionString(mongoConnectionString);
    settings.MaxConnectionPoolSize = 500; 
    return new MongoClient(settings);
});
builder.Services.AddSingleton<IMongoDatabase>(provider =>
{
    var client = provider.GetRequiredService<IMongoClient>();
    return client.GetDatabase(mongoDatabaseName);
});

BsonClassMap.RegisterClassMap<Player>(cm =>
{
    cm.AutoMap();
    cm.SetIgnoreExtraElements(true);  
});
BsonClassMap.RegisterClassMap<Region>(cm =>
{
    cm.AutoMap();
    cm.SetIgnoreExtraElements(true);  
});
var mongoDatabase = builder.Services.BuildServiceProvider().GetRequiredService<IMongoDatabase>();
await MongoDbSeederSevice.SeedAsync(mongoDatabase); 

var redisConfiguration = builder.Configuration["REDIS_CONNECTIONSTRING"];
var constantKey = "WebAppData";
var botToken = builder.Configuration["BOT_TOKEN"]!;
builder.Services.AddStackExchangeRedisCache(cacheOptions =>
{
    cacheOptions.Configuration = redisConfiguration;
    cacheOptions.InstanceName = "SampleInstance";
});
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection("Redis"));
builder.Services.AddScoped<IPlayerService, PlayerService>();
builder.Services.AddScoped<ICacheService, CacheService>(provider => 
    new CacheService(provider.GetRequiredService<IDistributedCache>(), provider.GetRequiredService<IPlayerService>()));
builder.Services.AddScoped<ILevelService, LevelService>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();


app.MapGet("/api/players", async (IPlayerService playerService, ICacheService cacheService) =>
{
    var cachedPlayers = await cacheService.GetAsync(cacheKey);
    if (!string.IsNullOrEmpty(cachedPlayers))
    {
        return Results.Ok(JsonSerializer.Deserialize<List<Player>>(cachedPlayers));
    }

    var players = await playerService.GetAsync();
    await cacheService.SetAsync(cacheKey, JsonSerializer.Serialize(players));
    return Results.Ok(players);
}).RequireAuthorization();

app.MapGet("/api/players/{id:long}", async (long id, ICacheService cacheService) =>
{
    var player = await cacheService.GetPlayerAsync(id);
    return player != null ? Results.Ok(player) : Results.NotFound();
}).WithName("GetPlayer").AllowAnonymous();

app.MapPost("/api/players", async ([FromBody] Player player, IPlayerService playerService, ICacheService cacheService) =>
{
    await playerService.CreateAsync(player);
    await cacheService.SetAsync($"{cacheKey}:{player.TelegramId}", JsonSerializer.Serialize(player));
    return Results.Ok(player);
}).AllowAnonymous();

app.MapPut("/api/players/{id:long}", async (long id, Player playerIn, IPlayerService playerService,
    ICacheService cacheService) =>
{
    var player = await playerService.GetPlayerAsync(id);
    if(player is null)
        return Results.NotFound();
    await playerService.UpdateAsync(id, playerIn);
    return Results.Ok();
}).AllowAnonymous();

app.MapPut("/api/players/{telegramId:long}/rating", async (long telegramId, [FromBody] int ratingChange,
    IPlayerService playerService, ICacheService cacheService) =>
{
    var success = await playerService.UpdateRatingAsync(telegramId, ratingChange);

    if (success)
    {
        await cacheService.RemoveAsync(cacheKey);  
        return Results.Ok();
    }
    else
    {
        return Results.NotFound($"Player with Telegram ID {telegramId} not found.");
    }
}).AllowAnonymous();

app.MapDelete("/api/players/{id:long}", async (long id, IPlayerService playerService,
    ICacheService cacheService) =>
{
    var player = await playerService.GetPlayerAsync(id);

    await playerService.RemoveAsync(id);
    await cacheService.RemoveAsync($"{cacheKey}:{id}");  
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/leaders", async ([FromServices] ICacheService cacheService) =>
{
    try
    {
        var leaders = await cacheService.GetTopPlayersAsync();
        return leaders.Count == 0 ? Results.NotFound("No leaders found.") : Results.Ok(leaders);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).AllowAnonymous();


app.MapPost("/api/verify", async (HttpRequest request) =>
{
    var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
    var payload = JsonSerializer.Deserialize<TgPayloadDto>(requestBody, jsonSerializerOptions);

    if (payload == null || string.IsNullOrEmpty(payload.InitData))
    {
        return Results.BadRequest();
    }
    var data = HttpUtility.ParseQueryString(payload.InitData);

    return VerificationService.IsValidData(data, constantKey, botToken) ? Results.Ok(new { valid = true }) : Results.BadRequest();
}).AllowAnonymous();


app.MapPost("/api/login", async (LoginModel login, IConfiguration config, JwtService jwtService) =>
{
    if (login.Username != config["SERVICE_USERNAME"] || login.Password != config["SERVICE_PASSWORD"])
        return Results.Unauthorized();
    var token = jwtService.GenerateToken(login.Username);
    return Results.Ok(new { token });
}).AllowAnonymous();

app.MapGet("/api/players/referrals/{telegramId:long}", async (long telegramId, IPlayerService playerService) =>
{
    var referrals = await playerService.GetReferralsAsync(telegramId);
    return Results.Ok(referrals);
}).AllowAnonymous();

app.MapGet("/api/players/referrer/{telegramId:long}", async (long telegramId, IPlayerService playerService, ICacheService cacheService) =>
{
    var cachedReferrer = await cacheService.GetAsync($"{referrerKey}:{telegramId}");
    if (!string.IsNullOrEmpty(cachedReferrer))
    {
        return Results.Ok(JsonSerializer.Deserialize<Player>(cachedReferrer));
    }

    var referrer = await playerService.GetReferrerAsync(telegramId);
    if (referrer == null) return Results.NotFound("Referrer not found.");
    await cacheService.SetAsync(cacheKey, JsonSerializer.Serialize(referrer));
    return Results.Ok(referrer);

}).AllowAnonymous();
app.MapGet("/api/players/ip/{playerId:long}", async (long playerId, IPlayerService playerService) =>
{
    var regionIp = await playerService.GetPlayerRegionIpAsync(playerId);
    
    return string.IsNullOrEmpty(regionIp) ? Results.NotFound()
        : Results.Ok(new { RegionIp = regionIp });
}).WithName("GetPlayerRegionIp").AllowAnonymous();

app.MapPost("/api/players/region", async ([FromBody] SetRegionDto assignRegionDto, IPlayerService playerService, ICacheService cacheService) =>
{
    var success = await playerService.AssignRegionToPlayerAsync(assignRegionDto.PlayerId, assignRegionDto.RegionId);

    if (!success) return Results.BadRequest();
    var player = await playerService.GetPlayerAsync(assignRegionDto.PlayerId);
        
    if (player != null)
    {
        await cacheService.SetAsync($"player:{player.TelegramId}", JsonSerializer.Serialize(player));
    }

    return Results.Ok();

}).WithName("SetRegionToPlayer").AllowAnonymous();



app.Run();

