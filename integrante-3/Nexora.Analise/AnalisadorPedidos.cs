#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexora.Analise;

public sealed class AnalisadorPedidos : IAnalisadorPedidos
{
    public TimeSpan Prazo { get; }

    // O grupo pode alterar o prazo sem editar as regras.
    public AnalisadorPedidos(TimeSpan prazo)
    {
        if (prazo <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(prazo), "Informe um prazo positivo.");
        Prazo = prazo;
    }

    // Passe um histórico completo dos pedidos selecionados, inclusive a outra origem.
    // A referência vem do chamador para que a mesma análise possa ser reproduzida.
    public IReadOnlyList<Ocorrencia> Analisar(IEnumerable<Evento> eventos, DateTimeOffset referencia)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        var saida = new List<Ocorrencia>();
        var correlacionaveis = new List<Evento>();
        var ids = new HashSet<long>();

        foreach (var original in eventos)
        {
            if (original is null || original.RegistroId <= 0 || !ids.Add(original.RegistroId))
                throw new ArgumentException("Cada linha do histórico precisa de um registroId positivo e único.", nameof(eventos));

            var evento = original with {
                PedidoId = Limpar(original.PedidoId),
                Sistema = Limpar(original.Sistema)?.ToLowerInvariant()
            };
            ValidarCampos(evento, saida);
            if (evento.PedidoId != null)
                correlacionaveis.Add(evento);
        }

        foreach (var pedido in correlacionaveis.GroupBy(e => e.PedidoId!))
            AnalisarPedido(pedido.Key, pedido.ToList(), referencia, saida);

        return saida.OrderBy(o => o.Chave, StringComparer.Ordinal).ToArray();
    }

    private static string? Limpar(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static void ValidarCampos(Evento e, List<Ocorrencia> saida)
    {
        // Zero é um valor monetário válido. Só null significa valor ausente.
        void Campo(string nome, bool ausente, bool invalido = false)
        {
            if (!ausente && !invalido) return;
            var tipo = ausente ? "CAMPO_AUSENTE" : "CAMPO_INVALIDO";
            var motivo = ausente ? "não foi informado" : "está fora do formato combinado";
            saida.Add(Criar(tipo, e.PedidoId, e.Sistema, nome,
                $"O campo {nome} {motivo} no registro {e.RegistroId}.",
                "O sistema de origem pode ter enviado dados incompletos ou usado outro mapeamento.",
                new[] { e }, e.RegistroId));
        }

        Campo("pedidoId", e.PedidoId is null);
        Campo("sistema", e.Sistema is null, e.Sistema is not ("vendas" or "estoque"));
        Campo("ocorridoEm", e.OcorridoEm is null);
        Campo("quantidadeTotal", e.QuantidadeTotal is null, e.QuantidadeTotal <= 0);
        Campo("valorTotal", e.ValorTotal is null, e.ValorTotal < 0);
    }

    private void AnalisarPedido(string id, List<Evento> linhas, DateTimeOffset referencia, List<Ocorrencia> saida)
    {
        var vendas = linhas.Where(e => e.Sistema == "vendas").ToArray();
        var estoque = linhas.Where(e => e.Sistema == "estoque").ToArray();

        // Uma venda e uma confirmação são o par normal. Duas linhas na mesma origem são duplicidade.
        foreach (var grupo in new[] { vendas, estoque }.Where(g => g.Length > 1))
            saida.Add(Criar("DUPLICADO", id, grupo[0].Sistema, "pedidoId",
                $"O pedido {id} aparece {grupo.Length} vezes em {grupo[0].Sistema}.",
                "Pode ter ocorrido um reenvio ou uma importação repetida.", grupo));

        if (vendas.Length == 0 || estoque.Length == 0)
        {
            // Uma origem ilegível pode ser a contraparte procurada. Evita acusar ausência sem evidência.
            if (vendas.Length + estoque.Length != linhas.Count) return;
            var existentes = vendas.Length > 0 ? vendas : estoque;
            var faltante = vendas.Length > 0 ? "estoque" : "vendas";
            // Basta um registro conhecido ter ultrapassado o prazo. No limite exato ainda há tempo.
            if (existentes.Any(e => e.OcorridoEm is { } t && referencia - t > Prazo))
                saida.Add(Criar("PEDIDO_AUSENTE", id, faltante, "pedidoId",
                    $"O pedido {id} consta em {existentes[0].Sistema}, mas não foi localizado em {faltante} após o prazo.",
                    "O envio pode ter falhado ou a integração ainda pode estar processando o pedido.", existentes));
            return;
        }

        // Havendo várias versões, a comparação dependeria de escolher uma linha arbitrariamente.
        if (vendas.Length != 1 || estoque.Length != 1) return;
        var venda = vendas[0];
        var recebimento = estoque[0];
        var par = new[] { venda, recebimento };

        void Divergencia(string campo, object emVendas, object emEstoque)
        {
            saida.Add(Criar("DIVERGENCIA", id, null, campo,
                $"Pedido {id}: {campo} em vendas = {emVendas}, em estoque = {emEstoque}.",
                "Um sistema pode ter recebido uma versão anterior do pedido.", par));
        }

        if (venda.QuantidadeTotal is > 0 && recebimento.QuantidadeTotal is > 0 &&
            venda.QuantidadeTotal != recebimento.QuantidadeTotal)
            Divergencia("quantidadeTotal", venda.QuantidadeTotal.Value, recebimento.QuantidadeTotal.Value);

        if (venda.ValorTotal is >= 0 && recebimento.ValorTotal is >= 0 && venda.ValorTotal != recebimento.ValorTotal)
            Divergencia("valorTotal", venda.ValorTotal.Value, recebimento.ValorTotal.Value);

        if (venda.OcorridoEm is not { } inicio || recebimento.OcorridoEm is not { } fim) return;
        if (fim < inicio)
            saida.Add(Criar("ORDEM_TEMPORAL_INVALIDA", id, null, "ocorridoEm",
                $"O estoque confirmou o pedido {id} antes da venda.",
                "Os relógios dos sistemas ou os horários enviados podem estar incorretos.", par));
        else if (fim - inicio > Prazo)
            saida.Add(Criar("ATRASO", id, "estoque", "ocorridoEm",
                $"O estoque confirmou o pedido {id} depois do prazo de {Prazo}.",
                "O processamento da fila ou o envio ao estoque pode ter demorado.", par));
    }

    private static Ocorrencia Criar(string tipo, string? pedido, string? sistema, string campo,
        string mensagem, string causa, IEnumerable<Evento> evidencias, long? linha = null)
    {
        // A identidade não inclui horário de execução, mensagem ou quantidade de evidências.
        // Para defeitos de campo a identidade é da linha. Para os demais é do pedido e da regra.
        var partes = JsonSerializer.Serialize(new object?[] {
            tipo, linha.HasValue ? null : pedido, linha.HasValue ? null : sistema, campo, linha
        });
        var chave = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partes)));
        return new(chave, tipo, pedido, sistema, campo, mensagem, causa,
            evidencias.Select(e => e.RegistroId).OrderBy(id => id).ToArray());
    }
}
