# Public camera implementation

Source: [HandyS11/RustPlusApi](https://github.com/HandyS11/RustPlusApi/tree/3b45e28edbdd81a077c01784b5707febc4832dc9/src/RustPlusApi.Camera), pinned commit `3b45e28edbdd81a077c01784b5707febc4832dc9` (the source revision of NuGet RustPlusApi.Camera 2.0.0-beta.9).

MIT license retained in `LICENSE` and included with desktop output. `CameraController.cs` and `IndexGenerator.cs` preserve the public implementation. `CameraRenderer.cs` uses the same ray decoding/palette/shuffle and replaces ImageSharp's pixel buffer/PNG encoding with SkiaSharp, eliminating the vulnerable ImageSharp transitive dependency without disabling CCTV, PTZ, turret or drone support.

The renderer also bounds camera allocations, ignores truncated ray records/negative offsets, and avoids offset arithmetic overflow. It is checked against upstream's five real captured frame sequences and approved PNGs (also pinned to this revision). These offline tests do not establish live relay availability or server permissions.
