# Changelog

## 0.1.2.0

Rebuilt after the N.I.N.A. plugin manifest review (isbeorn/nina.plugin.manifests#795)
and five adversarial review rounds of our own. **Needs a Bortle server from
2026-11 or later** (`/capture/hello` answers `"protocol": 2`); an older server
answers 404, which the plugin retries with backoff and names in its status line.

- **Sequences are named by the plugin, filed by the server.** Every N.I.N.A.
  sequence gets an id when it starts; its start, frames and end are sent to
  `/capture/sequences/<id>/`. The server knows which night holds which
  sequence, and a night ends only when the last of its sequences has ended.
  The plugin keeps no server session state at all, so a late end can only end
  its own sequence, a night sent days later is still one night, and a 404 is
  never a reason to open anything.
- **An image belongs to the sequence its exposure began in** (N.I.N.A.'s own
  timestamp), however late it is written: after the end, or after the next
  sequence started.
- **A backlog stays with its owner.** Queues are per N.I.N.A. profile, server
  address and plugin key, and sent only while exactly those are active, with
  that key. Frames saved before a profile had any key go with the key that
  same profile has when they are sent. A key is only a key when it is whole
  (`bnp_` + 32 characters), so frames saved while one is being typed wait.
- **Two N.I.N.A. instances** never overwrite each other's entries; one sends a
  given queue at a time, and a N.I.N.A. that cannot send holds no lock.
- **"Upload to Bortle" off stops the backlog too**, also after a restart.
  Switching it on mid-sequence still ends that sequence.
- **The end is on disk at once**: closing N.I.N.A. right after a sequence
  loses nothing. A sequence that never said it finished (N.I.N.A. started the
  next one, or crashed) is ended by the next start or the next run — the run
  of that same N.I.N.A.: a second instance on the PC never ends a sequence the
  first is still running. An end always goes to the account that got the
  sequence's start, even when the profile or key changed in between.
  "Finish Bortle session" placed mid-sequence ends Bortle's sequence; frames
  N.I.N.A. takes after it form a sequence of their own.
- **Robust against the PC**: entries keep their order when the clock is
  stepped back, also across a restart; a deleted queue folder is recreated and
  the upload loop survives it; a frame on a network share that is offline
  waits (up to 7 days) instead of being dropped; a locked entry is retried and
  a damaged one set aside and logged.
- **"Finish Bortle session"** no longer holds the sequence when nothing can be
  sent (uploads off, no or wrong key, another N.I.N.A.).
- Entries left by 0.1.0.x in `%LOCALAPPDATA%\NINA\Bortle\queue` record no owner
  and are not sent; the status line and the log say how many there are.

## 0.1.1.0

- New panel *Bortle Livestream* for N.I.N.A.'s imaging tab: one button to
  stream the night to YouTube. The Bortle server makes the stream from the
  frames the plugin uploads anyway, a growing live stack with the object,
  frame count and exposure. This PC sends no video, and N.I.N.A. and the
  equipment are not involved. The stream key is entered once in the Bortle
  cockpit (Profil → Livestream), never in N.I.N.A. For now only the
  operator of a Bortle instance can stream.

## 0.1.0.3

- A missing or refused key pauses uploads for 30 s, not 5 minutes. The key
  lives in the active NINA profile and is read again on every attempt, so a
  profile switch or a key pasted a moment later no longer leaves frames
  waiting until someone presses *Verbindung testen*.
- A successful *Verbindung testen* ends any pause and backoff at once.
- The status line says what is wrong: "Schlüssel fehlt in diesem
  NINA-Profil" or "Schlüssel abgewiesen (401) – im aktiven Profil prüfen".
  A refused key also raises one NINA notification, not one per retry.
- A full storage quota keeps the long pause.
- The server address and key fields save on every change, not when the field
  loses focus: a pasted key is in the profile even if NINA is closed or
  *Verbindung testen* is clicked right away. The uploader waits 1.5 s after
  the last change before trying again, so typing sends no request per
  keystroke.

## 0.1.0.2

- *Finish Bortle session* (and NINA's own end of a sequence) waits until NINA
  has saved the images it still had on its way before ending the session.
  NINA writes the last image of a sequence in the background, after the
  sequence moved on; on the first real test the session ended 13 s before its
  only frame was written, and the frame opened a session of its own.
- A frame saved up to 30 minutes after its session ended still goes to that
  session instead of starting a new one.

## 0.1.0.1

- The N.I.N.A. manifest names the maintainer as the author (was "Bortle").

## 0.1.0.0

First version.

- Every saved FITS frame is uploaded to Bortle through a queue on disk. The
  queue retries with backoff, pauses on a wrong key or a full quota, and
  survives restarts.
- The sequence start and end are reported, in order with the frames.
- New sequencer instruction "Finish Bortle session".
- Settings page with server address, key, connection test and queue status.
