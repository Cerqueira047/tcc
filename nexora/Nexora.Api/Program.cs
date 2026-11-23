using Nexora.Analise;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Api.Data;
using Nexora.Api.DTOs;
using Nexora.Api.Models;
using Nexora.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection não configurada. Configure via User Secrets ou variável de ambiente ConnectionStrings__DefaultConnection.");

builder.Services.AddDbContext<NexoraDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<AlertPriorityService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddSingleton<IAnalisadorPedidos>(new AnalisadorPedidos(TimeSpan.FromMinutes(
    builder.Configuration.GetValue("Monitoring:OrderTimeoutMinutes", 15))));
builder.Services.AddScoped<AnomalyDetectionService>();
builder.Services.AddScoped<AlertRecurrenceService>();
builder.Services.AddScoped<SystemPerformanceService>();
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddPolicy("NexoraWeb", policy =>
    policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
          .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("NexoraWeb");

// Bootstrap para o MVP: cria o banco/tabelas quando ainda não existem.
// Quando houver migrations versionadas, substituir por Database.MigrateAsync().
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();

app.MapGet("/", () => Results.Ok(new {
    sistema = "NEXORA API", status = "Online", versao = "1.0"
}));

app.MapGet("/api/status", async (NexoraDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Database.CanConnectAsync(ct);
        return Results.Ok(new { sistema = "NEXORA", api = "Online", banco = "Conectado" });
    }
    catch
    {
        return Results.Json(new { sistema = "NEXORA", api = "Online", banco = "Indisponível" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

var events = app.MapGroup("/api/events");

events.MapPost("/", async (
    CreateEventRequest request, NexoraDbContext db,
    AnomalyDetectionService detector, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.SystemName) ||
        string.IsNullOrWhiteSpace(request.EventId) ||
        string.IsNullOrWhiteSpace(request.RecordId) ||
        string.IsNullOrWhiteSpace(request.EventType))
        return Results.BadRequest(new { mensagem = "SystemName, EventId, RecordId e EventType são obrigatórios." });

    if (request.SystemName.Trim().Length > 100 || request.EventId.Trim().Length > 200 ||
        request.RecordId.Trim().Length > 200 || request.EventType.Trim().Length > 100)
        return Results.BadRequest(new { mensagem = "Tamanho máximo: SystemName 100, EventId 200, RecordId 200 e EventType 100 caracteres." });

    if (request.OccurredAt == default || request.OccurredAt > DateTime.UtcNow.AddMinutes(5))
        return Results.BadRequest(new { mensagem = "OccurredAt deve ser uma data válida e não pode estar no futuro." });

    if (request.Data.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { mensagem = "Data deve ser um objeto JSON." });

    var systemName = request.SystemName.Trim();
    var eventId = request.EventId.Trim();
    var recordId = request.RecordId.Trim();
    var eventType = request.EventType.Trim();

    if (await db.Events.AnyAsync(e => e.SystemName == systemName && e.EventId == eventId, ct))
        return Results.Conflict(new { mensagem = "Este evento já foi recebido pela NEXORA.", systemName, eventId });

    var item = new Event {
        SystemName = systemName, EventId = eventId, RecordId = recordId,
        EventType = eventType, OccurredAt = request.OccurredAt ?? DateTime.UtcNow,
        Data = request.Data.GetRawText()
    };
    db.Events.Add(item);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when
        (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
    {
        // A checagem prévia não elimina corrida entre requisições simultâneas;
        // o índice único do banco é a garantia final de idempotência.
        return Results.Conflict(new { mensagem = "Este evento já foi recebido pela NEXORA.", systemName, eventId });
    }

    var anomalies = await detector.AnalyzeRecordAsync(recordId, eventType, ct);
    return Results.Created($"/api/events/{item.Id}", new {
        evento = item, inconsistenciasDetectadas = anomalies
    });
});

events.MapGet("/", async (NexoraDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);
    page = Math.Min(page, int.MaxValue / pageSize);
    var query = db.Events.AsNoTracking().OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id);
    var total = await query.CountAsync(ct);
    var data = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, total, data });
});

events.MapGet("/{id:guid}", async (Guid id, NexoraDbContext db, CancellationToken ct) =>
{
    var item = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    if (item is null) return Results.NotFound(new { mensagem = "Evento não encontrado." });
    return Results.Ok(item);
});

events.MapGet("/anomalies", async (AnomalyDetectionService detector, CancellationToken ct) =>
    Results.Ok(await detector.GetCurrentAnomaliesAsync(ct)));

app.MapGet("/api/alerts", async (NexoraDbContext db, bool? resolved, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 200);
    page = Math.Min(page, int.MaxValue / pageSize);
    var query = db.Alerts.AsNoTracking().AsQueryable();
    if (resolved.HasValue) query = query.Where(a => a.Resolved == resolved.Value);
    query = query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
    var total = await query.CountAsync(ct);
    var data = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
    return Results.Ok(new { page, pageSize, total, data });
});

app.MapGet("/api/alerts/prioritized", async (
    NexoraDbContext db, AlertPriorityService priorityService, CancellationToken ct) =>
{
    var alerts = await db.Alerts.AsNoTracking().Where(a => !a.Resolved).ToListAsync(ct);
    return Results.Ok(alerts.Select(a => new {
        a.Id, a.RecordId, a.EventType, a.Severity, a.FinancialDifference, a.Message,
        prioridade = priorityService.CalcularPrioridade(a),
        classificacaoPrioridade = priorityService.ClassificarPrioridade(priorityService.CalcularPrioridade(a))
    }).OrderByDescending(a => a.prioridade));
});

app.MapPatch("/api/alerts/{id:guid}/resolve", async (Guid id, NexoraDbContext db, CancellationToken ct) =>
{
    var alert = await db.Alerts.Include(a => a.History).FirstOrDefaultAsync(a => a.Id == id, ct);
    if (alert is null) return Results.NotFound(new { mensagem = "Alerta não encontrado." });
    if (alert.Resolved) return Results.Conflict(new { mensagem = "Este alerta já foi resolvido." });
    alert.Resolved = true;
    var history = new AlertHistory
    {
        AlertId = alert.Id,
        Alert = alert,
        Action = "Resolvido",
        Description = "O alerta foi marcado como resolvido."
    };

    db.AlertHistories.Add(history);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException ex)
    {
        return Results.Conflict(new
        {
            erro = "Conflito ao salvar alterações",
            entidades = ex.Entries.Select(e => new
            {
                entidade = e.Metadata.ClrType.Name,
                estado = e.State.ToString(),
                chaves = string.Join(", ",
                    e.Properties
                        .Where(p => p.Metadata.IsPrimaryKey())
                        .Select(p => $"{p.Metadata.Name}={p.CurrentValue}"))
            })
        });
    }
    return Results.Ok(new
    {
        mensagem = "Alerta resolvido com sucesso."
    });
});

app.MapGet("/api/alerts/{id:guid}/history", async (Guid id, NexoraDbContext db, CancellationToken ct) =>
{
    var exists = await db.Alerts.AnyAsync(a => a.Id == id, ct);
    if (!exists) return Results.NotFound(new { mensagem = "Alerta não encontrado." });
    var history = await db.AlertHistories.AsNoTracking()
        .Where(h => h.AlertId == id).OrderBy(h => h.CreatedAt).ToListAsync(ct);
    return Results.Ok(history);
});

app.MapGet("/api/dashboard/summary", async (DashboardService service, CancellationToken ct) =>
    Results.Ok(await service.GetSummaryAsync(ct)));

app.MapGet("/api/alerts/recurrences", async (AlertRecurrenceService service, CancellationToken ct) =>
    Results.Ok(await service.GetRecurrencesAsync(ct)));

app.MapGet("/api/systems/performance", async (SystemPerformanceService service, CancellationToken ct) =>
    Results.Ok(await service.GetSystemFailuresAsync(ct)));

app.MapGet("/api/systems/health", async (SystemPerformanceService service, CancellationToken ct) =>
    Results.Ok(await service.GetHealthSummaryAsync(ct)));

app.MapGet("/api/systems", async (NexoraDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var observed = await db.Events.AsNoTracking().Select(e => e.SystemName).Distinct().OrderBy(x => x).ToListAsync(ct);
    var expected = config.GetSection("Monitoring:ExpectedSystems").Get<string[]>() ?? [];
    return Results.Ok(new { sistemasObservados = observed, sistemasEsperados = expected });
});

app.Run();

public partial class Program { }
