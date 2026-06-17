"""
LPL Compliance Agent — Tool Definitions
Week 7, Day 1

Four tools that give the agent access to real LPL infrastructure.
Each tool is an async function the agent can call by name.

Tools:
  1. search_compliance_docs   → existing RAG /ask endpoint
  2. query_advisor_activity   → MongoDB audit trail
  3. check_compliance_rules   → rule evaluation engine
  4. generate_compliance_report → structured report writer

The agent calls these by name with parameters.
The tool executes and returns a string result.
The agent reasons over the result and decides what to do next.
"""

import json
import logging
from datetime import datetime, timedelta
from typing import Optional

import httpx
from motor.motor_asyncio import AsyncIOMotorClient

log = logging.getLogger(__name__)

RAG_URL    = "http://localhost:8001"
MONGO_URL  = "mongodb://admin:LPLMongo2024!@localhost:27017"
TIMEOUT    = 60.0


# ── Tool 1 — Document Search ──────────────────────────────────────────────────

async def search_compliance_docs(
    query: str,
    session_id: str = "agent-search",
    **kwargs,
) -> dict:
    """Search LPL compliance documents using the RAG system. Returns relevant regulations and guidance."""

    log.info("[tool] search_compliance_docs: %s", query[:80])

    async with httpx.AsyncClient(timeout=TIMEOUT) as client:
        r = await client.post(
            f"{RAG_URL}/ask",
            json={"question": query, "session_id": session_id},
        )
        r.raise_for_status()

    data = r.json()

    # Extract clean result for agent reasoning
    return {
        "query":        query,
        "answer":       data.get("answer", "") if isinstance(data, dict) else str(data),
        "faithfulness": data.get("faithfulness_score", 0.0) if isinstance(data, dict) else 0.0,
        "sources": [
            s.get("source", "") if isinstance(s, dict) else str(s)
            for s in (data.get("sources", []) if isinstance(data, dict) else [])
        ],
        "confidence": (
            "high" if isinstance(data, dict) and data.get("faithfulness_score", 0) > 0.7
            else "low"
        ),
    }


# ── Tool 2 — Advisor Activity Query ──────────────────────────────────────────

async def query_advisor_activity(
    user_id: str = "",
    days: int = 30,
    limit: int = 20,
    **kwargs,
) -> dict:
    """Query an advisor's recent AI system activity from the audit trail.
    
    Args:
        user_id: the advisor ID e.g. "john.smith" - use the name from the goal directly
    """
    if not user_id:
        user_id = (
            kwargs.get("advisor_id") or
            kwargs.get("advisor") or
            kwargs.get("name") or
            "unknown"
        )

    log.info("[tool] query_advisor_activity: user=%s days=%d", user_id, days)

    client = AsyncIOMotorClient(MONGO_URL)
    db     = client["lpl_audit"]

    since = datetime.utcnow() - timedelta(days=days)

    try:
        cursor = db.audit_events.find(
            {
                "user_id":     user_id,
                "occurred_at": {"$gte": since},   # ← was "timestamp"
                "is_rejection": False,             # ← only real queries
            },
            {
                "_id":          0,
                "question":     1,
                "answer":       1,
                "occurred_at":  1,                 # ← was "timestamp"
                "is_success":   1,
                "branch_code":  1,
                "department":   1,
            }
        ).sort("occurred_at", -1).limit(limit)    # ← was "timestamp"

        events = await cursor.to_list(length=limit)

        # Build topic summary from questions
        topics = {}
        for event in events:
            q = event.get("question", "").lower()
            if any(w in q for w in ["suitability", "annuity", "variable"]):
                topics["suitability"] = topics.get("suitability", 0) + 1
            elif any(w in q for w in ["kyc", "know your customer", "identity"]):
                topics["kyc"] = topics.get("kyc", 0) + 1
            elif any(w in q for w in ["aml", "money laundering", "suspicious"]):
                topics["aml"] = topics.get("aml", 0) + 1
            elif any(w in q for w in ["record", "retain", "retention"]):
                topics["record_retention"] = topics.get("record_retention", 0) + 1
            elif any(w in q for w in ["best interest", "reg bi", "fiduciary"]):
                topics["best_interest"] = topics.get("best_interest", 0) + 1
            else:
                topics["general"] = topics.get("general", 0) + 1

        # Format for agent
        for event in events:
            if isinstance(event.get("occurred_at"), datetime):
                event["occurred_at"] = event["occurred_at"].isoformat()

        return {
            "user_id":        user_id,
            "period_days":    days,
            "total_queries":  len(events),
            "topic_summary":  topics,
            "recent_queries": [
                {
                    "question":   e.get("question", "")[:150],
                    "timestamp":  e.get("occurred_at", ""),
                    "success":    e.get("is_success", False),
                }
                for e in events[:10]
            ],
        }

    except Exception as e:
        log.error("[tool] query_advisor_activity failed: %s", e)
        return {
            "user_id":        user_id,
            "period_days":    days,
            "total_queries":  0,
            "topic_summary":  {},
            "recent_queries": [],
            "note":           f"Audit trail unavailable: {str(e)[:100]}",
        }
    finally:
        client.close()


# ── Tool 3 — Compliance Rule Check ───────────────────────────────────────────

# Rule definitions — in production these come from a database or config
COMPLIANCE_RULES = {
    "suitability": {
        "rule":        "FINRA Rule 2111",
        "requirement": "Advisor must document suitability analysis for all recommendations",
        "check":       lambda activity: activity.get("total_queries", 0) > 0,
        "gap_signal":  ["suitability", "risk", "investment objective"],
    },
    "record_retention": {
        "rule":        "FINRA Rule 4511",
        "requirement": "All client communications must be retained for 6+ years",
        "check":       lambda activity: True,   # always relevant
        "gap_signal":  ["retention", "records", "communication"],
    },
    "kyc": {
        "rule":        "FINRA Rule 2090",
        "requirement": "Know Your Customer — verify client identity and investment profile",
        "check":       lambda activity: True,
        "gap_signal":  ["kyc", "know your customer", "identity", "customer identification"],
    },
    "aml": {
        "rule":        "Bank Secrecy Act",
        "requirement": "AML program with SAR filing for suspicious activity over $5,000",
        "check":       lambda activity: True,
        "gap_signal":  ["aml", "anti-money laundering", "suspicious", "SAR"],
    },
    "best_interest": {
        "rule":        "Reg BI",
        "requirement": "Recommendations must be in client's best interest, not just suitable",
        "check":       lambda activity: True,
        "gap_signal":  ["best interest", "reg bi", "recommendation", "fiduciary"],
    },
    "senior_protection": {
        "rule":        "FINRA Rule 2165",
        "requirement": "Protect senior investors from financial exploitation",
        "check":       lambda activity: True,
        "gap_signal":  ["senior", "elderly", "trusted contact", "exploitation"],
    },
}


async def check_compliance_rules(
     activity_summary = None,
    focus_areas = None,
    **kwargs,
) -> dict:
    """Evaluate advisor activity against FINRA compliance rules.
    
    Args:
        activity_summary: the dict returned by query_advisor_activity tool
        focus_areas: optional list e.g. ["suitability", "kyc"] or omit entirely
    Do NOT pass rule_type, user_id, or any other parameters.
    """
    # Parse string to dict if agent passed JSON string instead of dict
    if isinstance(activity_summary, str):
        try:
            activity_summary = json.loads(activity_summary)
        except Exception:
            activity_summary = {}

    if not isinstance(activity_summary, dict):
        activity_summary = {}

    user_id  = activity_summary.get("user_id", "unknown")
    queries  = activity_summary.get("recent_queries", [])
    topics   = activity_summary.get("topic_summary", {})

    log.info("[tool] check_compliance_rules: user=%s", user_id)

    all_query_text = " ".join(
        q.get("question", "").lower() for q in queries
        if isinstance(q, dict)
    )

    findings  = []
    gaps      = []
    compliant = []

    rules_to_check = COMPLIANCE_RULES

    for rule_key, rule_def in rules_to_check.items():
        signals   = rule_def["gap_signal"]
        mentioned = any(s in all_query_text for s in signals)
        topic_hit = rule_key in topics

        if mentioned or topic_hit:
            compliant.append({
                "rule":    rule_def["rule"],
                "area":    rule_key,
                "status":  "reviewed",
                "finding": f"Advisor has queried this topic ({topics.get(rule_key, 0)} times)",
            })
        else:
            gaps.append({
                "rule":    rule_def["rule"],
                "area":    rule_key,
                "status":  "not_reviewed",
                "finding": f"No queries related to {rule_def['requirement']}",
                "action":  f"Recommend advisor review {rule_def['rule']} requirements",
            })

    low_faith_queries = [
        q for q in queries
        if isinstance(q, dict) and q.get("faithfulness", 1.0) < 0.6
    ]

    risk_level = (
        "HIGH"   if len(gaps) >= 3 or len(low_faith_queries) >= 2 else
        "MEDIUM" if len(gaps) >= 1 or len(low_faith_queries) >= 1 else
        "LOW"
    )

    return {
        "user_id":               user_id,
        "assessment_timestamp":  datetime.utcnow().isoformat(),
        "risk_level":            risk_level,
        "rules_checked":         len(rules_to_check),
        "compliant_areas":       len(compliant),
        "gap_count":             len(gaps),
        "gaps":                  gaps,
        "compliant":             compliant,
        "low_confidence_answers": len(low_faith_queries),
        "summary": (
            f"Advisor {user_id} shows {risk_level} compliance risk. "
            f"{len(compliant)} areas reviewed, {len(gaps)} gaps identified."
        ),
    }


# ── Tool 4 — Report Generator ─────────────────────────────────────────────────

async def generate_compliance_report(
    user_id: str = "unknown",
    activity = None,
    compliance_findings = None,
    report_type: str = "standard",
    **kwargs,
) -> dict:
    """Generate a structured compliance report for an advisor.
    
    Args:
        user_id: the advisor ID string e.g. "john.smith"
        activity: the dict returned by query_advisor_activity tool
        compliance_findings: the dict returned by check_compliance_rules tool
        report_type: "standard" or "detailed"
    Always call query_advisor_activity and check_compliance_rules first.
    """
    # Parse strings to dicts if agent passed JSON strings
    if isinstance(activity, str):
        try:
            activity = json.loads(activity)
        except Exception:
            activity = {}

    if isinstance(compliance_findings, str):
        try:
            compliance_findings = json.loads(compliance_findings)
        except Exception:
            compliance_findings = {}

    if not isinstance(activity, dict):
        activity = {}
    if not isinstance(compliance_findings, dict):
        compliance_findings = {}

    log.info("[tool] generate_compliance_report: user=%s type=%s", user_id, report_type)

    now        = datetime.utcnow()
    risk_level = compliance_findings.get("risk_level", "UNKNOWN")
    gaps       = compliance_findings.get("gaps", [])
    compliant  = compliance_findings.get("compliant", [])

    recommendations = []
    for i, gap in enumerate(gaps, 1):
        recommendations.append(
            f"{i}. Review {gap['rule']} - {gap['action']}"
        )

    if risk_level == "LOW":
        exec_summary = (
            f"Advisor {user_id} demonstrates satisfactory compliance awareness. "
            f"No immediate action required."
        )
    elif risk_level == "MEDIUM":
        exec_summary = (
            f"Advisor {user_id} shows moderate compliance gaps. "
            f"{len(gaps)} area(s) require attention within 30 days."
        )
    else:
        exec_summary = (
            f"Advisor {user_id} shows HIGH compliance risk. "
            f"Immediate review required. {len(gaps)} critical gaps identified."
        )

    return {
        "report_id":        f"RPT-{user_id}-{now.strftime('%Y%m%d-%H%M')}",
        "report_type":      report_type,
        "generated_at":     now.isoformat(),
        "generated_by":     "LPL Compliance Agent",
        "advisor_id":       user_id,
        "review_period":    f"Last {activity.get('period_days', 30)} days",
        "executive_summary": exec_summary,
        "risk_assessment": {
            "overall_risk":     risk_level,
            "queries_reviewed": activity.get("total_queries", 0),
            "rules_checked":    compliance_findings.get("rules_checked", 0),
            "compliant_areas":  len(compliant),
            "gaps_identified":  len(gaps),
        },
        "compliance_gaps": [
            {
                "regulation": gap["rule"],
                "area":       gap["area"],
                "finding":    gap["finding"],
                "action":     gap["action"],
                "priority":   "HIGH" if risk_level == "HIGH" else "MEDIUM",
                "due_date":   (now + timedelta(days=30)).strftime("%Y-%m-%d"),
            }
            for gap in gaps
        ],
        "compliant_areas": [
            {
                "regulation": c["rule"],
                "area":       c["area"],
                "finding":    c["finding"],
            }
            for c in compliant
        ],
        "recommendations": recommendations or ["No immediate action required."],
        "next_review_date": (now + timedelta(days=90)).strftime("%Y-%m-%d"),
        "attestation_required": risk_level in ("MEDIUM", "HIGH"),
    }


# ── Tool registry ─────────────────────────────────────────────────────────────

def get_tool_registry() -> dict:
    """
    Returns all agent tools as a name → callable dict.
    Import this in main.py to wire tools into the agent.
    """
    return {
        "search_compliance_docs":     search_compliance_docs,
        "query_advisor_activity":     query_advisor_activity,
        "check_compliance_rules":     check_compliance_rules,
        "generate_compliance_report": generate_compliance_report,
    }
