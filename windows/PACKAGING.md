# Windows GUI packaging and verification

GUI distributions support Windows x64 and ARM64 and the MQVPN 0.19.x
configuration interface. Both the GUI and updater are self-contained .NET
single-file applications, including the native SQLite library. Wintun is
bundled separately with each kernel. Native dependencies are checked from
the PE import and delay-import tables; a statically linked core does not
require `xquic.dll`.

CI stamps its actual Windows cores with their checked-out commit's snapshot
identity before packaging. Native x64 and ARM64 smoke checks require that
identity and the core/callback ABI metadata; final ZIP verification exercises
the updater with those same executables.

Kernel updates also accept `snapshot+YYYYMMDDTHHMMSSZ.gSHA` builds when
`mqvpn --version` explicitly reports core 0.19.x and callback ABI 3 on its
second line. A snapshot label alone is insufficient to establish compatibility.

The main CI builds each required native core once and uploads its executable,
source/submodule SHAs, file hashes and dependency licenses. The reusable
`windows-gui.yml` consumes those artifacts from the same run. It has no
independent push, PR or manual trigger. GUI-relevant core, build, submodule
and Windows changes select both native architectures; a documentation-only
change does not independently select GUI packaging.

The packaging script now requires an explicit core artifact directory and a
dependency lock. The previous `CoreMqvpnExe`/`CoreMqvpnZip` options and automatic
upstream-release fallback have been removed because they cannot establish
the required source pairing. Run these commands with PowerShell 7.2 or later
from a clean checkout matching the core artifact:

```powershell
./scripts/resolve_windows_gui_deps.ps1 -LockFile build/dependency-lock.json
./scripts/package_windows_gui.ps1 -Arch x64 `
  -CoreDirectory build/core-artifact `
  -DependencyLock build/dependency-lock.json
./scripts/verify_windows_gui_package.ps1 -Arch x64 `
  -SourceSha (git rev-parse HEAD) `
  -ZipPath dist/mqvpn_windows_gui_x64.zip `
  -ReportDirectory build/package-reports
```

Use `arm64` on a native ARM64 machine for the corresponding verification.
Choose fresh output paths for each package; the script never recursively
clears a caller-supplied output directory. The main CI selects static MSVC
runtime linkage consistently for BoringSSL, XQUIC and MQVPN and uses separate
cache keys for architecture and build flags. ARM64 follows the existing
release build's `OPENSSL_NO_ASM` choice; this is not a performance claim.

The lock fixes Wintun's version/hash and resolves Xray and GeoData once for
both architectures. Downloads do not carry the GitHub API token. The package
manifest records the exact inputs and file hashes. These hashes establish
consistency and provenance within the build; they are not release signatures.
Required third-party license materials travel with the package.

Final ZIP verification unpacks the archive, validates its manifest, hashes,
architecture, imports and licenses, then starts the actual self-contained
GUI in diagnostic mode. It opens/reopens SQLite, preserves Unicode config
content and runs actual core update installations in an isolated temporary
runtime. The shipped updater also installs and verifies the final ZIP in a
separate directory. Core version checks do not create a VPN or change routes.
Reports contain the source SHA, native architecture and loaded SQLite path.

Regular behavior checks call the production persistence, routing semantics,
process host and update transaction. File-sharing failures check rollback
using isolated synthetic data. Screenshot output is capture material without
a visual comparison baseline. Known security fixes are reviewed statically;
the routine GUI suite does not construct exploit archives or credential-leak
probes.

GUI updates validate the complete package before stopping cores and replacing
files. A failed replacement restores prior files and retains recovery
information. Configuration files are excluded from distributions and remain
user-owned during updates. Kernel updates require an exact component and
architecture match, preserve existing GeoData, and reject configuration
interfaces outside 0.19.x. Legacy GUI update archives without a package
manifest are intentionally rejected.

Offline checks do not establish real-server connectivity, WFP behavior,
system route switching or reconnect correctness on a live network. Those
require a designated Windows test environment. A successful cross-publish is
not native ARM64 execution evidence.
