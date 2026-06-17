# Setup guide

## Prerequisites

| Tool | Download | Purpose |
|---|---|---|
| Rancher Desktop | https://rancherdesktop.io | Docker runtime (free) |
| Ollama | https://ollama.com | Local LLM + embeddings |
| Python 3.11+ | https://python.org | AI engine runtime |
| Visual Studio 2022+ | https://visualstudio.microsoft.com | .NET 8 SDK for gateway |

---

## One-command setup

```powershell
.\setup-env.ps1
```

This script:
- Starts Kafka, Redis, MongoDB, OpenSearch as Docker containers
- Pulls llama3.2 and nomic-embed-text via Ollama
- Creates Python venv and installs all packages from requirements.txt
- Restores .NET NuGet packages for the gateway

First run takes 5–10 minutes. Subsequent runs are instant.

---

## Project structure

```
lpl-rag-system/
├── ai-engine/              Python — FastAPI RAG engine
│   ├── agent/              ReAct compliance agent
│   ├── infrastructure/     OpenSearch, Redis, Kafka clients
│   ├── lpl_vectors/        Embedded document chunks
│   ├── main.py             FastAPI entrypoint (port 8001)
│   └── requirements.txt    Python dependencies
└── gatekeeper-api/         C# .NET 8 — enterprise gateway
    └── LPL.Gatekeeper/     JWT auth, guardrails, Kafka audit
                            (port 5258)
```

---

## Running the system

**Terminal 1 — Start AI engine:**
```powershell
cd ai-engine
.\venv\Scripts\Activate.ps1
uvicorn main:app --reload --port 8001
```

**Terminal 2 — Start gateway:**
```
Open lpl-rag-system.sln in Visual Studio
Set LPL.Gatekeeper as startup project → F5
```

**Test it:**
```powershell
curl -X POST http://localhost:5258/api/query `
  -H "Content-Type: application/json" `
  -d '{"query": "What are the requirements of FINRA Rule 4511?"}'
```

---

## Connection strings

```
Kafka:       localhost:9092
Redis:       localhost:6379   password: LPLRedis2024!
MongoDB:     localhost:27017  admin / LPLMongo2024!
OpenSearch:  localhost:9200
Ollama:      localhost:11434
AI engine:   localhost:8001
Gateway:     localhost:5258
```

---

## Architecture

```
Advisor query
      ↓
LPL.Gatekeeper (port 5258)     C# .NET 8
  JWT auth · rate limiting · 9 guardrails
      ↓
AI Engine (port 8001)          Python FastAPI
  Semantic cache (Redis 0.92 cosine)
  ReAct compliance agent
  Hybrid retrieval: BM25 + knn → RRF fusion k=60
  Bedrock Titan embeddings (768-dim) / Ollama local
      ↓
OpenSearch      vector + BM25 index
MongoDB         trajectory logging · 7-year TTL (FINRA 4511)
Kafka           audit event pipeline
Redis           semantic cache · 65% hit rate
```

---

## Local stack vs production mapping

| Local | Production (AWS) |
|---|---|
| Kafka (Redpanda) | AWS MSK |
| Redis | AWS ElastiCache |
| MongoDB | MongoDB Atlas |
| OpenSearch | AWS OpenSearch Service |
| Ollama llama3.2 | AWS Bedrock Claude |
| Ollama nomic-embed-text | AWS Bedrock Titan (768-dim) |

---

## Troubleshooting

**venv activation blocked by PowerShell policy:**
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

**OpenSearch password error:**
The local setup uses `DISABLE_SECURITY_PLUGIN=true` — no password needed locally.

**Port 8001 already in use:**
```powershell
netstat -ano | findstr :8001
taskkill /PID <pid> /F
```

**Ollama model not found:**
```powershell
ollama serve
ollama pull nomic-embed-text
```
