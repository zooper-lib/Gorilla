---
name: grill-me
description: Interview the user relentlessly about a plan or design until reaching shared understanding, resolving each branch of the decision tree. Use when user wants to stress-test a plan, get grilled on their design, or mentions "grill me".
---

Interview me relentlessly about every aspect of this plan until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.

Ask the questions one at a time.

If a question can be answered by exploring the codebase, explore the codebase instead.

## Asking

- Show the code, file paths, or a concrete failure before naming the thing you are asking about. Never reason from a shorthand you coined but never showed.
- Expand jargon once at first use.
- State the tension as what breaks, for whom, and when — not as a category of concern.
- If I say I do not understand, the question was defective. Re-ask it with the artifact shown.

## Recording

- Write each decision into the plan document as it is settled, with its rationale and what it rules out.
- When a decision invalidates a rule elsewhere, list every affected file and what must change in it.
- Keep delivery planning out of the document. A sequencing constraint belongs there only as a property of the system, never as a breakdown of the work.
