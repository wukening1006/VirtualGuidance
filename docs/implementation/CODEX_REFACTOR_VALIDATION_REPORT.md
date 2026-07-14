# Codex Refactor Validation Report

> Date: 2026-07-14
> Branch: `refactor`
> Scope: `docs/implementation/CODEX_REFACTOR_EXECUTION_PLAN.md`

## Summary

P1-P11 have been implemented as staged commits on `refactor`.

This report records the validation evidence that can be proven from the local repository, backend tests, Unity Editor checks, Unity EditMode tests, and Play Mode live-backend validation. It does not claim XR device completion. PICO/XR real-device checks remain required for the parts of the plan that explicitly require physical headset validation.

## Latest Atomic Commits

- `cce3bd1 feat(unity): map headset pose through xr adapter`
- `d00fc4f fix(unity): cancel rotation gain on critical reset`
- `a24f4fe feat(unity): complete rotation gain lifecycle`
- `888b75c docs: record live backend playmode validation`
- `658072e test(unity): add live backend validation harness`
- `8f3d9a8 docs: record playmode backend validation limit`
- `358e52b docs: record dto contract validation`
- `cef5009 test(unity): lock guidance dto contract`
- `b89ae2f docs: update backend validation count`
- `658ce16 test(backend): lock guidance response contract`
- `59e1596 test(unity): cover pause reset flow`
- `b251e1c docs: record refactor validation status`
- `978ff2b fix(unity): use world-space debug canvas for xr`
- `eaab48a test(unity): add runtime refactor editmode checks`
- `5055e25 fix(unity): keep experiment logs outside assets`
- `8a7123b feat(unity): add experiment dashboard and logging`
- `0401587 feat(unity): add rotation gain scaffold`
- `81c02b7 feat(unity): add local guidance coordination`
- `063c8f7 refactor(backend): extract guidance pipeline`
- `c960b82 refactor(unity): move exhibit prefab folder`
- `faadfa5 refactor(unity): move scene-referenced scripts`
- `c7d3b98 refactor(unity): move low-reference scripts`
- `6d9e9ea chore: confirm refactor blockers`

## Verified Automatically

### Files and Git

- Worktree clean after validation, except the local `.pytest_cache` permission warning emitted by Git directory enumeration.
- `git diff --check` passed.
- New Unity test files include `.meta` files.
- `DualWorldGallery` is registered in `ProjectSettings/EditorBuildSettings.asset`:
  - `Assets/Scenes/DualWorldGallery.unity`
  - `guid: bcfc8f9e95b3231419b8cf6a7bba9852`
  - `enabled: 1`

### Backend

Command:

```powershell
D:\APP\APP_code\anaconda3\python.exe -m pytest backend/tests -p no:cacheprovider --basetemp Temp/codex-pytest-live-validator-20260714-1258
```

Result:

```text
15 passed, 1 warning
```

The warning is the existing Starlette/httpx deprecation warning from the installed Python environment.

Live backend was also started with the Anaconda Python executable:

```powershell
D:\APP\APP_code\anaconda3\python.exe -m uvicorn backend.app:app --host 127.0.0.1 --port 8000
```

Live checks against `http://127.0.0.1:8000` passed:

- `GET /health`: `status=ok`, `version=0.3.0`, `exhibit_count=8`.
- `POST /api/guidance` with `condition=ours_full`, `physical_space_id=narrow_room`: selected `E3`, returned `turn_left_slowly`, verifier approved.
- `POST /api/guidance` with `condition=B4_scripted_reset`: returned `reset_required`.

### Unity Editor

UnitySkills health checks:

- `debug_check_compilation`: `isCompiling=false`, `isUpdating=false`
- `unity_diagnose`: healthy, `consoleErrorCount=0`, `consoleWarningCount=0`
- `validate_find_missing_scripts(searchInPrefabs=false)`: `totalFound=0`
- `validate_find_missing_scripts(searchInPrefabs=true)`: two known PICO package prefab missing scripts remain:
  - `Packages/com.unity.xr.picoxr/Assets/Resources/Controller/ControllerWithHand/leftHand.prefab`
  - `Packages/com.unity.xr.picoxr/Assets/Resources/Controller/ControllerWithHand/rightHand.prefab`

XR setup diagnostic:

- XRI is installed and detected as major version 2.
- Android XR settings have one configured loader.
- The current `DualWorldGallery` scene has no `XROrigin`, `XRInteractionManager`, or `EventSystem` recognized by XRI.
- `XRTrackingAdapter` now reads the standard `UnityEngine.XR` head node when available and keeps the simulated pose path when headset tracking is disabled or unavailable.

### Unity EditMode Tests

`GuidanceRuntimeEditModeTests` was added under `Assets/Tests/Editor`.

Unity Test Runner result:

```text
Test run completed: 10/10 passed.
```

Covered checks:

- `GuidanceTaskController` advances `E1 -> E3 -> E5 -> Completed`.
- `LocalGuidanceValidator` rejects stale guidance and unsafe forward movement.
- `PauseResetController` clears visible guidance, shows the reset reason, and hides it after resume.
- `MotionDetector` applies start/stop angular-speed hysteresis and preserves signed turn direction.
- `RotationGainController` remains inactive by default, waits for a real turn sample, applies bounded gain, and terminates on stop, reversal, completion, or disable.
- Critical RDW reset immediately clears visible guidance and cancels active/pending rotation gain.
- `XRTrackingAdapter` maps tracking-space X/Z and horizontal head yaw into physical pose while preserving the virtual head transform in snapshots.
- `ExperimentConditionMatrix` covers 6 conditions, 4 tasks, 4 physical spaces, and produces cloned task routes.
- `GuidanceRequest`, `GuidanceResponse`, and `VerifierResult` public field names remain locked to the backend JSON contract.

### Play Mode Smoke

A Play Mode smoke run was executed on `Assets/Scenes/DualWorldGallery.unity`.

Result after `978ff2b`:

- Entered Play Mode successfully.
- Editor state during smoke: `isPlaying=true`, `isPaused=false`, `isCompiling=false`.
- Console during smoke: `logs=8`, `warnings=0`, `errors=0`.
- Exited Play Mode successfully.
- Editor state after smoke: `isPlaying=false`, `isPaused=false`, `isCompiling=false`.

`978ff2b` fixes the previous VR warning by creating the auto-generated `GuidanceDebugCanvas` as a world-space canvas when Unity reports XR/VR as enabled.

### Play Mode Live Backend Automation Note

`658072e` adds an Editor-only validation harness at `Assets/Tests/Editor/GuidanceLiveBackendValidator.cs`. It avoids the unstable PlayMode Test Runner path while still exercising the real scene and runtime components. Run it from:

```text
Tools > VirtualGuidance > Run Live Backend Validation
```

The harness enters Play Mode, uses the scene-installed `AgentClient` against the Anaconda-hosted backend, writes an ignored report to `Logs/Validation/unity_live_backend_validation_latest.json`, and exits Play Mode automatically.

Latest result on 2026-07-14:

```text
passed=true
scene=Assets/Scenes/DualWorldGallery.unity
liveSelectedTarget=E3
liveMovementInstruction=turn_left_slowly
checks=6/6 passed
experimentLogRecords=10
editorExitedPlayMode=true
consoleErrors=0
```

The six checks cover:

- Installer scene contract and runtime dependency resolution.
- A real Unity `AgentClient` request to the live backend, with the approved `E3 / turn_left_slowly` response applied to narration and movement UI.
- The verifier-rejected Unity presentation branch, using a rejected clone of the live response passed through the real `GuidanceReceived` event wiring.
- Arrival at the real E3 scene anchor through `ArrivalDetector`, advancing the task toward E5.
- Arrival at the real E5 scene anchor through `ArrivalDetector`, completing `E1 -> E3 -> E5` in Play Mode.
- Runtime inspection of the generated experiment JSONL, including request, response, task-state, and participant records.

The deterministic backend currently has no public request input that intentionally produces `verifier.approved=false`: baseline conditions bypass the final verifier, while `ours_full` builds a safe draft. The harness therefore proves the Unity rejected-response branch without claiming that the live backend emitted the rejected response.

## Phase Coverage

| Phase | Evidence |
|---|---|
| P1 | `CODEX_REFACTOR_P1_CONFIRMATION.md`; Build Settings contains `DualWorldGallery`; main scene Missing Script count is 0. |
| P2-P4 | Script and prefab moves are committed with `.meta` files preserved; main scene Missing Script count remains 0. |
| P5 | Backend pipeline/services refactor is committed; backend tests pass; live Anaconda-hosted backend responds on `/health` and `/api/guidance`. |
| P6-P8 | Core/task/navigation/local RDW runtime code is committed; EditMode tests cover route progression and local stale/unsafe guidance rejection; the Editor harness proves live approved guidance, rejected presentation, and real scene-anchor arrival in Play Mode. |
| P9 | Rotation gain lifecycle is committed; EditMode tests cover default-disabled behavior, turn start, bounded application, stop, reversal, completion, and explicit disable. Real-device comfort remains unclaimed. |
| P10 | Critical reset clears guidance and cancels active/pending gain; EditMode tests cover pause, cancel, and resume; Play Mode validation starts and stops without project errors. |
| P11 | Experiment dashboard/logger/matrix/session source is committed; EditMode test covers experiment matrix and task conversion; the Play Mode harness validates a completed T2 session with 10 JSONL records. |

## Still Requires Physical Validation

The following plan gates cannot be honestly completed from static checks, backend tests, Editor checks, or simulator-driven Play Mode validation alone:

- A complete multi-condition participant experiment session across B1-B5/Ours, T1-T4, and all four physical spaces.
- PICO build and permission validation.
- XR headset pose mapping on a real PICO device; the standard pose conversion is implemented and EditMode-tested, but has not been observed on hardware.
- Real user turn detection.
- Rotation gain start/stop/comfort under headset movement.
- Critical risk pause/reset safety under physical movement constraints.

These are explicitly marked as PM/XR requirements in the execution plan, and XR Simulator cannot replace the P9/P10 real-device acceptance criteria.
