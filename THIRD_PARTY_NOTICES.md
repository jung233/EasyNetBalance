# Third-party notices

EasyNetBalance bundles third-party software. The release installer includes the notices and license texts described below. The release workflow records the exact upstream source revision, patch digest, build details, and hashes in `MIHOMO-SOURCE.txt`; the corresponding patched Mihomo source archive, which contains the patch, is attached to the same GitHub release as the installer.

## Mihomo network core

EasyNetBalance builds and redistributes a modified Windows x64 Mihomo core from the [MetaCubeX/mihomo source repository](https://github.com/MetaCubeX/mihomo), pinned to tag `v1.19.31` and commit `ab405bad5beeeac8b003bb01f60f134f6df54471`. The source tree's root `LICENSE` identifies the GNU General Public License version 3 (GPL-3.0). EasyNetBalance's tracked patch is applied to that source before compilation; the patch and complete vendored source are included in the release source archive, and release provenance records the source revision, patch digest, Go toolchain, build command, and binary/source hashes. The binary is named `mihomo.exe` in the installer and is not presented as an official or endorsed Mihomo product.

Each release installer includes the upstream `LICENSE`, the full GPL version 3 text, a source/provenance record, a Go module inventory, and dependency license texts. The build collects license files from each Go module and vendors copies with the corresponding source. If a module has no verifiable license file, CI writes `MIHOMO-GO-LICENSE-GAPS.txt` and fails the package before publication. The matching patched source archive is distributed on the same GitHub release so recipients can obtain the corresponding source under GPL-3.0. See the [pinned Mihomo source](https://github.com/MetaCubeX/mihomo/tree/ab405bad5beeeac8b003bb01f60f134f6df54471) and [GPL-3.0 license](https://www.gnu.org/licenses/gpl-3.0.html).

## Tauri and application dependencies

The desktop application uses [Tauri](https://github.com/tauri-apps/tauri), whose Rust crate is dual-licensed under MIT or Apache-2.0. The installer includes the license files from the resolved Tauri crate and the Rust dependency license inventory. JavaScript package license files and the dependency inventory are included with the installer as well. Each dependency retains its own license; the bundled license directory is the authoritative notice for the exact versions used in that release.

EasyNetBalance does not use source code, binaries, or assets from Clash Verge Rev.
