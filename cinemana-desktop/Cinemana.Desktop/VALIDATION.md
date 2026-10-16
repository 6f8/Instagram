# Validation — 0.4.0 — 2026-10-15

- Final Release publish succeeded with zero compiler warnings and zero errors.
- 64 automated checks passed: 53 local checks and 11 live-service checks.
- Download planner: every episode's own qualities, independent best quality, explicit missing-quality notices, cancellation, Windows-safe names and destination containment.
- Transfers: byte-for-byte completion, no overwrite of existing files, no repeated download of a known complete file, Range and If-Range resume, restart when the server ignores Range, invalid range/changed ETag rejection, truncated-response recovery, complete-partial 416 recovery, non-video rejection, cancellation and pause mid-stream with successful byte-exact resume.
- Queue recovery: running/queued jobs restore paused and wait for user action. Signed video query parameters are not persisted in transfer metadata.
- Live: complete multi-season episode list returned 62 episodes across 5 seasons. A bounded 1024-byte CDN download returned HTTP 206 with the correct range and video MIME type. No full movie or series was downloaded during verification.
- WPF inspected: download buttons in the sidebar and title details; destination selector; all-quality dropdown; film preview generated six files without starting any transfer. Native playback remains independent of website pages.
- WPF complete-series all-quality preview: Breaking Bad, 62 episodes, 5 seasons, 372 files. The start button remained a user action; the batch was not started during testing.
- Not exhaustive: downloading every real title/quality end to end, all storage devices, all server failure modes and HLS remuxing. HLS playlists are intentionally excluded from direct-file downloads.

- Installed 0.4.0 for the current user; verified the installed DLL matches the published DLL and the Desktop shortcut points to 0.4.0.
