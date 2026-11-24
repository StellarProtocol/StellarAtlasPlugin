# Stellar Atlas

**Atlas is a shared map: while it runs, the chests, resources and magical creatures you find (what and where) are shared with the Atlas server. No names, UIDs or chat are sent.**

A chest and resource finder for Blue Protocol: Star Resonance (Stellar plugin). It shows the nearest known chests and
resources around you, from the crowd-sourced Atlas point server plus what your game shows live, and puts the game's own
guide marker on the one you pick. The points you have shown are also drawn on the game's own big map and minimap. To
stop sharing, turn Atlas off in the launcher.

## Magical creatures

Turn on any of the five magical creatures of Asteria Plains in SHOW (Golden Nappo, Silver Nappo, Loyal Boarlet, Lovely
Boarlet, Breezy Boarlet) and Atlas lists them in their own MAGICAL CREATURES panel:

- which lines each one is alive on (yours first), its spot, HP and distance; "Absent" with the next spawn time for the
  boarlets that appear on a fixed schedule;
- "Seen live" with its exact position and HP while it is in view;
- a click puts the game's guide marker on it, also when it is on another line (switching line is up to you);
- round head tiles on the game's big map and minimap: bright with a green ring on your line, faded with a line badge on
  other lines, faded with the next spawn time while a scheduled boarlet is absent.

The status of every line comes from the Atlas server (refreshed about every 30 s while you are in Asteria Plains or the
window is open). On servers without live status you still see the creatures other Atlas players spotted: a creature in
view (alive, or defeated) is shared the same way as a chest.

## Build

- Server: `StellarAtlasServer` (`https://atlas.stellarresonance.app`, config key `server_url`). The creature status
  server id is detected from the game; config key `CreatureServerId` (e.g. `sea`) overrides it.
- Build: copy `Local.props.example` to `Local.props`, set `GameInstallDir` and `AtlasClientKey` (never commit a key).
  Without a key the plugin only shows the copy cached on disk.
- Icons: `python -I tools/icons/gen-atlas-icon.py Resources/icons` (pre-drawn per size). Creature art:
  `python -I tools/icons/gen-creature-icons.py <folder with 10900..10904.png> Resources/creatures`.
