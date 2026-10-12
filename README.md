# Bortle NINA plugin

Sends every frame NINA saves to Bortle while you image, and tells Bortle when
the sequence has ended, so Bortle can develop the night (PLAN.md, Phase 10).

The plugin **only delivers data**. It controls no equipment, never starts or
stops a sequence, and cannot fail or delay one. Safety stays with NINA
(PLAN.md §1, guardrail 3).

## What happens during a night

1. **Sequence starts.** The plugin asks Bortle for a session. Bortle links it to
   tonight's plan, which carries the develop level chosen while planning.
2. **NINA saves a frame.** The plugin writes one small entry to its queue
   (`%LOCALAPPDATA%\NINA\Bortle\queue`) and returns immediately. A background
   task then streams the FITS file to
   `PUT <bortle>/capture/sessions/<id>/frames/<file>?target=<NINA target>`.
   The file on this PC is never changed or deleted.
3. **Something fails.**
   - **Network or server down:** the frame stays at the head of the queue and
     is retried after 10 s, then doubling up to 5 min. The queue survives NINA
     and the PC restarting.
   - **Wrong key or quota full:** the plugin pauses and says so on its settings
     page.
   - **Frame the server refuses** (not a FITS file): it is skipped and logged.
4. **Sequence ends.** The end goes through the same queue, so it reaches Bortle
   after every frame before it. Bortle then develops **all nights of each
   target together**, on the level from the plan: step by step, guarded, or
   automatic.

Snapshots are not sent. Neither are images NINA saves as XISF or TIFF: Bortle
stacks FITS, and the plugin warns once.

## The "Finish Bortle session" instruction

It is found in the Advanced Sequencer under *Bortle*. Put it **last in the End
area, after parking**. It waits until the last frames have reached Bortle, at
most *n* minutes (default 30), and then reports the end. If the time runs out,
the end stays queued and follows the frames, so nothing is lost.

You don't need to add it yourself when Bortle plans the night. Bortle's
sequence generator adds it whenever NINA lists this plugin as loaded. Without
the instruction, the end of the sequence triggers the same thing.

## Setup

1. In Bortle: **Profil → NINA-Plugin → Schlüssel erzeugen**. The key is shown
   once.
2. Unzip `Bortle-<version>.zip` into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\` so
   that you get `...\Plugins\3.0.0\Bortle\Bortle.NinaPlugin.dll`. Restart NINA.
3. NINA → Options → Plugins → Bortle: enter Bortle's address and the key, then
   press *Verbindung testen*.
   - On bortle.eu: `https://bortle.eu`.
   - On your own install: the cockpit address, e.g. `http://192.168.1.20:3100`.

## Layout

| Path | What it is |
|---|---|
| `src/Bortle.Capture.Core/` | Queue on disk, HTTP client, upload loop. Plain .NET 8, tested on any OS. |
| `src/Bortle.NinaPlugin/` | The NINA side: events, settings page, sequencer instruction. `net8.0-windows`, compiles on Linux too. |
| `tests/Bortle.Capture.Core.Tests/` | xunit tests of the core against a scripted fake server. |

```sh
dotnet test tests/Bortle.Capture.Core.Tests
./package.sh            # -> dist/Bortle-<version>.zip
```

The server side is `services/orchestrator/capture.py` (sessions and frames) and
`services/orchestrator/capture_keys.py` (keys). `tests/test_nina_plugin_contract.py`
keeps the names both sides hard-code in step.
