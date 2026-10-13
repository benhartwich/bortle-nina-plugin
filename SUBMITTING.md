# Submitting a release to N.I.N.A.'s plugin manager

N.I.N.A. lists a plugin once its manifest is merged into
[isbeorn/nina.plugin.manifests](https://github.com/isbeorn/nina.plugin.manifests).
This step is done **by the maintainer, by hand, after testing the release**.
The pull request is the maintainer's own declaration:
- that he is authorized to distribute the plugin,
- that he has reviewed, understood and tested its code,
- and that the AI assistance is disclosed.

The release workflow deliberately does not open the pull request.

## Before submitting

Install the release's zip in N.I.N.A., not a local build: the manifest's
checksum is that zip's. Then check that:

- [ ] N.I.N.A. loads the plugin; Options → Plugins → Bortle shows the settings
      page.
- [ ] *Verbindung testen* answers with your account.
- [ ] During a short sequence the frames arrive in Bortle and the queue drains.
- [ ] A sequence that Bortle loaded with "Finish Bortle session" deserializes,
      with no placeholder, and the end starts the development.
- [ ] With the network pulled mid-sequence, imaging carries on, and the frames
      follow once it is back.

## Steps

1. Fork `isbeorn/nina.plugin.manifests`.
2. Take `Bortle.NinaPlugin.<version>.manifest.json` from the GitHub release.
   Do not regenerate it.
3. Put it at `manifests/b/Bortle/3.2.0.9001/<version>/manifest.json`. The
   middle folder is the minimum N.I.N.A. version.
4. Validate it: `npm install && node validate-latest-manifest.js` in the fork.
5. Open the pull request with the text below.

For a version that should only reach N.I.N.A.'s beta channel, the manifest
repository describes a beta flag ("Releasing a Manifest as a Beta Version").
A 0.x release is a candidate for it.

## Pull request text

```
Add manifest for Bortle <version>

Bortle delivers the frames N.I.N.A. saves to Bortle (https://bortle.eu, or a
self-hosted install), which stacks and develops them when the sequence ends.
The plugin only delivers data: it controls no equipment and cannot delay or
fail a sequence.

Source: https://github.com/benhartwich/bortle-nina-plugin (AGPL-3.0)
Release: https://github.com/benhartwich/bortle-nina-plugin/releases/tag/<version>

AI-assisted development: the plugin was written with substantial help from an
AI model (Anthropic's Claude) under my direction; commits it co-wrote carry a
Co-Authored-By trailer. I have reviewed and tested this release in N.I.N.A.
and maintain it.
```
