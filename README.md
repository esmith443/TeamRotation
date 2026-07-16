# TeamRotation

Oxide/uMod plugin for Rust. Lets team leaders rotate offline players out of their team — kicks them, strips their auth from TCs/locks/turrets, deletes their bags/beds, and bans them from re-authorizing until wipe.

## Install

1. Drop `TeamRotation.cs` in `oxide/plugins/`
2. `oxide.reload TeamRotation`

## Usage

Team leader opens a TC and clicks the **Rotate Players** button (or runs `/rotateoffline` while standing near one). Any offline team members get:

- Kicked from the team
- De-authorized from nearby TCs, code locks, and turrets
- Key lock ownership transferred to the leader
- Bags/beds within range deleted
- Banned from re-authorizing to that leader's entities or rejoining the team until wipe

All actions apply to entities within the configured radius of the TC used.

## Config

`oxide/config/TeamRotation.json`

| Key | Default | Description |
|---|---|---|
| `Bag/Bed Deletion Radius (from TC)` | `50` | Range around the TC affected by rotation |
| `Require Permission` | `true` | Require `teamrotation.use` to see the button |
| `Feature Toggles` | all `true` | Enable/disable each action individually (de-auth TC/lock/turret, key transfer, bag deletion, kick, ban, block rejoin) |
| `Discord Webhook` | off | Alerts for rotations, admin commands, and banned-player attempts |
| `UI Settings` | — | Button color, text color, position |

## Permissions

- `teamrotation.use` — see the Rotate Players button
- `teamrotation.admin` — admin commands

```
oxide.grant user <name> teamrotation.use
```

## Commands

- `/rotateoffline` — rotate offline members from the nearest TC (admin)
- `/rotation.bans` — list all rotation bans (admin)
- `/rotation.unban <name or steamID>` — remove a player from ban lists (admin)
- `/rotation.clearbans` — clear all bans, e.g. on wipe (admin)

## Discord Webhooks

Sends alerts for rotations executed, admin commands run, and banned players attempting to re-authorize or rejoin. Rate-limited per player/action (default 60s).

## Notes

- Bans persist in a data file (`oxide/data/TeamRotation.json`) and survive plugin reloads — clear them manually or via `/rotation.clearbans` on wipe
- Rotation only affects offline members; the team leader can't rotate themselves

MIT
