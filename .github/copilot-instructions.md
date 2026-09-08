# Copilot Instructions

This is a Windows .NET 10 Terminal.Gui file manager.

## Before editing

- Start with `/README.md`.
- Read only the topic documents mapped to the files or behavior being changed.
- Inspect the linked code before editing; documentation describes contracts and intent.

## Project rules

- Prefer simple, clean, readable, reasonably extensible implementations.
- Treat directory enumeration, filtering/sorting, row formatting, and UI refreshes as
  performance-sensitive.
- Preserve Terminal.Gui thread affinity, focus, key-routing, and disposal behavior.
- Handle expected filesystem and native failures without crashing or hiding partial failure.
- Keep Windows-native details in `Platform` behind focused managed contracts when practical.
- Preserve cancellation, resource ownership, and accurate undo semantics.
- Update the relevant topic document when changing an invariant, workflow, ownership rule, or
  user-visible behavior.
- Put durable architectural tradeoffs in `/docs/decisions`; do not add temporary plans to canonical
  documentation.