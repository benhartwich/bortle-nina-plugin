# Bortle plugin for N.I.N.A.

Sends every frame N.I.N.A. saves to [Bortle](https://bortle.eu) while you
image, and tells Bortle when the sequence has ended. Bortle then develops the
night.

Bortle is an astrophotography cockpit that covers planning, capture, quality
control, stacking and the finished image. It runs as a hosted service at
bortle.eu or as your own install. This plugin is the piece that sits on the
telescope PC.

The plugin **only delivers data**:
- It controls no equipment.
- It never starts or stops a sequence.
- It cannot fail or delay a sequence.

Hardware safety (parking, meridian flip, safety monitor) stays entirely with
N.I.N.A.

## What happens during a night

1. **The sequence starts.** The plugin asks Bortle for a session. Bortle links
   it to tonight's plan, which carries the develop level chosen while planning:
   step by step, guarded, or automatic.
2. **N.I.N.A. saves a frame.** The plugin writes one small entry to its queue
   (`%LOCALAPPDATA%\NINA\Bortle\queue`) and returns immediately. A background
   task then streams the FITS file to Bortle. The file on this PC is never
   changed or deleted.
3. **Something fails.**
   - **Network or server down:** the frame stays at the head of the queue and
     is retried after 10 s, then doubling up to 5 min. The queue survives
     N.I.N.A. and the PC restarting.
   - **Wrong key or quota full:** the plugin pauses and says so on its settings
     page.
   - **Frame the server refuses** (not a FITS file): it is skipped and logged.
4. **The sequence ends.** The end goes through the same queue, so it reaches
   Bortle after every frame before it. Bortle then develops all nights of each
   target together.

Snapshots are not sent. Neither are images saved as XISF or TIFF: Bortle stacks
FITS, and the plugin warns once.

### The "Finish Bortle session" instruction

It is found in the Advanced Sequencer under *Bortle*. Put it **last in the End
area, after parking**. It waits until the last frames have reached Bortle, at
most *n* minutes (default 30), and then reports the end. If the time runs out,
the end stays queued and follows the frames, so nothing is lost.

When Bortle plans the night, it adds this instruction itself, provided
N.I.N.A. lists this plugin as loaded. Without the instruction, the end of the
sequence triggers the same thing.

## Setup

1. In Bortle: **Profil → NINA-Plugin → Schlüssel erzeugen**. The key is shown
   once.
2. Install the plugin. Either:
   - through N.I.N.A.'s plugin manager, once it is listed there, or
   - by unzipping the release zip into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Bortle\`
     and restarting N.I.N.A.
3. N.I.N.A. → Options → Plugins → Bortle: enter Bortle's address and the key,
   then press *Verbindung testen*.
   - Hosted: `https://bortle.eu`.
   - Own install: the cockpit address, e.g. `http://192.168.1.20:3100`.

## The API it speaks

Every call carries the plugin key as `Authorization: Bearer bnp_…`. A plugin
key can do nothing but these four calls.

| Call | What it does |
|---|---|
| `GET /capture/hello` | Connection test: the key's owner, tonight's plan, the develop level. |
| `POST /capture/sessions` `{"sequence": …}` | Opens a session, or continues the one opened within the last hours. |
| `PUT /capture/sessions/<id>/frames/<file>.fits?target=<name>` | One frame, streamed as the body. Sending the same frame twice stores it once. |
| `POST /capture/sessions/<id>/end` | Closes the session, which starts the development. |

How the answers are handled:
- 401/403 and 413 (quota) pause the plugin.
- 404 on a session means "start a new session".
- Any other 4xx skips the frame.
- 5xx and network errors are retried.

## Building

| Path | What it is |
|---|---|
| `src/Bortle.Capture.Core/` | Queue on disk, HTTP client, upload loop. Plain .NET 8, tested on any OS. |
| `src/Bortle.NinaPlugin/` | The N.I.N.A. side: events, settings page, sequencer instruction. `net8.0-windows`; it also compiles on Linux. |
| `tests/Bortle.Capture.Core.Tests/` | xunit tests of the core against a scripted fake server. |

```sh
dotnet test tests/Bortle.Capture.Core.Tests --filter "Category!=E2E"
./package.sh            # -> dist/Bortle-<version>.zip
```

`LiveServerTests` runs a night of two frames against a real Bortle:

```sh
BORTLE_E2E_URL=… BORTLE_E2E_KEY=bnp_… dotnet test --filter Category=E2E
```

### Releasing

1. Raise `AssemblyVersion` in `src/Bortle.NinaPlugin/Properties/AssemblyInfo.cs`.
2. Add a line to `CHANGELOG.md`.
3. Push a tag with exactly that version, e.g. `0.1.0.0`.

The release workflow builds on Windows and creates the zip and the N.I.N.A.
manifest with the N.I.N.A. team's own `CreateManifest.ps1`. Both are attached to
a GitHub release. Submitting the manifest to
[nina.plugin.manifests](https://github.com/isbeorn/nina.plugin.manifests) is a
deliberate, manual pull request by the maintainer.

## AI-assisted development

This plugin was written with substantial help from an AI model (Anthropic's
Claude), working under the direction of the maintainer. The commits it
co-wrote carry a `Co-Authored-By: Claude` trailer.

The maintainer, Benjamin Hartwich, is accountable for the code:
- he reviews it,
- tests it against N.I.N.A. and Bortle before every release,
- and maintains it.

Problems and questions go to this repository's issues.

## License

[GNU Affero General Public License v3.0](LICENSE), the same license as Bortle.

N.I.N.A. is © Stefan Berg and contributors, MPL-2.0. This plugin is not part
of N.I.N.A. and is not endorsed by its authors.
