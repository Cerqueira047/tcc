#nullable enable
using System;
using System.Linq;
using Nexora.Analise;

namespace Nexora.Testes;

// Executor sem dependências. Cada caso falho é exibido e o processo termina com código 1.
public static class Program
{
    private static readonly DateTimeOffset Inicio = new(2026, 11, 18, 13, 0, 0, TimeSpan.Zero);
    private static readonly AnalisadorPedidos Motor = new(TimeSpan.FromMinutes(15));
    private static readonly Evento Venda = new(1, "1050", "vendas", Inicio, 2, 59.90m);
    private static readonly Evento Estoque = Venda with { RegistroId = 2, Sistema = "estoque", OcorridoEm = Inicio.AddMinutes(5) };

    private static Ocorrencia[] Analisar(params Evento[] eventos) =>
        Motor.Analisar(eventos, Inicio.AddMinutes(16)).ToArray();

    // Compara a saída inteira para também descobrir alertas indevidos.
    private static void Esperar(Ocorrencia[] resultado, params string[] tipos)
    {
        var obtidos = resultado.Select(o => o.Tipo + "/" + o.Campo).OrderBy(t => t);
        Verificar(obtidos.SequenceEqual(tipos.OrderBy(t => t)), "Obtidos: " + string.Join(", ", obtidos));
    }

    private static void Verificar(bool condicao, string detalhe)
    {
        if (!condicao) throw new Exception(detalhe);
    }

    public static int Main()
    {
        var casos = new (string Nome, Action Executar)[] {
            ("Lote vazio", () => Esperar(Analisar())),
            ("Par correto em ordem de leitura inversa", () => Esperar(Analisar(Estoque, Venda))),
            ("Pedido 1050 ausente no estoque", () => {
                var r = Analisar(Venda);
                Esperar(r, "PEDIDO_AUSENTE/pedidoId");
                Verificar(r[0].Sistema == "estoque" && r[0].PedidoId == "1050" &&
                    r[0].Registros.SequenceEqual(new long[] { 1 }) && r[0].PossivelCausa.Length > 0, "Evidência incorreta.");
            }),
            ("Venda ausente", () => {
                var r = Analisar(Estoque with { OcorridoEm = Inicio });
                Esperar(r, "PEDIDO_AUSENTE/pedidoId");
                Verificar(r.Single().Sistema == "vendas", "Origem incorreta.");
            }),
            ("Sem alerta antes ou no limite do prazo", () => {
                foreach (int minuto in new[] { 0, 14, 15 })
                    Esperar(Motor.Analisar(new[] { Venda }, Inicio.AddMinutes(minuto)).ToArray());
            }),
            ("Atraso ao chegar o par substitui a ausência", () =>
                Esperar(Analisar(Venda, Estoque with { OcorridoEm = Inicio.AddMinutes(16) }), "ATRASO/ocorridoEm")),
            ("Recebimento no limite e fuso equivalente", () =>
                Esperar(Analisar(Venda, Estoque with { OcorridoEm = Inicio.AddMinutes(15).ToOffset(TimeSpan.FromHours(-3)) }))),
            ("Prazo configurável", () =>
                Esperar(new AnalisadorPedidos(TimeSpan.FromMinutes(3)).Analisar(new[] { Venda, Estoque }, Inicio.AddMinutes(6)).ToArray(), "ATRASO/ocorridoEm")),
            ("Quantidade e valor divergentes", () =>
                Esperar(Analisar(Venda, Estoque with { QuantidadeTotal = 1, ValorTotal = 59.91m }),
                    "DIVERGENCIA/quantidadeTotal", "DIVERGENCIA/valorTotal")),
            ("Cinco campos ausentes no mesmo registro", () =>
                Esperar(Analisar(new Evento(3, " ", null, null, null, null)), "CAMPO_AUSENTE/pedidoId",
                    "CAMPO_AUSENTE/sistema", "CAMPO_AUSENTE/ocorridoEm", "CAMPO_AUSENTE/quantidadeTotal", "CAMPO_AUSENTE/valorTotal")),
            ("Campo vazio não esconde outra divergência", () =>
                Esperar(Analisar(Venda, Estoque with { QuantidadeTotal = null, ValorTotal = 9m }),
                    "CAMPO_AUSENTE/quantidadeTotal", "DIVERGENCIA/valorTotal")),
            ("Valor zero válido e normalização das origens", () =>
                Esperar(Analisar(Venda with { ValorTotal = 0m }, Estoque with { ValorTotal = 0m, PedidoId = " 1050 ", Sistema = " ESTOQUE " }))),
            ("Valores inválidos não viram divergência", () =>
                Esperar(Analisar(Venda, Estoque with { QuantidadeTotal = 0, ValorTotal = -1m }),
                    "CAMPO_INVALIDO/quantidadeTotal", "CAMPO_INVALIDO/valorTotal")),
            ("Origem desconhecida impede falso alerta de ausência", () =>
                Esperar(Analisar(Venda, Estoque with { Sistema = "financeiro" }), "CAMPO_INVALIDO/sistema")),
            ("Horário de confirmação anterior à venda", () =>
                Esperar(Analisar(Venda, Estoque with { OcorridoEm = Inicio.AddMinutes(-1) }), "ORDEM_TEMPORAL_INVALIDA/ocorridoEm")),
            ("Duplicados em ambas as origens", () => {
                var r = Analisar(Venda, Estoque, Venda with { RegistroId = 3 }, Estoque with { RegistroId = 4 });
                Esperar(r, "DUPLICADO/pedidoId", "DUPLICADO/pedidoId");
                Verificar(r.Select(o => o.Sistema).Distinct().Count() == 2, "Faltou uma origem.");
            }),
            ("Reprocessamento preserva a chave e ordena as evidências", () => {
                var repetido = Venda with { RegistroId = 3 };
                var a = Analisar(Venda, repetido, Estoque).Single();
                var b = Analisar(Estoque, repetido, Venda, Venda with { RegistroId = 4 }).Single();
                Verificar(a.Chave == b.Chave && b.Registros.SequenceEqual(new long[] { 1, 3, 4 }), "Identidade instável.");
            }),
            ("Pedidos distintos ficam separados", () =>
                Esperar(Analisar(Venda, Estoque with { PedidoId = "01050", OcorridoEm = Inicio }),
                    "PEDIDO_AUSENTE/pedidoId", "PEDIDO_AUSENTE/pedidoId")),
            ("ID técnico repetido é erro de entrada", () => {
                try { Analisar(Venda, Venda); }
                catch (ArgumentException) { return; }
                throw new Exception("Aceitou duas leituras da mesma linha.");
            })
        };

        // Executa todos os cenários, mesmo se algum falhar.
        int falhas = 0;
        foreach (var caso in casos)
        {
            try { caso.Executar(); Console.WriteLine("OK  " + caso.Nome); }
            catch (Exception erro) { falhas++; Console.Error.WriteLine("FALHA  " + caso.Nome + " | " + erro.Message); }
        }
        Console.WriteLine($"{casos.Length - falhas}/{casos.Length} testes aprovados.");
        return falhas == 0 ? 0 : 1;
    }
}
