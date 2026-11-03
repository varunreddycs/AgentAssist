---
name: maps-data
description: Which maps tool answers which MAPS request (classify, subpack, inventory, ETA, approvers, BOM, choice items, shelf item, component details, pack compare), what to pass, and how to report tool errors. Use whenever the user asks for pack or component data or before calling any maps tool.
metadata:
  owner: agent-assist
---

# MAPS data

## Which tool
| Request | Tool | Argument |
| --- | --- | --- |
| What an item is (type, status, available actions) | `maps___classifyItems` | `ids`, the identifiers comma-separated, exactly as typed |
| Subpack | `maps___getSubpack` | `itemId`, the pack number |
| Inventory | `maps___getInventory` | `itemId`, the pack number |
| ETA | `maps___getEta` | `itemId`, the pack number |
| Approvers | `maps___getApprovers` | `itemId`, the pack number |
| BOM (bill of materials) | `maps___getBom` | `itemId`, the pack number |
| Choice items | `maps___getChoiceItems` | `itemId`, the component number |
| Shelf item | `maps___getShelfItem` | `itemId`, the component number |
| Component details | `maps___getComponentDetails` | `itemId`, the component number |
| Pack compare (the two BOMs side by side) | `maps___comparePacks` | `first` and `second`, the two pack numbers |

## Rules
1. Classify first. Call `maps___classifyItems` before any other maps tool, unless the conversation already classified that item. Use the type, status and available actions it returns; never infer them from the shape of the number.
2. Pass identifiers exactly as typed, hyphenated suffixes and letter case included. Never fix, shorten or guess a number.
3. Pack actions (Subpack, Inventory, ETA, Approvers, BOM) are for packs; component actions (Choice items, Shelf item, Component details) are for components. If the requested action does not apply to the item, say so in one sentence and offer its available actions instead of calling the tool.
4. Pack compare needs exactly two packs. With one, ask for the second; with three or more, ask which two.
5. Inventory and ETA are not available for a pack being sourced. Do not call those tools for it; say so and offer the other actions.
6. If a tool returns an error or says the service is unavailable, say so in one sentence and offer to try again.
7. Never invent or complete data. Report only what the tool returned; if a field is missing, say it is not available.
8. Present results per the response-style skill: a short sentence or a small table, no raw JSON, no tool or field names.
