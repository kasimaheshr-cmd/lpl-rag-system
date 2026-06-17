from fastapi import FastAPI, HTTPException, Path
try:
    # Pydantic v2
    from pydantic import BaseModel, field_validator
except ImportError:  # pragma: no cover
    # Pydantic v1 fallback
    from pydantic import BaseModel, validator as field_validator
import ollama
import chromadb
from chromadb.utils import embedding_functions
import io
import re
from pypdf import PdfReader
from fastapi import FastAPI, HTTPException, UploadFile, File, Form
from rank_bm25 import BM25Okapi
import uuid
from datetime import datetime, timedelta
from fastapi.responses import StreamingResponse
import json
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse
from agent.agent_router import router as agent_router

app = FastAPI(title="LPL RAG Engine", version="2.0.0")
app.include_router(agent_router, prefix="/agent", tags=["agent"])
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# --- ChromaDB setup ---
chroma_client = chromadb.PersistentClient(path="./lpl_vectors")

conversation_store = {}
MAX_HISTORY = 6        # keep last 6 exchanges (3 Q&A pairs)
SESSION_TTL = 3600     # sessions expire after 1 hour of inactivity

# Use nomic-embed-text for embeddings (the model you already downloaded)
embedding_fn = embedding_functions.OllamaEmbeddingFunction(
    url="http://localhost:11434/api/embeddings",
    model_name="nomic-embed-text"
)

collection = chroma_client.get_or_create_collection(
    name="lpl_documents",
    embedding_function=embedding_fn
)

# --- Data Models ---
class IngestRequest(BaseModel):
    content: str
    source: str
    department: str = "General"

class QuestionRequest(BaseModel):
    question: str
    department: str = "General"
    session_id: str = ""    # empty = start new session

    @field_validator("question")
    def question_not_empty(cls, v):
        if len(v.strip()) < 3:
            raise ValueError("Question too short")
        if len(v) > 1000:
            raise ValueError("Question too long — maximum 1000 characters")
        return v.strip()

class AnswerResponse(BaseModel):
    question: str
    answer: str
    sources: list[str]
    department: str
    session_id: str         # always return session_id so client can reuse it
    search_type: str = "hybrid"

# --- Helper: validate metadata strings ---
def _validate_source(source: str) -> str:
    if source is None:
        raise HTTPException(status_code=422, detail="source is required")

    normalized = source.strip()
    if not normalized:
        raise HTTPException(status_code=422, detail="source must be a non-empty string")

    if len(normalized) > 512:
        raise HTTPException(status_code=422, detail="source is too long (max 512 characters)")

    # Guard against obvious control characters that can break logs/clients.
    if any(ord(ch) < 32 for ch in normalized):
        raise HTTPException(status_code=422, detail="source contains invalid control characters")

    return normalized

# --- Helper: chunk long text ---
def chunk_text(text: str, chunk_size: int = 500) -> list[str]:
    words = text.split()
    chunks = []
    for i in range(0, len(words), chunk_size):
        chunk = " ".join(words[i:i + chunk_size])
        chunks.append(chunk)
    return chunks

def chunk_text_smart(text: str, chunk_size: int = 100, overlap: int = 10) -> list[str]:
    # Clean the text first
    text = re.sub(r'\s+', ' ', text).strip()
    text = re.sub(r'(\w)-\n(\w)', r'\1\2', text)  # fix hyphenated line breaks

    # Split at sentence boundaries
    sentences = re.split(r'(?<=[.!?])\s+', text)

    chunks = []
    current_chunk = []
    current_length = 0

    for sentence in sentences:
        sentence_length = len(sentence.split())

        if current_length + sentence_length > chunk_size and current_chunk:
            chunks.append(" ".join(current_chunk))
            # Keep last few sentences for overlap context
            overlap_sentences = []
            overlap_length = 0
            for s in reversed(current_chunk):
                if overlap_length + len(s.split()) <= overlap:
                    overlap_sentences.insert(0, s)
                    overlap_length += len(s.split())
                else:
                    break
            current_chunk = overlap_sentences
            current_length = overlap_length

        current_chunk.append(sentence)
        current_length += sentence_length

    if current_chunk:
        chunks.append(" ".join(current_chunk))

    return [c for c in chunks if len(c.strip()) > 20]

def get_or_create_session(session_id: str) -> dict:
    now = datetime.now()

    # Clean expired sessions (simple housekeeping)
    expired = [
        sid for sid, data in conversation_store.items()
        if now - data["last_used"] > timedelta(seconds=SESSION_TTL)
    ]
    for sid in expired:
        del conversation_store[sid]

    # Return existing session
    if session_id and session_id in conversation_store:
        conversation_store[session_id]["last_used"] = now
        return conversation_store[session_id]

    # Create new session
    new_id = str(uuid.uuid4())[:8]  # short 8-char ID
    conversation_store[new_id] = {
        "history": [],
        "created": now,
        "last_used": now
    }
    return conversation_store[new_id]

def build_conversation_prompt(history: list, context: str, question: str) -> str:
    prompt = """You are an AI assistant for LPL Financial.
Use ONLY the provided documents to answer questions.
If the answer is not in the documents, say "I don't have that information."

Documents:
{context}

""".format(context=context)

    # Add conversation history
    if history:
        prompt += "Previous conversation:\n"
        for exchange in history[-MAX_HISTORY:]:
            prompt += f"User: {exchange['question']}\n"
            prompt += f"Assistant: {exchange['answer']}\n\n"

    prompt += f"Current question: {question}\n\nAnswer:"
    return prompt

# --- Endpoints ---
@app.get("/health")
def health():
    doc_count = collection.count()
    return {
        "status": "running",
        "documents_stored": doc_count,
        "llm": "llama3.2",
        "embed_model": "nomic-embed-text",
        "vector_db": "chromadb"
    }

@app.post("/ingest")
def ingest_document(request: IngestRequest):
    try:
        chunks = chunk_text(request.content)
        ids = [f"{request.source}_chunk_{i}" for i in range(len(chunks))]
        metadatas = [
            {"source": request.source, "department": request.department}
            for _ in chunks
        ]
        collection.add(
            documents=chunks,
            metadatas=metadatas,
            ids=ids
        )
        return {
            "status": "ingested",
            "source": request.source,
            "chunks_stored": len(chunks)
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/session/{session_id}")
def get_session(session_id: str):
    if session_id not in conversation_store:
        raise HTTPException(status_code=404, detail="Session not found or expired")

    session = conversation_store[session_id]
    return {
        "session_id": session_id,
        "exchange_count": len(session["history"]),
        "created": session["created"].isoformat(),
        "last_used": session["last_used"].isoformat(),
        "history": session["history"]
    }


@app.post("/ask", response_model=AnswerResponse)
def ask_question(request: QuestionRequest):
    try:
        # Get or create session
        session = get_or_create_session(request.session_id)
        session_id = [k for k, v in conversation_store.items() if v is session][0]

        # Hybrid search
        docs, metas, scores = hybrid_search(request.question)

        if not docs:
            answer = "I don't have relevant documents to answer this question."
            sources = []
        else:
            sources = [m["source"] for m in metas]
            context = "\n\n".join([
                f"Source [{sources[i]}]: {docs[i]}"
                for i in range(len(docs))
            ])

            # Build prompt with conversation history
            prompt = build_conversation_prompt(
                session["history"],
                context,
                request.question
            )

            response = ollama.chat(
                model="llama3.2",
                messages=[{"role": "user", "content": prompt}]
            )
            answer = response["message"]["content"]

        # Save to session history
        session["history"].append({
            "question": request.question,
            "answer": answer,
            "sources": sources,
            "timestamp": datetime.now().isoformat()
        })

        # Trim history to max length
        if len(session["history"]) > MAX_HISTORY:
            session["history"] = session["history"][-MAX_HISTORY:]

        return AnswerResponse(
            question=request.question,
            answer=answer,
            sources=list(set(sources)),
            department=request.department,
            session_id=session_id,
            search_type="hybrid"
        )

    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.delete("/document/{source:path}")
def delete_document_by_source(
    source: str = Path(..., description="Document source identifier used during ingestion")
):
    """
    Deletes all chunks from the ChromaDB collection whose metadata 'source' matches `source`.
    """
    source = _validate_source(source)

    try:
        existing = collection.get(where={"source": source},include=["metadatas"])
        ids = existing.get("ids") or []
        if not ids:
            raise HTTPException(status_code=404, detail=f"No chunks found for source '{source}'")

        collection.delete(where={"source": source})

        return {
            "status": "deleted",
            "source": source,
            "chunks_deleted": len(ids),
        }
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to delete chunks for source '{source}': {e}")


@app.post("/ingest/pdf")
async def ingest_pdf(
    file: UploadFile = File(...),
    department: str = Form(default="General")
):
    # Validate file type
    if not file.filename.endswith(".pdf"):
        raise HTTPException(
            status_code=422,
            detail="Only PDF files are accepted"
        )

    # Validate file size (max 10MB)
    contents = await file.read()
    if len(contents) > 10 * 1024 * 1024:
        raise HTTPException(
            status_code=422,
            detail="File too large — maximum 10MB"
        )

    try:
        # Extract text from PDF
        pdf_reader = PdfReader(io.BytesIO(contents))
        full_text = ""
        page_count = len(pdf_reader.pages)

        for page_num, page in enumerate(pdf_reader.pages):
            page_text = page.extract_text()
            if page_text:
                full_text += f"\n{page_text}"

        if len(full_text.strip()) < 50:
            raise HTTPException(
                status_code=422,
                detail="PDF appears to be empty or scanned image — text extraction failed"
            )

        # Smart chunk the extracted text
        source_name = file.filename.replace(".pdf", "").replace(" ", "-").lower()
        chunks = chunk_text_smart(full_text)

        # Store in ChromaDB
        ids = [f"{source_name}_p{i}" for i in range(len(chunks))]
        metadatas = [
            {
                "source": source_name,
                "department": department,
                "page_count": page_count,
                "file": file.filename
            }
            for _ in chunks
        ]

        collection.add(
            documents=chunks,
            metadatas=metadatas,
            ids=ids
        )

        return {
            "status": "ingested",
            "filename": file.filename,
            "source": source_name,
            "pages_processed": page_count,
            "chunks_stored": len(chunks),
            "avg_chunk_size": round(sum(len(c.split()) for c in chunks) / len(chunks))
        }

    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"PDF processing failed: {str(e)}")

def hybrid_search(query: str, n_results: int = 3, distance_threshold: float = 0.45):
    # --- Step 1: Vector Search ---
    vector_results = collection.query(
        query_texts=[query],
        n_results=min(n_results * 2, collection.count()),
        include=["documents", "metadatas", "distances"]
    )

    vector_docs   = vector_results["documents"][0]
    vector_metas  = vector_results["metadatas"][0]
    vector_dists  = vector_results["distances"][0]

    # --- Step 2: BM25 Keyword Search ---
    all_docs = collection.get(include=["documents", "metadatas"])
    corpus   = all_docs["documents"]
    metas    = all_docs["metadatas"]

    # Tokenize corpus
    tokenized_corpus = [doc.lower().split() for doc in corpus]
    bm25 = BM25Okapi(tokenized_corpus)

    # Score all documents against query
    tokenized_query = query.lower().split()
    bm25_scores     = bm25.get_scores(tokenized_query)

    # Get top BM25 results
    top_bm25_indices = sorted(
        range(len(bm25_scores)),
        key=lambda i: bm25_scores[i],
        reverse=True
    )[:n_results * 2]

    bm25_docs  = [corpus[i] for i in top_bm25_indices]
    bm25_metas = [metas[i]  for i in top_bm25_indices]

    # --- Step 3: Reciprocal Rank Fusion ---
    # RRF score = 1 / (k + rank) — standard k=60
    k = 60
    rrf_scores = {}

    # Score from vector results
    for rank, (doc, meta, dist) in enumerate(
        zip(vector_docs, vector_metas, vector_dists)
    ):
        if dist < distance_threshold:
            key = doc[:100]  # use first 100 chars as unique key
            rrf_scores[key] = rrf_scores.get(key, {
                "doc": doc, "meta": meta, "score": 0
            })
            rrf_scores[key]["score"] += 1 / (k + rank)

    # Score from BM25 results
    for rank, (doc, meta) in enumerate(zip(bm25_docs, bm25_metas)):
        key = doc[:100]
        if key not in rrf_scores:
            rrf_scores[key] = {"doc": doc, "meta": meta, "score": 0}
        rrf_scores[key]["score"] += 1 / (k + rank)

    # Sort by combined score, return top n
    ranked = sorted(
        rrf_scores.values(),
        key=lambda x: x["score"],
        reverse=True
    )[:n_results]

    return (
        [r["doc"]  for r in ranked],
        [r["meta"] for r in ranked],
        [r["score"] for r in ranked]
    )

@app.post("/ask/stream")
async def ask_question_stream(request: QuestionRequest):
    try:
        session = get_or_create_session(request.session_id)
        session_id = [k for k, v in conversation_store.items() if v is session][0]

        docs, metas, scores = hybrid_search(request.question)

        if not docs:
            async def no_docs():
                yield f"data: {json.dumps({'token': 'I dont have relevant documents to answer this question.', 'done': False})}\n\n"
                yield f"data: {json.dumps({'token': '', 'done': True, 'session_id': session_id, 'sources': []})}\n\n"
            return StreamingResponse(no_docs(), media_type="text/event-stream")

        sources = [m["source"] for m in metas]
        context = "\n\n".join([
            f"Source [{sources[i]}]: {docs[i]}"
            for i in range(len(docs))
        ])

        prompt = build_conversation_prompt(
            session["history"], context, request.question
        )

        async def generate():
            full_answer = ""

            # Stream tokens from Ollama
            stream = ollama.chat(
                model="llama3.2",
                messages=[{"role": "user", "content": prompt}],
                stream=True
            )

            for chunk in stream:
                token = chunk["message"]["content"]
                full_answer += token

                # Send each token as Server-Sent Event
                yield f"data: {json.dumps({'token': token, 'done': False})}\n\n"
               
            # Save to session after full response
            session["history"].append({
                "question": request.question,
                "answer": full_answer,
                "sources": sources,
                "timestamp": datetime.now().isoformat()
            })
            if len(session["history"]) > MAX_HISTORY:
                session["history"] = session["history"][-MAX_HISTORY:]

            # Final event with metadata
            yield f"data: {json.dumps({'token': '', 'done': True, 'session_id': session_id, 'sources': list(set(sources))})}\n\n"

        return StreamingResponse(generate(), media_type="text/event-stream")

    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/ui", response_class=HTMLResponse)
def serve_ui():
    with open("test_ui.html", "r") as f:
        return HTMLResponse(f.read())