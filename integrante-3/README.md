# NEXORA · Inteligência e análise

**Entrega do integrante 3** · Biblioteca C# para comparar os pedidos de vendas com as confirmações do estoque. A API chama um método e recebe uma lista de ocorrências. O motor funciona em memória e pode ser testado separadamente.

## Comece pelo pedido 1050

Com o SDK do **.NET 8** instalado, abra o terminal na pasta deste README e execute os comandos abaixo. Os projetos usam apenas a biblioteca padrão do .NET.

```sh
dotnet run --project Exemplo -- exemplo-1050.json
dotnet run --project Testes
```

No exemplo, a venda ocorreu às 10h no horário de Brasília (13h UTC). Às 13h16 UTC, o estoque ainda não confirmou o pedido. O resultado tem uma ocorrência `PEDIDO_AUSENTE`, com `pedidoId` igual a `1050`, `sistema` igual a `estoque` e o registro 1 como evidência. O segundo comando executa 19 testes. Ele devolve código 1 se alguma verificação falhar.

| Arquivo | Para que serve |
| --- | --- |
| Nexora.Analise/Contratos.cs | Define o formato de entrada, a saída e o método que a API chama. |
| Nexora.Analise/AnalisadorPedidos.cs | Contém as regras comentadas. |
| exemplo-1050.json e Exemplo/Program.cs | Permitem alterar os dados e rodar uma demonstração. |
| Testes/Program.cs | Verifica cada regra e situações em que o sistema deve ficar sem alertas. |

## Combinados entre os três integrantes

O modelo proposto compara **um resumo do pedido em cada sistema**. A quantidade é a soma das unidades de todos os itens. O valor é o total final do mesmo pedido em reais, usando o mesmo critério de descontos e frete nos dois lados. Se o estoque trabalha por produto, o integrante 2 deve montar o resumo antes de enviar ao motor. Mudanças de status, cancelamentos e entregas parciais precisam de uma regra própria se o grupo decidir incluí-los no projeto.

| Campo JSON | Tipo em C# | Como preencher |
| --- | --- | --- |
| registroId | long | Identificador positivo e único da linha, gerado pelo banco. O sistema externo envia os campos abaixo. |
| pedidoId | string | Mesmo identificador nas duas origens. Preservar zeros à esquerda e maiúsculas/minúsculas. Espaços nas pontas são removidos. |
| sistema | string | `vendas` ou `estoque`. O motor aceita variações de maiúsculas e minúsculas. |
| ocorridoEm | DateTimeOffset | Instante da venda ou da confirmação no estoque. Enviar ISO 8601 com fuso, como `2026-11-18T10:00:00-03:00`. |
| quantidadeTotal | int | Quantidade inteira maior que zero. |
| valorTotal | decimal | Total em reais, como número JSON, por exemplo `59.90`. Zero é permitido. |

O DTO aceita campos de negócio nulos para apontar dados faltantes em históricos e importações. Para o endpoint de entrada, o integrante 1 pode validar os campos obrigatórios antes da gravação. A API deve rejeitar datas sem fuso e números com tipo incompatível antes de criar o DTO. O identificador técnico precisa existir antes da análise.

## Regras que o motor aplica

| Tipo da ocorrência | Quando aparece |
| --- | --- |
| CAMPO_AUSENTE | Uma linha tem campo obrigatório nulo ou texto em branco. Gera uma ocorrência por campo. |
| CAMPO_INVALIDO | A origem é desconhecida, a quantidade é zero ou negativa, ou o total é negativo. |
| DUPLICADO | Duas ou mais linhas distintas têm o mesmo pedido e a mesma origem. Uma ocorrência reúne os IDs dessa origem. |
| PEDIDO_AUSENTE | Passou o prazo e falta o registro correspondente em vendas ou no estoque. |
| DIVERGENCIA | Com um registro de cada lado, os totais válidos diferem. Quantidade e valor são comparados separadamente. |
| ATRASO | O estoque confirmou o pedido, mas o intervalo desde a venda ultrapassou o prazo. |
| ORDEM_TEMPORAL_INVALIDA | O horário da confirmação no estoque é anterior ao da venda. |

O prazo sugerido é **15 minutos**. No limite exato o pedido continua dentro do prazo. Dados incompletos só impedem as comparações que dependem deles. Havendo duplicidade, o motor registra as linhas e evita escolher uma versão para comparar. Um registro sem pedido continua recebendo alertas de campo, mas não pode ser associado a outro sistema.

## Integrante 1 · Conectar à API ASP.NET Core

Copie a pasta `Nexora.Analise` para o mesmo diretório que contém a pasta do projeto da API. Na pasta que contém as duas, adicione a referência. Substitua `SuaApi/SuaApi.csproj` pelo caminho real.

```sh
dotnet add SuaApi/SuaApi.csproj reference Nexora.Analise/Nexora.Analise.csproj
```

No `Program.cs`, importe o namespace e registre o serviço antes de `builder.Build()`. O motor pode ser compartilhado entre chamadas porque mantém apenas o prazo.

```csharp
using Nexora.Analise;

// O mesmo prazo vale para a análise acionada pela API e para a revisão periódica.
builder.Services.AddSingleton<IAnalisadorPedidos>(
    new AnalisadorPedidos(TimeSpan.FromMinutes(15)));
```

No serviço ou endpoint autenticado de análise, receba `IAnalisadorPedidos motor` por injeção de dependência. Depois de consultar o banco, a chamada completa do motor é:

```csharp
// eventosDoBanco é uma lista de Evento com as duas origens dos pedidos selecionados.
var referencia = DateTimeOffset.UtcNow;
var ocorrencias = motor.Analisar(eventosDoBanco, referencia);
// Entregue ocorrencias e referencia ao método do integrante 2 que atualiza o histórico.
// Em uma Minimal API, a resposta pode ser: return Results.Ok(ocorrencias);
```

Acione a análise após a gravação dos eventos e também em intervalos regulares, por exemplo a cada minuto. A segunda chamada detecta um pedido cujo prazo venceu sem que chegasse outro evento. Se a consulta estiver paginada, selecione primeiro os IDs dos pedidos e depois carregue **todas as linhas desses pedidos** nas duas origens. Uma página isolada pode produzir uma falsa ausência.

## Integrante 2 · Conectar ao PostgreSQL e EF Core

A biblioteca recebe qualquer coleção de `Evento`. O exemplo abaixo considera uma entidade com os nomes da tabela de campos. Adapte os nomes à entidade do grupo. Normalize o identificador na entrada para consultar pelo mesmo valor que o motor usa.

```csharp
// Trecho do repositório. O filtro inclui as duas origens do pedido.
var eventosDoBanco = await db.Eventos.AsNoTracking()
    .Where(e => e.PedidoId == pedidoId)
    .Select(e => new Evento(e.Id, e.PedidoId, e.Sistema, e.OcorridoEm,
                            e.QuantidadeTotal, e.ValorTotal))
    .ToListAsync(cancellationToken);
```

Use `using Nexora.Analise;` e `using Microsoft.EntityFrameworkCore;` nesse arquivo. Para auditar linhas sem `pedidoId`, selecione-as pelo identificador técnico e envie-as também ao motor. Cada linha física deve aparecer uma vez na consulta. Dois registros distintos podem representar um reenvio do mesmo pedido.

| Campo de saída | Uso no banco ou na API |
| --- | --- |
| chave | Texto de 64 caracteres. Índice único para reconhecer o mesmo alerta ao reprocessar. |
| tipo, pedidoId, sistema, campo | Filtros de consulta. Em uma comparação entre as duas origens, sistema pode ser nulo. |
| mensagem, possivelCausa | Explicação para o usuário e hipótese a investigar. |
| registros | Lista dos IDs das linhas que sustentam o alerta. Pode ser mapeada para bigint[] no PostgreSQL. |

**Histórico.** Guarde a primeira e a última detecção, o estado e as evidências. Ao analisar novamente o mesmo conjunto completo de pedidos, insira as novas chaves e atualize as que já existem. Marque como resolvidas as ocorrências abertas que sumiram **daquele conjunto analisado**. Uma confirmação tardia, por exemplo, encerra `PEDIDO_AUSENTE` e abre `ATRASO`. Para linhas sem pedido, delimite a atualização pelos IDs técnicos. Se duas execuções puderem analisar o mesmo pedido ao mesmo tempo, serialize a leitura e a atualização por pedido em uma transação. Assim, uma análise antiga não reabre um alerta já resolvido.

## Conferência rápida em grupo

1. O integrante 1 envia uma venda com `pedidoId` igual a `1050`. O integrante 2 confirma a gravação e o ID gerado.
2. Para reproduzir sem esperar, use a referência fixa do JSON de demonstração. Com somente a venda no histórico, deve aparecer uma ausência no estoque.
3. Acrescente a confirmação do estoque com outro `registroId`, o mesmo pedido, quantidade e valor, e horário `2026-11-18T13:16:00Z`. A próxima análise deve retornar apenas `ATRASO`.
4. Mude o horário da confirmação para `13:05:00Z` na mesma data e o valor para `49.90`. Deve aparecer apenas `DIVERGENCIA` no campo `valorTotal`. Ao devolver o valor a `59.90`, a saída deve ser uma lista vazia.
