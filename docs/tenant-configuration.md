# Tenant Configuration Guide

Retail Pulse is fully tenant-configurable through **content packs**. A pack
is a self-contained folder that owns everything a tenant scenario needs —
company metadata, brand/region/channel taxonomy, the agent roster, the
grounding knowledge corpus, and the curated starting prompts. The
platform reads a single pack at startup; swapping the active pack swaps
the entire scenario in place, no code change required.

> **Runtime wiring (issue #108).** The API host, the MCP server, and the
> web frontend all read the active pack. The API composition root loads
> the pack at startup and registers the singleton `LoadedPack`
> downstream (tenant provider, agent roster, in-memory RAG seeder, and
> the `/api/pack` endpoints). The MCP server independently reads the
> same pack directory for its tenant and its scenario seed manifest so
> the two processes stay in lockstep. The frontend fetches
> `/api/pack` and `/api/pack/starting-tasks` during bootstrap to build
> the header, brand list, theme variables, and Prompt Library. The
> legacy top-level `tenant.yaml` and `src/RetailPulse.Api/prompts.yaml`
> remain on disk only as historical baselines pinned by the
> `DefaultPack_TenantMatchesLegacyTenantYaml` equivalence test — they
> do not participate in runtime configuration.

---

## Quick Start

1. Pick a pack: set `Packs:Active` (config key) to the pack's directory
   name — for example `default`, `halcyon-pet-supply`, or
   `prairiehearth-craft-supply`. If unset, the loader defaults to
   `default`.
2. Optionally override the packs root with `Packs:Root` (default
   `packs`). The path is resolved from the process content root, so a
   deployment can ship packs alongside its binaries.
3. Restart the application. Switching the active pack is a
   config-only change plus a restart — there is no hot-swap path so
   the composition graph stays deterministic. The loader validates
   the pack as a single unit and reports every issue it finds in one
   aggregated error, and the API host fails fast rather than
   half-booting.

```jsonc
// appsettings.json (illustrative)
{
  "Packs": {
    "Active": "halcyon-pet-supply",
    "Root": "packs"
  }
}
```

On the next start-up the API host binds `PackOptions`, resolves
`<Root>/<Active>` under the process content root via
`PackPathResolver`, calls `PackLoader.Load(...)`, and registers the
resulting `LoadedPack` as a singleton. The MCP server reads the same
`Packs:Active` / `Packs:Root` keys, loads `pack.yaml` and
`seed/scenario.yaml` through `PackTenantLoader.LoadFromPackDirectory`,
and stamps their combined content hash into its SQLite `SeedMetadata`
row so pack switches reseed the fact tables (see [Seed
content-hash reseed](#seed-content-hash-reseed)).

---

## Pack Layout

```
packs/
  <pack-name>/
    pack.yaml              # required — metadata + tenant configuration
    agents.yaml            # required — agent roster (same shape as legacy prompts.yaml)
    starting-tasks.yaml    # optional — curated PromptLibrary categories
    knowledge/             # optional — markdown grounding corpus
      *.md
    seed/                  # required
      scenario.yaml        # required — MCP scenario seed manifest (issue #108)
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
| `version`     | no       | Semver-shaped tag; informational                               |
| `segment`     | no       | Retail-segment label (for example "Pet Supply")                |
| `attribution` | no       | Attribution note — sample packs advertise their fictional status |

### `tenant` block

The `tenant` block deserializes into `TenantConfiguration` (see
`src/RetailPulse.Contracts/TenantConfiguration.cs`). The loader
validates the required fields as a unit — a bad pack surfaces every
missing/malformed section in one report:

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

See any shipped pack for a complete example. The default pack's
`tenant:` block preserves the historical `tenant.yaml` verbatim so the
`DefaultPack_TenantMatchesLegacyTenantYaml` test can pin equivalence
between the pack-driven load and the pre-pack baseline.

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
- Rejects duplicate YAML section keys (for example two
  `demand-forecast:` entries) at parse time so a copy/paste mistake
  cannot silently drop earlier definitions.

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
`starting-tasks.yaml` (or one that returns an empty `categories:` list)
causes the frontend to fall back to its built-in `PROMPT_CATEGORIES`.
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

The `/api/pack/starting-tasks` endpoint projects the parsed categories
verbatim and the frontend `useActivePack` hook merges them into the
Prompt Library once the fetch resolves.

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
specific subset of the corpus; the shipped packs reuse that
configuration-side registry.

---

## `seed/scenario.yaml`

Every pack MUST ship a `seed/scenario.yaml` manifest. It is the
machine-readable source of every scenario-varying seed input the MCP
server writes into its SQLite fact tables at boot — seasonality
factors, competitor rosters, promo coefficient bands, supply
disruption vocabulary, store archetypes, and margin driver categories.
Prior to issue #108 those values were baked into `RetailPulseDb.cs`;
they now live inside the pack so a pack switch changes the SQLite
dataset wholesale, not just the tenant metadata.

The full schema — including validation rules — is defined by
`SeedManifest` in `src/RetailPulse.Contracts/SeedManifest.cs`. Rather
than duplicating every field here, refer to the shipped
`packs/default/seed/scenario.yaml` for a byte-verbatim port of the
historic defaults (verified by
`DefaultPack_SeedManifest_MatchesLegacyOracle`). The manifest is organized
into six logical sections; every one is required and every listed
child field is required unless noted:

| Section       | Required contents                                                                                             |
| ------------- | ------------------------------------------------------------------------------------------------------------- |
| `seasonality` | `factors: { <category>: [ { month (1-12), multiplier, event, description } ] }` — at least one category       |
| `competitive` | `competitorsByCategory`, `pricingSources`, `shareSources`, `activityTypes`, `impactLevels`, `activityTemplates` |
| `promos`      | `types: [ { name, liftBase, liftRange, coefBase, coefRange, code?, displayName?, description? } ]`, `successRatings` |
| `supply`      | `disruptionTypes`, `disruptionSeverities`, `disruptionDescriptions: { <type>: [ text, ... ] }`                |
| `stores`      | `types: [ ... ]`                                                                                              |
| `margin`      | `driverCategories`, `trendLabels`                                                                             |

`SeedManifestLoader` opts in to duplicate-key detection so a doubled
section in `scenario.yaml` fails the load in the same way it does in
`pack.yaml` and `agents.yaml`. Every seed error is aggregated into the
same `PackValidationException` as the rest of the pack.

### Seed content-hash reseed

The MCP server hashes the schema version, the active `pack.yaml`, and
every file under the pack's `seed/` directory into a single
content-hash it writes into the `SeedMetadata` table on first boot.
On every subsequent boot:

- **Unchanged pack** — the recomputed hash matches; the seeder is a
  no-op and caller-driven mutations (approvals, session state,
  runtime edits) survive the restart.
- **Changed pack** — switching `Packs:Active`, editing `pack.yaml`,
  or editing any file under `seed/` produces a new hash, and the
  seeder wipes the fact tables and reseeds from the new manifest
  before the server accepts traffic.

The API's in-memory knowledge base uses the same content-hash
idempotence: a document whose body changed is purged and re-ingested
so operators never see stale grounding after a pack update, while an
unchanged pack is a no-op.

> **Ownership**. Every scenario-varying seed input consumed by the
> MCP server is pack-owned. Non-scenario derived tables (households,
> contacts, per-brand store rows synthesized from `tenant.brands`)
> are still generated inside `RetailPulseDb`, but they draw entirely
> from the pack's tenant declaration and seed manifest.

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
`KnowledgeDocuments`, `StartingTasks`, and `Seed` (the
`SeedManifest`). The API composition root registers the singleton
and wires each section into its existing subsystem (the agent
registry, the in-memory knowledge base seeder, the `/api/pack`
endpoints), so the pack is the single source of truth for the
running scenario. The MCP server calls
`PackTenantLoader.LoadFromPackDirectory` on the same pack directory
to obtain the tenant provider and the seed manifest without
referencing the API assembly.

---

## Aggregate Validation Semantics

Every discoverable issue across every section of the pack —
`pack.yaml` (metadata + tenant), `agents.yaml` (structural + the #99
safety validator when supplied), `starting-tasks.yaml`, every file
under `knowledge/`, and `seed/scenario.yaml` — is aggregated into
one `PackValidationException`. Each `PackValidationIssue` names the
pack and the section (or file, or agent key) so operators reading
the exception message can jump straight to the source:

```
Pack 'wobble' failed validation with 10 issue(s):
  - [pack 'wobble' → pack.yaml#metadata] metadata.key 'some-other-key' does not match the pack directory name 'wobble'. ...
  - [pack 'wobble' → pack.yaml#tenant.company] tenant.company is required.
  - [pack 'wobble' → agents.yaml] Required section 'agents.yaml' is missing. ...
  - [pack 'wobble' → seed/scenario.yaml#promos.types] Seed manifest '...\seed\scenario.yaml' is missing required section 'promos.types'.
  - [pack 'wobble' → agents.yaml#planner] [Tools] Tool 'unknown_tool' is not in the tool catalog.
```

Safety-validator findings from `AgentDefinitionValidator` are folded
in with a `pack.agents.safety.<ruleId>` code when the validator is
configured for `RefuseStartup`. Under `QuarantineOffender` the
validator removes the offending agent from `LoadedPack.Agents` and
returns normally — no aggregate entry is produced.

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
5. Author `seed/scenario.yaml` for the scenario. Copy from a shipped
   pack and edit — every section listed in the
   [`seed/scenario.yaml`](#seedscenarioyaml) table is required.
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

## Legacy `tenant.yaml` and `prompts.yaml`

The pre-pack `tenant.yaml` at the repo root and
`src/RetailPulse.Api/prompts.yaml` are retained solely as historical
baselines. Neither file participates in runtime configuration — the
API host, the MCP server, and the frontend all read the active pack.
The `DefaultPack_TenantMatchesLegacyTenantYaml` test pins the
`packs/default/pack.yaml` `tenant:` block byte-for-byte against
`tenant.yaml` so a drift between the pack-loader path and the pre-pack
baseline surfaces as a test failure rather than an operational
surprise.
