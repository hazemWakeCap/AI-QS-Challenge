# Idea 4 — QS Copilot: How It Was Actually Built

> Companion to the product-facing [Feature 4 — QS Copilot](04-qs-copilot.md) and the original spec
> [`ideas/idea-4-qs-copilot.md`](../ideas/idea-4-qs-copilot.md). This doc is the **engineering
> reality**: what shipped, where the code lives, the enforcement boundaries, and how a QS (or a
> developer) actually uses it. It mirrors the deep dives for [Idea 1](12-idea-1-implementation.md),
> [Idea 2](13-idea-2-implementation.md), and [Idea 3](14-idea-3-implementation.md).

## From spec to shipped — the short version

The [idea spec](../ideas/idea-4-qs-copilot.md) argued one thing above all: the copilot is the
**interface and traceability layer** over the validated modules (Ideas 1/2/3/5), **not** the analytical
innovation. Its contribution is that every AED number arrives in plain English *with the rows behind
it*, and the model is **structurally incapable of inventing one** — "tools compute, the model narrates"
is a **code boundary, not a prompt**. It also demanded: the LLM as an orchestrator over typed/tested
tools, a proactive drift-watchlist opener (so it isn't a bare Q&A box), strict argument validation, the
validated-vs-directional forecast split, the `sum(EV)/sum(AC)` aggregation rule (never mean-of-rows),
and a fixed-question eval scored against **independently-computed ground truth**.

What shipped follows that framing faithfully, with three deliberate departures:

| Spec said | What shipped | Why |
|-----------|--------------|-----|
| Python tool functions + Streamlit/REPL chat, on the raw Anthropic tool-use loop | **C# tools in `QsEarlyWarning.Core.Agent`** driven through the **Microsoft Agent Framework** (`ChatClientAgent`) over an Anthropic `IChatClient`, exposed by an ASP.NET endpoint + React chat | One .NET runtime for the whole product; MAF gives the tool-use loop, tool-call middleware, and model-swappability for free. |
| Opus 4.8 orchestrating (Sonnet 5 for cheaper turns) | **Claude Sonnet 5 by default** (`Copilot:Model`, overridable) | Cheap, fast turns are the right default for a read-and-narrate agent whose arithmetic is entirely in tools; the model is only routing + prose. |
| 3–6 tools (`query_boq`, `evm_for_bcc`, `list_drifting`, `forecast_incremental_spend`, `directional_eac`, `resource_split`) | **10 tools** — the spec set *plus* the wrappers around Ideas 3 & 5 (`StressFlagsForPackage`, `ExplainVariance`) | The spec deferred wrapping the other ideas as a two-way door; it turned out cheap to land now, making the copilot the true front-end to the whole suite. |

Everything else — the deterministic-tool boundary, the proactive watchlist opener, strict arg
validation with typed errors, the `sum(EV)/sum(AC)` rule, the validated-vs-directional split, the
per-answer source trail, and the independent-ground-truth eval — is implemented as specified.

## The architecture — where the "tools compute, model narrates" boundary lives

```
POST /api/v1/copilot/ask
   │
   ├─ CopilotController          RLS resolve FIRST (401/403/404) → build QsAnalyticsTools from the tenant snapshot
   │                              (a non-member never reaches a tool call)
   ├─ ClaudeQsCostCopilotAgent   MAF ChatClientAgent over Claude (Sonnet 5); pre-flight scope guard;
   │      │                        tool-call middleware; graceful tool-error handling
   │      ├─ CopilotPrompts.System   the "tools compute, you narrate" system instructions
   │      ├─ 10 × QsAnalyticsTools    read-only, arg-validated, every result carries a `sources` block
   │      └─ ToolCallTracker         records (tool, args, sources) → sanitized evidence trail
   └─ CopilotAskResponse         { answer, refused, evidence[] }  → React chat + expandable "sources" panel
```

Three layers enforce the boundary, in order of strength:

1. **Code boundary (strongest).** Every AED figure is read from a pre-computed column or computed inside
   `QsAnalyticsTools`. The tools return numbers **pre-rounded**, so the model has nothing to sum, round,
   or invent. This is asserted by tests, not trusted to the prompt.
2. **Tenancy boundary.** The RLS-scoped project snapshot is resolved in `CopilotController` **before the
   LLM runs**; the per-request tools are built from *that* snapshot, so the copilot reads exactly the
   same data as the dashboard/forecast/stress-test and a non-member never reaches a tool.
3. **Prompt boundary (defence-in-depth).** `CopilotPrompts.System` instructs the model to ground every
   claim in a tool result, never do arithmetic, use `ProjectEvm` for aggregates, present
   `DirectionalEac` as unvalidated, and echo the resolved filter. A regex pre-flight (`OutOfScope`)
   refuses plainly off-topic asks before any model call.

## The tool surface — 10 read-only tools (`QsAnalyticsTools`)

Built **per request** from the caller's tenant snapshot. Each validates/clamps its args, returns a typed
`{ error }` object instead of throwing, and attaches a `sources` block.

| Tool | Idea | Returns |
|------|------|---------|
| `GetWatchlist(period, topK)` | 1 | ranked GREEN-about-to-tip centres with reason chips |
| `GetCostCentreDetail(bcc, period)` | 1 | EVM detail for one centre |
| `ExplainDrift(bcc, period)` | 1 | why a centre is drifting |
| `GetEvmSnapshot(bcc, period)` | 1 | per-period CV/CPI/SPI (no final-cost field) |
| `ForecastIncrementalSpend(bcc)` | 2 | **validated** h=1,2,3 P10/P50/P90 spend + trust badge — **no final cost** |
| `DirectionalEac(bcc, period)` | 2 | `EAC = BAC/CPI` + `VAC`, flagged **`validated:false`** |
| `ResourceSplit(bcc, period)` | — | resource-category AC shares (sum to 100%) |
| `ProjectEvm(period, discipline?, packageCode?)` | — | aggregated project/filtered CPI & SPI |
| `StressFlagsForPackage(package)` | 3 | Class-1 tie-out status + Class-2 flags for a package |
| `ExplainVariance(bcc, period)` | 5 | variance attribution (dominant resource + schedule lane) |

### The provenance contract (`CopilotSources`)

Every tool result carries a `sources` block — the sheet, resolved period, resolved filter,
excluded-row count, and the **source row IDs** (natural composite keys: `"{BccId}@P{PeriodId}"` for
panel rows, BOQ item refs for estimate rows). The agent's tool-call middleware reflects that block off
each result and records it into a **sanitized evidence trail** (`CopilotEvidence` — never raw framework
objects), which the UI renders as the expandable "sources" panel. This is what lets a wrong-period or
wrong-grain answer be **caught in the citation**.

## The two load-bearing correctness rules (enforced in code + independent ground truth)

### 1. Aggregated CPI/SPI is `sum(EV)/sum(AC)`, never the mean of per-row ratios

`ProjectEvm` is the only aggregation path. For each ratio it keys eligibility on the **denominator
only** (`AC > 0` for CPI, `PV > 0` for SPI), sums numerator and denominator separately, and divides:

```
CPI = Σ(EV) / Σ(AC)        SPI = Σ(EV) / Σ(PV)        # over the eligible rows in scope
```

Each ratio block reports `value, sumEv, sumDenominator, includedCount, excludedCount, rowIds`. A
zero-EV row with positive AC is **kept** in the CPI sum (eligibility is on the denominator), and an
empty scope returns `available:false` rather than dividing by zero. The eval test computes the
mean-of-rows form independently and asserts it **differs** from the aggregated form — proving the trap
is real and the tool avoids it.

### 2. Validated forecast vs directional EAC (never undo Idea 2's boundary)

`ForecastIncrementalSpend` returns the validated next-period band and **omits any final-cost field**
(the eval asserts no `eac`/`vac`/`finalCost` property exists on it). `DirectionalEac` returns
`EAC = BAC/CPI` and `VAC`, explicitly flagged **`validated:false`** with a note steering back to the
validated tool. `GetEvmSnapshot` likewise exposes **no** final-cost field. The system prompt reinforces:
if asked "what's the final cost", lead with the caveat.

## Validating the results — `dotnet test`

Two CI-safe suites (no model call) plus one opt-in live suite:

### `CopilotEvalTests` — the fixed-question ground-truth eval (the credibility artifact)

For each question the ground truth is computed **independently from the panel** and asserted against the
**deterministic tool output** (leakage guard: never scored against the model's own claim). 16 tests
covering:

| Assertion group | What it locks down |
|-----------------|--------------------|
| Numeric exact-match | `GetEvmSnapshot` CPI == independently-computed `EV/AC`; source row key present |
| **Aggregation trap** | `ProjectEvm` CPI == `Σ(EV)/Σ(AC)` **≠** mean-of-rows; SPI == `Σ(EV)/Σ(PV)` ≠ the AC aggregate; included count matches |
| Zero-EV handling | a zero-EV, positive-AC row is kept in the CPI sum, not dropped |
| Empty scope | an impossible filter → `available:false`, no divide-by-zero |
| Validated vs directional | `ForecastIncrementalSpend` has no final-cost field; `DirectionalEac` is `validated:false` and returns `EAC = BAC/CPI` |
| Adversarial args | invalid BCC / blank id / out-of-range period → typed error, not a guess |
| Cross-sheet | `ResourceSplit` shares sum to ~100%; `StressFlagsForPackage` cites item refs; `ExplainVariance` returns an attribution with the assumption flag |

### `CopilotToolScopeTests` — code-level enforcement

5 tests proving the tools are read-only, args are validated/clamped (out-of-range period → typed error;
bad `topK` clamped, not thrown), and unknown centres return typed errors.

> **Both suites pass (21 tests total) with no API key** — the numeric credibility is entirely offline
> and deterministic.

### `CopilotLiveEvalTests` — opt-in live-LLM routing eval

Gated on `ANTHROPIC_API_KEY`: absent (e.g. CI) it **soft-skips**, so it is never a regression gate. With
a key it runs the real Claude tool-use loop and checks the model **routes each question to the right
tool** (watchlist question → `GetWatchlist`; "project CPI" → `ProjectEvm`; "final cost" →
`DirectionalEac`) and produces an answer with a source trail — the "vs manual lookup / time-to-answer"
demo story. Numeric correctness stays in the deterministic suite.

## Graceful degradation

- **No API key** → `AddQsCostCopilot` registers `DisabledCopilotAgent`, which keeps the `/ask` endpoint
  alive with a clear "set `ANTHROPIC_API_KEY` and restart" message. The watchlist and validation views
  are unaffected.
- **A tool throws** → the middleware's `InvokeSafely` turns it into a structured `{ error }` the model
  can recover from — the run never 500s.
- **The LLM run fails** → the agent returns a friendly "temporarily unavailable" message (`refused`),
  never an exception to the client.
- **Off-topic ask** → the `OutOfScope` regex refuses before any model call.

## How users make use of it

### A. The Model & Copilot tab (primary — for the QS)

Open the app → **Model & Copilot** tab (`Copilot.tsx`). It **opens on the standing answer**: the
proactive **drift watchlist** for the current period (Feature 1), answered without being asked, with a
count of centres flagged. Below it is a chat box seeded with suggestions tied to the *actual* top centre
(e.g. *"Explain the drift risk for BCC-…"*, *"Next-period spend forecast for BCC-…?"*). Every answer
renders with an expandable **"sources"** panel: for each tool call, the resolved filter/period, the
excluded-row count, and the source row keys — so a wrong-period answer is caught in the citation.

**Workflow:** open to the standing watchlist each cycle, then ask ad-hoc follow-ups in plain English
and trust each number because its rows are one click away.

### B. The API (for integration / scripting)

```
POST /api/v1/copilot/ask
Body:    { "question": "...", "history": [{ "role": "user|assistant", "text": "..." }] }
Headers: X-User-Id, X-Project-Slug
Returns: { answer, refused, evidence: [{ tool, detail, sources }] }
```

The RLS snapshot is resolved **before** the LLM runs. Errors: `400` blank/too-long question or history
> 20 turns · `401` no identity · `403` not a member · `404` no data. History is capped at the last 10
turns inside the agent.

### C. Model swappability

The `IQsCostCopilotAgent` contract is owned by **Core** (which carries zero Microsoft-Agent-Framework
dependency) and takes plain `CopilotTurn` DTOs — so the Claude/MAF implementation is swappable without
touching the tools, the controller, or the tests.

## Honest limits

- **Interface layer, not the innovation.** The copilot does not itself detect cost trouble earlier — it
  reads and narrates the validated modules (Ideas 1/2/3/5) with a trail.
- **Design guarantee, not a hard proof.** "Tools compute, model narrates" is enforced by tool design +
  returned evidence; it's a strong boundary, not a formal proof that every narrated digit was
  individually cited.
- **Inherits each feature's boundaries.** Forecast is validated-vs-directional; variance is an
  attribution/hypothesis; stress flags are review prompts — the copilot must (and does) surface those
  caveats.
- **Single project, per-request tenancy.** Every answer is scoped to the caller's authorized project
  snapshot.

## Where to look in the code

| Concern | File |
|---------|------|
| The 10 read-only tools (arg validation, `sources`, aggregation rules) | `Core/Agent/QsAnalyticsTools.cs` |
| Core-owned agent contract + DTOs (`CopilotSources`, `CopilotEvidence`) | `Core/Agent/IQsCostCopilotAgent.cs` |
| The MAF agent (tool-use loop, middleware, scope guard, error handling) | `Agent/ClaudeQsCostCopilotAgent.cs` |
| System prompt (tools-compute-model-narrates rules) | `Agent/Prompts/CopilotPrompts.cs` |
| DI wiring (key present → Claude; absent → disabled) | `Agent/AgentServiceCollectionExtensions.cs`, `Agent/DisabledCopilotAgent.cs` |
| HTTP endpoint (RLS before LLM) | `Web.API/Controllers/CopilotController.cs` |
| UI (proactive opener + chat + sources panel) | `frontend/qs-early-warning/src/components/Copilot.tsx` |
| Ground-truth eval + tool-scope tests | `tests/QsEarlyWarning.Tests/CopilotEvalTests.cs`, `.../CopilotToolScopeTests.cs` |
| Opt-in live-LLM routing eval | `tests/QsEarlyWarning.Tests/CopilotLiveEvalTests.cs` |
</content>
</invoke>
