# NEXORA — Frontend Web

O frontend foi criado em `Nexora.Web`, separado da API existente.

## Requisitos
- Node.js 20.19+ ou 22.12+
- .NET SDK compatível com o `Nexora.Api.csproj` (o projeto enviado está configurado para .NET 10)
- PostgreSQL configurado como já estava no ambiente de desenvolvimento

## 1. Iniciar o backend
Abra a solução `Nexora.sln` no Rider e execute `Nexora.Api` como antes. A API deve responder em:

`http://localhost:5241/api/status`

Se a porta for diferente, crie `Nexora.Web/.env` com:

`VITE_API_URL=http://localhost:PORTA`

A API recebeu uma política CORS limitada aos endereços locais do Vite (`localhost:5173` e `127.0.0.1:5173`).

## 2. Iniciar o frontend
Abra o terminal do Rider na pasta `Nexora.Web` e execute:

```bash
npm install
npm run dev
```

Abra o endereço mostrado pelo Vite, normalmente `http://localhost:5173`.

## O que está conectado
- `/api/status`
- `/api/dashboard/summary`
- `/api/systems/health`
- `/api/systems`
- `/api/systems/performance`
- `/api/events` e `/api/events/anomalies`
- `/api/alerts`, `/api/alerts/recurrences`
- `PATCH /api/alerts/{id}/resolve`

Os dados são buscados na API. O dashboard atualiza automaticamente a cada 30 segundos e também possui botão de atualização manual. A resolução de alertas altera o estado no backend e registra o histórico.

## Observação
A tela de anomalias apresenta exatamente os dados retornados pelo endpoint atual. Alguns campos opcionais são tratados de forma tolerante porque o formato detalhado dos objetos de anomalia depende do serviço de detecção.
