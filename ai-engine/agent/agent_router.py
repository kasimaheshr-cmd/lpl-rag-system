"""
LPL Agent — FastAPI Router
Week 7, Day 1

Adds /agent/* endpoints to your existing FastAPI app.

Add to main.py:
    from agent.agent_router import router as agent_router
    app.include_router(agent_router, prefix="/agent", tags=["agent"])

Endpoints:
    POST /agent/run          — run the compliance agent
    POST /agent/run/stream   — streaming agent (SSE, each step as event)
    GET  /agent/run/{run_id} — retrieve past agent run
    GET  /agent/tools        — list available tools
    GET  /agent/health       — agent system health
"""

import json
import logging
from datetime import datetime
from typing import Optional

from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel

from agent.compliance_agent import ComplianceAgent, AgentResult, AgentStep
from agent.tools import get_tool_registry

log    = logging.getLogger(__name__)
router = APIRouter()

# In-memory run store (swap for Redis/MongoDB in production)
_run_store: dict[str, AgentResult] = {}


# ── Request / Response models ─────────────────────────────────────────────────

class AgentRunRequest(BaseModel):
    goal:       str
    user_id:    str
    session_id: Optional[str] = None


class AgentStepResponse(BaseModel):
    step_number:  int
    thought:      str
    tool_called:  Optional[str]  = None
    parameters:   Optional[dict] = None
    observation:  Optional[str]  = None
    is_final:     bool = False


class AgentRunResponse(BaseModel):
    run_id:        str
    goal:          str
    user_id:       str
    final_answer:  str
    total_steps:   int
    duration_ms:   float
    tools_called:  list[str]
    success:       bool
    steps:         list[AgentStepResponse]
    error:         Optional[str] = None


def _step_to_response(step: AgentStep) -> AgentStepResponse:
    return AgentStepResponse(
        step_number=step.step_number,
        thought=step.thought,
        tool_called=step.tool_call.tool_name if step.tool_call else None,
        parameters=step.tool_call.parameters if step.tool_call else None,
        observation=step.observation if step.observation else None,
        is_final=step.is_final,
    )


def _result_to_response(result: AgentResult) -> AgentRunResponse:
    return AgentRunResponse(
        run_id=result.run_id,
        goal=result.goal,
        user_id=result.user_id,
        final_answer=result.final_answer,
        total_steps=result.total_steps,
        duration_ms=result.duration_ms,
        tools_called=result.tools_called,
        success=result.success,
        steps=[_step_to_response(s) for s in result.steps],
        error=result.error or None,
    )


# ── Endpoints ─────────────────────────────────────────────────────────────────

@router.post("/run", response_model=AgentRunResponse,
             summary="Run the compliance agent")
async def run_agent(request: AgentRunRequest):
    """
    Execute the compliance agent for a given goal.

    The agent will:
    1. Plan the steps needed to achieve the goal
    2. Call tools in sequence (RAG search, audit trail, compliance check, report)
    3. Reason over intermediate results
    4. Return a structured final answer with full trace

    Example goals:
    - "Review john.smith's compliance for the last 30 days"
    - "What FINRA rules apply to variable annuity recommendations?"
    - "Generate a compliance report for advisor sarah.jones"
    - "Check if advisor mike.compliance has reviewed suitability requirements"
    """
    if not request.goal.strip():
        raise HTTPException(status_code=400, detail="Goal cannot be empty")

    tools = get_tool_registry()
    agent = ComplianceAgent(tools=tools)

    log.info("Agent run: user=%s goal=%s", request.user_id, request.goal[:80])

    result = await agent.run(
        goal=request.goal,
        user_id=request.user_id,
    )

    # Store for later retrieval
    _run_store[result.run_id] = result

    return _result_to_response(result)


@router.post("/run/stream", summary="Run the compliance agent with SSE streaming")
async def run_agent_stream(request: AgentRunRequest):
    """
    Same as /agent/run but streams each step as a Server-Sent Event.
    The client sees the agent's thinking in real time.

    SSE event format:
        data: {"type": "step", "step": {...}}
        data: {"type": "final", "answer": "..."}
        data: {"type": "error", "message": "..."}
    """

    async def event_stream():
        tools = get_tool_registry()
        agent = ComplianceAgent(tools=tools)

        # Monkey-patch to emit SSE events after each step
        original_run = agent.run

        async def tracked_run(goal: str, user_id: str):
            result = await original_run(goal=goal, user_id=user_id)

            # Emit each step
            for step in result.steps:
                step_data = {
                    "type":        "step",
                    "step_number": step.step_number,
                    "thought":     step.thought,
                    "tool_called": step.tool_call.tool_name if step.tool_call else None,
                    "observation": step.observation[:300] if step.observation else None,
                    "is_final":    step.is_final,
                }
                yield f"data: {json.dumps(step_data)}\n\n"

            # Emit final answer
            final_data = {
                "type":         "final",
                "run_id":       result.run_id,
                "answer":       result.final_answer,
                "total_steps":  result.total_steps,
                "duration_ms":  result.duration_ms,
                "tools_called": result.tools_called,
                "success":      result.success,
            }
            yield f"data: {json.dumps(final_data)}\n\n"

        try:
            async for event in tracked_run(
                goal=request.goal,
                user_id=request.user_id,
            ):
                yield event
        except Exception as e:
            error_data = {"type": "error", "message": str(e)}
            yield f"data: {json.dumps(error_data)}\n\n"

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",
        },
    )


@router.get("/run/{run_id}", response_model=AgentRunResponse,
            summary="Retrieve a past agent run")
async def get_agent_run(run_id: str):
    """Retrieve a previous agent run by its run_id."""
    result = _run_store.get(run_id)
    if not result:
        raise HTTPException(
            status_code=404,
            detail=f"Agent run '{run_id}' not found. Runs are stored in memory — restart clears them.",
        )
    return _result_to_response(result)


@router.get("/tools", summary="List available agent tools")
async def list_tools():
    """Returns all tools available to the compliance agent."""
    tools = get_tool_registry()
    return {
        "tools": [
            {
                "name":        name,
                "description": fn.__doc__.strip().split("\n")[0] if fn.__doc__ else "",
            }
            for name, fn in tools.items()
        ],
        "count": len(tools),
    }


@router.get("/health", summary="Agent system health check")
async def agent_health():
    """Check that all agent dependencies are reachable."""
    import httpx

    checks = {}

    # RAG engine
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            r = await client.get("http://localhost:8001/health")
            checks["rag_engine"] = "ok" if r.status_code == 200 else "degraded"
    except Exception:
        checks["rag_engine"] = "unreachable"

    # Ollama
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            r = await client.get("http://localhost:11434/api/tags")
            checks["ollama"] = "ok" if r.status_code == 200 else "degraded"
    except Exception:
        checks["ollama"] = "unreachable"

    # MongoDB
    try:
        from motor.motor_asyncio import AsyncIOMotorClient
        client = AsyncIOMotorClient(
            "mongodb://admin:LPLMongo2024!@localhost:27017",
            serverSelectionTimeoutMS=3000,
        )
        await client.admin.command("ping")
        checks["mongodb"] = "ok"
        client.close()
    except Exception:
        checks["mongodb"] = "unreachable"

    overall = "ok" if all(v == "ok" for v in checks.values()) else "degraded"

    return {
        "status":     overall,
        "components": checks,
        "timestamp":  datetime.utcnow().isoformat(),
        "tools":      len(get_tool_registry()),
    }


# ── Example goals for Swagger UI ──────────────────────────────────────────────

EXAMPLE_GOALS = [
    "Review john.smith's compliance activity for the last 30 days and identify any gaps",
    "Generate a compliance report for advisor sarah.jones",
    "What FINRA rules should advisor john.smith be aware of given their recent queries?",
    "Check if the compliance team has adequate coverage of KYC and AML topics this month",
    "Identify any advisors with low-confidence AI responses in the last 7 days",
]
