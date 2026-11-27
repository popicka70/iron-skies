# Specifications

This directory is the **living plan** for active work.

**Project goal:** a free, open-source, photorealistic WW2 combat flight simulator for
Linux and Windows, with single-player and authoritative multiplayer, engineering-grade
6-DOF flight dynamics, and persistent maps generated from real, openly licensed geodata.
The current workstream goal is a convincing 4,000 km European theatre; see
[`0001-continental-terrain.md`](0001-continental-terrain.md). The aircraft art workstream
runs in parallel and is tracked in
[`0002-aircraft-art-pass.md`](0002-aircraft-art-pass.md). The product goal and scope are
in [`../SPEC.md`](../SPEC.md); the milestone backlog is in
[`../ROADMAP.md`](../ROADMAP.md). A spec is not a design document: it records what is
being changed now, how it will be verified, and what remains.

Read [`../../AGENTS.md`](../../AGENTS.md) first for the workflow and rules.

## Convention

- **One spec per workstream.** File name: `NNNN-kebab-case.md`, numbers sequential.
  Never reuse a number; never overwrite a completed spec with a new workstream.
- **Status:** `proposed` (not started), `active` (in progress), `blocked` (waiting on a
  decision or external data), `done` (acceptance met), `superseded` (replaced by a newer
  spec; link it).
- **Length:** 1–3 pages. Link to architecture, ADRs, and plans instead of copying them.
- **Update in place** as work proceeds: checkboxes, status, changelog, remaining work.
  Do not create a new doc for each session or idea.
- **Every step is a commit and a push** on a feature branch; the spec lists the steps.
- **Completed specs remain** as history. New work gets the next number.
- **Older `docs/plan-*.md` files are implementation logs**, not current plans. Their
  headers point at the spec that superseded them.

## Template

```markdown
# NNNN — Title

**Status:** proposed | active | blocked | done | superseded by NNNN
**Updated:** YYYY-MM-DD
**Area:** terrain | flight-model | net | rendering | tools | docs

## Goal

What “done” means and why it matters. One short paragraph.

## Current state

What exists today, with paths and measured numbers.

## Requirements and constraints

Budgets, licences, compatibility, determinism, package size, performance.

## Plan

1. [ ] Step one — one commit.
2. [ ] Step two — one commit.
3. [ ] Step three — one commit.

## Acceptance and verification

Exact commands and expected output. Include the smoke tests that must stay green.

## Risks and decisions

Known trade-offs. Link ADRs. Record why alternatives were rejected.

## Remaining work

Ordered next steps for the next session. Keep this honest; it is the resume point.

## Changelog

- YYYY-MM-DD — commit — one line.
```

## Index

| Spec | Status | Area | Summary |
| --- | --- | --- | --- |
| [0001 — Continental terrain detail](0001-continental-terrain.md) | **active** | terrain | Finish the 4,000 km theatre: continental imagery, tile I/O, floating origin, vector detail. |
| [0002 — Aircraft art pass](0002-aircraft-art-pass.md) | **active** | rendering, art, tools | Reference-locked silhouettes, PBR detail, cockpits, damage, authored LODs and the P-51D. |

Next number: `0003` (do not create until the work starts).
