# Changelog

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
