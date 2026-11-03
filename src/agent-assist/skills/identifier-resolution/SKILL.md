---
name: identifier-resolution
description: How to recognise and handle MedPack and MedQuote identifiers (pack, component, standard pack, temporary component, quote and account numbers), bare-number messages, multiple numbers, hyphenated pack numbers, and items that are not found, discontinued or being sourced. Use the first time identifiers appear in a conversation.
metadata:
  owner: agent-assist
  source: MAPS conversation agent identifier-resolution skill
---

# Identifier resolution

## What counts as an identifier
- A token of letters and digits with at least two digits, optionally joined by hyphens: `DYNJ40380J`, `43221`, `DYNJ86645A-QPC`.
- A single digit is a quantity ("2 packs"), not an identifier.
- A hyphenated pack number usually has a base form before the first hyphen (`DYNJ86645A-QPC` -> `DYNJ86645A`). Keep the number as typed; mention the base form only if it helps the user.

## Never guess
- Never guess or "fix" a number. If a number looks mistyped, ask.
- Never infer the type (pack, component, quote, account) from the shape of the number alone, and never ask the user what type it is if a tool can tell you. Call `maps___classifyItems` with the identifiers exactly as typed (comma-separated in `ids`) and use the type, status and available actions it returns.

## Bare identifiers (the message is only numbers)
1. One number: ask what they want to do with it, as a numbered list of the actions for that item (Subpack, Inventory, ETA, Approvers, BOM for a pack; Choice items, Shelf item, Component details for a component).
2. Two numbers that are both packs: ask whether they want a subpack for one of them or a pack compare of the two.
3. Several numbers of mixed types: summarise what each one is in a short list, then ask which item they want to work on.

## Numbers with a request ("inventory for DYNJ40380J")
Check that the requested action applies to that item. If it does, the request is complete. If it does not (for example inventory for a component), say so in one sentence and offer what is available.

## Results and what to say
Read `references/identifier-rules.md` for the result-to-reply table (pack, component, standard pack, temporary component, discontinued, being sourced, not found, lookup unavailable).

## Several or hyphenated numbers
Run `scripts/normalize_ids.py <text>` to extract and normalise identifiers from a message when there are several or they are hyphenated. Pass the user's message as the argument; it returns each identifier with its base form, in the order typed.
