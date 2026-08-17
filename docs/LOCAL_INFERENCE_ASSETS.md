# Local Inference Asset Integrity

Local inference downloads two kinds of executable-adjacent assets at runtime: a
prebuilt `llama.cpp` server for the detected hardware, and GGUF model weights.
Both run on the user's machine with the user's privileges, so both follow the
same fail-closed rules as the audio assets described in
[AUDIO_MODEL_ASSETS.md](AUDIO_MODEL_ASSETS.md).

## Authoritative catalogs

| Asset | Source of truth | Runtime storage |
| --- | --- | --- |
| llama.cpp Windows builds | `LlamaBackendCatalog.Variants` | `<tray-data>\llama\runtimes\<runtime-key>\` |
| GGUF checkpoints | `LocalModelCatalog.Models` | `<tray-data>\llama\models\<model-id>\` |

The source catalogs hold the download URL, pinned SHA-256, and exact size. Do
not duplicate those values here.

## Rules

1. The `llama.cpp` release tag is pinned in `LlamaBackendCatalog.ReleaseTag`.
   Only the backend *variant* is chosen at runtime, from detected hardware. We
   do not resolve "latest" at runtime: an unpinned binary cannot be
   integrity-checked, and an upstream flag change would silently break the
   per-model run recipes.
2. Every asset must carry a lowercase 64-character SHA-256 and an HTTPS URL.
   An entry missing either is not downloadable
   (`LlamaBackendVariant.IsDownloadable` / `LocalModelInfo.IsDownloadable` are
   false) and the runtime refuses to fetch it.
3. Downloads stage to a temporary file, verify the hash, and only then move or
   extract. A mismatch deletes the partial file and surfaces an error.
4. Archive extraction rejects any entry whose resolved path escapes the
   destination directory.

## Provenance

**GGUF checkpoints.** HuggingFace publishes each LFS object's id, which is the
file's SHA-256. The catalog hashes are those published values, read from
`https://huggingface.co/api/models/<repo>/tree/<path>`. Re-read that endpoint to
re-verify rather than trusting a locally computed hash of a file you already
downloaded through the same channel.

**llama.cpp builds.** GitHub does not publish release-asset hashes, so these
must be computed from the downloaded archive:

```powershell
Get-FileHash .\llama-<tag>-bin-win-cuda-12.4-x64.zip -Algorithm SHA256
```

Record the release tag, the date, and who verified it in the change description.

**Current pinning.** Release `b10472`, all nine Windows assets, verified
2026-08-17. Each archive was downloaded from the release URL, its byte length
cross-checked against the size the GitHub releases API reports for that asset,
and its SHA-256 computed from the downloaded bytes. Both the hash and the size
are recorded in `LlamaBackendCatalog`, so a future re-verification that produces
a different length fails before the hash comparison.

Note the limit of that check: the API size and the archive come from the same
origin, so this establishes that the bytes we hashed are the bytes GitHub serves
for that release, not that the release itself is authentic. Independent
provenance for upstream binaries would require a signed upstream manifest, which
llama.cpp does not currently publish.

## Custom local builds

A user may point the app at their own `llama-server.exe` via the custom runtime
path setting. That path bypasses the catalog and the hash check entirely, by
design: the binary is the user's own. The UI must show an explicit "custom build,
not verified" state whenever it is in use, so the bypass is never silent.

## Bumping the pinned release

1. Download every Windows asset listed in `LlamaBackendCatalog.Variants` for the
   new tag.
2. Compute and record each SHA-256.
3. Update `ReleaseTag` and all hashes in one commit.
4. Re-verify the run recipes in `LocalModelCatalog` still parse against the new
   build. Speculative-decoding flags such as `--spec-type` are the ones most
   likely to change.
5. Run `dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --filter Inference`.
6. Launch one real model end to end and confirm a completion.

Re-verify every shipped asset hash before each public release and record the
evidence for release review.
