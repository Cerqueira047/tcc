using Nexora.Analise;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nexora.Api.Data;
using Nexora.Api.Models;

namespace Nexora.Api.Services;

public sealed class AnomalyDetectionService(NexoraDbContext db, IConfiguration configuration, IAnalisadorPedidos orders)
{
    public async Task<List<object>> AnalyzeRecordAsync(string recordId, string eventType, CancellationToken ct = default)
    {
        var events = await db.Events.AsNoTracking()
            .Where(e => e.RecordId == recordId && e.EventType == eventType)
            .OrderByDescending(e => e.OccurredAt).ToListAsync(ct);

        var result = Analyze(events, eventType, recordId);
        if (result is null) return [];

        var financialDifference = result.DiferencaFinanceira;
        var severity = result.Gravidade;
        var message = string.Join(" ", result.Inconsistencias);

        var alert = await db.Alerts.FirstOrDefaultAsync(a =>
            a.RecordId == recordId && a.EventType == eventType && !a.Resolved, ct);

        if (alert is null)
        {
            alert = new Alert {
                RecordId = recordId, EventType = eventType, Severity = severity,
                Message = message, FinancialDifference = financialDifference
            };
            alert.History.Add(new AlertHistory {
                Action = "Criado", Description = "Inconsistência detectada automaticamente: " + message
            });
            db.Alerts.Add(alert);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when
                (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                db.Entry(alert).State = EntityState.Detached;
            }
        }
        else if (alert.Message != message || alert.FinancialDifference != financialDifference || alert.Severity != severity)
        {
            alert.Message = message;
            alert.FinancialDifference = financialDifference;
            alert.Severity = severity;
            db.AlertHistories.Add(new AlertHistory {
                AlertId = alert.Id,
                Action = "Atualizado", Description = "A análise foi atualizada: " + message
            });
            await db.SaveChangesAsync(ct);
        }

        return [result];
    }

    public async Task<List<object>> GetCurrentAnomaliesAsync(CancellationToken ct = default)
    {
        var keys = await db.Events.AsNoTracking()
            .Select(e => new { e.RecordId, e.EventType }).Distinct().ToListAsync(ct);
        var result = new List<object>();
        foreach (var key in keys)
            result.AddRange(await AnalyzeReadOnlyAsync(key.RecordId, key.EventType, ct));
        return result;
    }

    private async Task<List<object>> AnalyzeReadOnlyAsync(string recordId, string eventType, CancellationToken ct)
    {
        var events = await db.Events.AsNoTracking()
            .Where(e => e.RecordId == recordId && e.EventType == eventType)
            .OrderByDescending(e => e.OccurredAt).ToListAsync(ct);
        var result = Analyze(events, eventType, recordId);
        return result is null ? [] : [result];
    }

    private sealed record AnalysisResult(string Registro, string TipoEvento, string Gravidade,
        List<string> Inconsistencias, List<object> ValoresComparados, string[] SistemasEsperadosAusentes,
        decimal DiferencaFinanceira, IReadOnlyList<OrderFinding> OcorrenciasPedidos);

    private AnalysisResult? Analyze(List<Event> events, string eventType, string recordId)
    {
        var latest = events.GroupBy(e => e.SystemName).Select(g => g.First()).ToList();
        var expected = configuration.GetSection("Monitoring:ExpectedSystems").Get<string[]>() ?? [];
        var observed = latest.Select(e => e.SystemName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = expected.Where(s => !observed.Contains(s)).ToArray();
        var issues = new List<string>();
        var details = new List<object>();
        var financialDifference = CompareMetric("valor financeiro", ReadMetric(latest, OrderAnalysis.AmountNames), issues, details);
        var quantityDifference = CompareMetric("quantidade", ReadMetric(latest, OrderAnalysis.QuantityNames), issues, details);
        if (missing.Length > 0) issues.Add("Aguardando evento dos sistemas: " + string.Join(", ", missing));

        var findings = OrderAnalysis.Analyze(orders, events, eventType, DateTimeOffset.UtcNow);
        foreach (var finding in findings)
        {
            // A regra do pedido também traz as evidências da divergência já resumida acima.
            if (finding.Tipo == "DIVERGENCIA" &&
                (finding.Campo == "valorTotal" && financialDifference > 0 ||
                 finding.Campo == "quantidadeTotal" && quantityDifference > 0))
                continue;
            issues.Add(finding.Mensagem);
        }

        if (issues.Count == 0) return null;
        var severity = financialDifference >= 500m ? "Alta"
            : financialDifference >= 100m || missing.Length > 0 ? "Média" : "Baixa";
        return new(recordId, eventType, severity, issues, details, missing, financialDifference, findings);
    }

    private static List<(string System, decimal Value)> ReadMetric(
        IEnumerable<Event> events, string[] acceptedNames)
    {
        var values = new List<(string, decimal)>();
        foreach (var item in events)
            if (OrderAnalysis.TryGetNumber(item.Data, acceptedNames, out var value))
                values.Add((item.SystemName, value));
        return values;
    }

    private static decimal CompareMetric(string label, List<(string System, decimal Value)> values,
        List<string> issues, List<object> details)
    {
        if (values.Count < 2) return 0m;
        var difference = values.Max(x => x.Value) - values.Min(x => x.Value);
        details.Add(new { indicador = label, valores = values.Select(x => new { sistema = x.System, valor = x.Value }) });
        if (difference > 0m) issues.Add($"Divergência de {label}: diferença de {difference}.");
        return difference;
    }
}
