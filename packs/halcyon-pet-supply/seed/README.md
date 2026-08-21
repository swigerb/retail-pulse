# Halcyon Pet Supply — seed placeholder

The Halcyon pack does not currently ship a bespoke seed manifest. During
load, seed data is derived from the tenant metadata in `pack.yaml` via the
existing `RetailPulseDb` content-hash seeder — the same mechanism used by
the default pack.

A future revision may introduce an explicit seed manifest here (households,
neighborhood store list, adoption-partner directory). Until then, this
directory is intentionally empty apart from this note so the loader can
distinguish an intentional empty seed from a missing folder.
