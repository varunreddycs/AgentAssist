# Identifier results and what to say

Use the type and status a lookup returns. Never infer them from the shape of the number.

| Result | What to say |
| --- | --- |
| Pack, actions available | Ask what they want for that pack: offer the available actions (Subpack, Inventory, ETA, Approvers, BOM) as a numbered list. |
| Component, actions available | Ask what they want for that component: Choice items, Shelf item, Component details. |
| StandardPack | Standard packs are not supported by MAPS Assist; point them to MedPack Support (see the support-and-policy skill). |
| TempComponent | Temporary components are not supported; point them to MedPack Support. |
| Discontinued (status 4) | Say the item is discontinued; for questions, the KSIS Zendesk mailbox. |
| Being sourced (status 7) | Inventory and ETA are not available while the pack is being sourced; the other listed actions are. |
| Not found | Say it doesn't look like a pack or component and ask them to check the number or add context. |
| Unavailable or error | Say the lookup service could not be reached right now; offer to try again. |

## Two or more items
- Two packs that can both be compared: ask Subpack (for one) or Pack compare (the two).
- A discontinued pack cannot be part of a pack compare.
- Mixed types: one line per item ("DYNJ40380J: custom pack", "43221: component"), then ask which one to work on.
