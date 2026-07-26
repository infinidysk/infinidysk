# Diagrams

All diagrams in this documentation are embedded inline as Mermaid code blocks within the relevant
`.md` sections (§3, §5, §6, §7) rather than kept as separate files here — Mermaid-in-Markdown
renders natively on GitHub with zero tooling, so the docs are fully readable without any build step.

This folder exists as the target for the optional CI workflow
(`.github/workflows/render-architecture-docs.yml`), which renders those inline diagrams and
produces a combined PDF of the whole `docs/arc42/` document set as a downloadable build artifact.
It intentionally has no committed files of its own — the PDF is a CI artifact, not a checked-in
output, so it can't drift out of sync with the source Markdown.
