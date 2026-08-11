# Terra High Handoff: Photon Hearing, Whisper, and Speech Configuration

## Objective

Bring the proven local speech-input patterns from Project Ali into Phos Agape Aphthartos so Photon can hear the user through an explicitly controlled microphone, produce real local Whisper transcripts, and let the user review and intentionally send a transcript.

Add the speech-input half of a polished **Speech & Voice** configuration page designed for a first-class main-menu destination. This lane must deliver real capability with honest unavailable states, not a decorative microphone button. Voice output/TTS is owned by a separate Max worker and must not be implemented or redesigned in this lane.

## Authoritative repositories

- Target: `C:\Users\clsor\Documents\Codex\HermesAgent`
- Read-only reference: `C:\Users\clsor\Documents\Codex\ProjectAli`

Before writing, inspect the current target worktree and announce exact owned files and baseline hashes. The target tree is intentionally dirty with active CAD, developer-tooling, and runtime work; do not reset or overwrite unrelated changes.

## Proven Ali source anchors

Use these current files as implementation references. Re-read them; do not copy names or behavior from memory.

- Whisper CLI provider  
  `src\Modules\Voice\WhisperCliSpeechToTextProvider.cs`  
  SHA-256 `2F39C440B11A250C88ED76DBB392186293DF080DC1A21448A7DE2D56E294C69B`
- Faster-Whisper wrapper  
  `src\Modules\Voice\Tools\local_whisper_stt.py`  
  SHA-256 `C9C8EEE91ACDB1155D55D14CDA27AEE4163FAC3EFDA1DDC578D987159A744C63`
- NAudio capture  
  `src\Modules\Voice\NAudioVoiceRecorder.cs`  
  SHA-256 `61E819FE0E53274D8276BB90E2A70A6BA9E4A49E01FFD5568C675DBB2B5A8BCE`
- Live input meter  
  `src\Modules\Voice\NAudioInputLevelMonitor.cs`  
  SHA-256 `D5EB0BAAEB6B2A85F2F8C60C6B781FFF94AE821D8A9B1E57B9158289472E7C9A`
- Runtime voice settings  
  `src\Modules\Voice\VoiceRuntimeSettings.cs`  
  SHA-256 `BFF05AFBC881C5E1DA750F1AE4B9A79C2E1FF7800CA1149B78204C2C0A84883B`
- Settings persistence  
  `src\Modules\Voice\VoiceRuntimeSettingsStore.cs`  
  SHA-256 `F5E014208B0E89B1691850CC65F7F2C62CD0884216171986962454D43A94F3A3`
- Stable device restoration  
  `src\Modules\Voice\VoiceDeviceSelection.cs`  
  SHA-256 `B9942A8869D3D78740F50EF4CACADC3D56A928C6C9359ECF263AF20B42259CC6`
- Integrated speech ingress  
  `src\Modules\Interaction\AliSpeechIngressPipeline.cs`  
  SHA-256 `8E0B6FEC16F0C3DA2858D84CF81837FEF8CE2B5788286D48AE5815147C07F483`
- Ali settings-page UX reference, especially the microphone and voice section  
  `src\UI\SettingsWindow.xaml`  
  SHA-256 `9517D0BD3EB6A199FB0147D754E054B075A11A8385ADBA89E574E8DC0E3D9DD5`

Also inspect rather than blindly copy:

- `src\Modules\Voice\VoiceCaptureSafetyGate.cs`
- `src\Modules\Voice\VoiceAudioNormalizer.cs`
- `src\Modules\Voice\VoiceSampleProcessor.cs`
- `src\Modules\Voice\VoiceInputLevelAnalyzer.cs`
- `src\Modules\Voice\VoiceContracts.cs`
- `src\Modules\Voice\LocalVoiceResourceLocator.cs`
- `tools\runtime-assets\requirements-whisper.txt`
- `external\modules\MicrophoneModule\**`
- `external\modules\VoiceActivityModule\**`
- `external\modules\SpeechToTextModule\**`

The Ali tree contains more than one speech implementation generation. Select the implementation that is actually composed and tested in the current Ali application; do not merge incompatible generations.

## First-pass ownership

Create only new isolated target roots:

- `src/Modules/HermesSpeechVoice/**`
- `src/Host/HermesSpeechVoice/**`
- `src/Host/HermesSpeechVoice.Smoke/**`

One new report under `docs/coordination/` is allowed.

Do **not** edit these shared seams in the first pass:

- `src/app/App.tsx`
- `src/app/styles.css`
- `src/Host/HermesDesktop/MainWindow.xaml.cs`
- `src/Host/HermesDesktop/HermesDesktop.csproj`
- `src/Modules/AgentDock/**`
- `src/Modules/HermesSystem/HermesSystemWorkspace.tsx`
- `src/Modules/HermesRuntimeConfiguration/**`
- launcher, installer, Compose, package manifests, CAD, Docker Control Center, or DeveloperServices files

After isolated gates pass, report the exact smallest menu/host/package/installer seams root must mount. Do not silently take them.

## Required Photon toolbar placement and ownership split

The final mounted UI must place two compact, first-class controls in the Photon panel header, at the top beside the existing `+` action:

1. **Microphone / Hear** — owned by this Whisper hearing lane. It starts/stops the explicit speech-input workflow and visibly reflects idle, listening, transcribing, review-ready, unavailable, and fault states.
2. **Voice / Speak** — owned by the separately assigned Max voice-output lane. It controls Photon speech output/TTS and is not part of this worker's implementation authority.

The controls must visually match the existing Photon header icons, have tooltips and accessible names, preserve the current `+` action, and remain usable at narrow dock widths. The two buttons must not be merged into one ambiguous toggle.

Collision rule: the hearing worker must finish and freeze its isolated input controller/protocol first. It may then provide an exact narrow mount patch or mount map for the microphone button only. It must not modify Max's voice-output implementation. If both lanes need the same Photon header file, stop and serialize the two patches through the root owner instead of racing shared bytes.

## Functional requirements

### Native authority

- Enumerate actual Windows audio-input devices with stable host-owned opaque IDs and labels.
- Expose the current/default device and an honest unavailable reason.
- Permit exactly one capture or test operation at a time.
- Support explicit push-to-talk or an explicit user-enabled listening session. Ambient listening must be off by default.
- Report bounded live input level, recording duration, and capture state.
- Support cancellation and deterministic cleanup of the active recorder and transcription process.
- Keep native paths, executable arguments, environment values, temporary paths, and raw device handles out of renderer messages.
- Use a fixed, trusted Whisper runtime/model authority. Renderer input may choose only host-advertised opaque model/language/device IDs.
- Bound recording duration, PCM/WAV bytes, transcript characters, native output, timeouts, and concurrent operations.
- Return only typed transcript, language if genuinely detected, duration, and bounded status/evidence.
- Delete or otherwise retire temporary audio after the operation unless an explicit user-facing diagnostic-retention setting is enabled.

### Composer experience

- Add an isolated microphone/composer controller and view suitable for later mounting next to the attachment button.
- The production microphone control belongs in the Photon panel's top header beside the existing `+` action. The bottom composer may show transcript-review state, but it is not the primary microphone-control location.
- Press or keyboard activation starts the explicit capture; release/stop ends capture and begins transcription.
- Show Listening, Processing, Ready, Cancelled, and Unavailable states clearly.
- Place the transcript into a reviewable draft.
- Provide an explicit **Send to Photon** intent, but do not automatically send or execute a transcript.
- Reject empty, too-quiet, clipped, timed-out, stale, or superseded captures with actionable text.
- Never claim that Photon heard something when the local authority did not return a verified transcript.

### Speech & Voice configuration page

Build the speech-input side of a polished Phos-styled module with:

- input-device selection;
- live microphone level meter;
- test microphone action;
- Whisper runtime/model selection from real host-advertised choices;
- language or Auto Detect only when supported;
- compute/device selection only when the installed authority exposes it;
- VAD threshold and silence duration only if real VAD is mounted;
- push-to-talk shortcut display/configuration with conflict/error feedback;
- maximum capture duration display;
- input gain/normalization only when the actual capture authority applies them;
- test transcription flow with a transcript preview;
- clear current status, model identity, and unavailable reason;
- explicit save/apply behavior and truthful unsaved/saving/saved/error states.

Reserve a clearly separated adjacent section or navigation destination for Max's voice-output settings. Do not create placeholder TTS voices, output-device controls, or speaking options in this hearing lane.

Do not copy Ali branding or WPF layout literally. Translate the proven interaction into current Phos React/CSS surfaces, typography, teal active accents, restrained purple identity, focus language, and responsive layout.

### Settings truthfulness

- Persist only settings the host actually consumes.
- Do not display controls that disagree with hidden runtime overrides.
- When a server/runtime default is unknown, say so and omit the override.
- A saved profile must name the exact device/model/language choices it controls.
- Do not expose arbitrary executable, model path, Python path, arguments, or environment editors in the renderer.

## Typed protocol expectations

Use a versioned renderer/native protocol with request IDs and latest-wins cancellation where appropriate. The minimum operations are:

- describe capability/runtime/device/model catalog;
- start capture;
- stop and transcribe;
- cancel;
- read current bounded level/status;
- test capture/transcription;
- read settings;
- review/update settings;
- commit settings.

Settings update should use review/commit or an equivalently explicit exact-value confirmation boundary. No free-form native command surface.

## Accessibility and layout

- Keyboard-operable controls and visible `:focus-visible` states.
- Proper labels, status/live regions, and meter semantics.
- Forced-colors support.
- Responsive at approximately 360, 800, and 1280 CSS pixels.
- No horizontal page overflow.
- Microphone state must not rely on color alone.

## Required evidence

- Exact Ali source anchors used and what was adapted versus omitted.
- Host Release build with 0 warnings/errors.
- Focused host smoke proving device projection, one-active capture, cancellation, bounds, stale-result rejection, and real or explicitly fake-separated transcription.
- If the exact local Whisper runtime exists, one real microphone-or-fixture transcription through the production authority. If it does not, report the missing immutable runtime/model receipt and do not claim live hearing.
- Focused React/controller tests.
- Strict TypeScript.
- Production Vite build where the isolated module is reachable by the build graph; otherwise provide a small compile harness and state that shared mounting is pending.
- Format and `git diff --check` for owned files.
- Exact final hashes.

## Package and provisioning rule

Ali uses NAudio and Faster-Whisper-related assets. If Hermes lacks an approved dependency or runtime:

1. do not edit package/project/installer files in this lane;
2. name the exact dependency, version, license/provenance, and minimum shared file changes;
3. keep the feature honestly unavailable until root integrates and verifies it.

## Security handling

Functionality is the current priority. Record newly encountered security issues in the handoff and continue unless an issue directly prevents truthful or safe operation. Do not expand into unrelated security remediation.

## Final handoff

Return:

- exact changed files and SHA-256 values;
- builds/tests and real-versus-fake evidence;
- supported devices/models/languages and actual capture mode;
- shared mount seams for main menu, composer, host registration, project references, packages, and installer/runtime assets;
- the exact Photon-header microphone mount seam beside `+`, plus any shared-file collision with Max's adjacent voice-output button;
- remaining functionality gaps;
- confirmation that no ambient recording or automatic transcript send is enabled.

Stop after the handoff so root can review and serialize shared integration.
