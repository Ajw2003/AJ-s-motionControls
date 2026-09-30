# AI-generated sound effects and music — plan

**Tier 5 plan.** Issue #26. Status, 2026-09-30: **drafted, awaiting the owner's approval.** Nothing
below is built yet.

## Decision

Owner's choice, 2026-09-30: generate the audio with **open models run locally** on this machine's
RTX 5070, not sample libraries and not better synthesis.

| Job | Model | Licence of the output | Why this one |
|---|---|---|---|
| Sound effects | Stable Audio Open 1.0 (via `diffusers`) | Stability AI Community Licence — free for commercial use under US$1M annual revenue | Best open SFX model whose output may ship. The ungated alternatives (AudioLDM 2, TangoFlux) are non-commercial only. |
| Music | ACE-Step | Apache-2.0 | Open music model with a shippable licence that fits in 12 GB. MusicGen's weights are non-commercial. |
| Picking the best take | LAION CLAP (already in the local model cache) | not shipped | Scores each candidate against its text prompt, so each file is the best of several takes rather than the first one out. |

## What this machine has (checked 2026-09-30)

- PyTorch 2.12.0 dev + CUDA 12.8, `torch.cuda.is_available()` true on the RTX 5070 (12 GB).
- ffmpeg 9.0, for trimming, loudness normalisation and loop crossfades.
- No Hugging Face token. Stable Audio Open is gated, which is the one step only the owner can do.

## Steps

1. **Environment.** A Python virtual environment at `tools/audio/ai/.venv` (git-ignored) with
   PyTorch cu128, `diffusers`, `transformers` and ACE-Step, plus a `setup.ps1` that builds it from
   scratch. About 10 GB of model downloads on the first run.
2. **Owner, once: unlock Stable Audio Open.** Accept the licence on its Hugging Face page and sign in
   with `hf auth login`. The exact steps will be handed over when step 1 is done.
3. **Sound effects.** `tools/audio/ai/generate_sfx.py`: one text prompt per sound, several takes
   each, ranked by CLAP, then trimmed, loudness-normalised and written as 48 kHz 16-bit `.wav` to
   `Assets/Audio/` under **the same 22 file names**, so `AudioBank` needs no code change. The prompts
   live in one table in the script, so re-rolling a sound is one edit and one run. The synthesised
   originals stay reproducible with `node tools/audio/generate-sfx.js`.
4. **Music.** `tools/audio/ai/generate_music.py`: a calm lobby/choosing track and an in-match
   track, each made loopable with an ffmpeg crossfade at the seam, written to `Assets/Audio/Music/`.
5. **Play the music in Unity.** `AudioChannel.Music` exists but nothing plays music yet. A small
   `MusicDirector` switches track on the match phase and crossfades. It should be built through
   `AudioManager`, so the music goes through the existing volume channel.
6. **Licences recorded.** `Assets/Audio/LICENSES.md` names the model and licence behind every file.
7. **Owner listens in game** and names the sounds to re-roll. This is the acceptance check, and it
   cannot be skipped.

## Risks

- **Short, sharp sounds** (a skip "plink", a UI tick) are where diffusion models are weakest. The
  fallback per sound is the current synthesised clip, with a note saying so. It is never silence.
- **ACE-Step on Python 3.13.** It may need 3.11 or 3.12. If so, the environment uses `uv` to fetch
  that interpreter locally rather than touching the system Python.
