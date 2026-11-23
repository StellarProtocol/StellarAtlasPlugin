# Stellar Atlas

A chest and resource finder for Blue Protocol: Star Resonance (Stellar plugin). It shows the nearest known chests and
resources around you, from the crowd-sourced Atlas point server plus what your game shows live, and puts the game's own
guide marker on the one you pick. With Share on it reports what you find (what and where, nothing else) so the map gets
better for everyone.

- Server: `StellarAtlasServer` (`https://atlas.stellarresonance.app`, config key `server_url`).
- Build: copy `Local.props.example` to `Local.props`, set `GameInstallDir` and `AtlasClientKey` (never commit a key).
  Without a key the plugin only shows the copy cached on disk.
- Icons: `python -I tools/icons/gen-atlas-icon.py Resources/icons` (pre-drawn per size).
