# Adding a Single Activity to a New Project — Table Sequence

Traced through one real activity in the dataset — **ARC-MAS-01, "Concrete blockwork 200mm solid, internal walls"** (BOQ item 3.01). You build **top-down along the data dependencies**: each sheet depends on a key created in the one before it.

## The order

### 1. Define the norm first — `2_ESTIMATE_NORMS`
The "recipe" is project-independent, so it exists before anything is priced. Create the **`Norm Code`** and its productivity + resource content:

```
ARC | Masonry | ARC-MAS-01 | Concrete blockwork 200mm solid | m²
  Output 14 m²/gang-shift | Gang: Mason ×3, Labourer ×3 (=6)
  Material: block 12.5 No./m² · mortar · Equipment: Site mixer | Self-perform
```

This is reusable — the same norm serves every project that has blockwork.

### 2. Add the priced BOQ line — `1_BOQ`
Now attach the activity to *this* project with its **quantity**, and point it back at the norm via **`Norm Ref`**:

```
Sec 3 | 3.01 | Concrete blockwork… | m² | Qty 5400 | Norm Ref = ARC-MAS-01
  Direct+Indirect Unit 637.9 → Amount 3,444,671 | +Margin 20% +Cont 7%
  → TOTAL Amount 4,374,732
```

### 3. Map it — `3_BOQ_MAPPING`
One row that ties the BOQ item to the norm **and** assigns the **`Estimate Package`** + operation code (the classification everything downstream rolls up to):

```
3 | 3.01 | … | ARC-MAS-01 | EP-ARC-MAS | OP-01 | Material+Manpower+Equipment | Self-perform
```

### 4. Explode into priced resources — `4_ESTIMATE_DATASHEET`
Multiply norm content × BOQ Qty, apply **`Unit Rate (AED)`** — this is where the money is actually built. Multiple rows per item:

```
3.01 | ARC-MAS-01 | EP-ARC-MAS | MATERIAL  block  → 390,536
3.01 |             …          | MATERIAL  mortar → 375
3.01 |             …          | MANPOWER         → 2,392,971
3.01 |             …          | EQUIPMENT mixer  → 327,857
                     Total Contract Amt = 4,374,732  ← must equal BOQ TOTAL
```

### 5. Open a cost centre & track it — `9_HISTORICAL_DATA`
Once execution starts, create the **`BCC_ID`**, tie it to the **`WBS_Code`** (= BOQ item) and a **`Zone_Area`**, and generate **one row per month**. Budget fields are seeded from the estimate; Plan/Actual/EVM fill in over time:

```
BCC-ARC-MAS-301 | WBS 3.01 | Zone FLOORS-B2-RF | EP-ARC-MAS
  BAC 3,444,671 | Budget_Qty 5400 | Direct_Unit 637.9   ← from the estimate
  Oct-25: NOT STARTED → Nov-25: EV 16,879 / AC 17,688 / CPI 0.95 → …
```

## How the keys thread through

```
        creates key ─────────────► reused downstream
 ┌──────────────────────────────────────────────────────────────┐
 │ Norm Code   ARC-MAS-01 ──► 1_BOQ(Norm Ref) ─► 3_MAP ─► 4_DS   │
 │ BOQ Sec/Item  3 / 3.01 ──► 3_MAP ─► 4_DS ─► 9_HIST(WBS_Code)  │
 │ Est. Package  EP-ARC-MAS ─► 4_DS(Package) ─► 9_HIST(Package_Code)│
 └──────────────────────────────────────────────────────────────┘
```

## Two reconciliation checks worth knowing
- **Sell price ties out:** `4_ESTIMATE_DATASHEET` Total Contract Amt **= 4,374,732 =** `1_BOQ` TOTAL Amount. The datasheet must roll back up to the BOQ.
- **The budget you *track* against is the cost, not the sell price:** in `9_HISTORICAL_DATA`, `BAC = 3,444,671` = the BOQ **Direct+Indirect Amount** (margin/contingency stripped out), and `Direct_Unit_Cost 637.9` matches the BOQ unit. You earn value and burn cost against the cost budget.

## The one-line rule
**Norm (recipe) → BOQ (priced quantity) → Mapping (package link) → Datasheet (resource rates) → BCC/Historical (monthly tracking).** You can't add an activity to a later sheet until its key exists in the earlier one — the norm and the BOQ item are the two anchors everything else references.

One nuance: a single BOQ line can fan into **several BCCs** if the work is split by zone/floor (here 3.01→BCC-…-301, 3.02→302, …), so step 5 is "one BCC per cost-tracked chunk × 12 months," not necessarily one row per BOQ line.
