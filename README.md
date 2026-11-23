# TCC · Nexora

O projeto do grupo está em [nexora](nexora/README.md). A API usa a biblioteca [Nexora.Analise](integrante-3/Nexora.Analise) do integrante 3 ao receber eventos em `POST /api/events` e ao consultar `GET /api/events/anomalies`. A interface web e as regras gerais de alertas continuam na pasta do projeto do grupo.

## Testar sua parte

Na raiz do repositório, com o SDK .NET 8 instalado, execute:

```sh
dotnet run --project integrante-3/Testes
```

Para testar a API completa, configure o PostgreSQL conforme o [guia da API](nexora/README.md) e use o SDK .NET 10. Envie os dois exemplos de `PedidoAtualizado` do [arquivo de requisições](nexora/Nexora.Api/Nexora.Api.http) com `occurredAt` em UTC. Na segunda resposta, confira `inconsistenciasDetectadas[0].ocorrenciasPedidos`: as divergências indicam o campo e os IDs dos eventos. Vendas e estoque são comparados por `recordId`. O prazo padrão de 15 minutos pode ser ajustado em `Monitoring:OrderTimeoutMinutes`. A ausência após o prazo é reavaliada quando chega outro evento desse pedido ou quando alguém consulta as anomalias.

## Trazer mudanças do projeto do Vinícius

A pasta `nexora/` acompanha a branch `master` de [ViniciusMeyer18/NexoraAtualizado](https://github.com/ViniciusMeyer18/NexoraAtualizado) via Git subtree. Na raiz de uma cópia de `Cerqueira047/tcc`, com a branch `main` atualizada e as alterações locais salvas:

```sh
git subtree pull --prefix=nexora https://github.com/ViniciusMeyer18/NexoraAtualizado.git master --squash
```

Revise e teste a integração, depois execute `git push origin main` para publicá-la no seu TCC. Se os dois projetos alterarem as mesmas linhas, o Git pedirá para resolver o conflito. Cada repositório tem publicação independente.
