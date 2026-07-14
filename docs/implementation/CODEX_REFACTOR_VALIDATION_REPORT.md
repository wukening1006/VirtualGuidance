# Codex Refactor Validation Report

> Date: 2026-07-14
> Branch: `refactor`
> Scope: `docs/implementation/CODEX_REFACTOR_EXECUTION_PLAN.md`

## Summary

P1-P11 have been implemented as staged commits on `refactor`.

This report records the validation evidence that can be proven from the local repository, backend tests, Unity Editor checks, Unity EditMode tests, and a Play Mode smoke run. It does not claim XR device completion. PICO/XR real-device checks remain required for the parts of the plan that explicitly require physical headset validation.

## Latest Atomic Commits

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
python -m pytest backend/tests -p no:cacheprovider --basetemp .pytest-tmp
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

### Unity EditMode Tests

`GuidanceRuntimeEditModeTests` was added under `Assets/Tests/Editor`.

Unity Test Runner result:

```text
Test run completed: 6/6 passed.
```

Covered checks:

- `GuidanceTaskController` advances `E1 -> E3 -> E5 -> Completed`.
- `LocalGuidanceValidator` rejects stale guidance and unsafe forward movement.
- `PauseResetController` clears visible guidance, shows the reset reason, and hides it after resume.
- `RotationGainController` remains inactive by default and does not start gain from narration alone.
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

On 2026-07-14, a temporary PlayMode live-backend probe was attempted using Unity Test Runner and `UnityWebRequest` against the Anaconda-hosted backend. PlayMode discovery could identify the probe after adding a test asmdef, but the UnitySkills PlayMode runner did not return a stable completed result for the probe job. The temporary probe assets were removed and are not committed.

Therefore, the live backend HTTP checks above prove the backend endpoint and JSON behavior from the local machine, while full Unity Play Mode backend request/response validation remains unclaimed until a stable PlayMode runner or manual in-Editor flow can produce authoritative evidence.

## Phase Coverage

| Phase | Evidence |
|---|---|
| P1 | `CODEX_REFACTOR_P1_CONFIRMATION.md`; Build Settings contains `DualWorldGallery`; main scene Missing Script count is 0. |
| P2-P4 | Script and prefab moves are committed with `.meta` files preserved; main scene Missing Script count remains 0. |
| P5 | Backend pipeline/services refactor is committed; backend tests pass; live Anaconda-hosted backend responds on `/health` and `/api/guidance`. |
| P6-P8 | Core/task/navigation/local RDW runtime code is committed; EditMode tests cover route progression and local stale/unsafe guidance rejection. |
| P9 | Rotation gain scaffold is committed; EditMode test proves default-disabled behavior. |
| P10 | Pause/reset code is committed; EditMode tests cover guidance clearing and reset resume; Play Mode smoke starts and stops without console errors. |
| P11 | Experiment dashboard/logger/matrix/session source is committed; EditMode test covers experiment matrix and task conversion. |

## Still Requires Physical Validation

The following plan gates cannot be honestly completed from static checks, backend tests, Editor checks, or simulator smoke alone:

- Approved/rejected guidance branch validation with the full backend running in Play Mode.
- E1 -> E3 -> E5 task progression with real scene movement and arrival detection.
- Runtime logging inspection from a full experiment session.
- PICO build and permission validation.
- XR headset pose mapping between physical and virtual spaces.
- Real user turn detection.
- Rotation gain start/stop/comfort under headset movement.
- Critical risk pause/reset safety under physical movement constraints.

These are explicitly marked as PM/XR requirements in the execution plan, and XR Simulator cannot replace the P9/P10 real-device acceptance criteria.
