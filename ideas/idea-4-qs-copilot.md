# Idea 4 — QS Copilot: Conversational Agent Over the Workbook

**One-line pitch.** A Claude-powered agent that opens on a standing drift watchlist and lets a QS
*ask* anything after that ("which packages are drifting and why?", "what's my forecast final cost?",
"explain BCC-STR-12's overrun") — it does the joins and reads the EVM figures through tested tools and
shows the exact rows behind every number.

**The QS pain it kills.** A QS lives in ad-hoc questions, not fixed dashboards. Every new question
today means another spreadsheet pivot, and the answer arrives with no trail back to source. A dashboard
answers the questions its builder anticipated; a copilot answers the ones the QS actually has, in their
words, with the number traced to the sheet and rows it came from. The trap to avoid: a bare Q&A box is a
clever demo, not a tool a QS reaches for under deadline. So the copilot leads with a proactive answer
(the drift watchlist) the moment it opens, then takes free-form follow-ups.

**Approach.** The LLM is an **orchestrator over deterministic tools**, never the calculator.
- Build a small set of typed, tested tools over the workbook, e.g. `query_boq(filter)`,
  `evm_for_bcc(bcc_id, period)`, `list_drifting(threshold)`, `forecast_eac(bcc_id)`,
  `resource_split(bcc_id, period)`. Each returns structured data plus the source row IDs.
- Every AED figure is either **read straight from the pre-computed columns** in `9_HISTORICAL_DATA`
  (CPI, SPI, EAC, VAC, Alert_Level, Rolling_3M_CPI, EAC_vs_BAC_Ratio) or computed in tested tool code.
  The model never does arithmetic. Tools return numbers pre-formatted so the model has nothing to
  round, sum, or invent.
- Claude (Opus 4.8 as orchestrator, or Sonnet 5 for cheaper turns) runs the Anthropic tool-use loop
  with adaptive thinking: it interprets the question, calls tools, and composes a natural-language
  answer **with a source trail** (which sheet and rows fed the number).
- The tool arguments are validated against the real key set (BCC_IDs, periods, package codes). An
  unknown BCC ID returns a tool error the model surfaces as a clarification, not a guessed answer.

**Data used.** All sheets, but *only through tools*: `1_BOQ`, `2_ESTIMATE_NORMS`, `3_BOQ_MAPPING`,
`4_ESTIMATE_DATASHEET`, `9_HISTORICAL_DATA`. Tools own the joins (`Norm Code`, `BOQ Sec`+`Item`,
`BCC_ID`+`Period_ID`, `Package_Code`), the row-5 header quirk, and the NOT STARTED / zero-earned-value
handling, so the LLM never touches a raw cell.

**How you'd judge it's good.** A fixed **question set** (15-20 questions) with ground-truth answers
computed independently from the sheets (e.g. "top 5 packages by VAC", "project CPI in Sep-2026",
"EV of BCC-X"). Score two things: exact-match on the numbers, and whether the cited rows are the
correct ones. This turns a "clever demo" into a measured artifact: answer accuracy plus citation
correctness. Baseline to beat: a no-tools LLM that reads the sheet text directly (it will hallucinate
figures and mis-cite).

**What the QS sees.** On open, a ranked drift watchlist (this period's AMBER and drifting cost centres)
answered without being asked. Then a chat box that returns a direct number, a one-paragraph
explanation, the resolved filter it used (period, package, threshold), and an expandable "sources"
panel — not a dashboard to learn. Feels like a sharp junior QS who never gets tired and always shows
their working.

**Build effort for a hackathon.** Medium, and front-loadable: 3-4 solid tools plus the tool-use loop
plus the watchlist opener plus the eval set is a strong, honest demo. Directly uses "Claude included"
from the brief and is the **highest-wow** thing to show live.

**Risks / gotchas.** The failure mode is the LLM doing math itself — enforce "tools compute, model
narrates" and have tools return numbers pre-formatted with their source rows. NOT STARTED and
zero-earned-value rows must be excluded or annotated by the tools, or they skew CPI averages. Ambiguous
questions need graceful clarification, and the answer must echo the resolved filter so a wrong-period
answer is caught in the citation. Guard against invented BCC IDs by validating tool arguments against
the real key set.

## Recommended deliverable

**A Claude agent app** — the one idea whose right form *is* an LLM.
- **Form:** a small application on the **Anthropic API tool-use loop** (Opus 4.8 orchestrating; Sonnet
  5 for cheaper turns), with the typed, tested tools (`query_boq`, `evm_for_bcc`, `list_drifting`,
  `forecast_eac`, `resource_split`) as plain Python functions over the workbook. Wrap it in a thin
  chat UI (Streamlit chat, or a terminal REPL for the hackathon) that opens on the proactive drift
  watchlist, then takes free-form questions and renders the answer + its "sources" panel.
- **Packaging options, in order:** (1) standalone chat app — fastest to demo, fullest control of the
  watchlist opener and citation UI; (2) an **MCP server** exposing the same tools, so it drops into
  Claude Code / Claude Desktop and any MCP client; (3) a **Claude Code skill** wrapping the tools.
  Build the tools once as a clean Python module, then do (1) for the demo; (2)/(3) are thin re-wraps
  of the same functions later.
- **Why this form:** the whole value is natural-language questions answered with traced numbers, which
  only an LLM-plus-tools delivers. The hard rule (tools compute, model narrates) is a code boundary,
  not a prompt, so it survives whichever packaging you pick.
- **Demo artifact:** the chat app answering the fixed 15–20 question eval live, each answer showing its
  source rows, scored on numeric accuracy + citation correctness vs the no-tools baseline.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** Keep, with a reframe. A pure Q&A box is a clever demo, not a tool a QS reaches for
under deadline — a QS often wants a standing alert, not a question prompt. Reframe the copilot as a
**traced-answers engine that opens with a proactive drift watchlist** and then takes ad-hoc questions.
The durable value is not "chat over a spreadsheet"; it is "every AED number comes with the rows behind
it, and the model is structurally incapable of inventing one." That trust property is what a cost
report to a client needs.

**What already exists (don't rebuild blindly).** `9_HISTORICAL_DATA` already carries CPI, SPI, EAC, VAC,
Alert_Level, Rolling_3M_CPI, and EAC_vs_BAC_Ratio, pre-computed per cost centre per period. The copilot
should mostly *read and cite* these, not recompute them. That is a feature, not a shortcut: it is
exactly what makes the "tools compute, model narrates" rule cheap to enforce and hard to get wrong. The
only real math the tools do is filtering, joining, and ranking over numbers that already exist.

**Dream-state delta.** CURRENT: QS pivots a fresh spreadsheet per question, spots trouble weeks late,
answers have no trail. --> THIS IDEA: asks in plain English, gets a traced number and its source rows in
seconds, and sees the drift watchlist on open. --> 12-MONTH IDEAL: the copilot pushes the morning
watchlist unprompted and answers any ad-hoc question with citations, wrapping the classifier, forecaster,
and root-cause decomposer as tools.

**Approaches considered & pick.**
- A) Minimal viable — 4 typed read-only tools + the Anthropic tool-use loop (Opus 4.8) + fixed eval set,
  chat only. Effort M, low risk. Reuses the pre-computed EVM columns.
- B) Ideal — A plus wraps Ideas 1/2/5 as additional tools (copilot as the suite front-end) plus scheduled
  pushed alerts. Effort L, higher integration risk in the timebox.
- **Chosen: A, plus one borrow from B — ship the proactive watchlist opener now.** Because it kills the
  "clever demo" critique by leading with a standing answer, stays buildable and demoable in the timebox,
  and the tool interface is forward-compatible with wrapping 1/2/5 later. Reversible two-way door: yes —
  wrapping the other ideas is additive and can land after the hackathon.

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Chat-only, or a proactive opener? | Add proactive watchlist opener | A QS wants the standing alert, not just a Q&A box; it also anchors the demo. |
| D2 | May the LLM do any EVM arithmetic? | Cut | Hallucinated AED figures destroy trust; tools read pre-computed columns or compute in tested code and return pre-formatted numbers + source rows. |
| D3 | Wrap Ideas 1/2/5 as tools now? | Defer | Two-way door; the core demo stands alone and the tool interface stays compatible for later. |
| D4 | How is "good" measured? | Add fixed question set (15-20) with independent ground truth + citation check | Turns a clever demo into a measured artifact; this is the honest metric. |
| D5 | Argument validation (BCC IDs, periods, thresholds)? | Add strict validation against the real key set | Guards against invented BCC IDs and silent wrong joins; unknown IDs become a clarification, not a guess. |

**Top failure modes.**
1) The model computes a figure itself and states a wrong AED number. The QS notices when the sources
   panel rows don't reconcile to the stated figure — prevented by never letting the model do math and
   returning the number *with* its source rows.
2) A silent wrong join, or NOT STARTED / zero-earned-value rows folded into an average, skews CPI. The
   QS notices an implausible project CPI — prevented by tools excluding or annotating those rows and
   reporting the row count they used.
3) An ambiguous question is answered confidently against the wrong period or package. The QS notices the
   wrong month in the citation — prevented by echoing the resolved filter (period, package, threshold) in
   the answer and asking to confirm when the question is ambiguous. (Watch too: GREEN/AMBER-only labels
   with no RED, CPI outliers, and the row-5 header quirk.)

**Honest success metric.** On the fixed question set: numeric exact-match accuracy AND citation
correctness (right sheet and right rows), reported together. Baseline it must beat: a no-tools LLM that
reads the sheet text directly (it hallucinates figures and mis-cites). Leakage trap: the ground-truth
answers and the cited row IDs must be computed independently from the sheets and checked against actual
rows — never scored against the model's own claim, or the metric grades itself.

**Deferred to a real build (written down, not chosen).** Wrapping Ideas 1/2/5 as tools so the copilot is
the front-end to the whole suite; scheduled/pushed alerts (the watchlist arrives before the QS asks);
scaling across multiple towers.

**Verdict.** BUILD-WITH-CHANGES — lead with the proactive watchlist opener, ship the fixed-question eval,
and keep the LLM strictly out of the arithmetic.
