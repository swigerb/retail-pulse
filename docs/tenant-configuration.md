# Tenant Configuration Guide

Retail Pulse is fully tenant-configurable through **content packs**. A pack
is a self-contained folder that owns everything a tenant scenario needs —
company metadata, brand/region/channel taxonomy, the agent roster, the
grounding knowledge corpus, and the curated starting prompts. The
platform reads a single pack at startup; swapping the active pack swaps
the entire scenario in place, no code change required.

> **Foundation status (issue #108)**. The pack loader and the shipped
> packs (`default`, `halcyon-pet-supply`, `prairiehearth-craft-supply`)
> are landed. Wiring the runtime composition root (`Program.cs`, MCP
> server, Teams bot) to `PackLoader` instead of the pre-pack flat-file
> reads is intentionally scoped out of the foundation and picked up in
> the downstream integration issues so this guide is safe to read now.
> The legacy top-level `tenant.yaml` and `src/RetailPulse.Api/prompts.yaml`
> continue to drive the running app until that wiring lands.

---

## Quick Start

1. Pick a pack: set `Packs:Active` (config key) to the pack's directory
   name — for example `default`, `halcyon-pet-supply`, or
   `prairiehearth-craft-supply`. If unset, the loader defaults to
   `default`.
2. Optionally override the packs root with `Packs:Root` (default
   `packs`). The path is resolved from the process content root, so a
   deployment can ship packs alongside its binaries.
3. Restart the application. The loader validates the pack as a single
   unit and reports every issue it finds in one aggregated error — the
   process fails fast rather than half-booting.

```jsonc
// appsettings.json (illustrative — the pre-pack flat-file reads still
// drive the running app until the downstream wiring lands)
{
  "Packs": {
    "Active": "halcyon-pet-supply",
    "Root": "packs"
  }
}
```

---

## Pack Layout

```
packs/
  <pack-name>/
    pack.yaml              # required — metadata + tenant configuration
    agents.yaml            # required — agent roster (same shape as prompts.yaml)
    starting-tasks.yaml    # optional — curated PromptLibrary categories
    knowledge/             # optional — markdown grounding corpus
      *.md
    seed/                  # optional — reserved for future explicit seed manifest
      README.md
```

`<pack-name>` MUST match the pack's `metadata.key`. The loader flags a
mismatch so an operator picking a pack by its configured key always
finds the source on disk.

---

## `pack.yaml`

### `metadata` block

| Field         | Required | Purpose                                                        |
| ------------- | -------- | -------------------------------------------------------------- |
| `key`         | yes      | Lowercase kebab-case identifier; MUST match the directory name |
| `displayName` | yes      | Human-readable label shown in operator surfaces                |
| `description` | no       | Free-form summary of the retail scenario                       |
| `version`     | no       | Semver-shaped tag; informational in the foundation             |
| `segment`     | no       | Retail-segment label (for example "Pet Supply")                |
| `attribution` | no       | Attribution note — sample packs advertise their fictional status |

### `tenant` block

The `tenant` block is the same shape the legacy top-level `tenant.yaml`
used. The loader validates the required fields as a unit — a bad pack
surfaces every missing/malformed section in one report:

| Field                         | Required |
| ----------------------------- | -------- |
| `company`                     | yes      |
| `industry`                    | yes      |
| `brands[]` (with `name`)      | yes      |
| `regions[]`                   | yes      |
| `channels[]`                  | yes      |
| `distribution.model`          | yes      |
| `theme.primaryColor`          | yes      |
| `description`, brand variants, brand `priceSegment`, theme extras, `distribution.distributorTypes` | no |

See any shipped pack for a complete example — `packs/default/pack.yaml`
preserves the legacy `tenant.yaml` verbatim.

---

## `agents.yaml`

`agents.yaml` matches the shape of the legacy `src/RetailPulse.Api/prompts.yaml`
file: a top-level `agents:` map whose entries are individual agent
definitions. The loader:

- Defaults each agent's `key` to its YAML section name when omitted.
- Aggregates a duplicate-`name` diagnostic across the whole roster (the
  existing #99 safety validator only trips this when Content Safety is
  enabled at runtime; the pack layer catches it unconditionally).
- Passes the roster through the existing `AgentDefinitionValidator`
  (#99) when a validator is supplied to `LoadAsync` — the safety
  validator remains the single source of truth for tool-allowlist,
  jailbreak, and Content Safety checks. Pack authors must reuse the
  tool names in `AgentDefinitionValidatorToolCatalog.KnownToolNames`.

Pack authors MUST include the mandatory orchestration entries the
composition root resolves by key:

- `router` — intent classifier (role `orchestration`, no tools).
- `retail-pulse` — general/fallback specialist (role `specialist`).

Optional orchestration entries the composition root looks up when
present: `council-synthesis`, `council-vote`, `scorecard-synthesis`,
`exec-brief`, `planner`, `memory-management`.

---

## `starting-tasks.yaml`

Curated PromptLibrary categories. Optional — a pack with no
`starting-tasks.yaml` serves the platform-neutral defaults downstream.
Category ids MUST be unique inside the file; every category MUST declare
a `label` and at least one prompt.

```yaml
categories:
  - id: nutrition
    label: "Nutrition Planning"
    emoji: "🥣"
    prompts:
      - "How is Meadowbowl Nutrition auto-ship depletion trending this quarter?"
      - "Compare grain-inclusive vs grain-free share by region"
```

---

## `knowledge/`

Optional grounding corpus. Every `*.md` file in `knowledge/` is loaded
as a `PackKnowledgeDocument`; the loader:

- Uses the filename as the ingest `Source` (dedup key downstream).
- Extracts the first H1 heading as the document `Title` and falls back
  to the filename stem when no heading is present.
- Fails the pack when a file is empty or a duplicate filename appears.

Named-source bindings (`Knowledge:Sources:Named` in `appsettings.json`)
are still the mechanism by which an agent scopes retrieval to a
specific subset of the corpus. In the foundation the shipped packs
continue to reuse the legacy named-source registry; a future revision
will let a pack declare its own named-source manifest inline.

---

## `seed/`

Reserved for future explicit seed manifests (households, contacts,
store list). The shipped packs currently include only a `README.md`
placeholder — seed data is derived from tenant metadata via the
existing `RetailPulseDb` content-hash seeder, exactly as it was before
the pack layout landed. This keeps the default pack's runtime
behaviour byte-identical to the pre-pack world.

---

## Loader API

The pack loader is the single downstream integration point:

```csharp
PackLoader loader = PackLoader.ForDirectory(packsRoot);
IReadOnlyList<string> packs = loader.DiscoverPacks();

// Structural load — throws PackValidationException aggregating every
// issue found across every section of the pack.
LoadedPack pack = loader.Load("default");

// Structural + safety load. When a validator is supplied, the loader
// hands the pack's agent roster to the existing #99 safety validator
// so its Content Safety, jailbreak, tool-allowlist, and audit path
// runs in the single place they already run today.
LoadedPack pack = await loader.LoadAsync("halcyon-pet-supply",
    agentDefinitionValidator, cancellationToken);
```

The `LoadedPack` exposes every section a downstream host needs:
`Metadata`, `Tenant`, `Agents` (a `PromptConfiguration`),
`KnowledgeDocuments`, and `StartingTasks`. Downstream integration
wires each into its existing subsystem (the agent registry, the
in-memory knowledge base seeder, the PromptLibrary endpoint) so the
pack becomes the single source of truth for the running scenario.

---

## Aggregate Validation Semantics

Every discoverable issue across every section is aggregated into one
`PackValidationException`. Each `PackValidationIssue` names the pack
and the section (or file, or agent key) so operators reading the
exception message can jump straight to the source:

```
Pack 'wobble' failed validation with 10 issue(s):
  - [pack 'wobble' → pack.yaml#metadata] metadata.key 'some-other-key' does not match the pack directory name 'wobble'. ...
  - [pack 'wobble' → pack.yaml#tenant.company] tenant.company is required.
  - [pack 'wobble' → agents.yaml] Required section 'agents.yaml' is missing. ...
```

Optional sections that are simply absent degrade to empty collections
— they only become issues when the section file exists but is
malformed.

---

## Authoring a New Pack

1. Copy the shape of `packs/default/` (or one of the fictional packs
   for a leaner starting point) into `packs/<your-pack>/`.
2. Set `metadata.key` to `<your-pack>` and fill in the tenant block.
3. Rewrite `agents.yaml` — keep the `router` and `retail-pulse` keys;
   the specialist roster is yours to design. Every tool reference MUST
   appear in `AgentDefinitionValidatorToolCatalog.KnownToolNames` or
   the safety validator will reject the pack at startup.
4. Curate `starting-tasks.yaml` and the `knowledge/*.md` grounding
   corpus for the scenario.
5. Add a placeholder `seed/README.md` explaining how seed data is
   currently derived.
6. Run the pack test suite:
   `dotnet test tests/RetailPulse.Tests/RetailPulse.Tests.csproj --filter "FullyQualifiedName~Packs"`.
   The `ShippedPackContractTests` will pick up your pack automatically
   and fail if it doesn't load cleanly.

### Sample-content guardrails

Sample packs shipped in this repo (including `halcyon-pet-supply` and
`prairiehearth-craft-supply`) are entirely fictional. Contributors
MUST NOT introduce brand names, metrics, or vocabulary that reference
external solution accelerators, competitive comparisons, or third-party
analysis — the sample packs exist to exercise the pack contract, not to
represent any real retailer, cooperative, or supplier.

---

## Legacy `tenant.yaml`

The pre-pack `tenant.yaml` at the repo root continues to drive the
running app in the foundation. The default pack extracts its full
content into `packs/default/pack.yaml`'s `tenant:` block so the eventual
wiring flip is observably a no-op: the pack-loader path produces the
same `TenantConfiguration` object the legacy loader does, and the
equivalence test `DefaultPack_TenantMatchesLegacyTenantYaml` pins that
guarantee.
