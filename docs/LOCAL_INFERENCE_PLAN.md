# Local Inference via llama.cpp: Implementation Plan

Working plan for running models locally on the Windows host. Status is tracked
inline; update it as phases land.

| Phase | Scope | Status |
| --- | --- | --- |
| 1 | Hardware probe, backend selection, model recommender | Landed |
| 2 | Runtime and GGUF download managers | Landed |
| 3 | Server process and settings UI | Landed |
| 4 | Gateway provider registration | Not started |
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

## Phase 2: download managers (landed)

| File | Role |
| --- | --- |
| `Inference/VerifiedFileDownloader.cs` | Fetch to `.part`, verify SHA-256, then move. Shared by both managers. |
| `Inference/SafeZipExtractor.cs` | Zip extraction with an explicit path-traversal guard. |
| `Inference/LlamaRuntimeManager.cs` | Install and resolve a backend variant, or a custom build. |
| `Inference/GgufModelManager.cs` | Download and manage multi-shard checkpoints. |

Decisions worth preserving:

- **Nothing unverified reaches a final path.** A missing pinned hash fails before
  any network traffic. A mismatch, a length disagreement, or a truncated
  response deletes the partial file and throws. The error never echoes the
  computed hash, which would be a confirmation oracle.
- **Resume is opt-in per request.** GGUF shards run to tens of gigabytes, so a
  dropped connection must be recoverable; small archives just restart. A server
  that ignores `Range` and answers 200 triggers a clean restart rather than
  appending a full body onto an existing prefix and corrupting the file. A
  partial file at or past the expected size is discarded, since it is the
  residue of an attempt that already failed verification and resuming from its
  end would loop forever.
- **A runtime directory is only trusted with its completion marker.** A CUDA
  variant has two archives. An interrupted install can leave a directory holding
  llama-server.exe but none of its CUDA DLLs, which would look installed and
  then fail at launch with a missing-DLL error. The marker is written only after
  every archive extracted and the executable was found; without it the directory
  is torn down and rebuilt.
- **Archives are deleted as they extract.** A CUDA pair is close to 800 MB and
  keeping both would double peak disk use for no benefit.
- **The server executable is located recursively.** Upstream has moved between a
  flat layout and a build/bin layout across releases.
- **Free space is checked before starting, counting only missing shards**, so
  resuming a mostly-complete model is not blocked by the full model size.
  Unknown free space proceeds rather than refusing.

Both managers keep the existing per-key single-flight gate
(`SingleFlightDownload.RunAsync`). Runtimes land under
`llama/runtimes/<runtime-key>/` and models under `llama/models/<model-id>/` in
the tray data directory, with upstream shard names preserved because llama.cpp
discovers the remaining shards by name from the first one.

**Custom local build.** When `LocalInferenceCustomRuntimePath` is set it wins
over the catalog: validate the path, skip download and hashing entirely, and
show an explicit "custom build, not verified" state so the bypass is never
silent.

All nine `b10472` archives were downloaded, size-checked against the releases
API, and hashed on 2026-08-17; those values are pinned in `LlamaBackendCatalog`
and guarded by `AssetHashPinningTests`. See `LOCAL_INFERENCE_ASSETS.md` for the
provenance and its limits.

## Phase 3: server process and UI (landed)

| File | Role |
| --- | --- |
| `Inference/LlamaServerArguments.cs` | Pure argument and URL construction. |
| `Inference/ProcessJobObject.cs` | Kill-on-close Win32 job object. |
| `Inference/LlamaServerProcess.cs` | Launch, health poll, stderr tail, stop. |
| `Inference/LocalInferenceService.cs` | Sequences probe, selector, runtime, model, server. |
| `Pages/LocalInferencePage.xaml{,.cs}` | Settings surface. |
| `Services/DisplayAdapterEnumerator.cs` | Registry adapter fallback injected into the probe. |

The server host lives in `OpenClaw.Shared` rather than the tray project because
it has no WinUI dependency, which keeps the whole flow unit testable.

Decisions worth preserving:

- **The child runs inside a kill-on-close job object.** Without it a tray crash
  leaves llama-server holding tens of gigabytes of VRAM with no UI left to stop
  it, and the next launch fails on a port conflict or an allocation error. A test
  proves an assigned process really dies when the job handle closes. Job creation
  failure is logged and tolerated: losing the safety net beats refusing to run.
- **The health poll checks for child exit each tick.** A rejected recipe flag
  makes llama-server exit immediately; without that check the user would wait out
  the full ten-minute ready timeout for an error that was known in a second.
- **stderr is tailed, stdout is drained but discarded.** The tail is the only
  place a bad flag or a CUDA initialization failure is explained. stdout is
  llama-server's request logging, which would put prompt content into our
  diagnostics, so it is read only to keep the pipe from blocking the child.
- **`-m`, `--host`, and `--port` are launcher-owned.** A recipe that sets one is
  rejected rather than silently duplicated or overridden.
- **Loopback unless explicitly widened.** Binding all interfaces exposes an
  unauthenticated inference endpoint to the LAN, so it is a separate opt-in with
  its own warning, and the health poll still targets loopback.
- **Start never begins a download.** Kicking off a multi-hour transfer from a
  Start button would be a surprising amount of work to trigger by accident.
- **Confirmation is inline, not a `ContentDialog`.** `REACTOR_DIALOG_001` keeps
  new surfaces off imperative dialogs; the per-file suppressions in
  `.editorconfig` are deliberately not extended. An in-page bar also keeps the
  size being confirmed on screen.
- **Progress repaints are throttled to 150 ms.** A 22 GB model produces roughly
  280,000 progress callbacks; repainting on each saturates the dispatcher.

Settings live on `SettingsData` with `SettingsManager` passthroughs:
`LocalInferenceEnabled`, `LocalInferenceModelId`,
`LocalInferenceBackendOverride`, `LocalInferenceCustomRuntimePath`,
`LocalInferencePort`, `LocalInferenceAutoStart`,
`LocalInferenceRegisterWithGateway`, `LocalInferenceBindBeyondLoopback`.

The page is registered in `Presentation/HubPageRegistry.cs` and initialized from
`Windows/HubWindow.xaml.cs`. `App` builds the service lazily and the shutdown
coordinator stops the server so the GPU is released before exit; the job object
remains the crash backstop, not a substitute for an orderly stop. Strings are
seeded English-only across all five locales using the repo's
deferred-translation pattern and registered in `LocalizationValidationTests`.

## Phase 4: gateway registration

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

**Schema, confirmed against the gateway source (`../openclaw`).** This repo
does not vendor the gateway's config schema, but it lives in the gateway
checkout and is no longer a guess:

- `src/config/zod-schema.core.ts` defines `ModelProviderSchema` (the shape of
  one entry under `models.providers.<providerId>`) and `ModelProvidersSchema`
  (the `Record<providerId, ModelProviderSchema>` map), plus a `superRefine`
  that requires `baseUrl` and a non-empty `models[]` array for any provider id
  outside `BUILT_IN_MODEL_PROVIDER_OVERLAY_IDS` (`openai`, `ollama`,
  `lmstudio`, `vllm`, etc.). Our registered id will not be a built-in, so both
  fields are mandatory.
- `docs/gateway/local-models.md` and `docs/gateway/local-model-services.md` in
  the gateway repo document this exact scenario (a local OpenAI-compatible
  server such as llama-server) with worked examples.

Patch target is `models.providers.<providerId>`, merged (`mode: "merge"`) via
`config.patch`:

```json5
{
  "models": {
    "mode": "merge",
    "providers": {
      "llama-local": {
        "baseUrl": "http://127.0.0.1:8080/v1",
        "apiKey": "sk-local",
        "api": "openai-completions",
        "timeoutSeconds": 300,
        "models": [
          {
            "id": "my-local-model",
            "name": "My Local Model",
            "reasoning": false,
            "input": ["text"],
            "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
            "contextWindow": 32768,
            "maxTokens": 4096
          }
        ]
      }
    }
  }
}
```

Notes for the builder:

- `api: "openai-completions"` is the right value for a plain
  OpenAI-compatible `/v1/chat/completions` server like llama-server; it is
  also the default when `api` is omitted on a custom provider with a
  `baseUrl`.
- `apiKey` accepts any non-empty string for a loopback/LAN `baseUrl`; there is
  no real secret to protect for a local server, but the field is still
  registered as sensitive (`SecretInputSchema.optional().register(sensitive)`),
  so it round-trips through `config.get` as the sentinel
  `__OPENCLAW_REDACTED__`. This confirms the redaction-sentinel safety rail
  already planned above: resending the config unchanged is safe because
  `restoreRedactedValues(...)` on the gateway swaps the sentinel back before
  merge/validate, but our own outgoing patch must not write a sentinel into a
  field it did not read one from.
- `models[].id` is provider-local; a model is addressed elsewhere in config
  (e.g. `agents.defaults.model.primary`) as `<providerId>/<modelId>`. The
  provider id and the model id here should be generated deterministically from
  our runtime/model identity so re-registration is idempotent.
- `config.patch` requires a `baseHash` from a prior `config.get` (compare-and-
  swap), matching `PatchConfigDetailedAsync(fullConfig, baseHash)`.
- Optional and not required for the first cut: `localService` (`command`,
  `args`, `healthUrl`, `readyTimeoutMs`, `idleStopMs`) lets the gateway itself
  spawn and health-check a local server. We already own process lifecycle via
  `LlamaServerProcess`, so registration should populate `baseUrl` +
  `models[]` only and leave `localService` unset, to avoid a double process
  owner.

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

Phase 2 adds, against an in-memory `HttpMessageHandler` fake: tampered bodies
and length disagreements rejected with no residue, resume via `Range`, clean
restart when a server ignores `Range`, truncated-then-retried downloads,
monotonic aggregate progress, traversal and sibling-prefix rejection in zip
extraction, interrupted-install rebuild, and the free-space precheck.

Still to add: the provider patch builder preserving unrelated config while
blocking on a redaction sentinel.

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

### Real-behavior proof captured 2026-08-19

Run on the development host (GB10 DGX Spark: ARM64 Windows, RTX Spark N1X,
24512 MiB VRAM, driver 616.29):

| Step | Result |
| --- | --- |
| Hardware probe | `Arm64`, CUDA 13, 25,702,694,912 bytes VRAM, one GPU (NPU correctly excluded) |
| Backend selected | `b10472-cuda13-arm64`, both llama.cpp and cudart archives |
| Runtime install | 293 MB downloaded, both SHA-256 verified, 43 files extracted, 15 s |
| `llama-server --version` | `version: 0.1.1-dev (build 10472, commit 60eeeb608)` |
| Model download | Qwen3.6-35B-A3B UD-Q4_K_M, 22,663,387,424 bytes, SHA-256 verified, 11.9 min at 35 MB/s |
| Server start | Ready in 78 s |
| Completion | `POST /v1/chat/completions` returned HTTP 200 in 4.0 s; "What is 2+2?" answered `4` |
| Speculative decoding | `--spec-type draft-mtp` active: draft acceptance 0.88 (45/51), mean length 3.65 |
| Throughput | 40.4 tokens/second eval |
| Stop | `Starting -> Ready -> Stopped`, no spurious failure |
| Idempotence | A second run skipped both downloads and started in 78 s |

Two defects were found only by this run and are fixed: the downloader deleted
the partial file on a dropped connection (losing 10.6 GB of good bytes), and a
deliberate stop was reported as an unexpected crash.

Still not covered: the Vulkan and x64 CUDA paths (need that hardware), the
DeepSeek path (needs ~160 GB), and gateway registration (Phase 4).

Not verifiable in the current environment and explicitly deferred: exercising
the registration patch against a real running gateway (the schema itself is
now confirmed from the gateway source, see Phase 4, but end-to-end proof
still needs a live instance), the Vulkan and x64 CUDA paths, and the
DeepSeek path. The arm64 CUDA path is no longer deferred: it is the one proven
end to end above. State the rest as blockers in the PR rather than implying
coverage.
