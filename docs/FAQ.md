# FAQ

**Is OpenReflect compatible with Macrium `.mrimg` files?**
No — deliberately. `.mrimg` is proprietary; OpenReflect uses its own open, fully documented
format (`.orf`, docs/FORMAT.md). OpenReflect is a functional equivalent, not a file-format clone.

**Can I restore to a physical disk?**
Not in 0.1.0 — file and mounted-VHD targets only. Restore-to-disk ships with the 0.2 offline
(WinPE-style) workflow, where the gates can run against a non-running system
(docs/RISKS.md documents why: an early internal test wrote to the live boot disk; the hard
device-target lockout is the direct result).

**How secure is the encryption?**
AES-256-GCM per region with PBKDF2-HMAC-SHA256 (default 600,000 iterations, configurable
100k–5M). Nonces are unique per region (4-byte random prefix + region counter). Passwords are
taken from an environment variable or prompt — never stored in job files or logs. Losing the
password means losing the image.

**What happens if a backup is interrupted?**
The image keeps a dirty-shutdown flag and no footer. OpenReflect detects it, refuses to build
chains on top of it, and can resume it (`ResumeAsync` — tested in CoreTests.S23).

**How do incrementals find their parent?**
Explicitly via `--parent` (CLI) / the GUI field; scheduled jobs pick the newest matching image in
the destination folder. Differentials need the chain root.

**Does it image live Windows volumes?**
v0.1 reads files, mounted VHDs and idle volumes directly. Real VSS snapshots arrive in 0.2 — the
provider seam (ISnapshotProvider) is already in place and tested.

**Why deflate and not zstd?**
Codec is self-describing per region (FORMAT.md §7), so zstd drops in for 0.2 without a format
change. Deflate is built into .NET — zero dependencies for the 0.1 core.

**What are the machine requirements?**
Windows 10/11 x64, .NET 10 runtime (or use the self-contained publish), and for builds the
pinned scoop SDK via `build.ps1`.
