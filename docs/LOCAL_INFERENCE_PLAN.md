# Local Inference via llama.cpp: Implementation Plan

Working plan for running models locally on the Windows host. Status is tracked
inline; update it as phases land.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Hardware probe, backend selection, model recommender | Landed |
| 2 | Runtime and GGUF download managers | Not started (catalog hashes pinned, unblocked) |
| 3 | Server process and settings UI | Not started |
| 4 | Gateway provider registration | Not started (blocked on live schema) |
| 5 | Optional `localinference.status` node capability | Not started |

## Context

Every model the Companion can talk to is owned by the gateway: the tray calls
`models.list` and renders whatever the gateway reports
(`src/OpenClaw.Chat/ChatModelChoice.cs`). There is no way to run a model on the
user's own machine, so a workstation with a large NVIDIA GPU sits idle while
every turn goes to a remote provider.

This work makes the Windows app detect the host's hardware (NVIDIA GPU plus
VRAM, system RAM, CPU architecture), download a matching prebuilt `llama.cpp`
server, download a GGUF checkpoint the machine can hold, launch `llama-server`
with the checkpoint's tuned run recipe, and register the resulting
OpenAI-compatible endpoint with the gateway so the models appear in the normal
chat model picker.

Two existing subsystems are reused rather than reinvented: the hash-pinned,
single-flight, fail-closed asset downloader
(`src/OpenClaw.Shared/Audio/WhisperModelManager.cs`, `SingleFlightDownload.cs`,
and `PiperVoiceManager.cs` for the archive case) and the safe whole-config patch
builder (`src/OpenClaw.Shared/ChannelConfigPatchBuilder.cs`).

Decisions taken with the maintainer:

- The endpoint is auto-registered in the gateway config so models appear in the
  existing picker.
- Backends: CUDA (x64 and arm64), Vulkan, CPU. No ROCm, SYCL, OpenVINO, or
  OpenCL-Adreno.
- llama.cpp binaries come from a pinned release tag with pinned per-asset
  SHA-256; only the variant is chosen at runtime. A user-supplied custom local
  build is also supported and bypasses download entirely.
- DeepSeek V4 Flash is in the catalog but gated: never auto-recommended, and its
  roughly 155 GB download is confirmed explicitly.

## Architecture

New pure code lives in `src/OpenClaw.Shared/Inference/`, with a thin tray-side
service and settings page on top. Nothing is added to `App.xaml.cs` or
`ConnectionPage.xaml.cs` beyond construction and wiring; both are active
god-file reduction targets per `ARCHITECTURE.md`.

```
HardwareProbe ──► BackendSelector ──► LlamaRuntimeManager (download + extract)
      │                                        │
      └──────► ModelRecommender ──► GgufModelManager (download GGUF shards)
                                                │
                                     LlamaServerProcess (spawn, health, shutdown)
                                                │
                                     LocalInferenceProviderRegistrar (config.patch)
```

## Phase 1: probe, selection, recommender (landed)

Pure, testable, no UI and no network.

| File | Role |
| --- | --- |
| `Inference/HostHardwareInfo.cs` | Hardware snapshot. `TotalNvidiaVramBytes` sums across adapters because llama.cpp's default `--split-mode layer` spreads a model over every visible device. |
| `Inference/PhysicalMemoryProbe.cs` | Single owner of the `GlobalMemoryStatusEx` interop, lifted out of `DeviceStatusProvider`, which now calls it. |
| `Inference/NvidiaSmiParser.cs` | Pure parser for the `nvidia-smi` CSV query and version banner, plus vendor classification. |
| `Inference/HardwareProbe.cs` | Orchestrates detection, caches, exposes `RefreshAsync`. |
| `Inference/LlamaBackendCatalog.cs` | Pinned release tag and the six Windows variants. |
| `Inference/BackendSelector.cs` | Hardware to backend plan with an ordered fallback chain. |
| `Inference/LocalModelCatalog.cs` | The three models with sizes, hashes, and run recipes. |
| `Inference/ModelRecommender.cs` | Pure fit assessment and recommendation. |

Decisions worth preserving:

- **The probe never throws.** Every source is best-effort and every failure
  degrades to null or empty, so an unclassifiable host lands on the CPU backend
  instead of breaking the settings page.
- **Unknown CUDA version degrades to the CUDA 12 build.** A CUDA 12 runtime
  works on newer drivers; a CUDA 13 runtime does not work on older ones, so the
  unknown case must degrade downward.
- **`Win32_VideoController.AdapterRAM` is never used as a VRAM number.** It is a
  32-bit field that wraps above 4 GB, which is exactly the range that matters.
  Only `nvidia-smi` populates a size; the platform fallback supplies vendor and
  name only. That fallback is injected as a delegate from the tray because
  reading the display-adapter registry needs a Windows-targeted TFM and
  `OpenClaw.Shared` targets plain `net10.0`.
- **Vulkan is preferred only when `vulkan-1.dll` is present.** Shipping a Vulkan
  build to a host without a loader turns "no acceleration" into "the server will
  not start", which is strictly worse.
- **An unclassified adapter is not treated as Vulkan-capable.** Seeing an
  adapter we cannot name is not evidence that a Vulkan build will drive it.

### Backend selection matrix

Pinned release: `b10472`.

| Condition | Assets |
| --- | --- |
| NVIDIA, x64, CUDA 13 or newer | `llama-b10472-bin-win-cuda-13.3-x64.zip` plus `cudart-llama-bin-win-cuda-13.3-x64.zip` |
| NVIDIA, x64, CUDA 12.x or unknown | `llama-b10472-bin-win-cuda-12.4-x64.zip` plus `cudart-llama-bin-win-cuda-12.4-x64.zip` |
| NVIDIA, arm64 | `llama-b10472-bin-win-cuda-13.4-arm64.zip` plus `cudart-llama-bin-win-cuda-13.4-arm64.zip` |
| Non-NVIDIA adapter, x64, Vulkan loader present | `llama-b10472-bin-win-vulkan-x64.zip` |
| Otherwise | `llama-b10472-bin-win-cpu-x64.zip` or `llama-b10472-bin-win-cpu-arm64.zip` |

CUDA variants need two archives extracted into the same directory. A missing
`cudart` produces a missing-DLL startup failure rather than anything
diagnostic, so the pairing is asserted structurally in tests.

### Model catalog

| Model | Size | Status |
| --- | --- | --- |
| Qwen3.6-35B-A3B UD-Q4_K_M | 22,663,387,424 bytes, single file | Default recommendation |
| Qwen3.8-27B | Unpublished | Entry present with no shards, so not downloadable |
| DeepSeek V4 Flash 0731 UD-Q4_K_XL | About 155 GB across 5 shards | Gated, never auto-recommended |

Shard hashes are the HuggingFace LFS object ids, which are the files' SHA-256.
Run recipes are stored as structured argument lists and deliberately exclude
`-m`, `--host`, and `--port`, which the process launcher owns; a test enforces
that separation.

## Phase 2: download managers (blocked on hash pinning)

`Inference/LlamaRuntimeManager.cs` and `Inference/GgufModelManager.cs`, modelled
on `PiperVoiceManager` and `WhisperModelManager`:

- Per-key single flight via the existing `SingleFlightDownload.RunAsync`.
- Stage to a `.tmp` file, verify SHA-256, and only then move or extract.
- Delete the partial file on any failure.
- Zip extraction rejects entries whose resolved path escapes the destination.
- Runtimes land in `<tray-data>\llama\runtimes\<runtime-key>\`, models in
  `<tray-data>\llama\models\<model-id>\`.
- GGUF downloads need aggregate cross-shard progress, a free-disk-space
  precheck, and `Range`-based resume. Restarting a 50 GB shard from zero after a
  dropped connection is not acceptable.

**Custom local build.** When `LocalInferenceCustomRuntimePath` is set it wins
over the catalog: validate the path, skip download and hashing entirely, and
show an explicit "custom build, not verified" state so the bypass is never
silent.

GitHub does not publish release-asset hashes, so all nine `b10472` archives were
downloaded, size-checked against the releases API, and hashed on 2026-08-17.
Those values are pinned in `LlamaBackendCatalog` and guarded by
`AssetHashPinningTests`, so this phase is unblocked. See
`LOCAL_INFERENCE_ASSETS.md` for the provenance and its limits.

## Phase 3: server process and UI

`OpenClawTray.Services.LlamaServerProcess` spawns `llama-server.exe` with
`--port <p> --host 127.0.0.1 -m <model>` plus the recipe args.

- Port comes from a free-port scan; reuse `PortDiagnosticsService` and
  `WindowsTcpListenerSnapshot` for conflict reporting.
- Bind to `127.0.0.1` by default. Binding beyond loopback exposes an
  unauthenticated inference endpoint to the LAN and must be an explicit, warned
  opt-in.
- Health: poll `GET /health` until ready or timeout, and surface the stderr tail
  on failure. A recipe flag an older build does not know fails here, and the
  user needs to see why.
- Assign the child to a Win32 job object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so a tray crash cannot orphan a process
  holding tens of gigabytes of VRAM. Graceful stop via `AppShutdownCoordinator`.

`Pages/LocalInferencePage.xaml{,.cs}` follows `VoiceSettingsPage`, the closest
analogue: catalog combo, download button, progress bar driven by
`IProgress<(long downloaded, long total)>`, page-held `CancellationTokenSource`,
status text from resources. Register it in `Presentation/HubPageRegistry.cs`
(enum value, tag string, type map) and wire `Initialize` in
`Windows/HubWindow.xaml.cs`. All strings go through `LocalizationHelper` and
`Strings/en-us/Resources.resw`, with no em dashes per `AGENTS.md`.

Settings added to `SettingsData`: `LocalInferenceEnabled`,
`LocalInferenceModelId`, `LocalInferenceBackendOverride`,
`LocalInferenceCustomRuntimePath`, `LocalInferencePort`,
`LocalInferenceAutoStart`, `LocalInferenceRegisterWithGateway`,
`LocalInferenceBindBeyondLoopback`. Change effects route through
`SettingsChangeCoordinator` and `SettingsChangeEffects`.

## Phase 4: gateway registration (blocked on live schema)

Once the server is healthy, patch the gateway config to add an
OpenAI-compatible provider pointing at it, via
`IOperatorGatewayClient.PatchConfigDetailedAsync(fullConfig, baseHash)`.

Reuse `ChannelConfigPatchBuilder`'s `SetNestedValue` and
`FindRedactionSentinel`, which are `internal static` in the same assembly, and
apply the same **redaction-sentinel safety rail**: if the cached config holds
`[REDACTED]` or `***` outside the paths being written, refuse the patch and
route the user to the Config page. Silently clobbering real API keys with
redaction placeholders while enabling local inference would be a severe
regression.

**Blocker.** This repo does not contain the gateway's config schema;
`ConfigPage` fetches it at runtime from `config.get`. The dot-path and shape for
registering an OpenAI-compatible provider is therefore not verifiable from this
checkout. First step of this phase: connect to a real gateway, inspect the
schema the Config page renders, and write the builder against the real shape. Do
not guess it.

**WSL reachability.** When the gateway runs in WSL, `127.0.0.1` inside the
distro is not the Windows host. `WINDOWS_NODE_ARCHITECTURE.md` records this:
NAT-mode WSL2 reaches the host via `$(hostname).local` or
`host.docker.internal`, while mirrored networking can use `localhost`. Use
`GatewayHostAccessClassifier` and `GatewayRecord` to detect a WSL-managed
gateway and resolve the base URL accordingly. NAT mode requires binding beyond
loopback, which must be an explicit consent step.

## Phase 5: optional node capability

A `localinference.status` command. Per `AGENTS.md`, any new Windows node call
must be registered in the capability registry, added to
`McpToolBridge.CommandDescriptions`, documented in
`src/OpenClaw.WinNode.Cli/skill.md`, and covered by
`OpenClaw.WinNode.Cli.Tests`. That is a real slice of work, not a footnote.

## Docs

- `LOCAL_INFERENCE_ASSETS.md` covers the fail-closed download rules, hash
  provenance, and the release-bump procedure. Added.
- `ARCHITECTURE.md` needs ownership rows for the new services once they exist.
- A user-facing `LOCAL_INFERENCE.md` should cover the hardware matrix, the model
  catalog, the custom-build escape hatch, and the WSL caveat.

## Verification

Unit tests in `tests/OpenClaw.Shared.Tests/Inference/`:

- Backend selection across architecture, vendor, and CUDA-version tuples, plus
  the CUDA-and-cudart pairing invariant.
- Model fit across host shapes: large-VRAM workstation, small GPU with large
  RAM, no GPU, small laptop, undetectable hardware; DeepSeek never
  auto-selected; the unpublished checkpoint reported as pending.
- Catalog integrity: HTTPS URLs, pinned lowercase 64-character SHA-256 on
  everything downloadable, unique path-safe ids and runtime keys, shard
  ordering, and the reserved-argument separation.
- nvidia-smi parsing against captured real output, including the multi-GPU,
  `[N/A]` memory, and missing-banner cases.

Still to add in later phases: download managers against an `HttpMessageHandler`
fake (corrupt body rejected, `.tmp` deleted, nothing at the final path,
concurrent callers coalesced), zip traversal rejection, and the provider patch
builder preserving unrelated config while blocking on a redaction sentinel.

Required repo validation per `AGENTS.md`: `./build.ps1`, then the Shared and
Tray test projects. In this linked worktree, set `OPENCLAW_REPO_ROOT` first or
`ReadmeValidationTests` fails on repo-root discovery.

Real behavior proof, which CI cannot establish:

1. On an NVIDIA host, screenshot the detected hardware and confirm it matches
   `nvidia-smi`.
2. Download the CUDA runtime, confirm hash verification passes, and run
   `llama-server.exe --version` from the extracted directory.
3. Download Qwen3.6-35B-A3B, start the server, and prove readiness with
   `GET /health` and a real `POST /v1/chat/completions`.
4. Corrupt a downloaded GGUF and confirm the app refuses it, deletes the partial
   file, and shows a clear error.
5. Kill the tray and confirm `llama-server.exe` dies with it.
6. With registration enabled, confirm the local models appear in the chat model
   picker and that a turn reaches the local server.
7. Cover both mirrored and NAT WSL networking, or record which was not covered.

Not verifiable in the current environment and explicitly deferred: the gateway
provider config schema, the Vulkan and arm64 CUDA paths, and the DeepSeek path.
State these as blockers in the PR rather than implying coverage.
