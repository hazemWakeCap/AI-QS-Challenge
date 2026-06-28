# The Challenge — AI Quantity Surveyor

## The real-world problem

On a construction project, a Quantity Surveyor (QS) is the person who answers two questions, over and over, for months or years:

1. **What will this end up costing?** Prices move, work slips, suppliers change. The QS has to keep predicting the *final* cost of the project, not just report what's been spent.
2. **Are we drifting off budget — and where?** Every cost centre has a planned budget. The QS constantly compares what was *planned*, what's been *earned* (the value of work actually done), and what's been *spent*.

The pain point: a human QS, working in spreadsheets, often spots a problem **weeks too late** — after the invoices are paid and the money is gone. A concrete pour budgeted at 50,000 that quietly became 65,000 should have been flagged the moment it started drifting, not at month-end.

## What you have

A complete, real-shaped dataset for one building project — the bill of quantities, the estimating norms, how they map together, the resource-level cost breakdown, and a large set of historical month-by-month records from past work. See `DATA_DICTIONARY.md` for what every sheet and column means.

## What we're asking

**Build something that helps a QS see cost trouble early.**

That's deliberately open. We're not handing you a target metric, a required output format, or a definition of "correct." Part of the work — the interesting part — is deciding:

- What's actually worth predicting or detecting here?
- How would you even know your answer is any good? Does this need a formal way to be judged, or not?
- What would a real QS find genuinely useful versus just clever?

You can take this toward forecasting, toward early-warning detection, toward something we haven't thought of. Use whatever tools you like — Claude included — to explore the data, form a point of view, and build.

## How to start

Open the data. Understand it (the dictionary is there to help). Then decide what problem inside this problem is worth solving — and go.
