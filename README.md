# Stellar Atlas

**Atlas is a shared map: while it runs, the chests and resources you find (what and where) are shared with the Atlas server. No names, UIDs or chat are sent.**

A chest and resource finder for Blue Protocol: Star Resonance (Stellar plugin). It shows the nearest known chests and
resources around you, from the crowd-sourced Atlas point server plus what your game shows live, and puts the game's own
guide marker on the one you pick. The points you have shown are also drawn on the game's own big map and minimap. To
stop sharing, turn Atlas off in the launcher.

- Server: `StellarAtlasServer` (`https://atlas.stellarresonance.app`, config key `server_url`).
- Build: copy `Local.props.example` to `Local.props`, set `GameInstallDir` and `AtlasClientKey` (never commit a key).
  Without a key the plugin only shows the copy cached on disk.
- Icons: `python -I tools/icons/gen-atlas-icon.py Resources/icons` (pre-drawn per size).
