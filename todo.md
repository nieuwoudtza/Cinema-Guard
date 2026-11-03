# To do

## Linux version (command line + Docker)

A headless Linux build of Cinema Guard and a Docker image to run it on a server. No GUI, no Mac.
Asked for by Bryce Shreeve (bryce.shreeve@gmail.com, Ubuntu server) and a blog comment from July 2025.
Estimate: 4-6 hours.

- [ ] **Core goes cross-platform.** `net10.0-windows` -> `net10.0`; replace the Windows-only parts
      behind small platform switches, with Windows behaving exactly as now:
  - Recycle Bin (`Tools/RecycleBin.cs`, shell32) -> plain delete or freedesktop trash
  - Engine process cleanup (`Speech/EngineHost.cs`, kernel32 job objects) -> kill the process group
  - GPU detection (`Speech/Hardware.cs`, registry) -> `nvidia-smi` or `/sys`
  - `\\?\` long paths, `.exe` names, drive-letter roots in `RunHistory.cs`
  - DPAPI (`System.Security.Cryptography.ProtectedData`) -> another way to store secrets
- [ ] **Console app.** New `cinemaguard` CLI project around the existing `Pipeline/Headless.cs`, plus
      setup commands (`setup`, `models install`, FFmpeg check). Settings stay JSON.
- [ ] **Linux engine packs.** linux-x64 runtimes for Whisper.net (CPU, CUDA, Vulkan) and sherpa-onnx;
      Phonon on a Python venv / python-build-standalone with CPU torch (the main risk).
- [ ] **FFmpeg.** Use the system `ffmpeg`/`ffprobe` (7.1+), bundled in the Docker image.
- [ ] **Docker images.** CPU image and NVIDIA GPU image; models, settings and cache on a volume.
- [ ] **Test** in WSL2 and Docker on a full-length film (CPU and GPU), and rerun the Windows tests.
- [ ] **Later:** subtitle downloads (`subloader-cli` is Windows-only) -> call the OpenSubtitles API directly.
- [ ] **When it's out:** email Bryce Shreeve (thread "Cinema Guard Mac") and update the README.
