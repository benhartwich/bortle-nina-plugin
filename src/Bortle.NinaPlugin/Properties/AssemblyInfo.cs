using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] The plugin's identity in NINA. Never change it: NINA keeps the
// plugin's settings under this GUID.
[assembly: Guid("6f1c2b8e-4d0a-4e5b-9a63-2b7c0d9e5a41")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

// [MANDATORY] The name NINA shows — and the name Bortle looks for in
// /application/plugins before it puts "Finish Bortle session" into a sequence
// (services/planner/nina_sequence.py, BORTLE_PLUGIN_NAME).
[assembly: AssemblyTitle("Bortle")]
[assembly: AssemblyDescription("Sends every frame NINA saves to your Bortle, which develops the night when the sequence ends.")]
[assembly: AssemblyCompany("Bortle")]
[assembly: AssemblyProduct("Bortle")]
[assembly: AssemblyCopyright("Copyright © 2026 Benjamin Hartwich")]

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("License", "AGPL-3.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.gnu.org/licenses/agpl-3.0.html")]
[assembly: AssemblyMetadata("Repository", "https://github.com/benhartwich/bortle")]
[assembly: AssemblyMetadata("Homepage", "https://bortle.eu")]
[assembly: AssemblyMetadata("Tags", "Bortle,Upload,Processing")]
[assembly: AssemblyMetadata("LongDescription", @"Delivers your frames to Bortle (bortle.eu or your own install) while you image.

- Every frame NINA saves is uploaded in the background. A queue on disk keeps
  them through network loss and restarts; nothing is ever deleted on this PC.
- When the sequence ends, Bortle develops all nights of that target together,
  on the level you chose when planning (step by step, guarded, or automatic).
- The ""Finish Bortle session"" instruction, placed last in the End area after
  parking, waits for the last frames and reports the end. Without it, the end
  of the sequence does the same.

The plugin only delivers data. It controls no equipment and never delays or
fails a sequence; safety stays with NINA.

Create the plugin key in Bortle under Profil → NINA-Plugin.")]

[assembly: ComVisible(false)]
