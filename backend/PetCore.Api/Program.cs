using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PetCore.Api.Data;
using PetCore.Api.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient("ml", c => { c.BaseAddress = new Uri(Environment.GetEnvironmentVariable("ML_URL") ?? "http://localhost:8000"); c.Timeout = TimeSpan.FromSeconds(3); });
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Port=5432;Database=PetCoreFoodAI;Username=petcore;Password=change-me"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

// Apply versioned PostgreSQL migrations on startup.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    var model = await db.AiModels.FirstOrDefaultAsync();
    if (model is null) db.AiModels.Add(new AiModel { Name = "PetCore Presence and Food Pattern", Algorithm = "Isolation Forest", Version = "2.0.0", Parameters = "{\"n_estimators\":100,\"contamination\":0.05,\"window\":\"1 minute\"}", Status = "Coletando dados" });
    else if (model.Version != "2.0.0") { model.Name = "PetCore Presence and Food Pattern"; model.Algorithm = "Isolation Forest"; model.Version = "2.0.0"; model.Parameters = "{\"n_estimators\":100,\"contamination\":0.05,\"window\":\"1 minute\"}"; model.Status = "Coletando dados"; model.TrainedAt = null; }
    await db.SaveChangesAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "PetCore Food AI API" }));
app.MapGet("/api/devices", async (AppDbContext db) => Results.Ok(await db.Devices.OrderBy(x => x.Identifier).ToListAsync()));
app.MapGet("/api/models", async (AppDbContext db) => Results.Ok(await db.AiModels.OrderByDescending(x => x.Id).ToListAsync()));
app.MapGet("/api/measurements", async (AppDbContext db, int? limit) => Results.Ok(await db.Measurements.Include(x => x.Device).OrderByDescending(x => x.ReceivedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).Select(x => new { x.Id, device = x.Device!.Identifier, x.Sensor, x.Value, x.Unit, x.MeasuredAt, x.ReceivedAt, x.Origin }).ToListAsync()));
app.MapGet("/api/analyses", async (AppDbContext db, int? limit) => Results.Ok(await db.AiAnalyses.Include(x => x.Measurement).OrderByDescending(x => x.AnalyzedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).Select(x => new { x.Id, x.MeasurementId, x.Result, x.Score, x.Explanation, x.AnalyzedAt, measurement = x.Measurement!.Value }).ToListAsync()));
app.MapGet("/api/alerts", async (AppDbContext db) => Results.Ok(await db.Alerts.OrderByDescending(x => x.CreatedAt).ToListAsync()));
app.MapGet("/api/dashboard", async (AppDbContext db) =>
{
    var latest = await db.Measurements.OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync();
    var pet = await db.Measurements.Where(x => x.Sensor == "petcore/status").OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync();
    var food = await db.Measurements.Where(x => x.Sensor == "petcore/comida").OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync();
    var device = await db.Devices.OrderByDescending(x => x.LastSeenAt).FirstOrDefaultAsync();
    var live = device?.LastSeenAt is { } seen && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(15);
    return Results.Ok(new { measurements = await db.Measurements.CountAsync(), alertsOpen = await db.Alerts.CountAsync(x => x.Status == "aberto"), anomalies = await db.AiAnalyses.CountAsync(x => x.Result == "anomalia"), latestAt = latest?.ReceivedAt, latestValue = latest?.Value, petState = pet?.Value, petAt = pet?.ReceivedAt, foodState = food?.Value, foodAt = food?.ReceivedAt, device = device?.Identifier, deviceStatus = live ? "Online" : "Sem comunicação", lastSeenAt = device?.LastSeenAt });
});
app.MapGet("/api/ml/status", async (IHttpClientFactory f) => { try { return Results.Ok(await (await f.CreateClient("ml").GetAsync("/health")).Content.ReadFromJsonAsync<JsonElement>()); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Results.Json(new { status = "indisponível" }, statusCode: 503); } });

app.MapPost("/api/measurements", async (IngestRequest req, AppDbContext db, IHttpClientFactory clients) =>
{
    var validTopic = req.Topic is "petcore/status" or "petcore/horario" or "petcore/comida";
    var validValue = req.Topic switch
    {
        "petcore/status" => req.Value is "PET DETECTADO" or "SEM PRESENCA",
        "petcore/horario" => System.Text.RegularExpressions.Regex.IsMatch(req.Value ?? "", "^(SEM HORA|([01]\\d|2[0-3]):[0-5]\\d:[0-5]\\d)$"),
        "petcore/comida" => req.Value is "COM COMIDA" or "SEM COMIDA",
        _ => false
    };
    if (string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId.Length > 100 || string.IsNullOrWhiteSpace(req.Value) || req.Value.Length > 200 || !validTopic || !validValue)
        return Results.BadRequest(new { error = "Informe deviceId, value e um tópico petcore permitido." });
    DateTimeOffset? measuredAt = null;
    if (!string.IsNullOrWhiteSpace(req.EventTime) && DateTimeOffset.TryParse(req.EventTime, out var parsed)) measuredAt = parsed;
    var raw = JsonSerializer.Serialize(req.Data ?? new Dictionary<string, object?> { ["topic"] = req.Topic, ["value"] = req.Value, ["eventTime"] = req.EventTime });
    var fingerprintSource = req.EventId is { Length: > 0 } eventId
        ? $"{req.DeviceId}|{req.Topic}|{eventId}"
        : $"{req.DeviceId}|{req.Topic}|{req.Value}|{req.EventTime}";
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
    var duplicate = await db.Measurements.AnyAsync(x => x.Fingerprint == fingerprint);
    if (duplicate) return Results.Ok(new { accepted = true, duplicate = true });
    var device = await db.Devices.SingleOrDefaultAsync(x => x.Identifier == req.DeviceId);
    if (device is null) { device = new Device { Identifier = req.DeviceId }; db.Devices.Add(device); }
    device.LastSeenAt = DateTimeOffset.UtcNow; device.Status = "Online";
    var measurement = new Measurement { Device = device, Sensor = req.Topic, Value = req.Value.Trim(), Unit = req.Topic == "petcore/horario" ? "hora local" : "estado", MeasuredAt = measuredAt, Origin = "Node-RED/MQTT", Fingerprint = fingerprint, OriginalData = raw };
    db.Measurements.Add(measurement);
    await db.SaveChangesAsync();
    if (req.Topic is "petcore/status" or "petcore/comida")
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var minuteStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            var alreadyAnalyzed = await db.AiAnalyses.AnyAsync(x => x.Measurement!.DeviceId == device.Id && x.AnalyzedAt >= minuteStart);
            if (alreadyAnalyzed) return Results.Created($"/api/measurements/{measurement.Id}", new { accepted = true, id = measurement.Id, analysis = "already_analyzed_this_minute" });
            var recentHistory = await db.Measurements.Where(x => x.DeviceId == device.Id && x.Id != measurement.Id && (x.Sensor == "petcore/status" || x.Sensor == "petcore/comida" || x.Sensor == "petcore/horario")).OrderByDescending(x => x.ReceivedAt).Take(20000).Select(x => new { x.Id, topic = x.Sensor, x.Value, at = x.MeasuredAt ?? x.ReceivedAt }).ToListAsync();
            var history = recentHistory.AsEnumerable().Reverse().ToList();
            var response = await clients.CreateClient("ml").PostAsJsonAsync("/analyze", new { measurementId = measurement.Id, deviceId = device.Identifier, topic = measurement.Sensor, value = measurement.Value, at = measurement.MeasuredAt ?? measurement.ReceivedAt, history });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var state = result.GetProperty("result").GetString() ?? "coletando";
                var score = result.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : (double?)null;
                var explanation = result.GetProperty("explanation").GetString() ?? "";
                var model = await db.AiModels.FirstOrDefaultAsync();
                if (state != "coletando" && model is not null) { model.Status = "Treinado"; model.TrainedAt ??= DateTimeOffset.UtcNow; model.Version = result.GetProperty("version").GetString() ?? model.Version; model.Name = "PetCore Presence and Food Pattern"; if (result.TryGetProperty("parameters", out var parameters)) model.Parameters = parameters.GetRawText(); }
                var analysis = new AiAnalysis { MeasurementId = measurement.Id, ModelId = state == "coletando" ? null : model?.Id, Result = state, Score = score, Explanation = explanation };
                db.AiAnalyses.Add(analysis);
                await db.SaveChangesAsync();
                if (state == "anomalia") { db.Alerts.Add(new Alert { AnalysisId = analysis.Id, MeasurementId = measurement.Id, Title = "Padrão de presença/alimentação fora do histórico", Message = explanation }); await db.SaveChangesAsync(); }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* Measurement remains stored; ML status is independently visible. */ }
    }
    return Results.Created($"/api/measurements/{measurement.Id}", new { accepted = true, id = measurement.Id });
});

app.MapPost("/api/ml/train", async (AppDbContext db, IHttpClientFactory f) =>
{
    try
    {
        var latest = await db.Measurements.Where(x => x.Sensor == "petcore/status" || x.Sensor == "petcore/comida" || x.Sensor == "petcore/horario").OrderByDescending(x => x.ReceivedAt).Take(20000).Select(x => new { x.Id, topic = x.Sensor, x.Value, at = x.MeasuredAt ?? x.ReceivedAt }).ToListAsync();
        var history = latest.AsEnumerable().Reverse().ToList();
        var response = await f.CreateClient("ml").PostAsJsonAsync("/train", new { history });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (response.IsSuccessStatusCode && body.TryGetProperty("status", out var status) && status.GetString() == "treinado")
        {
            var model = await db.AiModels.FirstAsync();
            model.Status = "Treinado";
            model.TrainedAt = body.GetProperty("trainedAt").GetDateTimeOffset();
            model.Version = body.GetProperty("version").GetString() ?? model.Version;
            model.Parameters = body.GetProperty("parameters").GetRawText();
            await db.SaveChangesAsync();
        }
        return Results.Content(body.GetRawText(), "application/json", statusCode: (int)response.StatusCode);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Results.Json(new { error = "Serviço ML indisponível" }, statusCode: 503); }
});
app.Run();
