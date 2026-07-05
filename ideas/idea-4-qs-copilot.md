# Idea 4 — QS Copilot: Conversational Agent Over the Workbook

**One-line pitch.** A Claude-powered agent that opens on a standing drift watchlist and lets a QS
*ask* anything after that ("which packages are drifting and why?", "what's my next-period spend
forecast?", "explain BCC-STR-12's overrun") — it does the joins and reads the EVM figures through tested tools and
shows the exact rows behind every number.

**The QS pain it kills.** A QS lives in ad-hoc questions, not fixed dashboards. Every new question
today means another spreadsheet pivot, and the answer arrives with no trail back to source. A dashboard
answers the questions its builder anticipated; a copilot answers the ones the QS actually has, in their
words, with the number traced to the sheet and rows it came from. The trap to avoid: a bare Q&A box is a
clever demo, not a tool a QS reaches for under deadline. So the copilot leads with a proactive answer
(the drift watchlist) the moment it opens, then takes free-form follow-ups.

The copilot is the **interface and traceability layer** over validated detection/forecast/decomposition
modules (the classifier, forecaster, and variance attribution bridge of Ideas 1/2/5) — not the analytical
innovation itself. Its contribution is that every AED number arrives in plain English *with the rows
behind it*.

**Approach.** The LLM is an **orchestrator over deterministic tools**, never the calculator.
- Build a small set of typed, tested tools over the workbook, e.g. `query_boq(filter)`,
  `evm_for_bcc(bcc_id, period)`, `list_drifting(threshold)`, `forecast_incremental_spend(bcc_id, horizon)`,
  `directional_eac(bcc_id)`, `resource_split(bcc_id, period)`. Each returns structured data plus the source row IDs.
- Every AED figure is either **read straight from the pre-computed columns** in `9_HISTORICAL_DATA`
  (CPI, SPI, CV, VAC, Alert_Level, Rolling_3M_CPI) or computed in tested tool code. The model never does
  arithmetic. Tools return numbers pre-formatted so the model has nothing to round, sum, or invent.
- **`EAC_AED` is directional, not validated — the tool contract must say so.** `EAC_AED` is exactly
  `BAC/CPI` (the workbook formula), so `directional_eac` returns it flagged as an unvalidated
  extrapolation, never as a settled forecast. The forecast a QS should trust is
  `forecast_incremental_spend` (with its horizon and P10–P90 band), mirroring the validated-vs-directional
  boundary established in Idea 2. The copilot must not present a final-cost number as validated.
- Claude (Opus 4.8 as orchestrator, or Sonnet 5 for cheaper turns) runs the Anthropic tool-use loop
  with adaptive thinking: it interprets the question, calls tools, and composes a natural-language
  answer **with a source trail** (which sheet and rows fed the number).
- The tool arguments are validated against the real key set (BCC_IDs, periods, package codes). An
  unknown BCC ID returns a tool error the model surfaces as a clarification, not a guessed answer.
- **Correctness rule (must hold in tool code and in the independent ground truth):** any project- or
  portfolio-level CPI is `sum(EV) / sum(AC)` over the rows in scope — **never the mean of per-row CPI**.
  Same for SPI and any aggregated ratio. Per-row CPI is only ever reported per row.

**Data used.** All sheets, but *only through tools*: `1_BOQ`, `2_ESTIMATE_NORMS`, `3_BOQ_MAPPING`,
`4_ESTIMATE_DATASHEET`, `9_HISTORICAL_DATA`. Tools own the joins (`Norm Code`, `BOQ Sec`+`Item`,
`BCC_ID`+`Period_ID`, `Package_Code`), the row-5 header quirk, and the NOT STARTED / zero-earned-value
handling, so the LLM never touches a raw cell.

**How you'd judge it's good.** A fixed **question set** (15-20 questions) with ground-truth answers
computed independently from the sheets (e.g. "top 5 packages by VAC", "project CPI in Sep-2026",
"EV of BCC-X"). **Primary comparison: manual spreadsheet lookup** — a QS answering the same questions
by filtering, pivoting, or reading a fixed dashboard. Score four things against that baseline: (1)
numeric exact-match; (2) citation correctness (right sheet, right rows); (3) **filter/aggregation
correctness** — right period, package, threshold, grain, and the aggregated (not mean-of-rows) form for
CPI/SPI — plus the **excluded-row count** (NOT STARTED / zero-EV rows the tool dropped); and (4)
**median time-to-answer**, where the copilot's edge is speed with a trail, not just accuracy. Keep the
**no-tools LLM only as a safety demonstration** (it hallucinates figures and mis-cites). Include
**adversarial cases** in the set: an ambiguous period, an invalid BCC ID, NOT STARTED rows, a zero-AC/EV
row, a weighted-vs-unweighted CPI question (the `sum(EV)/sum(AC)` trap), and a question needing a
cross-sheet join. Leakage guard: ground truth and cited row IDs are computed independently and checked
against actual rows, never scored against the model's own claim.

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
zero-earned-value rows must be excluded or annotated by the tools, or they skew CPI averages. A known
silent-error trap: computing project/portfolio CPI as the **mean of per-row CPI** instead of
`sum(EV)/sum(AC)` — it looks plausible, cites real rows, and is wrong; the tool and its independent
ground truth must both use the aggregated form. Ambiguous
questions need graceful clarification, and the answer must echo the resolved filter so a wrong-period
answer is caught in the citation. Guard against invented BCC IDs by validating tool arguments against
the real key set.

## Codex Review — Findings and Recommendations (2026-07-05)

> **Checked 2026-07-05 (Claude): sound, no correction needed.** Consistent with the CEO review. The key
> add is the baseline: benchmark against **manual spreadsheet lookup** (numeric accuracy + citation/filter
> correctness + median time-to-answer), keeping the no-tools LLM only as a safety demo. Expose the
> resolved period / grain / exclusions / source rows on every answer, and build the adversarial eval
> cases listed (ambiguous period, invalid ID, NOT STARTED / zero-EV rows, weighted-vs-unweighted CPI,
> cross-sheet joins).

> **Codex follow-up (2026-07-05) — partially handled.** The proactive opener and resolved filter are
> already present, but the operative evaluation and demo sections still benchmark only against a
> no-tools LLM and score only numeric/citation accuracy. Add the manual-spreadsheet baseline,
> time-to-answer, filter/aggregation correctness, and adversarial cases to those sections. Also define
> project CPI aggregation explicitly (`sum(EV) / sum(AC)`, not mean row CPI) because it is a likely
> silent-error case in both the tool and its independent ground truth.

> **Resolved 2026-07-05 (Claude):** propagated through the operative spec — Approach now positions the
> copilot as the interface/traceability layer over validated detection/forecast/decomposition modules
> and states the `sum(EV)/sum(AC)` (never mean-of-rows) CPI rule; "How you'd judge it's good" makes
> manual spreadsheet lookup the primary baseline (numeric + citation + filter/aggregation correctness +
> excluded-row count + median time-to-answer), demotes the no-tools LLM to a safety demo, and adds the
> six adversarial cases; Risks/gotchas flags the mean-of-per-row-CPI silent-error trap; Recommended
> deliverable's demo now scores the new metric; and the CEO Review success-metric line is reconciled.

> **Codex final check (2026-07-05): resolved.** The evaluation and trace contract are now adequate.
> When exposing Idea 2, distinguish validated incremental-spend forecasts from the directional
> final-cost extrapolation; a generic “forecast final cost” question must not silently present the
> latter as validated.

> **Codex re-review (2026-07-05): the response contract is still not operative.** The pitch still uses
> “what's my forecast final cost?”, the tool list still exposes `forecast_eac`, and the tool-reading
> paragraph treats precomputed EAC like other reportable figures. Define two distinct outputs:
> `forecast_incremental_spend` (validated, with horizon and band) and `directional_eac` (explicitly
> unvalidated, with `BAC/CPI` identified as the workbook formula). Also replace “root-cause decomposer”
> with “variance attribution bridge” when naming Idea 5. This prevents the copilot from undoing the
> evidence boundaries established in Ideas 2 and 5.

> **Resolved 2026-07-05 (Claude):** made the response contract operative. Split `forecast_eac` into
> `forecast_incremental_spend` (validated, with horizon + band) and `directional_eac` (`BAC/CPI`, flagged
> unvalidated); the pitch now asks for next-period spend; EAC is presented as directional, not settled;
> and Idea 5 is named the variance attribution bridge everywhere (pitch, tool list, deliverable, dream-state).

### Findings

- The deterministic-tool boundary is correct, but the copilot is an interface and traceability layer;
  it does not itself detect cost trouble earlier.
- A no-tools LLM is a weak product baseline. The real alternative is a QS answering the same questions
  through spreadsheet filtering, pivots, or a fixed dashboard.
- Citing source rows is necessary but insufficient. Answers can still be wrong because of an incorrect
  period, aggregation grain, exclusion rule, or project-level weighting choice.
- A chat-first experience risks becoming a hackathon demo rather than a recurring QS workflow. The
  proactive watchlist is therefore load-bearing, not optional polish.

### Recommendations for the implementation agent

1. Position the copilot as the interface over validated detection/forecast/decomposition modules, not
   as the analytical innovation itself.
2. For every answer, expose the resolved period, BCC/package filters, aggregation method, excluded-row
   count, source row IDs, and tool/function used.
3. Benchmark against manual spreadsheet lookup on numeric accuracy, citation/filter correctness, and
   median time-to-answer. Keep the no-tools LLM comparison only as a safety demonstration.
4. Open on the actionable watchlist; use chat for drill-down and ad-hoc questions.
5. Build adversarial eval cases: ambiguous period, invalid ID, NOT STARTED rows, zero AC/EV, weighted
   versus unweighted CPI, and requests requiring joins across sheets.

## Recommended deliverable

**A Claude agent app** — the one idea whose right form *is* an LLM.
- **Form:** a small application on the **Anthropic API tool-use loop** (Opus 4.8 orchestrating; Sonnet
  5 for cheaper turns), with the typed, tested tools (`query_boq`, `evm_for_bcc`, `list_drifting`,
  `forecast_incremental_spend`, `directional_eac`, `resource_split`) as plain Python functions over the
  workbook. Wrap it in a thin
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
- **Demo artifact:** the chat app answering the fixed 15–20 question eval live (including the six
  adversarial cases), each answer showing its resolved filter, excluded-row count, and source rows —
  scored **vs manual spreadsheet lookup** on numeric accuracy, citation/filter/aggregation correctness,
  and median time-to-answer, with the no-tools LLM shown only as a safety demonstration.

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
and variance attribution bridge as tools.

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
2) A silent wrong join, NOT STARTED / zero-earned-value rows folded into an average, or a project CPI
   taken as the mean of per-row CPI instead of `sum(EV)/sum(AC)`, skews the figure. The QS notices an
   implausible project CPI — prevented by tools excluding or annotating those rows, using the aggregated
   form, and reporting the row count they used.
3) An ambiguous question is answered confidently against the wrong period or package. The QS notices the
   wrong month in the citation — prevented by echoing the resolved filter (period, package, threshold) in
   the answer and asking to confirm when the question is ambiguous. (Watch too: GREEN/AMBER-only labels
   with no RED, CPI outliers, and the row-5 header quirk.)

**Honest success metric.** On the fixed question set, measured **against manual spreadsheet lookup** as
the primary baseline: numeric exact-match, citation correctness (right sheet and rows), filter/aggregation
correctness (right period/package/threshold/grain, aggregated `sum(EV)/sum(AC)` CPI not mean-of-rows, and
excluded-row count), and median time-to-answer — reported together. The no-tools LLM stays only as a
safety demonstration. The set includes the adversarial cases (ambiguous period, invalid ID, NOT STARTED,
zero AC/EV, weighted-vs-unweighted CPI, cross-sheet join). Leakage trap: the ground-truth answers and the
cited row IDs must be computed independently from the sheets and checked against actual rows — never
scored against the model's own claim, or the metric grades itself.

**Deferred to a real build (written down, not chosen).** Wrapping Ideas 1/2/5 as tools so the copilot is
the front-end to the whole suite; scheduled/pushed alerts (the watchlist arrives before the QS asks);
scaling across multiple towers.

**Verdict.** BUILD-WITH-CHANGES — lead with the proactive watchlist opener, ship the fixed-question eval,
and keep the LLM strictly out of the arithmetic.
