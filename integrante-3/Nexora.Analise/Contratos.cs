#nullable enable
using System;
using System.Collections.Generic;

namespace Nexora.Analise;

// Uma linha representa um resumo de pedido emitido por um sistema.
// RegistroId identifica a linha no histórico. PedidoId liga as duas origens.
// Campos anuláveis permitem analisar inclusive dados incompletos já armazenados.
public sealed record Evento(
    long RegistroId,
    string? PedidoId,
    string? Sistema,
    DateTimeOffset? OcorridoEm,
    int? QuantidadeTotal,
    decimal? ValorTotal);

// Chave identifica o problema entre análises. Os IDs apontam as linhas usadas como evidência.
public sealed record Ocorrencia(
    string Chave,
    string Tipo,
    string? PedidoId,
    string? Sistema,
    string Campo,
    string Mensagem,
    string PossivelCausa,
    IReadOnlyList<long> Registros);

// Contrato que a API pode registrar na injeção de dependência.
public interface IAnalisadorPedidos
{
    IReadOnlyList<Ocorrencia> Analisar(IEnumerable<Evento> eventos, DateTimeOffset referencia);
}
