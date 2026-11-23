using System.Text.Json;
using Nexora.Analise;
using Nexora.Api.Models;

namespace Nexora.Api.Services;

public sealed record OrderFinding(string Tipo, string Campo, string? Sistema, string Mensagem,
    string PossivelCausa, IReadOnlyList<Guid> Registros);

internal static class OrderAnalysis
{
    internal static readonly string[] AmountNames = ["amount", "valorTotal", "totalAmount"];
    internal static readonly string[] QuantityNames = ["quantity", "quantidade", "quantidadeTotal", "totalQuantity"];

    internal static IReadOnlyList<OrderFinding> Analyze(IAnalisadorPedidos analyzer,
        IEnumerable<Event> events, string eventType, DateTimeOffset reference)
    {
        if (!string.Equals(eventType, "PedidoAtualizado", StringComparison.OrdinalIgnoreCase))
            return [];

        var rows = new List<Evento>();
        var ids = new Dictionary<long, Guid>();
        foreach (var item in events.OrderBy(e => e.Id))
        {
            var system = item.SystemName.Trim().ToLowerInvariant() switch
            {
                "sistemavendas" or "vendas" => "vendas",
                "sistemaestoque" or "estoque" => "estoque",
                _ => null
            };
            // Eventos financeiros e de outros serviços continuam na análise geral da API.
            if (system is null) continue;
            long rowId = rows.Count + 1L;
            ids.Add(rowId, item.Id);
            var hasQuantity = TryGetNumber(item.Data, QuantityNames, out var qty);
            int? quantity = !hasQuantity ? null :
                qty == decimal.Truncate(qty) && qty >= int.MinValue && qty <= int.MaxValue ? (int)qty : -1;
            decimal? amount = TryGetNumber(item.Data, AmountNames, out var value) ? value : null;
            rows.Add(new(rowId, item.RecordId, system, item.OccurredAt, quantity, amount));
        }

        var result = new List<OrderFinding>();
        foreach (var issue in analyzer.Analisar(rows, reference))
        {
            var evidence = issue.Registros.Select(id => ids[id]).ToArray();
            var message = issue.Mensagem;
            if (issue.Tipo is "CAMPO_AUSENTE" or "CAMPO_INVALIDO")
                message = message.Replace($"registro {issue.Registros[0]}.", $"registro {evidence[0]}.");
            result.Add(new(issue.Tipo, issue.Campo, issue.Sistema, message, issue.PossivelCausa, evidence));
        }
        return result.OrderBy(f => f.Tipo, StringComparer.Ordinal)
            .ThenBy(f => f.Campo, StringComparer.Ordinal).ThenBy(f => f.Registros[0]).ToArray();
    }

    internal static bool TryGetNumber(string json, string[] acceptedNames, out decimal value)
    {
        value = 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in doc.RootElement.EnumerateObject())
                if (acceptedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out value);
        }
        catch (JsonException) { }
        return false;
    }
}
