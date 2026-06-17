# lpl-rag-system environment setup
# Run this once on any new machine after installing:
#   1. Rancher Desktop  https://rancherdesktop.io
#   2. Ollama           https://ollama.com
#   3. Python 3.11+     https://python.org
#   4. Visual Studio 2022/2026 with .NET 8 SDK

Write-Host "=== lpl-rag-system environment setup ===" -ForegroundColor Cyan

# --- Check prerequisites ---
Write-Host "`n[1/5] Checking prerequisites..." -ForegroundColor Yellow

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Docker not found. Install Rancher Desktop first." -ForegroundColor Red
    exit 1
}

if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Ollama not found. Install from https://ollama.com" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Python not found. Install Python 3.11+ from https://python.org" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: dotnet not found. Install .NET 8 SDK first." -ForegroundColor Red
    exit 1
}

Write-Host "Prerequisites OK" -ForegroundColor Green

# --- Start containers ---
Write-Host "`n[2/5] Starting containers..." -ForegroundColor Yellow

# Kafka / Redpanda
$kafka = docker ps -q -f name=kafka
if (-not $kafka) {
    Write-Host "  Starting Kafka (Redpanda)..."
    docker run -d --name kafka -p 9092:9092 `
        redpandadata/redpanda:latest `
        redpanda start --overprovisioned --smp 1 --memory 512M `
        --reserve-memory 0M --node-id 0 --check=false
} else {
    Write-Host "  Kafka already running"
}

# Redis
$redis = docker ps -q -f name=redis
if (-not $redis) {
    Write-Host "  Starting Redis..."
    docker run -d --name redis -p 6379:6379 `
        redis redis-server --requirepass LPLRedis2024!
} else {
    Write-Host "  Redis already running"
}

# MongoDB
$mongo = docker ps -q -f name=mongo
if (-not $mongo) {
    Write-Host "  Starting MongoDB..."
    docker run -d --name mongo -p 27017:27017 `
        -e MONGO_INITDB_ROOT_USERNAME=admin `
        -e MONGO_INITDB_ROOT_PASSWORD=LPLMongo2024! `
        mongo
} else {
    Write-Host "  MongoDB already running"
}

# OpenSearch
$opensearch = docker ps -q -f name=opensearch
if (-not $opensearch) {
    Write-Host "  Starting OpenSearch..."
    docker run -d --name opensearch -p 9200:9200 `
        -e discovery.type=single-node `
        -e "DISABLE_SECURITY_PLUGIN=true" `
        opensearchproject/opensearch:latest
} else {
    Write-Host "  OpenSearch already running"
}

Write-Host "Containers started" -ForegroundColor Green

# --- Pull Ollama models ---
Write-Host "`n[3/5] Pulling Ollama models..." -ForegroundColor Yellow

Write-Host "  Pulling llama3.2 (LLM)..."
ollama pull llama3.2

Write-Host "  Pulling nomic-embed-text (embeddings, 768-dim)..."
ollama pull nomic-embed-text

Write-Host "Ollama models ready" -ForegroundColor Green

# --- Python virtual environment ---
Write-Host "`n[4/5] Setting up Python environment..." -ForegroundColor Yellow

cd ai-engine

if (-not (Test-Path "venv")) {
    Write-Host "  Creating virtual environment..."
    python -m venv venv
}

Write-Host "  Activating venv and installing packages..."
.\venv\Scripts\Activate.ps1
pip install -r requirements.txt

Write-Host "Python environment ready" -ForegroundColor Green
cd ..

# --- Restore .NET packages ---
Write-Host "`n[5/5] Restoring .NET packages..." -ForegroundColor Yellow
dotnet restore gatekeeper-api\LPL.Gatekeeper\LPL.Gatekeeper.csproj
Write-Host ".NET packages restored" -ForegroundColor Green

# --- Done ---
Write-Host "`n=== Setup complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Connection strings:" -ForegroundColor White
Write-Host "  Kafka:       localhost:9092"
Write-Host "  Redis:       localhost:6379   password: LPLRedis2024!"
Write-Host "  MongoDB:     localhost:27017  admin / LPLMongo2024!"
Write-Host "  OpenSearch:  localhost:9200"
Write-Host "  Ollama:      localhost:11434"
Write-Host ""
Write-Host "To run ai-engine (Python FastAPI):" -ForegroundColor Cyan
Write-Host "  cd ai-engine"
Write-Host "  .\venv\Scripts\Activate.ps1"
Write-Host "  uvicorn main:app --reload --port 8001"
Write-Host ""
Write-Host "To run gatekeeper-api (C# .NET 8):" -ForegroundColor Cyan
Write-Host "  Open lpl-rag-system.sln in Visual Studio"
Write-Host "  Set LPL.Gatekeeper as startup project -> F5"
Write-Host "  Gateway runs on port 5258"
