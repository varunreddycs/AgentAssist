---
name: clarification
description: When and how to ask the user a clarifying question, including one question per turn, numbered choices, resolving references like 'the second one' or 'that pack', subpack versus pack compare, and how many packs a compare needs. Use before asking any question that is not a simple yes or no.
metadata:
  owner: agent-assist
  source: MAPS conversation agent clarification skill
---

# Clarification

## Ask only when you must
Ask when a required piece is missing (which item, which action) or a reference could mean more than one thing. If the conversation already answers it, do not ask.

## One question per turn
- Ask exactly one question.
- With 2 to 8 clear options, write the question and present the options as a numbered list. Keep options short (one to three words).
- With an open question (for example "Which pack number?"), just ask it; no list.

## Reading the answer
- Treat a reply of a number ("2"), an option's text ("inventory") or a close variant as that choice from the last list you showed.
- "The first one", "the second one", "the last one" refer to the last list of items you showed, in its order.
- "It", "that pack", "this one" refer to the item the conversation is about. If more than one item could be meant, ask.

## Packs: subpack or compare
- One pack and the user says "sub", "subpack", "pack of X": that is a Subpack request for that pack.
- Two packs and the request is unclear: ask "Do you want a subpack for one pack, or to compare the two packs?" with options Subpack and Pack compare.
- Pack compare needs exactly two packs. With one pack, ask for the second. With three or more, ask which two (comparing one pack to many is best done in MedPack).
- If any of the numbers is a component, say which ones are components and ask for two pack numbers.

## Missing item
If the user asks for an action ("check inventory") and no item is in the conversation, ask "Which pack number?" Do not offer a list of guesses.

## Tone
Short and specific. Example: "DYNJ40380J is a custom pack for ABC Hospital. What would you like to see?" followed by the numbered options.
