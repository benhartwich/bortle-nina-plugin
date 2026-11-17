# Changelog

## 0.1.2.0

Fixes from the N.I.N.A. plugin manifest review (isbeorn/nina.plugin.manifests#795):

- **A backlog stays with its owner.** Entries are queued per N.I.N.A. profile,
  server address and plugin key, and sent only while exactly those are active.
  Before, one shared queue sent whatever was waiting with the *current*
  settings, so an image queued for account A could reach account B's server
  with B's key. Server sessions are kept per destination too.
- **Two N.I.N.A. instances no longer lose each other's entries.** Entry names
  carry a per-instance tag and are never overwritten, removal deletes exactly
  the entry that was sent, and a cross-process lock lets one instance send a
  queue at a time. (Only upload records were affected, never the FITS files.)
- **"Upload to Bortle" off stops the backlog too**, also after a restart, not
  only new frames.
- **A delayed end closes its own session.** Every sequence gets a session name
  when it starts; its start, frames and end carry it, so N.I.N.A.'s delayed
  "sequence finished" can no longer close the next sequence's session.
- From a second, adversarial review before release:
  - A frame route that always answers 404 (a proxy rule) no longer opens a new
    server session on every step: one replacement, then the usual backoff.
  - A queue is held only while this N.I.N.A. sends it; a profile switch lets
    another instance take it over, and an empty pending queue never blocks.
  - Which session a frame belongs to is judged by when it was captured, not
    when it is sent: a late frame or a long outage no longer splits a night.
  - The last image of a sequence, still being written when the next sequence
    starts, stays with its own sequence.
  - Entries are sent in capture order across the queue for a key and the
    profile's queue from before it had a key; a half-typed key (not yet
    `bnp_` + 32 characters) does not count as a key.
  - The session end is on disk at once and held back until N.I.N.A. has saved
    the last images, so closing N.I.N.A. right after a sequence loses nothing.
    Switching uploads on mid-sequence still ends that sequence's session.
  - A frame on a folder that is not reachable (a network share offline) waits
    instead of being dropped; given up after 7 days.
  - "Finish Bortle session" no longer holds the sequence when nothing can be
    sent (uploads off, no or wrong key, another N.I.N.A.).
  - A queue entry locked for a moment is retried; a damaged one is set aside
    and logged.
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
