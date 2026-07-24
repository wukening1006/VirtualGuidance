# VirtualGuidance Real-Device Readiness Optimization Plan

**Date**: 2026-07-24  
**Goal**: Enable complete flow execution on PICO device as soon as possible  
**Current State**: ~80% complete, 3 P0 blockers preventing real-device run

---

## Executive Summary

The system architecture is fundamentally sound:
- ✅ Candidate-set reservation → MLLM selection → validate → commit pattern correctly implemented
- ✅ Unity RiskSnapshot authority properly enforced
- ✅ Fail-closed safety validation throughout
- ✅ Backend contract well-designed with strict identity matching
- ✅ T2 route correctly configured (E1 → E5 direct, E3/E4 as runtime detours)
- ✅ `avatar_action` fully removed from formal path

**3 P0 blockers remain**, all with runtime evidence or confirmed gaps:

1. **Direction Authority Conflict**: Backend uses static compass table; Unity uses live pose calculation; validator hard-rejects mismatches
2. **Desktop Locomotion Missing**: No keyboard/mouse driver for editor iteration; can't complete loop without headset
3. **XR Loader Config Mismatch**: Standalone build targets PICO Live Preview, not OpenXR/SteamVR as intended

---

## P0 Blockers (Must Fix Before Real Device)

### P0-1: Direction Authority Conflict ⚠️ **BREAKS HAPPY PATH**

**Evidence**:
- Runtime console log (20260724_021112, condition `ours_full`, target E5, risk=low):
  - `"Local RDW check rejected conflicting movement 'ri[ght]'" → target_abandoned`
- Static backend table (`backend/agents/rdw_affordance_agent.py:8-17`):
  ```python
  TARGET_DIRECTION = {"E1": "forward", "E2": "forward", "E3": "left", ...}
  ```
- Live Unity calculation (`Assets/Scripts/Navigation/NavigationTargetManager.cs:144-168`):
  ```csharp
  relativeYawDegrees = Vector3.SignedAngle(userForward.normalized, toTarget.normalized, Vector3.up);
  ```
- Hard-reject validation (`Assets/Scripts/RDW/LocalGuidanceValidator.cs:115-119`):
  ```csharp
  if (movementDirection != targetDirection) { reason = "conflicting movement"; return false; }
  ```

**Root Cause**:
- Unity sends authoritative `semantic_candidates[].relative_direction` (computed from live pose)
- Backend ignores this field and uses static `TARGET_DIRECTION[exhibit_id]`
- Static table only valid at start pose (user facing +Z); any rotation breaks alignment
- Will fire constantly on real device with arbitrary user orientation

**Impact**: High-severity silent failure — target gets abandoned, intervention fallback triggers, user never reaches destination

**Fix Strategy**:
Replace all backend usages of `TARGET_DIRECTION` table with Unity's authoritative `relative_direction` from `semantic_candidates`.

**Files to Modify**:
1. `backend/agents/rdw_affordance_agent.py`
   - Delete `TARGET_DIRECTION` dict (lines 8-17)
   - Delete `target_direction()` function (lines 90-91)
   - Modify `_build_redirection()` (lines 47-79):
     - Build `exhibit_id → relative_direction` lookup from `request.semantic_candidates`
     - Use this lookup instead of calling `target_direction()`
   - Modify `build()` (lines 23-45):
     - Line 38: `preferred_direction = lookup[semantic_target]` instead of `target_direction(semantic_target)`

2. `backend/agents/guide_mock.py`
   - Remove import of `target_direction` (line 5)
   - Add helper `get_direction_from_candidates(target_id, semantic_candidates) -> str`
   - Lines 53, 69, 168: replace `target_direction(target)` with helper call

3. `backend/agents/safety_verifier_agent.py`
   - Remove import of `target_direction` (line 66 or wherever used)
   - Thread `semantic_candidates` through to verifier
   - Use Unity's direction instead of static table

**Validation**:
- Run `pytest backend/tests/` — all tests should pass with new lookup
- PlayMode test in Unity: rotate user 90° left, request E5 → backend should return `relative_direction="right"` (not "forward")
- Console logs should show zero "conflicting movement" rejections

---

### P0-2: Desktop Locomotion Driver Missing ⚠️ **BLOCKS ITERATION**

**Evidence**:
- No keyboard/mouse input handler exists in codebase (Glob search: zero matches for WASD, arrow keys, mouse delta)
- `XRTrackingAdapter.cs` EditorSimulation mode uses static pose that never updates
- `MotionDetector.cs` never sees motion → guidance loop never triggers
- Cannot complete E1 → E5 flow without VR headset

**Impact**: Developers cannot iterate on guidance flow in Unity Editor; every test requires headset setup

**Fix Options**:

**Option A: Minimal Keyboard Driver** (Recommended for speed)
- New script: `Assets/Scripts/Input/DesktopLocomotionDriver.cs`
- On Update when `Application.isEditor && !XRSettings.enabled`:
  - WASD/arrows: accumulate translation in virtual space
  - Q/E or mouse delta: rotate virtual yaw
  - Inject accumulated transform into `UserPoseTracker` or `XRTrackingAdapter`
- Add to MainScene, disabled on Standalone builds
- **Effort**: 2-4 hours

**Option B: Mock Animation Path** (Alternative)
- Playback pre-recorded pose timeline (E1 start → walk toward E5 → approach boundary)
- Useful for repeatable automated tests
- Doesn't support interactive exploration
- **Effort**: 4-6 hours

**Option C: Live Preview Only** (Defer desktop iteration)
- Accept that all iteration requires PICO Live Preview connection
- Adds ~30s overhead per iteration (build → stream → headset on)
- Not recommended: kills productivity during multi-condition testing

**Recommendation**: Implement Option A. Critical for rapid iteration before real-device deployment.

---

### P0-3: XR Loader Config Mismatch ⚠️ **WRONG BUILD TARGET**

**Evidence**:
- `ProjectSettings/XRPluginManagement.asset` for Standalone (Windows):
  - Loader: `PXR_PTLoader` (PICO Live Preview loader)
  - OpenXR disabled in loader list
- Documentation claims SteamVR/OpenXR compatibility
- PICO Live Preview requires live editor connection — not standalone-runnable
- OpenXR Interaction Profiles all disabled in `XR Interaction Toolkit` settings

**Impact**: Standalone builds won't run; incorrect runtime assumptions

**Fix Strategy**:
Clarify target platform and align config:

**If target is SteamVR (PICO Connect → PC → SteamVR → Unity)**:
1. Unity → Edit → Project Settings → XR Plug-in Management → Standalone
2. Enable: `OpenXR` loader
3. Disable: `PXR_PTLoader` (or remove from loader list)
4. XR Interaction Toolkit → Enable required OpenXR Interaction Profiles:
   - Generic XR Controller
   - HTC Vive Controller
   - (Optional: Eye Gaze Interaction Profile if using gaze tracking)
5. Rebuild player, test with SteamVR running

**If target is PICO native APK (Direct on-device, no PC)**:
1. Switch Build Target to Android
2. Enable: `PXR_PTLoader` for Android platform
3. Configure PICO SDK version in XR settings
4. Build APK, sideload to device
5. Note: Backend must be reachable from device (local network or cloud deploy)

**If target is PICO Live Preview (Current config, for prototyping only)**:
- Accept that this is dev-only
- Standalone builds will fail
- Document limitation clearly

**Recommendation**: Decide target path with user. If "真机" (real device) means wireless PICO Connect streaming, choose SteamVR path. If it means native APK, choose PICO native path.

**Validation**:
- SteamVR path: Launch SteamVR, run Standalone build, confirm tracking + backend requests work
- Native APK path: Deploy APK, confirm tracking + network connectivity to backend

---

## P1 Issues (Should Fix Soon)

### P1-1: MLLM Integration Incomplete

**Status**: Codebase has vLLM+Qwen2.5-VL infrastructure but not yet tested end-to-end

**Evidence**:
- `backend/agents/guide_llm.py` has complete OpenAI-compatible client setup
- `GuideDraftGenerator` expects vLLM endpoint at `http://localhost:8000`
- No recent commit message mentions successful MLLM round-trip
- All current runs use `GuideMockAgent` (deterministic baselines)

**Risk**: MLLM may hallucinate unsafe responses, refuse to call tools, or return malformed JSON

**Fix**:
1. Start vLLM server with Qwen2.5-VL model
2. Configure `backend/app.py` to use `GuideLLMAgent` instead of mock
3. Run condition `B1_MLLM_ONLY` in PlayMode
4. Capture and analyze first 10 MLLM responses
5. Add safety checks:
   - Reject responses with `movement_instruction == RESET_REQUIRED` (already done at line 107)
   - Validate `selected_target` membership (already done at line 109)
   - Log prompt + response for every call (for offline analysis)

**Effort**: 8-12 hours (includes prompt tuning if MLLM misbehaves)

---

### P1-2: Six-Condition Test Matrix Incomplete

**Current State**:
- All 6 conditions defined in `Condition` enum
- Mock agent has branches for each condition
- No automated test suite that runs all 6 conditions × 4 physical spaces × T2/T3/T4 tasks

**Needed**:
- Automated test harness: `Assets/Tests/Editor/FullConditionSweep.cs`
- Runs all condition × space × task combinations (6 × 4 × 3 = 72 scenarios)
- Captures metrics: completion rate, reset count, boundary violations, target abandonment rate
- Outputs CSV for offline analysis

**Effort**: 12-16 hours

---

### P1-3: Real-Device Calibration Flow Untested

**Current State**:
- `UserCalibrationController.cs` has complete pose recording logic
- Physical space bounds defined in `physical_spaces.json`
- Never tested on actual PICO device with real room boundaries

**Risk**: Calibration may be unintuitive, bounds may misalign, reset may trigger too often

**Fix**:
1. Deploy to device (after P0-3 fix)
2. Record 3 different physical spaces (large/medium/narrow)
3. Validate boundary detection accuracy
4. Tune `PhysicalRiskEvaluator` clearance thresholds if needed

**Effort**: 4-6 hours (on-device testing)

---

## P2 Issues (Technical Debt, Low Priority)

### P2-1: Orphaned Avatar Code

**Files**:
- `Assets/Scripts/Avatar/GuideAvatarController.cs` (never instantiated)
- `AvatarAction` enum still in codebase (unused after removal from DTOs)

**Fix**: Delete files, clean up enum if not used elsewhere

---

### P2-2: Dead Methods in Core Classes

**Examples**:
- `NavigationCoordinator.CommitPrematureTarget()` — never called
- `SemanticTargetSelector.semanticNextTargetId` — never read after assignment

**Fix**: Remove dead code after confirming via call graph analysis

---

### P2-3: Logging Incomplete

**Current State**:
- Backend writes structured JSON logs (`backend/logs/`)
- Unity logs to console but no structured on-device persistence

**Recommendation**: Add Unity-side JSON logger that writes to Application.persistentDataPath for post-session analysis

---

## Recommended Fix Order

**Phase 1: Unblock Desktop Iteration** (1 day)
1. Fix P0-2: Implement desktop locomotion driver
2. Validate: Complete E1 → E5 flow in PlayMode using WASD

**Phase 2: Fix Direction Authority** (0.5 day)
1. Fix P0-1: Replace backend static table with Unity's `relative_direction`
2. Validate: Rotate user 90° in editor, confirm guidance adjusts correctly

**Phase 3: Real-Device Config** (0.5 day)
1. Fix P0-3: Align XR loader with target platform (SteamVR or PICO native)
2. Validate: Build and deploy, confirm tracking works

**Phase 4: First Real-Device Run** (1 day)
1. On-device calibration + T2 E1→E5 run with condition `B4_SCRIPTED_RESET`
2. Capture logs, analyze boundary handling
3. Iterate on clearance thresholds if needed

**Phase 5: MLLM Integration** (2-3 days)
1. Fix P1-1: vLLM + Qwen2.5-VL end-to-end test
2. Condition sweep: B1 vs B5 vs ours_full
3. Prompt tuning based on hallucination patterns

**Total Estimated Time to Real-Device Run**: 3-4 days (assumes no major surprises)

---

## Success Criteria

✅ **Real-Device Run Complete** when:
1. User calibrates physical space on PICO device
2. Starts T2 task at E1, facing arbitrary direction (not aligned to +Z)
3. Backend returns guidance with correct relative direction
4. User reaches E5 without validator rejection or boundary violation
5. All 6 conditions runnable with deterministic mock
6. Zero "conflicting movement" errors in console logs

---

## Risk Mitigation

**If desktop driver takes too long**:
- Accept Live Preview-only iteration (P0-2 → P1)
- Prioritize P0-1 and P0-3 instead

**If XR config is broken beyond quick fix**:
- Spawn dedicated agent to audit XR Plug-in Management + Interaction Profiles
- Budget 1 extra day for deep XR troubleshooting

**If MLLM hallucinates unsafely**:
- Keep mock agent as primary, use MLLM only for B1 condition
- Defer full MLLM integration to post-real-device milestone

---

## Next Actions

**User Decision Required**:
1. **XR Target Platform**: SteamVR (PICO Connect wireless) or Native APK (direct on-device)?
2. **Fix Order**: Start with desktop driver (P0-2) or direction authority (P0-1)?
3. **MLLM Priority**: Integrate now or defer until after first real-device run?

**Ready to Execute Once Approved**:
- All P0 fixes have concrete file/line change lists above
- Estimated effort: 3-4 days total to first successful real-device run
- Can proceed in phases with validation gates between each
