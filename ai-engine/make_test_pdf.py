from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import letter

c = canvas.Canvas("test-financial-policy.pdf", pagesize=letter)
c.setFont("Helvetica-Bold", 14)
c.drawString(50, 750, "LPL Financial Advisor Policy Manual 2024")

c.setFont("Helvetica", 11)
sections = [
    ("Section 4: Client Communication Standards", [
        "All client communications must be reviewed by compliance before distribution.",
        "Advisors must retain copies of all client correspondence for seven years",
        "per FINRA regulations. Digital communications including email and text",
        "messages are subject to the same retention requirements as written letters.",
        "Any communication that constitutes a recommendation must include appropriate",
        "risk disclosures and suitability documentation.",
    ]),
    ("Section 5: Trade Execution Policy", [
        "Trades must be executed at the best available price across all accessible",
        "markets. Any deviation from best execution must be documented with written",
        "client acknowledgment prior to trade placement. Pre-trade compliance checks",
        "are mandatory for all accounts exceeding five hundred thousand dollars.",
        "Block trading is permitted only with prior written approval from the",
        "compliance officer and must be allocated fairly across all participating accounts.",
    ]),
    ("Section 6: Supervision Requirements", [
        "Branch managers must review all new account documentation within 24 hours",
        "of account opening. Quarterly reviews of advisor activity are mandatory.",
        "Any suspicious activity must be reported to the compliance department",
        "within one business day. Failure to report suspicious activity may result",
        "in suspension of trading privileges and potential regulatory referral.",
        "All supervisory reviews must be documented and retained for examination.",
    ]),
    ("Section 7: Outside Business Activities", [
        "Advisors must obtain prior written approval before engaging in any outside",
        "business activity including board memberships and consulting arrangements.",
        "Approved outside business activities must be disclosed to clients when",
        "relevant to recommendations. Annual re-approval is required for all",
        "ongoing outside business activities. Compensation received from outside",
        "activities must be reported to the compliance department quarterly.",
    ]),
]

y = 710
for title, lines in sections:
    c.setFont("Helvetica-Bold", 11)
    c.drawString(50, y, title)
    y -= 18
    c.setFont("Helvetica", 10)
    for line in lines:
        c.drawString(60, y, line)
        y -= 15
    y -= 10

c.save()
print("PDF created: test-financial-policy.pdf")