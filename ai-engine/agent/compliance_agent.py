"""
LPL Financial - Compliance Agent
Week 7, Day 1

An agentic AI layer on top of the RAG system.
Instead of answering one question, the agent:
  1. Understands the goal
  2. Plans the steps needed
  3. Calls tools in sequence
  4. Reasons over intermediate results
  5. Returns a structured final answer

Uses Ollama llama3.2 locally - zero API cost.
"""

import json
import logging
import re
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum
from typing import Any, Optional

import httpx

log = logging.getLogger(__name__)

OLLAMA_URL      = "http://localhost:11434/api/chat"
OLLAMA_MODEL    = "llama3.2"
MAX_ITERATIONS  = 8
REQUEST_TIMEOUT = 120.0


class StepStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    DONE    = "done"
    FAILED  = "failed"
    SKIPPED = "skipped"


@dataclass
class ToolCall:
    tool_name:  str
    parameters: dict
    result:     Any        = None
    error:      str        = ""
    latency_ms: float      = 0.0
    status:     StepStatus = StepStatus.PENDING


@dataclass
class AgentStep:
    step_number:  int
    thought:      str
    tool_call:    Optional[ToolCall] = None
    observation:  str  = ""
    is_final:     bool = False


@dataclass
class AgentMemory:
    run_id:      str
    goal:        str
    user_id:     str
    advisor_id:  str = ""
    steps:       list[AgentStep] = field(default_factory=list)
    created_at:  datetime = field(default_factory=datetime.utcnow)

    def to_context_string(self) -> str:
        advisor_hint = self.advisor_id or "the advisor mentioned in the goal"
        lines = [
            "GOAL: " + self.goal,
            "",
            "CONTEXT:",
            "- Request from: " + self.user_id,
            "- Advisor to review: " + advisor_hint,
            "- IMPORTANT: Pass '" + advisor_hint + "' as user_id to query_advisor_activity",
            "- IMPORTANT: Use EXACTLY the parameter names from the tool schemas",
            "- IMPORTANT: Never search for a user_id - use the advisor name directly",
            "",
        ]
        for step in self.steps:
            lines.append(
                "Step " + str(step.step_number) + " - Thought: " + step.thought
            )
            if step.tool_call:
                lines.append(
                    "  -> Called: " + step.tool_call.tool_name +
                    "(" + json.dumps(step.tool_call.parameters) + ")"
                )
                lines.append("  -> Result: " + step.observation)
            lines.append("")
        return "\n".join(lines)


@dataclass
class AgentResult:
    run_id:         str
    goal:           str
    user_id:        str
    final_answer:   str
    steps:          list[AgentStep]
    total_steps:    int
    duration_ms:    float
    tools_called:   list[str]
    success:        bool
    error:          str = ""


AGENT_SYSTEM_PROMPT = """You are a compliance AI agent for LPL Financial.
You help compliance officers and advisors by reasoning through complex tasks
that require multiple steps and tool calls.

- COMPLIANCE REVIEW WORKFLOW: When asked to review activity AND generate a report,
  you MUST call ALL of these tools in this exact order:
  1. query_advisor_activity  
  2. check_compliance_rules
  3. generate_compliance_report
  Only set is_final=true AFTER generate_compliance_report has been called and returned.
  Never skip generate_compliance_report if the goal mentions "generate a report".

TOOL SCHEMAS - use EXACTLY these parameter names, no others:

search_compliance_docs(query: str)
  - Searches LPL compliance documents using RAG
  - query: the compliance question or topic to search for
  - Example: search_compliance_docs(query="FINRA Rule 2111 suitability")

query_advisor_activity(user_id: str, days: int = 30)
  - Gets an advisor recent queries from the audit trail
  - user_id: the advisor ID exactly as given in the context e.g. "john.smith"
  - days: number of days to look back, default 30
  - Example: query_advisor_activity(user_id="john.smith", days=30)

check_compliance_rules(activity_summary: dict, focus_areas: list = null)
  - Checks activity against FINRA compliance rules
  - activity_summary: the FULL dict returned by query_advisor_activity
  - focus_areas: optional list e.g. ["suitability", "kyc"] or omit entirely
  - Example: check_compliance_rules(activity_summary={...dict from previous step...})

generate_compliance_report(user_id: str, activity: dict, compliance_findings: dict)
  - Generates a structured compliance report
  - user_id: same advisor ID used in query_advisor_activity
  - activity: the FULL dict returned by query_advisor_activity
  - compliance_findings: the FULL dict returned by check_compliance_rules
  - Example: generate_compliance_report(user_id="john.smith", activity={...}, compliance_findings={...})

RESPONSE FORMAT - respond in valid JSON only, no other text:

If you need to call a tool:
{
  "thought": "your reasoning about what to do next",
  "action": "tool_name",
  "parameters": {"param1": "value1"},
  "is_final": false
}

If you have enough information to answer:
{
  "thought": "your final reasoning",
  "action": "final_answer",
  "answer": "your complete answer to the user goal",
  "is_final": true
}

Rules:
- Think step by step before each action
- Always use tool results to inform your next step
- Never make up compliance information - only use what tools return
- If a tool fails, note it and try a different approach
- Be concise in thoughts, thorough in final answers
- IMPORTANT: Use EXACTLY the parameter names shown in the tool schemas above
- IMPORTANT: The advisor name in the goal IS the user_id - use it directly
- IMPORTANT: Never search for a user_id - it is always the advisor name from the goal
- IMPORTANT: Pass complete dict outputs from one tool as inputs to the next tool
"""


class ComplianceAgent:
    """ReAct-style agent (Reason + Act) for LPL compliance tasks."""

    def __init__(self, tools: dict):
        self.tools = tools

    def _build_prompt(self, memory: AgentMemory) -> list[dict]:
        return [
            {"role": "system", "content": AGENT_SYSTEM_PROMPT},
            {"role": "user",   "content": memory.to_context_string()},
        ]

    async def _call_llm(self, messages: list[dict]) -> dict:
        payload = {
            "model":    OLLAMA_MODEL,
            "messages": messages,
            "stream":   False,
            "format":   "json",
            "options":  {"temperature": 0},
        }
        async with httpx.AsyncClient(timeout=REQUEST_TIMEOUT) as client:
            r = await client.post(OLLAMA_URL, json=payload)
            r.raise_for_status()

        content = r.json()["message"]["content"]
        try:
            return json.loads(content)
        except json.JSONDecodeError:
            match = re.search(r"\{.*\}", content, re.DOTALL)
            if match:
                return json.loads(match.group())
            raise ValueError("LLM did not return valid JSON: " + content[:200])

    async def _execute_tool(self, tool_call: ToolCall) -> str:
        tool_fn = self.tools.get(tool_call.tool_name)
        if not tool_fn:
            tool_call.status = StepStatus.FAILED
            tool_call.error  = "Unknown tool: " + tool_call.tool_name
            return "ERROR: tool '" + tool_call.tool_name + "' not found"

        t0 = time.monotonic()
        try:
            result               = await tool_fn(**tool_call.parameters)
            tool_call.result     = result
            tool_call.status     = StepStatus.DONE
            tool_call.latency_ms = (time.monotonic() - t0) * 1000
            if isinstance(result, dict):
                return json.dumps(result, indent=2)[:2000]
            return str(result)[:2000]
        except Exception as e:
            tool_call.status     = StepStatus.FAILED
            tool_call.error      = str(e)
            tool_call.latency_ms = (time.monotonic() - t0) * 1000
            log.error("Tool %s failed: %s", tool_call.tool_name, e)
            return "ERROR: " + str(e)

    async def run(self, goal: str, user_id: str) -> AgentResult:
        run_id  = "agent-" + uuid.uuid4().hex[:8]
        t_start = time.monotonic()
        log.info("[%s] Starting: %s", run_id, goal[:80])

        memory = AgentMemory(run_id=run_id, goal=goal, user_id=user_id)

        # Extract advisor name from goal automatically
        advisor_match = re.search(
            r"(?:advisor|review|for)\s+([\w.]+)",
            goal.lower()
        )
        if advisor_match:
            memory.advisor_id = advisor_match.group(1)
            log.info("[%s] Extracted advisor: %s", run_id, memory.advisor_id)

        final_answer = ""
        success      = True
        error_msg    = ""

        for iteration in range(MAX_ITERATIONS):
            log.info("[%s] Iteration %d/%d", run_id, iteration + 1, MAX_ITERATIONS)
            messages = self._build_prompt(memory)

            try:
                llm_response = await self._call_llm(messages)
            except Exception as e:
                log.error("[%s] LLM call failed: %s", run_id, e)
                error_msg = str(e)
                success   = False
                break

            thought  = llm_response.get("thought", "")
            is_final = llm_response.get("is_final", False)
            action   = llm_response.get("action", "")

            step = AgentStep(step_number=iteration + 1, thought=thought, is_final=is_final)

            if is_final or action == "final_answer":
                final_answer  = llm_response.get("answer", thought)
                step.is_final = True
                memory.steps.append(step)
                log.info("[%s] Final answer after %d steps", run_id, iteration + 1)
                break

            if action and action != "final_answer":
                parameters     = llm_response.get("parameters", {})
                tool_call      = ToolCall(tool_name=action, parameters=parameters)
                step.tool_call = tool_call
                log.info("[%s] Calling: %s(%s)", run_id, action, json.dumps(parameters)[:100])
                step.observation = await self._execute_tool(tool_call)
                memory.steps.append(step)
                continue

            log.warning("[%s] No action and not final", run_id)
            memory.steps.append(step)

        else:
            log.warning("[%s] Hit max iterations", run_id)
            final_answer = (
                "I was unable to complete the full analysis within the "
                "allowed steps. Here is what I found so far:\n\n" +
                memory.to_context_string()
            )
            success = False

        duration_ms  = (time.monotonic() - t_start) * 1000
        tools_called = [
            s.tool_call.tool_name
            for s in memory.steps
            if s.tool_call and s.tool_call.status == StepStatus.DONE
        ]

        return AgentResult(
            run_id=run_id,
            goal=goal,
            user_id=user_id,
            final_answer=final_answer,
            steps=memory.steps,
            total_steps=len(memory.steps),
            duration_ms=round(duration_ms),
            tools_called=tools_called,
            success=success,
            error=error_msg,
        )