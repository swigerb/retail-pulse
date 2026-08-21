# Seed placeholder — Default pack

The default pack does not ship an explicit seed manifest. Metrics are
generated deterministically by `RetailPulseDb` from the tenant metadata
in `../pack.yaml` (brands, regions, channels, categories) using the
existing content-hash reseed behaviour — no additional inputs are
needed to reproduce today's demo dataset.

This folder is reserved for future explicit seed contributions (for
example: hand-authored inventory snapshots or fixture files that must
survive a metadata edit). Downstream wiring for #108 defines the
manifest shape when a real need appears; the pack loader treats an
empty `seed/` directory as valid.
